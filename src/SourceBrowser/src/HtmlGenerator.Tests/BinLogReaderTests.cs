using Microsoft.SourceBrowser.BinLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class BinLogReaderTests
    {
        [DataTestMethod]
        [DataRow(@"C:\src\MyApp_wpftmp.csproj")]
        [DataRow(@"C:\src\MyApp_abc123_wpftmp.csproj")]
        [DataRow("MyApp_wpftmp.csproj")]
        [DataRow(@"C:\src\MyApp_WPFTMP.csproj")]
        public void Temporary_wpf_projects_are_recognized(string projectFilePath)
        {
            BinLogCompilerInvocationsReader.IsTemporaryWpfProject(projectFilePath).ShouldBeTrue();
        }

        [DataTestMethod]
        [DataRow(@"C:\src\MyApp.csproj")]
        [DataRow(@"C:\src\wpftmp\MyApp.csproj")]
        [DataRow("MyApp.vbproj")]
        [DataRow(null)]
        public void Real_projects_are_not_treated_as_temporary(string projectFilePath)
        {
            BinLogCompilerInvocationsReader.IsTemporaryWpfProject(projectFilePath).ShouldBeFalse();
        }
    }
}
