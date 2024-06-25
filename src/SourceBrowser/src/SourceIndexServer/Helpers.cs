using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.SourceBrowser.SourceIndexServer.Models;

namespace Microsoft.SourceBrowser.SourceIndexServer
{
    public static class Helpers
    {
        public static async Task ProxyRequestAsync(this HttpContext context, string targetUrl, Action<HttpRequestMessage> configureRequest = null)
        {
            var fs = new AzureBlobFileSystem(targetUrl);
            var uri = new Uri(targetUrl);
            using (var data = fs.OpenSequentialReadStream(uri.LocalPath))
            {
                await data.CopyToAsync(context.Response.Body).ConfigureAwait(false);
            }
        }

        private static async Task<bool> UrlExistsAsync(string proxyRequestUrl)
        {
            var fs = new AzureBlobFileSystem(proxyRequestUrl);
            var uri = new Uri(proxyRequestUrl);
            return fs.FileExists(uri.LocalPath);
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

            var proxyRequestUrl = proxyUri + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path).ToLowerInvariant();

            if (!await UrlExistsAsync(proxyRequestUrl).ConfigureAwait(false))
            {
                await next().ConfigureAwait(false);
                return;
            }

            await context.ProxyRequestAsync(proxyRequestUrl).ConfigureAwait(false);
        }

        public static string IndexProxyUrl => Environment.GetEnvironmentVariable("SOURCE_BROWSER_INDEX_PROXY_URL");
    }
}