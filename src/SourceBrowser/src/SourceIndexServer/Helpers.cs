using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.SourceBrowser.SourceIndexServer.Models;

namespace Microsoft.SourceBrowser.SourceIndexServer
{
    public static class Helpers
    {
        private static readonly bool shouldLowerCase = Environment.GetEnvironmentVariable("SOURCE_BROWSER_ASPIRE_ORCHESTRATED") != "true";

        private static string NormalizePath(string path)
        {
            // Don't lowercase when running under Aspire orchestration
            return shouldLowerCase ? path.ToLowerInvariant() : path;
        }

        private static async Task ProxyRequestAsync(this HttpContext context, string targetPath, Action<HttpRequestMessage> configureRequest = null)
        {
            var fs = new AzureBlobFileSystem(IndexProxyUrl);
            var props = fs.FileProperties(targetPath);
            context.Response.Headers.Append("Content-Md5", Convert.ToBase64String(props.ContentHash));
            context.Response.Headers.Append("Content-Type", props.ContentType);
            context.Response.Headers.Append("Etag", props.ETag.ToString());
            context.Response.Headers.Append("Last-Modified", props.LastModified.ToString("R"));
            using (var data = fs.OpenSequentialReadStream(targetPath))
            {
                await data.CopyToAsync(context.Response.Body).ConfigureAwait(false);
            }
        }

        private static bool FileExists(string proxyRequestPath)
        {
            var fs = new AzureBlobFileSystem(IndexProxyUrl);
            return fs.FileExists(proxyRequestPath);
        }

        public static async Task ServeProxiedIndex(HttpContext context, Func<Task> next)
        {
            var path = context.Request.Path.Value;

            if (!path.EndsWith(".html", StringComparison.Ordinal) && !path.EndsWith(".txt", StringComparison.Ordinal))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var proxyUri = IndexProxyUrl;
            if (string.IsNullOrEmpty(proxyUri))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var proxyRequestPathSuffix = NormalizePath(path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);

            if (!FileExists(proxyRequestPathSuffix))
            {
                await next().ConfigureAwait(false);
                return;
            }

            await context.ProxyRequestAsync(proxyRequestPathSuffix).ConfigureAwait(false);
        }

        public static string IndexProxyUrl => Environment.GetEnvironmentVariable("SOURCE_BROWSER_INDEX_PROXY_URL");
    }
}