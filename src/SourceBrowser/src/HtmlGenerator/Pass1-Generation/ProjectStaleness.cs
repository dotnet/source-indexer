using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// Computes a per-project staleness key used to decide, on an incremental run (/incremental), whether
    /// a project's Pass1 output can be reused as-is instead of being regenerated -- and, correspondingly,
    /// whether Pass2 can retain that project's existing finalized output instead of re-copying it.
    ///
    /// This is deliberately scoped to a single project's own inputs (its source, its direct references,
    /// and its compilation/parse options) rather than a full transitive closure over everything it
    /// depends on -- staleness of a project's own generated pages is not the same question as whether the
    /// finalized, cross-assembly aggregates (references, "Used By" backlinks) that mention it need to be
    /// recomputed, and that second question is handled entirely in Pass2 by always recalculating those
    /// aggregates over the full current project set, never by cascading invalidation through Pass1.
    /// </summary>
    public static class ProjectStaleness
    {
        public static async Task<string> ComputeKeyAsync(Project project)
        {
            // Prefer the compiler's own deterministic build identity (MVID) when this project already has
            // a deterministic, previously-built output on disk -- that's exactly the guarantee a
            // deterministic build gives us: identical sources + references + options produce an identical
            // MVID, so comparing MVIDs is a cheap, precise staleness check with no risk of hash collisions
            // in the input space we'd otherwise have to enumerate by hand.
            var mvidKey = TryGetDeterministicMvidKey(project);
            if (mvidKey != null)
            {
                return "mvid:" + mvidKey;
            }

            // Fallback for projects that aren't (yet) built deterministically on disk: hash the actual
            // inputs that would affect what Pass1 generates for this project.
            return await ComputeContentHashKeyAsync(project).ConfigureAwait(false);
        }

        private static string TryGetDeterministicMvidKey(Project project)
        {
            try
            {
                var compilationOptions = project.CompilationOptions;
                if (compilationOptions == null || !compilationOptions.Deterministic)
                {
                    return null;
                }

                var outputFilePath = project.OutputFilePath;
                if (string.IsNullOrEmpty(outputFilePath) || !File.Exists(outputFilePath))
                {
                    outputFilePath = project.CompilationOutputInfo.AssemblyPath;
                }

                if (string.IsNullOrEmpty(outputFilePath) || !File.Exists(outputFilePath))
                {
                    return null;
                }

                using var stream = File.OpenRead(outputFilePath);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                {
                    return null;
                }

                var metadataReader = peReader.GetMetadataReader();
                var mvid = metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid);
                return mvid.ToString("N");
            }
            catch
            {
                // Fall back to the content hash for any failure reading a deterministic MVID (project not
                // built yet, PE parsing failure, file locked, etc.) -- this is strictly a fast path, never
                // a correctness requirement.
                return null;
            }
        }

        private static async Task<string> ComputeContentHashKeyAsync(Project project)
        {
            var documentHashes = new List<string>();
            foreach (var document in project.Documents)
            {
                var text = await document.GetTextAsync().ConfigureAwait(false);
                var hash = Sha256Hex(text.ToString());
                documentHashes.Add((document.FilePath ?? document.Name) + "=" + hash);
            }

            documentHashes.Sort(StringComparer.Ordinal);

            var referenceKeys = new List<string>();
            foreach (var reference in project.MetadataReferences)
            {
                var path = (reference as PortableExecutableReference)?.FilePath;
                if (path != null && File.Exists(path))
                {
                    referenceKeys.Add(path + "@" + File.GetLastWriteTimeUtc(path).Ticks);
                }
                else
                {
                    referenceKeys.Add(reference.Display ?? reference.ToString());
                }
            }

            foreach (var projectReference in project.ProjectReferences)
            {
                var referencedProject = project.Solution.GetProject(projectReference.ProjectId);
                referenceKeys.Add("project:" + (referencedProject?.AssemblyName ?? projectReference.ProjectId.ToString()));
            }

            referenceKeys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("src:").Append(string.Join(";", documentHashes)).Append('|');
            sb.Append("refs:").Append(string.Join(";", referenceKeys)).Append('|');
            sb.Append("opts:")
              .Append(project.CompilationOptions?.GetHashCode() ?? 0)
              .Append('/')
              .Append(project.ParseOptions?.GetHashCode() ?? 0);

            return Sha256Hex(sb.ToString());
        }

        private static string Sha256Hex(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
