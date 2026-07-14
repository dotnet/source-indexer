using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// Owns the concurrency/atomicity contract for merging multiple /config:&lt;name&gt; runs' staged
    /// output into the one shared index -- this is the piece that makes the merge step both (a)
    /// standalone-invocable (independent of whether Pass1 ran in this process) and (b) safe when two
    /// /config: runs race against the same /out.
    ///
    /// This class deliberately does NOT know how to actually merge declarations/references/files --
    /// that's ConfigLocationMerger / ConfigReferenceMerger / ConfigFileDeduper, each already built and
    /// unit-tested in isolation. ConfigMergeCoordinator only guarantees that whatever merge logic runs,
    /// it (a) only runs when there's actually more than one registered config to merge, (b) never
    /// interleaves with a concurrent merge attempt against the same /out, and (c) never leaves behind a
    /// half-written output directory if it's interrupted mid-write.
    /// </summary>
    public static class ConfigMergeCoordinator
    {
        /// <summary>
        /// True when merging is meaningful for this /out -- i.e. two or more configs are registered.
        /// With 0 or 1 registered configs there's nothing to merge and the existing single-config output
        /// is already the correct, byte-identical-to-today result, so callers should skip the merge step
        /// entirely rather than run it as a no-op (keeps the default/no-config and single-config paths
        /// untouched, which is the back-compat guarantee the characterization test asserts).
        /// </summary>
        public static bool ShouldMerge(string outRoot)
        {
            return ConfigRegistry.GetRegisteredConfigs(outRoot).Count > 1;
        }

        /// <summary>
        /// Runs <paramref name="mergeAction"/> under a mutex scoped to this /out root, so two /config:
        /// invocations (or a /config: run's auto-tail merge racing an explicit standalone merge
        /// invocation) can never execute the merge concurrently against the same /out. This is what
        /// makes "every run re-merges" safe: at most one merge is ever in flight per /out at a time,
        /// serializing the rest.
        /// </summary>
        public static void RunGuarded(string outRoot, Action mergeAction)
        {
            if (mergeAction == null)
            {
                throw new ArgumentNullException(nameof(mergeAction));
            }

            var lockScopePath = Path.Combine(Path.GetFullPath(outRoot), ConfigRegistry.ConfigsFileName);
            RunUnderMutex(lockScopePath, mergeAction);
        }

        /// <summary>
        /// Acquires the named mutex for <paramref name="scopePath"/>, runs <paramref name="action"/>,
        /// and releases the mutex. Shared by <see cref="RunGuarded"/> and
        /// <see cref="ConfigRegistry.EnsureConfigRegistered"/> so the cross-process locking contract
        /// (including abandoned-mutex recovery) lives in exactly one place.
        /// </summary>
        internal static void RunUnderMutex(string scopePath, Action action)
        {
            using (var mutex = CreateNamedMutex(scopePath))
            {
                // AbandonedMutexException means a prior holder (e.g. an indexing process that crashed
                // mid-merge) exited without releasing. The wait still SUCCEEDS and this thread now owns
                // the mutex, so treat it as acquired and proceed -- otherwise a single crashed run would
                // permanently wedge every future merge/registration against this /out.
                try
                {
                    mutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                }

                try
                {
                    action();
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        /// <summary>
        /// Populates a fresh temp directory (via <paramref name="populateStagingDir"/>, given the temp
        /// directory's path) and then swaps it into <paramref name="targetDir"/> via two directory
        /// renames (move the old directory aside, then move staging into place), so a reader never
        /// observes a torn mix of old-and-new content -- it's always either the complete old directory
        /// or the complete new one. Note this is not a single atomic filesystem operation: there is a
        /// brief instant between the two renames where <paramref name="targetDir"/> does not exist at
        /// all (a caller hitting this should treat it like any other transient not-found and retry) --
        /// what's guaranteed is that it's never PARTIALLY written, never a mix of two different merges'
        /// output. Safe to call even when <paramref name="targetDir"/> doesn't exist yet.
        /// </summary>
        public static void WriteAtomically(string targetDir, Action<string> populateStagingDir)
        {
            if (populateStagingDir == null)
            {
                throw new ArgumentNullException(nameof(populateStagingDir));
            }

            var parentDir = Path.GetDirectoryName(Path.GetFullPath(targetDir));
            var stagingDir = Path.Combine(parentDir, Path.GetFileName(targetDir) + "." + Guid.NewGuid().ToString("N") + ".staging");

            Directory.CreateDirectory(stagingDir);
            try
            {
                populateStagingDir(stagingDir);

                var previousDir = targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + "." + Guid.NewGuid().ToString("N") + ".previous";

                // Two-step swap (rename current out of the way, rename staging in, then delete the old
                // one) rather than delete-then-move, so a crash between steps still leaves a complete
                // directory at either targetDir or previousDir -- never an empty/missing targetDir.
                //
                // Windows refuses to rename a directory while any file inside it is open (even briefly,
                // e.g. a concurrent reader's File.ReadAllText) with a transient "access denied" -- retry
                // the rename rather than let that abort the whole merge.
                var hadExisting = Directory.Exists(targetDir);
                if (hadExisting)
                {
                    MoveDirectoryWithRetry(targetDir, previousDir);
                }

                MoveDirectoryWithRetry(stagingDir, targetDir);

                if (hadExisting)
                {
                    Directory.Delete(previousDir, recursive: true);
                }
            }
            catch
            {
                if (Directory.Exists(stagingDir))
                {
                    Directory.Delete(stagingDir, recursive: true);
                }

                throw;
            }
        }

        private static void MoveDirectoryWithRetry(string sourceDir, string destDir)
        {
            const int maxAttempts = 20;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(sourceDir, destDir);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(10);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(10);
                }
            }
        }

        internal static Mutex CreateNamedMutex(string scopePath)
        {
            // Scope the mutex name to the exact path being guarded so unrelated /out roots (e.g.
            // different solutions indexed on the same machine) don't contend with each other. The name
            // must be derived deterministically ACROSS processes: string.GetHashCode() is randomized
            // per-process on modern .NET, so hashing the path that way gives every process a different
            // mutex name and silently defeats the cross-process guarantee. Use a stable content hash.
            var normalizedPath = Path.GetFullPath(scopePath).ToUpperInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
            var mutexName = "SourceBrowser.ConfigMerge." + Convert.ToHexString(hash, 0, 8);
            return new Mutex(initiallyOwned: false, name: mutexName);
        }
    }
}
