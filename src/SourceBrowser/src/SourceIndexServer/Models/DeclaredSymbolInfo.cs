using System;

namespace Microsoft.SourceBrowser.SourceIndexServer
{
    public class DeclaredSymbolInfo : IEquatable<DeclaredSymbolInfo>
    {
        public ushort AssemblyNumber;
        public string AssemblyName { get; set; }
        public string ProjectFilePath { get; set; }
        public ushort Glyph;
        public string Name;
        public ulong ID;
        public string Kind;
        public string Description;
        public ushort MatchLevel;

        public DeclaredSymbolInfo()
        {
        }

        public ushort KindRank
        {
            get
            {
                return SymbolKindText.Rank(Kind);
            }
        }

        public string GetNamespace()
        {
            var description = Description;
            if (string.IsNullOrEmpty(description))
            {
                return "";
            }

            int lastDot = description.LastIndexOf('.');
            if (lastDot == -1)
            {
                return "";
            }

            return description.Substring(0, lastDot);
        }

        public int Weight
        {
            get
            {
                return MatchLevel * 10 + KindRank;
            }
        }

        // Bias results toward the canonical framework assemblies so a well-known BCL type (e.g.
        // System.Collections.Generic.Dictionary) outranks an equally-weighted same-named type from a
        // niche assembly like Microsoft.CodeAnalysis. Lower is better; only used as a tiebreaker after
        // match quality has been compared, so it never promotes a worse match.
        // See https://github.com/KirillOsenkov/SourceBrowser/issues/249.
        public static int GetAssemblyRank(string assemblyName)
        {
            if (assemblyName == null)
            {
                return 3;
            }

            if (assemblyName == "System.Private.CoreLib" ||
                assemblyName == "System.Runtime" ||
                assemblyName == "mscorlib")
            {
                return 0;
            }

            if (assemblyName == "System" ||
                assemblyName.StartsWith("System.", StringComparison.Ordinal))
            {
                return 1;
            }

            return 2;
        }

        public bool Equals(DeclaredSymbolInfo other)
        {
            if (other == null)
            {
                return false;
            }

            return
                AssemblyNumber == other.AssemblyNumber &&
                ProjectFilePath == other.ProjectFilePath &&
                Glyph == other.Glyph &&
                Name == other.Name &&
                Kind == other.Kind &&
                ID == other.ID &&
                Description == other.Description;
        }

        public string GetUrl()
        {
            return "/" + AssemblyName + "/A.html#" + Serialization.ULongToHexString(ID);
        }

        public override bool Equals(object obj)
        {
            DeclaredSymbolInfo other = obj as DeclaredSymbolInfo;
            if (other == null)
            {
                return false;
            }

            return Equals(other);
        }

        public override int GetHashCode()
        {
            return
                AssemblyNumber.GetHashCode() ^
                ProjectFilePath.GetHashCode() ^
                Glyph.GetHashCode() ^
                Name.GetHashCode() ^
                Kind.GetHashCode() ^
                Description.GetHashCode() ^
                ID.GetHashCode();
        }
    }
}
