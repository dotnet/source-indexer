using System;
using System.Linq;
using Microsoft.SourceBrowser.Common;
using Microsoft.SourceBrowser.SourceIndexServer;
using Microsoft.SourceBrowser.SourceIndexServer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class QueryUnitTests
    {
        [TestMethod]
        public void PrefilterPositive()
        {
            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "mscorlib",
                ProjectFilePath = "ndp\\clr\\src\\mscorlib\\mscorlib.csproj",
                Name = "ConsoleColor",
                Description = "System.ConsoleColor",
                Kind = SymbolKindText.Class
            },
            "System.get_ConsoleC",
            "Console mscor",
            "Console clr",
            "System.Con Color",
            "Cons class",
            "Conso class struct",
            "Console System.",
            "Console System.C",
            "Console System.Co",
            "System.Con",
            "System.Console",
            "System.ConsoleColor",
            "System.set_ConsoleColor",
            "System.add_Cons",
            "get_Console",
            "set_Con",
            "add_ConsoleColor");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Core",
                ProjectFilePath = "ndp\\clr\\src\\bcl\\System.Core\\System.Core.csproj",
                Name = "Where",
                Description = "System.Linq.Enumerable.Where<T>(bla bla bla)",
                Kind = SymbolKindText.Method
            },
            "Where System",
            "Where System.",
            "Where System.Core",
            "Enumerable.Where",
            "Linq.Enumerable.Where");
        }

        [TestMethod]
        public void PrefilterNegative()
        {
            NoMatch(new DeclaredSymbolInfo
            {
                AssemblyName = "mscorlib",
                ProjectFilePath = "ndp\\clr\\src\\mscorlib\\mscorlib.csproj",
                Name = "ConsoleColor",
                Description = "System.ConsoleColor",
                Kind = SymbolKindText.Class
            },
            "Console Core",
            "System.Con Back",
            "Console env\\Editor",
            "Consol struct",
            "Console System.Cor",
            "Console System.Core");
        }

        [TestMethod]
        public void TypeAliasesMatchIndexedNames()
        {
            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = "Size",
                Description = "nint.Size",
                Kind = SymbolKindText.Property
            },
            "IntPtr.Size",
            "System.IntPtr.Size");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = "nint",
                Description = "nint",
                Kind = SymbolKindText.Struct
            },
            "IntPtr",
            "System.IntPtr");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = "Size",
                Description = "nuint.Size",
                Kind = SymbolKindText.Property
            },
            "UIntPtr.Size",
            "System.UIntPtr.Size");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = "MaxValue",
                Description = "System.Int32.MaxValue",
                Kind = SymbolKindText.Field
            },
            "int.MaxValue");

            NoMatch(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = "MaxValue",
                Description = "nint.MaxValue",
                Kind = SymbolKindText.Property
            },
            "int.MaxValue");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "Example",
                Name = "Size",
                Description = "IntPtr.Size",
                Kind = SymbolKindText.Property
            },
            "IntPtr.Size");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "Example",
                Name = "Widget",
                Description = "IntPtr.Nested.Widget",
                Kind = SymbolKindText.Property
            },
            "Widget IntPtr.Nested");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "Example",
                Name = "Member",
                Description = "Long.Member",
                Kind = SymbolKindText.Property
            },
            "Long.Member");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "Example",
                Name = "Size",
                Description = "IntPtr.Size",
                Kind = SymbolKindText.Property
            },
            "IntPtr.get_Size");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "Example",
                Name = "operator +",
                Description = "IntPtr.operator +",
                Kind = SymbolKindText.Method
            },
            "IntPtr.op_Addition");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = "MaxValue",
                Description = "System.Int64.MaxValue",
                Kind = SymbolKindText.Field
            },
            "long.MaxValue");

            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = "Int32",
                Description = "System.Int32",
                Kind = SymbolKindText.Struct
            },
            "int");
        }

        [TestMethod]
        [DataRow("bool", "Boolean")]
        [DataRow("byte", "Byte")]
        [DataRow("sbyte", "SByte")]
        [DataRow("short", "Int16")]
        [DataRow("ushort", "UInt16")]
        [DataRow("int", "Int32")]
        [DataRow("uint", "UInt32")]
        [DataRow("long", "Int64")]
        [DataRow("ulong", "UInt64")]
        [DataRow("char", "Char")]
        [DataRow("float", "Single")]
        [DataRow("double", "Double")]
        [DataRow("decimal", "Decimal")]
        [DataRow("string", "String")]
        [DataRow("object", "Object")]
        [DataRow("void", "Void")]
        public void KeywordAndClrTypeNamesFindSpecialTypes(string keyword, string clrName)
        {
            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = clrName,
                Description = "System." + clrName,
                Kind = SymbolKindText.Struct
            },
            keyword,
            clrName,
            "System." + clrName);
        }

        [TestMethod]
        [DataRow("nint", "IntPtr")]
        [DataRow("nuint", "UIntPtr")]
        public void KeywordAndClrTypeNamesFindNativeIntegerTypes(string keyword, string clrName)
        {
            Match(new DeclaredSymbolInfo
            {
                AssemblyName = "System.Private.CoreLib",
                Name = keyword,
                Description = keyword,
                Kind = SymbolKindText.Struct
            },
            keyword,
            clrName,
            "System." + clrName);
        }

        [TestMethod]
        [DataRow("int.MaxValue", "MaxValue", "Int32.MaxValue")]
        [DataRow("IntPtr.Size", "Size", "nint.Size")]
        [DataRow("System.IntPtr.Size", "Size", "nint.Size")]
        public void DottedTypeAliasesUseSingleCoreInterpretation(
            string queryText,
            string coreSearchTerm,
            string namespaceFilter)
        {
            var query = new Query(queryText);
            var interpretations = query.Interpretations
                .Where(i => i.CoreSearchTerm == coreSearchTerm)
                .ToArray();

            Assert.AreEqual(1, interpretations.Length);
            Assert.AreEqual(namespaceFilter, interpretations[0].Namespace);
        }

        private void Match(DeclaredSymbolInfo declaredSymbolInfo, params string[] queryStrings)
        {
            foreach (var queryString in queryStrings)
            {
                Match(declaredSymbolInfo, queryString, true);
            }
        }

        private void NoMatch(DeclaredSymbolInfo declaredSymbolInfo, params string[] queryStrings)
        {
            foreach (var queryString in queryStrings)
            {
                Match(declaredSymbolInfo, queryString, false);
            }
        }

        private static void Match(DeclaredSymbolInfo declaredSymbolInfo, string queryString, bool expected)
        {
            var query = new Query(queryString);
            bool actual =
                query.Filter(declaredSymbolInfo) &&
                query.Interpretations.Any(i =>
                    declaredSymbolInfo.Name.StartsWith(
                        i.CoreSearchTerm,
                        StringComparison.OrdinalIgnoreCase) &&
                    i.Filter(declaredSymbolInfo));
            Assert.AreEqual(expected, actual, queryString);
        }

        [TestMethod]
        public void TestSplitBySpaces1()
        {
            Split("");
            Split(" ");
            Split("  ");
            Split("\"", "\"");
            Split("\"\"", "\"\"");
            Split("\"\"\"", "\"\"", "\"");
            Split("\" \"", "\" \"");
            Split("\" \" ", "\" \"");
            Split(" \" \"", "\" \"");
            Split("a", "a");
            Split("a\"", "a", "\"");
            Split("\"a", "\"a");
            Split(" a", "a");
            Split("  a", "a");
            Split("a ", "a");
            Split("a  ", "a");
            Split("a b", "a", "b");
            Split("a b c", "a", "b", "c");
            Split("a b \"c\"", "a", "b", "\"c\"");
            Split("a b c\"", "a", "b", "c", "\"");
            Split("a b \"c", "a", "b", "\"c");
            Split("a b ", "a", "b");
            Split(" a b ", "a", "b");
            Split("\"a b\"", "\"a b\"");
            Split("\"a\" \"b\"", "\"a\"", "\"b\"");
            Split("\"a \"b\"", "\"a \"", "b", "\"");
            Split("\"a\" b", "\"a\"", "b");
            Split("a \"b\"", "a", "\"b\"");
        }

        private void Split(string query, params string[] expectedParts)
        {
            var actualParts = query.SplitBySpacesConsideringQuotes();
            Assert.IsTrue(
                Enumerable.SequenceEqual(expectedParts, actualParts),
                "Expected: " + string.Join(",", expectedParts) + "\r\n",
                "Actual: " + string.Join(",", actualParts));
        }
    }
}
