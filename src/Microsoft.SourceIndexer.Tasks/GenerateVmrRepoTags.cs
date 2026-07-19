using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.SourceIndexer.Tasks
{
    // Reads the dotnet/dotnet VMR's src/source-manifest.json and emits one repo tag per mapped
    // subfolder, so search can be scoped to the originating repo (dotnet/runtime, dotnet/winforms, ...)
    // instead of the whole VMR. Source links are unaffected -- this only feeds HtmlGenerator's
    // /repoPath tagging, not /serverPath, so files still link back to dotnet/dotnet.
    public class GenerateVmrRepoTags : Task
    {
        // Full path to the extracted VMR's src/source-manifest.json.
        [Required]
        public string ManifestPath { get; set; }

        // Folder the manifest's repository paths are relative to (the extracted VMR's src directory).
        [Required]
        public string SrcRoot { get; set; }

        // One item per repository: ItemSpec is the subfolder's full path, RepoDisplayName its owner/repo tag.
        [Output]
        public ITaskItem[] RepoTags { get; set; }

        public override bool Execute()
        {
            try
            {
                ExecuteCore();
                return !Log.HasLoggedErrors;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, true);
                return false;
            }
        }

        private void ExecuteCore()
        {
            RepoTags = Array.Empty<ITaskItem>();

            if (!File.Exists(ManifestPath))
            {
                // Non-VMR builds (or a manifest that moved) simply produce no sub-tags.
                Log.LogMessage(MessageImportance.Normal, $"Source manifest '{ManifestPath}' not found; no sub-repo tags generated.");
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            if (!doc.RootElement.TryGetProperty("repositories", out var repositories) ||
                repositories.ValueKind != JsonValueKind.Array)
            {
                Log.LogWarning($"Source manifest '{ManifestPath}' has no 'repositories' array; no sub-repo tags generated.");
                return;
            }

            var tags = new List<ITaskItem>();
            foreach (var repo in repositories.EnumerateArray())
            {
                var path = repo.TryGetProperty("path", out var p) ? p.GetString() : null;
                var remoteUri = repo.TryGetProperty("remoteUri", out var r) ? r.GetString() : null;
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(remoteUri))
                {
                    continue;
                }

                var item = new TaskItem(Path.GetFullPath(Path.Combine(SrcRoot, path)));
                item.SetMetadata("RepoDisplayName", GetDisplayName(remoteUri));
                tags.Add(item);
            }

            RepoTags = tags.ToArray();
            Log.LogMessage(MessageImportance.Normal, $"Generated {tags.Count} sub-repo tag(s) from '{ManifestPath}'.");
        }

        // Turns a clone URL into an owner/repo tag, matching the RepoDisplayName convention used for
        // top-level repositories (the URL minus the github.com prefix).
        private static string GetDisplayName(string remoteUri)
        {
            var s = remoteUri.Trim();
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - ".git".Length);
            }

            const string prefix = "https://github.com/";
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return s.Substring(prefix.Length).Trim('/');
            }

            // Fall back to the URL path for any non-github remote.
            return new Uri(s).AbsolutePath.Trim('/');
        }
    }
}
