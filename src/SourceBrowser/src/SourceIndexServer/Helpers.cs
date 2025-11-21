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
        private static async Task ProxyRequestAsync(this HttpContext context, string targetPath, Action<HttpRequestMessage> configureRequest = null)
        {
            try
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
            catch (Exception ex)
            {
                Program.Logger?.LogError(ex, $"ProxyRequestAsync: Failed to serve '{targetPath}' from '{IndexProxyUrl}'");
                throw new InvalidOperationException($"Failed to proxy file '{targetPath}' from storage. IndexProxyUrl: '{IndexProxyUrl}'", ex);
            }
        }

        private static bool FileExists(string proxyRequestPath)
        {
            try
            {
                var fs = new AzureBlobFileSystem(IndexProxyUrl);
                return fs.FileExists(proxyRequestPath);
            }
            catch (Exception ex)
            {
                Program.Logger?.LogError(ex, $"FileExists: Error checking '{proxyRequestPath}' in '{IndexProxyUrl}'");
                return false;
            }
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

            var proxyRequestPathSuffix = (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path).ToLowerInvariant();

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