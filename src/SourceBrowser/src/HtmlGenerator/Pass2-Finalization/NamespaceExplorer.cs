using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class NamespaceExplorer
    {
        public static void WriteNamespaceExplorer(string assemblyName, IEnumerable<DeclaredSymbolInfo> types, string rootPath)
        {
            new NamespaceExplorer().WriteFile(assemblyName, types, rootPath, "../");
        }

        private void WriteFile(string assemblyName, IEnumerable<DeclaredSymbolInfo> types, string rootPath, string pathPrefix)
        {
            var fileName = Path.Combine(rootPath, Constants.Namespaces);
            NamespaceTreeNode root = ConstructTree(types);

            // The tree is emitted as a compact JSON payload the client materializes lazily, rather than
            // as one multi-MB nested-<div> document. On large indexes the full HTML was ~7MB and forced
            // the browser to parse and build tens of thousands of collapsed DOM nodes up front; the JSON
            // form is a few hundred KB and the client only creates DOM for branches the user expands.
            using (var sw = new StreamWriter(fileName))
            {
                Markup.WriteNamespaceExplorerPrefix(assemblyName, sw, pathPrefix);
                WriteData(root, assemblyName, pathPrefix, sw);
                Markup.WriteNamespaceExplorerSuffix(sw);
            }
        }

        private void WriteData(NamespaceTreeNode root, string assemblyName, string pathPrefix, StreamWriter sw)
        {
            sw.Write("<script>var namespaceExplorerData=");

            // This JSON is written straight into a <script> block, so the encoder must escape the
            // HTML-sensitive characters (notably '<', so a type/namespace name containing "</script>"
            // can't break out of the script context -- an XSS vector when indexing untrusted source).
            // JavaScriptEncoder.Create(UnicodeRanges.All) still leaves non-ASCII identifiers unescaped
            // (keeping the payload compact) but escapes '<'/'>'/'&', unlike UnsafeRelaxedJsonEscaping.
            var options = new JsonWriterOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
            using (var stream = new MemoryStream())
            {
                using (var json = new Utf8JsonWriter(stream, options))
                {
                    json.WriteStartObject();
                    json.WriteString("assemblyName", assemblyName);
                    json.WriteString("pathPrefix", pathPrefix);
                    json.WritePropertyName("children");
                    WriteChildrenJson(root, json);
                    json.WriteEndObject();
                }

                stream.Position = 0;
                using (var reader = new StreamReader(stream))
                {
                    sw.Write(reader.ReadToEnd());
                }
            }

            sw.WriteLine(";</script>");
        }

        // Each node is a fixed-shape array so the payload stays small:
        //   namespace     -> [name, [children...]]
        //   leaf type     -> [name, glyph, id]
        //   type w/ nested -> [name, glyph, id, [children...]]
        // The client distinguishes types from namespaces by whether element 1 is a number (a glyph).
        private void WriteChildrenJson(NamespaceTreeNode node, Utf8JsonWriter json)
        {
            json.WriteStartArray();
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    WriteNodeJson(child.Value, json);
                }
            }
            json.WriteEndArray();
        }

        private void WriteNodeJson(NamespaceTreeNode node, Utf8JsonWriter json)
        {
            json.WriteStartArray();

            if (node.TypeDeclaration != null)
            {
                var type = node.TypeDeclaration;
                json.WriteStringValue(type.Name);
                json.WriteNumberValue(type.Glyph);
                json.WriteStringValue(Serialization.ULongToHexString(type.ID));

                if (node.Children != null)
                {
                    WriteChildrenJson(node, json);
                }
            }
            else
            {
                json.WriteStringValue(node.Title);
                WriteChildrenJson(node, json);
            }

            json.WriteEndArray();
        }

        public NamespaceTreeNode ConstructTree(IEnumerable<DeclaredSymbolInfo> types)
        {
            var root = new NamespaceTreeNode("");

            // DeclaredSymbols enumerates in a nondeterministic order because it is folded from a
            // ConcurrentBag of per-partition collectors. Since GetOrCreate merges case-insensitively,
            // the casing that survives for a shared namespace/type node -- and therefore the emitted
            // order relative to a case-differing sibling -- is whichever type is seen first. Insert in a
            // stable order (by symbol id) so the output is reproducible run-to-run.
            foreach (var type in types.OrderBy(type => type.ID))
            {
                Insert(root, type);
            }

            return root;
        }

        private void Insert(NamespaceTreeNode root, DeclaredSymbolInfo type)
        {
            var namespaceString = type.GetNamespace();
            var parts = namespaceString.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            var nodeWhereToInsert = GetOrCreateNode(root, parts, 0);

            // { is to sort types after namespaces
            var inserted = nodeWhereToInsert.GetOrCreate("{" + type.Name);
            inserted.TypeDeclaration = type;
        }

        private NamespaceTreeNode GetOrCreateNode(NamespaceTreeNode node, string[] parts, int index)
        {
            if (index == parts.Length)
            {
                return node;
            }

            node = node.GetOrCreate(parts[index]);
            return GetOrCreateNode(node, parts, ++index);
        }

        public class NamespaceTreeNode
        {
            public SortedList<string, NamespaceTreeNode> Children;
            public DeclaredSymbolInfo TypeDeclaration { get; set; }
            public string Title { get; private set; }

            public NamespaceTreeNode(string namespacePart)
            {
                Title = namespacePart;
            }

            public void Add(NamespaceTreeNode node)
            {
                if (Children == null)
                {
                    Children = new SortedList<string, NamespaceTreeNode>(StringComparer.OrdinalIgnoreCase);
                }

                Children.Add(node.Title, node);
            }

            public NamespaceTreeNode GetOrCreate(string title)
            {
                if (Children == null)
                {
                    Children = new SortedList<string, NamespaceTreeNode>(StringComparer.OrdinalIgnoreCase);
                }

                // need to try finding both folders and files
                string other = title.StartsWith("{", StringComparison.Ordinal) ? title.TrimStart('{') : "{" + title;
                if (!Children.TryGetValue(title, out NamespaceTreeNode result) && !Children.TryGetValue(other, out result))
                {
                    result = new NamespaceTreeNode(title);
                    Children.Add(title, result);
                }

                return result;
            }
        }
    }
}
