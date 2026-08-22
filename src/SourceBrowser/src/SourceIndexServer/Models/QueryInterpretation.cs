using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    public class Interpretation
    {
        private static readonly FrozenDictionary<string, string> typeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bool"] = "Boolean",
                ["byte"] = "Byte",
                ["sbyte"] = "SByte",
                ["short"] = "Int16",
                ["ushort"] = "UInt16",
                ["int"] = "Int32",
                ["uint"] = "UInt32",
                ["long"] = "Int64",
                ["ulong"] = "UInt64",
                ["char"] = "Char",
                ["float"] = "Single",
                ["double"] = "Double",
                ["decimal"] = "Decimal",
                ["string"] = "String",
                ["object"] = "Object",
                ["void"] = "Void",
                ["IntPtr"] = "nint",
                ["UIntPtr"] = "nuint",
                ["System.IntPtr"] = "nint",
                ["System.UIntPtr"] = "nuint",
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        public string CoreSearchTerm { get; set; }
        public bool IsVerbatim { get; set; }
        public string Namespace { get; set; }
        public string AlternateNamespace { get; set; }
        public List<string> FilterNames { get; private set; }
        public List<string> FilterDotSeparatedNames { get; private set; }

        public Interpretation()
        {
            FilterNames = new List<string>();
            FilterDotSeparatedNames = new List<string>();
        }

        public Interpretation Clone()
        {
            var newInterpretation = new Interpretation();
            newInterpretation.CoreSearchTerm = CoreSearchTerm;
            newInterpretation.IsVerbatim = IsVerbatim;
            newInterpretation.Namespace = Namespace;
            newInterpretation.AlternateNamespace = AlternateNamespace;
            newInterpretation.FilterNames = new List<string>(FilterNames);
            newInterpretation.FilterDotSeparatedNames = new List<string>(FilterDotSeparatedNames);
            return newInterpretation;
        }

        public bool Filter(DeclaredSymbolInfo symbol)
        {
            return
                FilterDottedNames(symbol) &&
                FilterWords(symbol);
        }

        private bool FilterWords(DeclaredSymbolInfo symbol)
        {
            if (this.FilterNames.Count == 0)
            {
                return true;
            }

            foreach (var word in this.FilterNames)
            {
                if (symbol.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) == -1 &&
                    (symbol.AssemblyName == null || symbol.AssemblyName.IndexOf(word, StringComparison.OrdinalIgnoreCase) == -1) &&
                    (symbol.ProjectFilePath == null || symbol.ProjectFilePath.IndexOf(word, StringComparison.OrdinalIgnoreCase) == -1))
                {
                    return false;
                }
            }

            return true;
        }

        private bool FilterDottedNames(DeclaredSymbolInfo symbol)
        {
            if (this.Namespace == null)
            {
                return FilterAssemblies(symbol) || FilterDotSeparatedNames.Any(n => FilterNamespace(symbol, n));
            }
            else
            {
                return FilterAssemblies(symbol) &&
                    (FilterNamespace(symbol, this.Namespace) ||
                    (this.AlternateNamespace is not null &&
                    FilterNamespace(symbol, this.AlternateNamespace)));
            }
        }

        private static bool FilterNamespace(DeclaredSymbolInfo symbol, string namespacePrefix)
        {
            var description = symbol.Description;
            int openParen = description.IndexOf('(');
            int descriptionLength = openParen < 0 ? description.Length : openParen;
            ReadOnlySpan<char> remainingDescription = description.AsSpan(0, descriptionLength);

            while (true)
            {
                int matchIndex = remainingDescription.IndexOf(
                    namespacePrefix,
                    StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    return false;
                }

                if (matchIndex == 0 || !IsIdentifierCharacter(remainingDescription[matchIndex - 1]))
                {
                    return true;
                }

                remainingDescription = remainingDescription.Slice(matchIndex + 1);
            }
        }

        private static bool IsIdentifierCharacter(char character)
        {
            return character == '_' || char.IsLetterOrDigit(character);
        }

        internal static string NormalizeTypeAliases(string text)
        {
            return NormalizeTypeAliases(text, out _);
        }

        internal static string NormalizeTypeAliases(string text, out bool preserveOriginal)
        {
            preserveOriginal = false;
            int aliasLength = text.IndexOf('.');
            if (aliasLength < 0)
            {
                aliasLength = text.Length;
            }
            else if (text.AsSpan(0, aliasLength).Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                int memberSeparator = text.IndexOf('.', aliasLength + 1);
                aliasLength = memberSeparator < 0 ? text.Length : memberSeparator;
            }

            if (typeAliases
                .GetAlternateLookup<ReadOnlySpan<char>>()
                .TryGetValue(text.AsSpan(0, aliasLength), out string indexedName))
            {
                preserveOriginal = true;
                return string.Concat(indexedName.AsSpan(), text.AsSpan(aliasLength));
            }

            return text;
        }

        private bool FilterAssemblies(DeclaredSymbolInfo symbol)
        {
            if (!this.FilterDotSeparatedNames.Any())
            {
                return true;
            }

            foreach (var assemblyName in this.FilterDotSeparatedNames)
            {
                if (symbol.AssemblyName.IndexOf(assemblyName, StringComparison.OrdinalIgnoreCase) != -1)
                {
                    return true;
                }
            }

            return false;
        }
    }
}