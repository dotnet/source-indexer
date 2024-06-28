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
        public static async Task ProxyRequestAsync(this HttpContext context, string targetPath, Action<HttpRequestMessage> configureRequest = null)
        {
            var fs = new AzureBlobFileSystem(IndexProxyUrl);
            using (var data = fs.OpenSequentialReadStream(targetPath))
            {
                await data.CopyToAsync(context.Response.Body).ConfigureAwait(false);
            }
        }

        private static async Task<bool> UrlExistsAsync(string proxyRequestPath)
        {
            var fs = new AzureBlobFileSystem(IndexProxyUrl);
            return fs.FileExists(proxyRequestPath);
        }

        public static async Task ServeProxiedIndex(HttpContext context, Func<Task> next)
        {
            var path = context.Request.Path.ToUriComponent();

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

            var proxyRequestPathSuffix = (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path).ToLowerInvariant();

            if (!await UrlExistsAsync(proxyRequestPathSuffix).ConfigureAwait(false))
            {
                await next().ConfigureAwait(false);
                return;
            }

            Program.Logger.LogError($"HELLO FALLBACK TIME\n");
            await context.ProxyRequestAsync(proxyRequestPathSuffix).ConfigureAwait(false);
        }

        public static string IndexProxyUrl => Environment.GetEnvironmentVariable("SOURCE_BROWSER_INDEX_PROXY_URL");
    }
}