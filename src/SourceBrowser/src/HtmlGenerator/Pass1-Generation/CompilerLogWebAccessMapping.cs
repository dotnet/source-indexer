using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    internal sealed class CompilerLogWebAccessMapping
    {
        private const string WildcardMarker = "source-link-wildcard-6f76e67f";

        private CompilerLogWebAccessMapping(
            string localPathPrefix,
            string localPathSuffix,
            string urlPrefix,
            string urlSuffix,
            IReadOnlyList<string> excludedLocalPathPrefixes,
            bool isExact)
        {
            LocalPathPrefix = localPathPrefix;
            LocalPathSuffix = localPathSuffix;
            UrlPrefix = urlPrefix;
            UrlSuffix = urlSuffix;
            ExcludedLocalPathPrefixes = excludedLocalPathPrefixes;
            IsExact = isExact;
        }

        public string LocalPathPrefix { get; }
        public string LocalPathSuffix { get; }
        private string UrlPrefix { get; }
        private string UrlSuffix { get; }
        private IReadOnlyList<string> ExcludedLocalPathPrefixes { get; }
        private bool IsExact { get; }

        public static CompilerLogWebAccessMapping CreateExact(
            string localPath,
            string url,
            IReadOnlyList<string> excludedLocalPathPrefixes)
        {
            return TryNormalizeUrlTemplate(url, expectWildcard: false, out var urlPrefix, out var urlSuffix)
                ? new CompilerLogWebAccessMapping(
                    Path.GetFullPath(localPath),
                    string.Empty,
                    urlPrefix,
                    urlSuffix,
                    excludedLocalPathPrefixes,
                    isExact: true)
                : null;
        }

        public static CompilerLogWebAccessMapping CreateTemplate(
            string localPathPrefix,
            string localPathSuffix,
            string urlTemplate,
            string fixedWildcardPrefix,
            IReadOnlyList<string> excludedLocalPathPrefixes)
        {
            if (!TryNormalizeUrlTemplate(urlTemplate, expectWildcard: true, out var urlPrefix, out var urlSuffix))
            {
                return null;
            }

            return new CompilerLogWebAccessMapping(
                Path.GetFullPath(localPathPrefix),
                localPathSuffix,
                urlPrefix + EscapePath(fixedWildcardPrefix),
                urlSuffix,
                excludedLocalPathPrefixes,
                isExact: false);
        }

        public static string GetWebAccessUrl(
            IEnumerable<CompilerLogWebAccessMapping> mappings,
            string fullPath)
        {
            foreach (var mapping in mappings.OrderByDescending(mapping => mapping.Specificity))
            {
                if (mapping.TryGetWebAccessUrl(fullPath, out var url))
                {
                    return url;
                }
            }

            return null;
        }

        private int Specificity => IsExact
            ? int.MaxValue
            : LocalPathPrefix.Length + LocalPathSuffix.Length;

        private bool TryGetWebAccessUrl(string fullPath, out string url)
        {
            url = null;
            if (ExcludedLocalPathPrefixes.Any(prefix =>
                fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (IsExact)
            {
                if (!string.Equals(fullPath, LocalPathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                url = UrlPrefix;
                return true;
            }

            if (!fullPath.StartsWith(LocalPathPrefix, StringComparison.OrdinalIgnoreCase) ||
                !fullPath.EndsWith(LocalPathSuffix, StringComparison.OrdinalIgnoreCase) ||
                fullPath.Length < LocalPathPrefix.Length + LocalPathSuffix.Length)
            {
                return false;
            }

            var wildcardLength = fullPath.Length - LocalPathPrefix.Length - LocalPathSuffix.Length;
            var wildcardValue = fullPath.Substring(LocalPathPrefix.Length, wildcardLength);
            url = UrlPrefix + EscapePath(wildcardValue) + UrlSuffix;
            return true;
        }

        private static bool TryNormalizeUrlTemplate(
            string urlTemplate,
            bool expectWildcard,
            out string urlPrefix,
            out string urlSuffix)
        {
            urlPrefix = null;
            urlSuffix = null;
            var wildcardIndex = urlTemplate.IndexOf('*');
            if (expectWildcard
                ? wildcardIndex < 0 || wildcardIndex != urlTemplate.LastIndexOf('*')
                : wildcardIndex >= 0)
            {
                return false;
            }

            var candidate = expectWildcard
                ? urlTemplate.Substring(0, wildcardIndex) + WildcardMarker + urlTemplate.Substring(wildcardIndex + 1)
                : urlTemplate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            var canonicalUrl = GetWebAccessUrl(uri);
            if (!expectWildcard)
            {
                urlPrefix = canonicalUrl;
                urlSuffix = string.Empty;
                return true;
            }

            wildcardIndex = canonicalUrl.IndexOf(WildcardMarker, StringComparison.Ordinal);
            if (wildcardIndex < 0)
            {
                return false;
            }

            urlPrefix = canonicalUrl.Substring(0, wildcardIndex);
            urlSuffix = canonicalUrl.Substring(wildcardIndex + WildcardMarker.Length);
            return true;
        }

        private static string GetWebAccessUrl(Uri sourceLinkUri)
        {
            if (!string.Equals(sourceLinkUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                return sourceLinkUri.AbsoluteUri;
            }

            var pathParts = sourceLinkUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).Split('/', 3);
            if (pathParts.Length != 3)
            {
                return sourceLinkUri.AbsoluteUri;
            }

            return $"https://github.com/{pathParts[0]}/{pathParts[1]}/tree/{pathParts[2]}{sourceLinkUri.Query}{sourceLinkUri.Fragment}";
        }

        private static string EscapePath(string path)
        {
            return string.Join(
                "/",
                path.Replace('\\', '/')
                    .Split('/')
                    .Select(Uri.EscapeDataString));
        }
    }
}
