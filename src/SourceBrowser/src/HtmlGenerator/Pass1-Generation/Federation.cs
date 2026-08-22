using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Federation
    {
        private static readonly HttpClient httpClient = new HttpClient();

        // Order matters: GetExternalAssemblyIndex returns the first federation that owns an assembly,
        // so earlier entries take precedence. source.dot.net indexes modern .NET and is preferred first.
        // referencesource.microsoft.com used to serve the .NET Framework index but is now dead -- it
        // just 301-redirects to https://github.com/microsoft/referencesource -- so it is no longer a
        // usable federation and has been dropped. See
        // https://github.com/KirillOsenkov/SourceBrowser/issues/199.
        public static IEnumerable<string> DefaultFederatedIndexUrls = new[]
        {
            "https://source.dot.net",
            "https://sourceroslyn.io"
        };

        private class Info
        {
            public Info(string server, HashSet<string> assemblies, bool supportsSymbolRedirect)
            {
                if (server == null)
                {
                    throw new ArgumentNullException(nameof(server));
                }

                if (!server.EndsWith("/", StringComparison.Ordinal))
                {
                    server += "/";
                }

                Server = server;
                Assemblies = assemblies ?? throw new ArgumentNullException(nameof(assemblies));
                SupportsSymbolRedirect = supportsSymbolRedirect;
            }

            public string Server { get; }
            public HashSet<string> Assemblies { get; }
            public bool SupportsSymbolRedirect { get; }
        }

        private sealed class Capabilities
        {
            [JsonPropertyName("symbolRedirect")]
            public bool SymbolRedirect { get; set; }
        }

        private readonly List<Info> federations = new List<Info>();

        public Federation()
        {
        }

        public Federation(IEnumerable<string> servers) : this(servers.ToArray())
        {
        }

        public Federation(params string[] servers)
        {
            AddFederations(servers);
        }

        public void AddFederations(IEnumerable<string> servers)
        {
            if (servers == null)
            {
                return;
            }

            foreach (var server in servers)
            {
                AddFederation(server);
            }
        }

        public void AddFederations(params string[] servers)
        {
            AddFederations((IEnumerable<string>)servers);
        }

        public void AddFederation(string server)
        {
            var url = GetAssemblyUrl(server);

            var assemblyList = httpClient.GetStringAsync(url).GetAwaiter().GetResult();
            var assemblyNames = GetAssemblyNames(assemblyList);
            bool supportsSymbolRedirect = GetSupportsSymbolRedirect(server);

            federations.Add(new Info(server, assemblyNames, supportsSymbolRedirect));
        }

        public void AddFederation(string server, string assemblyListFile)
        {
            AddFederation(server, assemblyListFile, supportsSymbolRedirect: false);
        }

        internal void AddFederation(
            string server,
            string assemblyListFile,
            bool supportsSymbolRedirect)
        {
            var fileText = File.ReadAllText(assemblyListFile);
            var assemblyNames = GetAssemblyNames(fileText);
            var info = new Info(server, assemblyNames, supportsSymbolRedirect);
            federations.Add(info);
        }

        private static bool GetSupportsSymbolRedirect(string server)
        {
            try
            {
                string json = httpClient.GetStringAsync(
                    GetServerUrl(server, "Federation.json")).GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<Capabilities>(json)?.SymbolRedirect == true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private HashSet<string> GetAssemblyNames(string assemblyList)
        {
            var assemblyNames = new HashSet<string>(assemblyList
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split(';')[0]), StringComparer.OrdinalIgnoreCase);
            return assemblyNames;
        }

        private string GetAssemblyUrl(string server)
        {
            return GetServerUrl(server, "Assemblies.txt");
        }

        private static string GetServerUrl(string server, string relativePath)
        {
            string url = server;
            if (!url.EndsWith("/", StringComparison.Ordinal))
            {
                url += "/";
            }

            return url + relativePath;
        }

        public int GetExternalAssemblyIndex(string assemblyName)
        {
            // Order must match order in GetServers().
            for (int i = 0; i < federations.Count; i++)
            {
                if (federations[i].Assemblies.Contains(assemblyName))
                {
                    return i;
                }
            }

            return -1;
        }

        public string GetExternalSymbolPath(int externalAssemblyIndex, string assemblyName, string symbolId)
        {
            if (federations[externalAssemblyIndex].SupportsSymbolRedirect)
            {
                return "api/symbolredirect?symbolId=" + symbolId;
            }

            return assemblyName + "/A.html#" + symbolId;
        }

        public IEnumerable<string> GetServers()
        {
            // Order must match order in GetExternalAssemblyIndex().
            for (int i = 0; i < federations.Count; i++)
            {
                yield return federations[i].Server;
            }
        }
    }
}
