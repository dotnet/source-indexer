using System.IO;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class FederationTests
    {
        [TestMethod]
        [DataRow(false, "System.Runtime/A.html#8281103e6f23cb5c")]
        [DataRow(true, "api/symbolredirect?symbolId=8281103e6f23cb5c")]
        public void ExternalSymbolPathUsesAdvertisedResolver(bool supportsSymbolRedirect, string expected)
        {
            string assemblyListFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(assemblyListFile, "System.Runtime");
                var federation = new Federation();
                federation.AddFederation(
                    "https://source.example/",
                    assemblyListFile,
                    supportsSymbolRedirect);

                Assert.AreEqual(
                    expected,
                    federation.GetExternalSymbolPath(
                        externalAssemblyIndex: 0,
                        "System.Runtime",
                        "8281103e6f23cb5c"));
            }
            finally
            {
                File.Delete(assemblyListFile);
            }
        }
    }
}
