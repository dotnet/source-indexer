using Microsoft.SourceBrowser.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class LogTests
    {
        [TestMethod]
        public void Severe_exceptions_increment_the_error_count()
        {
            var before = Log.ErrorCount;
            Log.Exception("boom", isSevere: true);
            Log.ErrorCount.ShouldBe(before + 1);
        }

        [TestMethod]
        public void Severe_errors_are_counted_even_when_warnings_are_suppressed()
        {
            var previous = Log.SuppressWarnings;
            Log.SuppressWarnings = true;
            try
            {
                var before = Log.ErrorCount;
                Log.Exception("boom", isSevere: true);
                Log.ErrorCount.ShouldBe(before + 1);
            }
            finally
            {
                Log.SuppressWarnings = previous;
            }
        }
    }
}
