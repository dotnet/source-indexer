using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.SourceBrowser.SourceIndexServer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class SpecialTypeCompatibilityTests
    {
        [TestMethod]
        public void EveryPredefinedTypeKeywordHasASearchInterpretation()
        {
            var keywordKinds = SyntaxFacts.GetKeywordKinds()
                .Concat(SyntaxFacts.GetContextualKeywordKinds())
                .Where(SyntaxFacts.IsPredefinedType);

            foreach (var keywordKind in keywordKinds)
            {
                string keyword = SyntaxFacts.GetText(keywordKind);
                string dottedName = keyword + ".Member";
                string normalizedName = Interpretation.NormalizeTypeAliases(dottedName);

                if (keyword.Equals("nint", StringComparison.OrdinalIgnoreCase) ||
                    keyword.Equals("nuint", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.AreEqual(dottedName, normalizedName, keyword);
                }
                else
                {
                    Assert.AreNotEqual(dottedName, normalizedName, keyword);
                }
            }
        }
    }
}
