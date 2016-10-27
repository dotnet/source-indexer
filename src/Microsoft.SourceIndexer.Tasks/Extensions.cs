using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.Framework;

namespace Microsoft.SourceIndexer.Tasks
{
    public static class Extensions
    {
        public static BuildEngineResult BuildProjectFilesInParallel(this IBuildEngine3 engine, string[] projectFileNames, string targetNames, IDictionary globalProperties, IList<string> removeGlobalProperties, string toolsVersion, bool returnTargetOutputs)
        {
            return engine.BuildProjectFilesInParallel(
                projectFileNames,
                CreateArray(targetNames, 1),
                CreateArray(globalProperties, projectFileNames.Length),
                CreateArray(removeGlobalProperties, projectFileNames.Length),
                CreateArray(toolsVersion, projectFileNames.Length),
                returnTargetOutputs
            );
        }

        private static T[] CreateArray<T>(T value, int count)
        {
            return Enumerable.Repeat(value, count).ToArray();
        }
    }
}
