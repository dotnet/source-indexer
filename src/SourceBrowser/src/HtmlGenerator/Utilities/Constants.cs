namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Constants
    {
        public static readonly string IDResolvingFileName = "A";
        public static readonly string PartialResolvingFileName = "P";
        public static readonly string ReferencesFileName = "R";
        public static readonly string ReferencePackFileName = "references.pack";
        public static readonly string ReferenceIndexFileName = "references.index";
        public static readonly string DeclaredSymbolsFileName = "D";
        public static readonly string MasterIndexFileName = "DeclaredSymbols.txt";
        public static readonly string ReferencedAssemblyList = "References";
        public static readonly string UsedReferencedAssemblyList = "UsedReferences";
        public static readonly string ReferencingAssemblyList = "ReferencingAssemblies";
        public static readonly string ProjectInfoFileName = "i";
        public static readonly string MasterProjectMap = "Projects";
        public static readonly string MasterAssemblyMap = "Assemblies";
        public static readonly string Namespaces = "namespaces.html";

        public static readonly string ClassificationIdentifier = "i";
        public static readonly string ClassificationKeyword = "k";
        public static readonly string ClassificationTypeName = "t";
        public static readonly string ClassificationComment = "c";
        public static readonly string ClassificationLiteral = "s";
        public static readonly string ClassificationXmlLiteralDelimiter = "xld";
        public static readonly string ClassificationXmlLiteralName = "xln";
        public static readonly string ClassificationXmlLiteralAttributeName = "xlan";
        public static readonly string ClassificationXmlLiteralAttributeValue = "xlav";
        public static readonly string ClassificationXmlLiteralAttributeQuotes = "xlaq";
        public static readonly string ClassificationXmlLiteralEntityReference = "xler";
        public static readonly string ClassificationXmlLiteralCDataSection = "xlcs";
        public static readonly string ClassificationXmlLiteralEmbeddedExpression = "xlee";
        public static readonly string ClassificationXmlLiteralProcessingInstruction = "xlpi";
        public static readonly string ClassificationNamespace = "n";
        public static readonly string ClassificationMethod = "method";
        public static readonly string ClassificationField = "field";
        public static readonly string ClassificationConstructor = "constructor";
        public static readonly string ClassificationPreprocessKeyword = "k preprocess";
        public static readonly string ClassificationProperty = "property";
        public static readonly string ClassificationUnknown = "unknownClassification";

        public static readonly string ClassificationExcludedCode = "e";
        public static readonly string RoslynClassificationKeyword = "keyword";
        public static readonly string DeclarationMap = "DeclarationMap";
        public static readonly string ClassificationPunctuation = "punctuation";
        public static readonly string ProjectExplorer = "ProjectExplorer";
        public static readonly string SolutionExplorer = "SolutionExplorer";
        public static readonly string HuffmanFileName = "Huffman.txt";
        public static readonly string TopReferencedAssemblies = "TopReferencedAssemblies";
        public static readonly string BaseMembersFileName = "BaseMembers";
        public static readonly string ImplementedInterfaceMembersFileName = "ImplementedInterfaceMembers";
        public static readonly string GuidAssembly = "GuidAssembly";
        public static readonly string MSBuildPropertiesAssembly = "MSBuildProperties";
        public static readonly string MSBuildItemsAssembly = "MSBuildItems";
        public static readonly string MSBuildTargetsAssembly = "MSBuildTargets";
        public static readonly string MSBuildTasksAssembly = "MSBuildTasks";
        public static readonly string MSBuildFiles = "MSBuildFiles";
        public static readonly string TypeScriptFiles = "TypeScriptFiles";
        public static readonly string AssemblyPaths = "AssemblyPaths.txt";

        /// <summary>
        /// Per-project solution-folder chain (one segment per line, top-down; empty file for a project
        /// at the solution root), persisted next to that project's other Pass1 output. Solely so the
        /// config merge step can reconstruct SolutionExplorer.html's navigation tree across all
        /// registered configs' obj/&lt;config&gt; roots without re-parsing the original .sln/.slnx --
        /// the merge step runs as a separate invocation from whichever one(s) ran Pass1, with no access
        /// to the solution file itself. This is purely additive: nothing else reads or depends on this
        /// file, so it cannot regress any other output.
        /// </summary>
        public static readonly string SolutionFolderFileName = "SolutionFolder.txt";

        /// <summary>
        /// Per-assembly staleness key written by Pass1 (see <see cref="ProjectStaleness"/>) next to that
        /// assembly's raw index, and copied by Pass2 alongside the finalized output. Comparing the two
        /// copies is how incremental runs decide whether an assembly's Pass1 output (and Pass2 copy) can
        /// be skipped entirely.
        /// </summary>
        public static readonly string StalenessKeyFileName = "StalenessKey";

        /// <summary>
        /// Written at the root of the merged <c>index/</c> website output (NOT the same file as
        /// <see cref="ConfigRegistry.ConfigsFileName"/>, which lives one level up in the run's private
        /// /out root and is never servable) so the client config-selector can discover, at runtime, which
        /// configs the current site was merged from. A simple JSON array of config names, e.g.
        /// <c>["linux","windows"]</c>. Only ever written by <see cref="ConfigAwareProjectFinalizer.Finalize"/>
        /// (i.e. only for a 2+-config merge); a single/no-config site never has this file, which is how
        /// the client selector knows to render nothing rather than an empty/single-option UI.
        /// </summary>
        public static readonly string RegisteredConfigsFileName = "configs.json";
    }
}
