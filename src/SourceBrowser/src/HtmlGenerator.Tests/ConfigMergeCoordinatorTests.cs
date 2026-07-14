using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class ConfigMergeCoordinatorTests
    {
        private string tempRoot;

        [TestInitialize]
        public void Setup()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "ConfigMergeCoordinatorTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ShouldMerge_IsFalse_WhenZeroOrOneConfigsRegistered()
        {
            ConfigMergeCoordinator.ShouldMerge(tempRoot).ShouldBeFalse();

            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows");
            ConfigMergeCoordinator.ShouldMerge(tempRoot).ShouldBeFalse();
        }

        [TestMethod]
        public void ShouldMerge_IsTrue_WhenTwoOrMoreConfigsRegistered()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows");
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "linux");

            ConfigMergeCoordinator.ShouldMerge(tempRoot).ShouldBeTrue();
        }

        [TestMethod]
        public void RunGuarded_SerializesConcurrentMergeAttempts()
        {
            // Two "merges" racing against the same /out must never execute their bodies concurrently --
            // this is what stops the merged-output write from tearing. Detect any overlap with a shared
            // in-critical-section flag: if RunGuarded ever let two callers in at once, this flips true.
            var inCriticalSection = 0;
            var overlapDetected = false;
            const int iterations = 25;

            void MergeAttempt()
            {
                for (int i = 0; i < iterations; i++)
                {
                    ConfigMergeCoordinator.RunGuarded(tempRoot, () =>
                    {
                        if (Interlocked.Increment(ref inCriticalSection) > 1)
                        {
                            overlapDetected = true;
                        }

                        Thread.Sleep(2);

                        Interlocked.Decrement(ref inCriticalSection);
                    });
                }
            }

            var t1 = new Thread(MergeAttempt);
            var t2 = new Thread(MergeAttempt);
            var t3 = new Thread(MergeAttempt);

            t1.Start();
            t2.Start();
            t3.Start();

            t1.Join();
            t2.Join();
            t3.Join();

            overlapDetected.ShouldBeFalse();
        }

        [TestMethod]
        public void WriteAtomically_CreatesTargetDirectory_WhenItDoesNotYetExist()
        {
            var targetDir = Path.Combine(tempRoot, "index");

            ConfigMergeCoordinator.WriteAtomically(targetDir, stagingDir =>
            {
                File.WriteAllText(Path.Combine(stagingDir, "a.txt"), "v1");
            });

            Directory.Exists(targetDir).ShouldBeTrue();
            File.ReadAllText(Path.Combine(targetDir, "a.txt")).ShouldBe("v1");

            // No leftover staging/previous directories.
            Directory.GetDirectories(tempRoot).ShouldBe(new[] { targetDir });
        }

        [TestMethod]
        public void WriteAtomically_ReplacesExistingTargetDirectory_LeavingNoStagingOrPreviousLeftovers()
        {
            var targetDir = Path.Combine(tempRoot, "index");

            ConfigMergeCoordinator.WriteAtomically(targetDir, stagingDir => File.WriteAllText(Path.Combine(stagingDir, "a.txt"), "v1"));
            ConfigMergeCoordinator.WriteAtomically(targetDir, stagingDir => File.WriteAllText(Path.Combine(stagingDir, "a.txt"), "v2"));

            File.ReadAllText(Path.Combine(targetDir, "a.txt")).ShouldBe("v2");
            Directory.GetDirectories(tempRoot).ShouldBe(new[] { targetDir });
        }

        [TestMethod]
        public void WriteAtomically_UnderRunGuarded_NeverExposesATornDirectory_ToAConcurrentReader()
        {
            // The requested race test: multiple "merges" racing to atomically rewrite the same target
            // directory (each guarded, as the real /config: auto-tail and /mergeConfigsOnly both would
            // be) while a reader repeatedly inspects the directory concurrently. Every rewrite writes
            // two files that must always agree with each other -- if the reader ever observes them
            // disagreeing, it saw a torn/partial write.
            var targetDir = Path.Combine(tempRoot, "index");
            ConfigMergeCoordinator.WriteAtomically(targetDir, stagingDir =>
            {
                File.WriteAllText(Path.Combine(stagingDir, "a.txt"), "0");
                File.WriteAllText(Path.Combine(stagingDir, "b.txt"), "0");
            });

            const int writerIterations = 20;
            var stopReading = false;
            var readerException = (System.Exception)null;
            var torn = false;

            void Writer(int writerId)
            {
                for (int i = 0; i < writerIterations; i++)
                {
                    var version = writerId + "-" + i;
                    ConfigMergeCoordinator.RunGuarded(tempRoot, () =>
                    {
                        ConfigMergeCoordinator.WriteAtomically(targetDir, stagingDir =>
                        {
                            File.WriteAllText(Path.Combine(stagingDir, "a.txt"), version);
                            Thread.Sleep(1);
                            File.WriteAllText(Path.Combine(stagingDir, "b.txt"), version);
                        });
                    });
                }
            }

            void Reader()
            {
                try
                {
                    while (!Volatile.Read(ref stopReading))
                    {
                        // The real guarantee WriteAtomically documents is that a SINGLE generation is
                        // never torn: at any instant, targetDir holds either the complete old directory
                        // or the complete new one, never a mix of the two. It does NOT guarantee that two
                        // SEPARATE, independent File.ReadAllText calls a moment apart land in the SAME
                        // generation -- an arbitrary number of complete, individually-consistent swaps can
                        // happen between reading a.txt and reading b.txt if this thread is descheduled for
                        // a while (exactly what CPU contention under a full parallel test run causes).
                        // Comparing a and b directly conflates "genuinely torn" with "read two different,
                        // individually-valid generations", which is not a correctness violation.
                        //
                        // A reader that needs a and b from the SAME generation has to validate that no
                        // swap happened between the two reads, the same way any client bridging multiple
                        // independent reads over a swappable directory must: read a, read b, then re-read
                        // a. If the re-read still matches the first read, no swap could have intervened
                        // (WriteAtomically only ever changes a.txt's content by swapping the whole
                        // directory), so a/b are provably from one generation and are safe to compare. If
                        // the re-read differs, a swap raced the sample -- discard it and try again rather
                        // than treating it as torn.
                        string a1, b, a2;
                        try
                        {
                            a1 = File.ReadAllText(Path.Combine(targetDir, "a.txt"));
                            b = File.ReadAllText(Path.Combine(targetDir, "b.txt"));
                            a2 = File.ReadAllText(Path.Combine(targetDir, "a.txt"));
                        }
                        catch (IOException)
                        {
                            // Transient "not found" during the brief instant between the two renames --
                            // expected/acceptable per the documented contract, a real caller would retry.
                            continue;
                        }

                        if (a1 != a2)
                        {
                            // A swap happened between the first and second read of a.txt, so b's read is
                            // not provably from the same generation as a1 -- not a validated sample.
                            continue;
                        }

                        if (a1 != b)
                        {
                            torn = true;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    readerException = ex;
                }
            }

            var writer1 = new Thread(() => Writer(1));
            var writer2 = new Thread(() => Writer(2));
            var reader = new Thread(Reader);

            reader.Start();
            writer1.Start();
            writer2.Start();

            writer1.Join();
            writer2.Join();
            stopReading = true;
            reader.Join();

            readerException.ShouldBeNull();
            torn.ShouldBeFalse();
        }

        [TestMethod]
        public void ConcurrentRegisterAndMergeCycles_NeverProduceAFinalViewStaleWrtTheRegistry()
        {
            // Models the exact TOCTOU the merge step must not have: two /config: runs racing their full
            // "register config -> enter guarded merge -> read registry -> decide -> write" cycle against
            // one /out. If the registry read happened OUTSIDE (or before) the mutex, a run that only saw
            // 1 config at read time could still win the write race against a run that correctly saw 2 --
            // producing a stale single-config view even though both configs are registered. Since
            // RunConfigMergeIfNeeded reads ConfigRegistry.GetRegisteredConfigs *inside* the RunGuarded
            // callback (mirrored exactly here), the mutex totally orders every read+write pair: whichever
            // cycle's write is LAST to land is guaranteed to have read the registry no earlier than its
            // own config's registration, so the final persisted view can never regress behind what's
            // actually registered at that point.
            var targetDir = Path.Combine(tempRoot, "index");
            const int cycles = 15;

            void RegisterAndMergeCycle(string configName)
            {
                for (int i = 0; i < cycles; i++)
                {
                    ConfigRegistry.EnsureConfigRegistered(tempRoot, configName);

                    ConfigMergeCoordinator.RunGuarded(tempRoot, () =>
                    {
                        // The load-bearing ordering: the registry is read *inside* the same guarded
                        // section that performs the write below, exactly as RunConfigMergeIfNeeded does.
                        var registeredCount = ConfigRegistry.GetRegisteredConfigs(tempRoot).Count;

                        ConfigMergeCoordinator.WriteAtomically(targetDir, stagingDir =>
                        {
                            File.WriteAllText(Path.Combine(stagingDir, "configCount.txt"), registeredCount.ToString());
                        });
                    });
                }
            }

            var t1 = new Thread(() => RegisterAndMergeCycle("windows"));
            var t2 = new Thread(() => RegisterAndMergeCycle("linux"));

            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            // Both configs are registered by now, and both threads' cycles have fully drained -- so the
            // very last write to land (whichever thread's last iteration wins the mutex race) must have
            // read the registry no earlier than both configs' registration, i.e. must see 2. A stale
            // single-config read (1) surviving as the final on-disk state would mean the TOCTOU this test
            // targets had crept back in.
            ConfigRegistry.GetRegisteredConfigs(tempRoot).Count.ShouldBe(2);
            File.ReadAllText(Path.Combine(targetDir, "configCount.txt")).ShouldBe("2");
        }
    }
}
