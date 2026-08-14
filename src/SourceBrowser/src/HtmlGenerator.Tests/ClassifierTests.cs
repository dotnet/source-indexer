using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class ClassifierTests
    {
        [TestMethod]
        [DataRow("struct S { public static S operator +(S left, S right) => left; }")]
        [DataRow("struct S { public static implicit operator int(S value) => 0; }")]
        public async Task OperatorDeclarationsAreLinkable(string source)
        {
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
                .WithProjectCompilationOptions(
                    projectId,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddDocument(
                    documentId,
                    "Operator.cs",
                    source);
            var document = solution.GetDocument(documentId);
            var classifier = new Classification();
            var text = await document.GetTextAsync();

            var ranges = (await classifier.ClassifyAsync(document, text)).ToArray();

            Assert.AreEqual(Constants.ClassificationKeyword, ranges.Single(r => r.Text == "operator").ClassificationType);
        }

        [TestMethod]
        [DataRow("OPERATOR")]
        [DataRow("OpErAtOr")]
        public async Task VisualBasicOperatorDeclarationsAreLinkable(string operatorKeyword)
        {
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "TestProject", "TestProject", LanguageNames.VisualBasic)
                .WithProjectCompilationOptions(
                    projectId,
                    new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary))
                .AddDocument(
                    documentId,
                    "Operator.vb",
                    $"Structure S\nPublic Shared {operatorKeyword} +(value As S) As S\nReturn value\nEnd Operator\nEnd Structure");
            var document = solution.GetDocument(documentId);
            var classifier = new Classification();
            var text = await document.GetTextAsync();

            var ranges = (await classifier.ClassifyAsync(document, text)).ToArray();

            Assert.AreEqual(
                Constants.ClassificationKeyword,
                ranges.Single(r => r.Text == operatorKeyword).ClassificationType);
        }

        [TestMethod]
        public async Task ConversionOperatorNamesUseSearchableSyntax()
        {
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
                .WithProjectCompilationOptions(
                    projectId,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddDocument(
                    documentId,
                    "Decimal32.cs",
                    "struct Decimal32 { public static explicit operator Decimal32(decimal value) => default; }");
            var document = solution.GetDocument(documentId);
            var root = await document.GetSyntaxRootAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            var declaration = root.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>().Single();
            var symbol = semanticModel.GetDeclaredSymbol(declaration);

            Assert.AreEqual("explicit operator Decimal32", SymbolIdService.GetName(symbol));
        }
    }
}
