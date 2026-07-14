using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public enum ReferenceKind
    {
        DerivedType,
        InterfaceInheritance,
        InterfaceImplementation,
        Override,
        InterfaceMemberImplementation,
        Instantiation,
        Write,
        Read,
        Reference,
        GuidUsage,
        EmptyArrayAllocation,
        MSBuildPropertyAssignment,
        MSBuildPropertyUsage,
        MSBuildItemAssignment,
        MSBuildItemUsage,
        MSBuildTargetDeclaration,
        MSBuildTargetUsage,
        MSBuildTaskDeclaration,
        MSBuildTaskUsage
    }

    public class Reference
    {
        public string ToAssemblyId { get; set; }
        public string FromAssemblyId { get; set; }
        public string ToSymbolId { get; set; }
        public string FromLocalPath { get; set; }
        public string Url { get; set; }
        public string ReferenceLineText { get; set; }
        public int ReferenceColumnStart { get; set; }
        public int ReferenceColumnEnd { get; set; }
        public int ReferenceLineNumber { get; set; }
        public string ToSymbolName { get; set; }
        public ReferenceKind Kind { get; set; }

        /// <summary>
        /// Which config(s) (e.g. "windows"/"linux"/"mac" builds of the same sources) this reference
        /// was observed under. Null/empty means "not applicable" -- ordinary single-config or no-config
        /// runs never populate this, so <see cref="WriteTo"/>'s shard format and <see cref="Reference(string, string)"/>'s
        /// parser are entirely unaffected by it; it exists purely as in-memory metadata attached by
        /// <see cref="ConfigReferenceMerger"/> when merging N configs' independently-generated shards
        /// for the same assembly into one served reference index. A reference that resolves
        /// identically under every indexed config (the common case for platform-agnostic code) ends up
        /// tagged with all of them; a reference that only exists inside a "#if WINDOWS"-gated call site
        /// is tagged with only the configs that actually produced it.
        /// </summary>
        public HashSet<string> ConfigSet { get; set; }

        public Reference()
        {
        }

        public Reference(string separatedLine, string sourceLine)
        {
            var parts = separatedLine.Split(';');
            // Note: FromAssemblyId is deliberately not string.Intern'd. This constructor runs per
            // reference inside the parallel Pass2 finalization, and string.Intern takes a process-wide
            // lock (serializing that work) and roots every distinct string for the process lifetime.
            // These Reference instances are transient -- grouped, written, and discarded per shard --
            // so interning buys no lasting sharing while costing contention and unbounded pool growth.
            FromAssemblyId = parts[0];
            Url = parts[1];
            FromLocalPath = parts[2];
            ReferenceLineNumber = int.Parse(parts[3]);
            ReferenceColumnStart = int.Parse(parts[4]);
            ReferenceColumnEnd = int.Parse(parts[5]);
            if (parts.Length >= 7)
            {
                Kind = (ReferenceKind)int.Parse(parts[6]);
            }

            ReferenceLineText = sourceLine;
            ToSymbolName = ReferenceLineText.Substring(ReferenceColumnStart, ReferenceColumnEnd - ReferenceColumnStart);
        }

        public void WriteTo(TextWriter writer)
        {
            writer.Write(FromAssemblyId);
            writer.Write(';');
            writer.Write(Url);
            writer.Write(';');
            writer.Write(FromLocalPath);
            writer.Write(';');
            writer.Write(ReferenceLineNumber);
            writer.Write(';');
            writer.Write(ReferenceColumnStart);
            writer.Write(';');
            writer.Write(ReferenceColumnEnd);
            writer.Write(';');
            writer.Write((int)Kind);
            writer.WriteLine();
            writer.WriteLine(ReferenceLineText);
        }

        /// <summary>
        /// True when two references represent the SAME referencing occurrence -- used only by
        /// <see cref="ConfigReferenceMerger"/> to decide whether two configs' independently-generated
        /// reference records for a given target symbol are the identical call/usage site (and should
        /// collapse into one entry with a unioned <see cref="ConfigSet"/>) or genuinely distinct
        /// occurrences (kept as separate entries, each with its own, narrower, config tag). Excludes
        /// <see cref="ReferenceLineText"/>/<see cref="ToSymbolName"/> deliberately: those are derived
        /// display data, not identity.
        /// </summary>
        public bool HasSameOccurrenceAs(Reference other)
        {
            if (other == null)
            {
                return false;
            }

            return
                string.Equals(FromAssemblyId, other.FromAssemblyId, StringComparison.Ordinal) &&
                string.Equals(FromLocalPath, other.FromLocalPath, StringComparison.OrdinalIgnoreCase) &&
                ReferenceLineNumber == other.ReferenceLineNumber &&
                ReferenceColumnStart == other.ReferenceColumnStart &&
                ReferenceColumnEnd == other.ReferenceColumnEnd &&
                Kind == other.Kind;
        }
    }
}
