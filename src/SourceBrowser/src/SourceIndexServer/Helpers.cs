using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.SourceBrowser.SourceIndexServer.Models;
using Microsoft.VisualBasic;

namespace Microsoft.SourceBrowser.SourceIndexServer
{
    public static class Helpers
    {
        public static async Task ProxyRequestAsync(this HttpContext context, string targetUrl, Action<HttpRequestMessage> configureRequest = null)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, targetUrl))
            {
                foreach (var (key, values) in context.Request.Headers)
                {
                    switch (key.ToLower())
                    {
                        // We shouldn't copy any of these request headers
                        case "host":
                        case "authorization":
                        case "cookie":
                        case "content-length":
                        case "content-type":
                            continue;
                        default:
                            req.Headers.TryAddWithoutValidation(key, values.ToArray());
                            break;
                    }
                }

                configureRequest?.Invoke(req);
                var fs = new AzureBlobFileSystem(targetUrl);
                var uri = new Uri(targetUrl);

                var data = fs.OpenSequentialReadStream(uri.LocalPath);
                context.Response.Body = data;
            }
        }

        private static readonly HttpClient s_client = new HttpClient(new HttpClientHandler() { CheckCertificateRevocationList = true });

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