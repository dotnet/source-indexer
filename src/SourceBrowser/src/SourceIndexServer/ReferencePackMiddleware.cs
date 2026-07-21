using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.SourceBrowser.SourceIndexServer.Models;

namespace Microsoft.SourceBrowser.SourceIndexServer
{
    // Serves per-symbol reference files out of the packed references.pack/references.index produced by the
    // HtmlGenerator. Matches requests of the form /{assembly}/R/{16hex}.html and returns the exact packed
    // bytes; anything that doesn't match, or whose assembly has no pack, falls through to the static file
    // middleware so the MSBuild/Guid assemblies (which still emit individual files) keep working.
    public sealed class ReferencePackMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string _rootPath;
        private readonly string _rootPathPrefix;
        private readonly IFileSystem _blobFileSystem;
        private readonly ConcurrentDictionary<string, Lazy<ReferencePack>> _packs = new(StringComparer.OrdinalIgnoreCase);

        public ReferencePackMiddleware(RequestDelegate next, string rootPath)
        {
            _next = next;
            _rootPath = rootPath;
            _rootPathPrefix = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // On the proxied deployment (source.dot.net) the index -- including the reference packs -- is
            // uploaded to blob storage rather than shipped with the webapp, so the packs must be read from
            // there. A local deployment leaves this null and reads from disk.
            if (!string.IsNullOrEmpty(Helpers.IndexProxyUrl))
            {
                _blobFileSystem = new AzureBlobFileSystem(Helpers.IndexProxyUrl);
            }
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value;

            if (TryMatch(path, out string assembly, out string symbolId) &&
                TryGetPack(assembly) is ReferencePack pack &&
                pack.TryGetFragment(symbolId, out long offset, out int length))
            {
                context.Response.ContentType = "text/html";
                context.Response.ContentLength = length;
                await pack.WriteFragmentAsync(offset, length, context.Response.Body);
                return;
            }

            // The packed reference store, the master search index and the per-project build
            // intermediates all sit under the index root purely so the server can read them; nothing
            // links to them and the client only ever calls api/symbols. Return 404 rather than let the
            // static-file handler stream e.g. the multi-hundred-MB references.pack to anyone who guesses
            // the URL.
            if (IsServerOnlyArtifact(path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await _next(context);
        }

        // Index data the server reads at startup plus the per-project finalization intermediates. These
        // are the raw search index and per-project link tables -- internal server/build data that is
        // never linked or fetched by the browser, which only calls api/symbols and navigates .html/.js.
        // Notably absent, and therefore still servable: i.txt and diagnostics.txt (users open these
        // directly) and the Assemblies.txt / Projects.txt content listings, which are just enumerations
        // of what the site serves (and Assemblies.txt is the federation manifest other SourceBrowser
        // instances fetch to federate against this site).
        private static readonly HashSet<string> ServerOnlyFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "DeclaredSymbols.txt",                 // master search index
            "Huffman.txt",                         // master huffman tables
            "D.txt",                               // per-project declared symbols
            "BaseMembers.txt",                     // per-project base member links
            "ImplementedInterfaceMembers.txt",     // per-project interface member links
        };

        // Matches the reference pack/index and the server-only index/intermediate .txt files, but only
        // when the request actually resolves to a file inside the index tree, so unrelated static assets
        // served from wwwroot (a robots.txt, say) are left alone.
        private bool IsServerOnlyArtifact(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/')
            {
                return false;
            }

            var fileName = path.Substring(path.LastIndexOf('/') + 1);

            bool blocked =
                fileName.EndsWith(".pack", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".index", StringComparison.OrdinalIgnoreCase) ||
                ServerOnlyFileNames.Contains(fileName);

            if (!blocked)
            {
                return false;
            }

            var relative = path.Substring(1).Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(_rootPath, relative));

            // Reject anything that escapes the index root (e.g. via ..) rather than probing outside it.
            if (!candidate.StartsWith(_rootPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return File.Exists(candidate);
        }

        // Matches /{assembly}/R/{id}.html where id is exactly 16 lowercase hex characters. The assembly
        // segment is taken verbatim; a nested id path (e.g. an extra '/') would fail the segment count.
        private static bool TryMatch(string path, out string assembly, out string symbolId)
        {
            assembly = null;
            symbolId = null;

            if (string.IsNullOrEmpty(path) || path[0] != '/')
            {
                return false;
            }

            int rSegment = path.IndexOf("/R/", StringComparison.Ordinal);
            if (rSegment <= 0)
            {
                return false;
            }

            assembly = path.Substring(1, rSegment - 1);
            if (assembly.Length == 0 || assembly.IndexOf('/') >= 0)
            {
                return false;
            }

            var file = path.Substring(rSegment + 3);
            if (file.Length != 16 + 5 || !file.EndsWith(".html", StringComparison.Ordinal))
            {
                return false;
            }

            symbolId = file.Substring(0, 16);
            for (int i = 0; i < symbolId.Length; i++)
            {
                char c = symbolId[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        private ReferencePack TryGetPack(string assembly)
        {
            var lazy = _packs.GetOrAdd(assembly, a => new Lazy<ReferencePack>(() =>
            {
                if (_blobFileSystem is not null)
                {
                    return ReferencePack.TryLoadFromBlob(_blobFileSystem, a, out var blobPack) ? blobPack : null;
                }

                var referencesFolder = Path.Combine(_rootPath, a, Constants.ReferencesFileName);
                return ReferencePack.TryLoad(referencesFolder, out var pack) ? pack : null;
            }));

            return lazy.Value;
        }
    }
}
