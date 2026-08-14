using System;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.SourceIndexServer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class ClrOperatorCompatibilityTests
    {
        [TestMethod]
        public void EveryRoslynOperatorNameHasSearchInterpretations()
        {
            var operatorNames = typeof(WellKnownMemberNames)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f =>
                    f.FieldType == typeof(string) &&
                    (f.Name.EndsWith("OperatorName", StringComparison.Ordinal) ||
                    f.Name.EndsWith("ConversionName", StringComparison.Ordinal)))
                .Select(f => (string)f.GetValue(null));

            foreach (string operatorName in operatorNames)
            {
                AssertHasAlternateInterpretation(operatorName.Substring("op_".Length));
            }
        }

        [TestMethod]
        [DataRow("LogicalAnd", WellKnownMemberNames.LogicalAndOperatorName)]
        [DataRow("LogicalOr", WellKnownMemberNames.LogicalOrOperatorName)]
        [DataRow("UnsignedLeftShift", WellKnownMemberNames.UnsignedLeftShiftOperatorName)]
        public void MetadataOnlyOperatorNamesResolveToClrName(string friendlyName, string clrName)
        {
            var query = new Query(friendlyName);

            Assert.IsTrue(query.Interpretations.Any(i => i.CoreSearchTerm == clrName));
        }

        private static void AssertHasAlternateInterpretation(string operatorName)
        {
            var query = new Query(operatorName);

            Assert.IsTrue(
                query.Interpretations.Any(
                    i => !i.CoreSearchTerm.Equals(operatorName, StringComparison.OrdinalIgnoreCase)),
                operatorName);
        }
    }
}
