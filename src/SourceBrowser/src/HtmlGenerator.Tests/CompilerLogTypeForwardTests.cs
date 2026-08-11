using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace HtmlGenerator.Tests;

[TestClass]
public sealed class CompilerLogTypeForwardTests
{
    [TestMethod]
    public void Metadata_emit_omits_pdb_only_compiler_log_data()
    {
        var sourceText = SourceText.From("public class C { }", Encoding.UTF8);
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: "Embedded.cs");
        var compilation = CSharpCompilation.Create(
            "Embedded",
            [syntaxTree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var sourceLinkStream = new MemoryStream(Encoding.UTF8.GetBytes("""{"documents":{}}"""));
        var emitData = new EmitData(
            assemblyFileName: "Embedded.dll",
            xmlFilePath: null,
            emitPdb: true,
            win32ResourceStream: null,
            sourceLinkStream,
            resources: null,
            embeddedTexts: [EmbeddedText.FromSource(syntaxTree.FilePath, sourceText)]);

        var result = Program.EmitMetadataForTypeForwards(
            compilation,
            emitData,
            new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        using var assemblyStream = result.AssemblyStream;
        using var pdbStream = result.PdbStream;

        result.Success.ShouldBeTrue(string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString())));
        assemblyStream.Length.ShouldBeGreaterThan(0);
        pdbStream.ShouldBeNull();
    }
}
