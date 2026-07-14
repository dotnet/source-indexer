using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class ProjectFinalizer
    {
        public string ProjectDestinationFolder { get; private set; }

        /// <summary>
        /// True when this run retained the existing output copy from a previous run instead of
        /// re-copying it from Pass1's source index -- i.e. an incremental run (/incremental) determined
        /// this assembly's staleness key hasn't changed. Cross-assembly aggregates (references, "Used By")
        /// are still recomputed for retained projects every run; only the expensive per-assembly copy is
        /// skipped.
        /// </summary>
        public bool RetainedExistingOutput { get; private set; }

        private string projectSourcePath;
        private string referencesFolder;
        public SolutionFinalizer SolutionFinalizer;

        public string AssemblyId { get; private set; }
        public string[] ReferencedAssemblies { get; set; }
        public List<string> ReferencingAssemblies { get; private set; }
        public Dictionary<ulong, DeclaredSymbolInfo> DeclaredSymbols { get; set; }
        public Dictionary<ulong, Tuple<string, ulong>> BaseMembers { get; set; }
        public MultiDictionary<ulong, Tuple<string, ulong>> ImplementedInterfaceMembers { get; set; }

        public long DocumentCount { get; set; }
        public long LinesOfCode { get; set; }
        public long BytesOfCode { get; set; }
        public long DeclaredSymbolCount { get; set; }
        public long DeclaredTypeCount { get; set; }
        public long PublicTypeCount { get; set; }

        public ProjectFinalizer(SolutionFinalizer solutionFinalizer, string directory)
        {
            this.BaseMembers = new Dictionary<ulong, Tuple<string, ulong>>();
            this.ImplementedInterfaceMembers = new MultiDictionary<ulong, Tuple<string, ulong>>();
            this.SolutionFinalizer = solutionFinalizer;
            ReferencingAssemblies = new List<string>();
            this.AssemblyId = string.Intern(Path.GetFileName(directory));

            // Copy Pass1's raw per-assembly folder into the finalized output root before doing anything
            // else with it. Everything below this point -- reading, byte-patching declarations, consuming
            // and deleting reference shards, appending "Used By" backlinks -- operates only on this copy,
            // so Pass1's own folder (under SolutionFinalizer.SourceIndexFolder) is never mutated and stays
            // a pure, re-derivable artifact.
            this.ProjectDestinationFolder = Path.Combine(solutionFinalizer.SolutionDestinationFolder, AssemblyId);

            this.RetainedExistingOutput = Configuration.Incremental && CanRetainExistingOutput(directory, this.ProjectDestinationFolder);

            if (!this.RetainedExistingOutput)
            {
                // Always start from a clean copy of Pass1's current output rather than layering onto
                // whatever (possibly stale, possibly already-patched) folder a previous finalize run may
                // have left behind -- that's what makes re-running Pass2 alone safe and idempotent.
                if (Directory.Exists(this.ProjectDestinationFolder))
                {
                    Directory.Delete(this.ProjectDestinationFolder, recursive: true);
                }

                FileUtilities.CopyDirectory(directory, this.ProjectDestinationFolder);
            }

            this.referencesFolder = Path.Combine(this.ProjectDestinationFolder, Constants.ReferencesFileName);
            ReadProjectInfo();
            ReadDeclarationLines();
            ReadBaseMembers();
            ReadImplementedInterfaceMembers();
        }

        /// <summary>
        /// An incremental run can skip re-copying (and skip Pass1 regenerating, separately) an assembly
        /// whose staleness key file -- written by Pass1 into its source folder and carried along into the
        /// output copy the first time it's copied -- is unchanged and whose output copy already exists and
        /// looks complete. This is the Pass2-side half of the same staleness check ProjectStaleness/
        /// ProjectGenerator perform for Pass1; both gate on the identical key so a project's regen and its
        /// copy are always skipped or performed together.
        /// </summary>
        private static bool CanRetainExistingOutput(string sourceDirectory, string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
            {
                return false;
            }

            var sourceKeyFile = Path.Combine(sourceDirectory, Constants.StalenessKeyFileName + ".txt");
            var outputKeyFile = Path.Combine(outputDirectory, Constants.StalenessKeyFileName + ".txt");
            var outputMarkerFile = Path.Combine(outputDirectory, Constants.ProjectInfoFileName + ".txt");

            if (!File.Exists(sourceKeyFile) || !File.Exists(outputKeyFile) || !File.Exists(outputMarkerFile))
            {
                return false;
            }

            return string.Equals(File.ReadAllText(sourceKeyFile), File.ReadAllText(outputKeyFile), StringComparison.Ordinal);
        }

        public override string ToString()
        {
            return AssemblyId;
        }

        public void ReadDeclarationLines()
        {
            DeclaredSymbols = new Dictionary<ulong, DeclaredSymbolInfo>();
            var assemblyIndex = Path.Combine(ProjectDestinationFolder, Constants.DeclaredSymbolsFileName + ".txt");
            if (!File.Exists(assemblyIndex))
            {
                return;
            }

            var declarationLines = File.ReadAllLines(assemblyIndex);
            foreach (var declarationLine in declarationLines)
            {
                var symbolInfo = new DeclaredSymbolInfo(declarationLine)
                {
                    AssemblyName = this.AssemblyId
                };
                if (symbolInfo.IsValid)
                {
                    DeclaredSymbols[symbolInfo.ID] = symbolInfo;
                }
            }
        }

        public string ProjectInfoLine => projectSourcePath;

        /// <summary>Optional repo/solution tags read back from ProjectInfo.txt (empty when untagged).</summary>
        public string RepoName { get; private set; } = string.Empty;
        public string SolutionName { get; private set; } = string.Empty;

        private void ReadBaseMembers()
        {
            var baseMembersFile = Path.Combine(ProjectDestinationFolder, Constants.BaseMembersFileName + ".txt");
            if (!File.Exists(baseMembersFile))
            {
                return;
            }

            var lines = File.ReadAllLines(baseMembersFile);
            foreach (var line in lines)
            {
                var parts = line.Split(';');
                var derivedId = Serialization.HexStringToULong(parts[0]);
                var baseAssemblyName = string.Intern(parts[1]);
                var baseId = Serialization.HexStringToULong(parts[2]);
                BaseMembers[derivedId] = Tuple.Create(baseAssemblyName, baseId);
            }
        }

        private void ReadImplementedInterfaceMembers()
        {
            var implementedInterfaceMembersFile = Path.Combine(ProjectDestinationFolder, Constants.ImplementedInterfaceMembersFileName + ".txt");
            if (!File.Exists(implementedInterfaceMembersFile))
            {
                return;
            }

            var lines = File.ReadAllLines(implementedInterfaceMembersFile);
            foreach (var line in lines)
            {
                var parts = line.Split(';');
                var implementationId = Serialization.HexStringToULong(parts[0]);
                var interfaceAssemblyName = string.Intern(parts[1]);
                var interfaceMemberId = Serialization.HexStringToULong(parts[2]);
                ImplementedInterfaceMembers.Add(implementationId, Tuple.Create(interfaceAssemblyName, interfaceMemberId));
            }
        }

        private void ReadProjectInfo()
        {
            var projectInfoFile = Path.Combine(ProjectDestinationFolder, Constants.ProjectInfoFileName + ".txt");
            if (File.Exists(projectInfoFile))
            {
                var lines = File.ReadAllLines(projectInfoFile);
                projectSourcePath = Serialization.ReadValue(lines, "ProjectSourcePath");
                DocumentCount = Serialization.ReadLong(lines, "DocumentCount");
                LinesOfCode = Serialization.ReadLong(lines, "LinesOfCode");
                BytesOfCode = Serialization.ReadLong(lines, "BytesOfCode");
                DeclaredSymbolCount = Serialization.ReadLong(lines, "DeclaredSymbols");
                DeclaredTypeCount = Serialization.ReadLong(lines, "DeclaredTypes");
                PublicTypeCount = Serialization.ReadLong(lines, "PublicTypes");
                RepoName = Serialization.ReadValue(lines, "RepoName");
                SolutionName = Serialization.ReadValue(lines, "SolutionName");
            }

            var referenceList = Path.Combine(ProjectDestinationFolder, Constants.ReferencedAssemblyList + ".txt");
            if (File.Exists(referenceList))
            {
                ReferencedAssemblies = File.ReadAllLines(referenceList).Select(s => string.Intern(s)).ToArray();
            }
        }
    }
}
