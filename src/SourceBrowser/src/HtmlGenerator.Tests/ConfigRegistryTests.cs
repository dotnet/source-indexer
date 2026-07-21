using System.Collections.Generic;
using System.IO;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class ConfigRegistryTests
    {
        private string tempRoot;

        [TestInitialize]
        public void Setup()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "ConfigRegistryTests_" + System.Guid.NewGuid().ToString("N"));
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
        public void NoConfigRegistered_ConfigsFileNeverCreated()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, null);
            ConfigRegistry.EnsureConfigRegistered(tempRoot, string.Empty);

            File.Exists(Path.Combine(tempRoot, ConfigRegistry.ConfigsFileName)).ShouldBeFalse();
            ConfigRegistry.GetRegisteredConfigs(tempRoot).ShouldBeEmpty();
        }

        [TestMethod]
        public void SingleConfig_IsRegistered()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows");

            ConfigRegistry.GetRegisteredConfigs(tempRoot).ShouldBe(new[] { "windows" });
        }

        [TestMethod]
        public void RegisteringSameConfigTwice_DoesNotDuplicate()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows");
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows");

            ConfigRegistry.GetRegisteredConfigs(tempRoot).ShouldBe(new[] { "windows" });
        }

        [TestMethod]
        public void RegisteringSameConfigDifferentCase_DoesNotDuplicate()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows");
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "WINDOWS");

            ConfigRegistry.GetRegisteredConfigs(tempRoot).ShouldBe(new[] { "windows" });
        }

        [TestMethod]
        public void MultipleConfigs_AllRetained()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows");
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "linux");
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "mac");

            var configs = ConfigRegistry.GetRegisteredConfigs(tempRoot);
            configs.Count.ShouldBe(3);
            configs.ShouldContain("windows");
            configs.ShouldContain("linux");
            configs.ShouldContain("mac");
        }

        [TestMethod]
        public void ConcurrentRegistrations_FromManyThreads_LoseNoEntries()
        {
            const int threadCount = 16;
            var threads = new System.Threading.Thread[threadCount];

            for (int i = 0; i < threadCount; i++)
            {
                var configName = "config" + i;
                threads[i] = new System.Threading.Thread(() => ConfigRegistry.EnsureConfigRegistered(tempRoot, configName));
            }

            foreach (var t in threads)
            {
                t.Start();
            }

            foreach (var t in threads)
            {
                t.Join();
            }

            var configs = ConfigRegistry.GetRegisteredConfigs(tempRoot);
            configs.Count.ShouldBe(threadCount);
            for (int i = 0; i < threadCount; i++)
            {
                configs.ShouldContain("config" + i);
            }
        }

        [TestMethod]
        public void ConfigRegisteredWithoutAxisTags_HasEmptyAxisTags()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows");

            var entries = ConfigRegistry.GetRegisteredConfigEntries(tempRoot);
            entries.Count.ShouldBe(1);
            entries[0].Name.ShouldBe("windows");
            entries[0].AxisTags.ShouldBeEmpty();
        }

        [TestMethod]
        public void ConfigRegisteredWithAxisTags_RoundTrips()
        {
            var axisTags = new Dictionary<string, string> { ["os"] = "windows", ["arch"] = "x64" };
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows-x64", axisTags);

            var entries = ConfigRegistry.GetRegisteredConfigEntries(tempRoot);
            entries.Count.ShouldBe(1);
            entries[0].Name.ShouldBe("windows-x64");
            entries[0].AxisTags.Count.ShouldBe(2);
            entries[0].AxisTags["os"].ShouldBe("windows");
            entries[0].AxisTags["arch"].ShouldBe("x64");

            // GetRegisteredConfigs (names-only) is unaffected by axis tags being present.
            ConfigRegistry.GetRegisteredConfigs(tempRoot).ShouldBe(new[] { "windows-x64" });
        }

        [TestMethod]
        public void MixOfTaggedAndUntaggedConfigs_EachRetainsOwnTags()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows-x64", new Dictionary<string, string> { ["os"] = "windows", ["arch"] = "x64" });
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "linux-x64", new Dictionary<string, string> { ["os"] = "linux", ["arch"] = "x64" });
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "legacy");

            var entriesByName = new Dictionary<string, ConfigRegistryEntry>();
            foreach (var entry in ConfigRegistry.GetRegisteredConfigEntries(tempRoot))
            {
                entriesByName[entry.Name] = entry;
            }

            entriesByName.Count.ShouldBe(3);
            entriesByName["windows-x64"].AxisTags["os"].ShouldBe("windows");
            entriesByName["linux-x64"].AxisTags["os"].ShouldBe("linux");
            entriesByName["legacy"].AxisTags.ShouldBeEmpty();
        }

        [TestMethod]
        public void ReRegisteringSameConfig_WithDifferentAxisTags_KeepsFirstWriterTags()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows-x64", new Dictionary<string, string> { ["os"] = "windows" });
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows-x64", new Dictionary<string, string> { ["os"] = "should-not-win" });

            var entries = ConfigRegistry.GetRegisteredConfigEntries(tempRoot);
            entries.Count.ShouldBe(1);
            entries[0].AxisTags["os"].ShouldBe("windows");
        }

        [TestMethod]
        public void AxisTaggedConfig_Configs_txt_UsesTabAndSemicolonDelimitedFormat()
        {
            ConfigRegistry.EnsureConfigRegistered(tempRoot, "windows-x64", new Dictionary<string, string> { ["os"] = "windows", ["arch"] = "x64" });

            var line = File.ReadAllLines(Path.Combine(tempRoot, ConfigRegistry.ConfigsFileName))[0];
            line.ShouldStartWith("windows-x64\t");
            line.ShouldContain("os=windows");
            line.ShouldContain("arch=x64");
        }
    }
}
