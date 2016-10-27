using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace Microsoft.SourceIndexer.Tasks
{
    public class ResolveLivePackageReferences : Task
    {
        [Required]
        public ITaskItem[] References { get; set; }

        public string AdditionalAssemblyReferences { get; set; } = string.Empty;

        [Required]
        public string LiveReferenceCacheFile { get; set; }

        [Output]
        public ITaskItem[] ReferencePaths { get; set; }

        public override bool Execute()
        {
            try
            {
                return ExecuteCore();
            }
            catch (Exception ex)
            {
                LogException(ex);
                return false;
            }
        }

        private void LogException(Exception ex)
        {
            var agg = ex as AggregateException;
            if (agg != null)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    LogException(inner);
                }
            }
            else
            {
                Log.LogErrorFromException(ex, true);
            }
        }

        private bool ExecuteCore()
        {
            var assemblyNameToProjectFile =
                JsonConvert.DeserializeObject<Dictionary<string, LiveReferenceData>>(File.ReadAllText(LiveReferenceCacheFile));
            var referencePaths = new List<ITaskItem>();
            var referencedProjects = new List<LiveReferenceData>();
            foreach (var reference in References)
            {
                var assemblyName = reference.GetMetadata("FileName");
                LiveReferenceData referenceData;
                if (assemblyNameToProjectFile.TryGetValue(assemblyName, out referenceData))
                {
                    referencedProjects.Add(referenceData);
                }
                else
                {
                    referencePaths.Add(reference);
                }
            }
            var additionalAssemblyReferences =
                AdditionalAssemblyReferences.Split(';').Where(s => !string.IsNullOrEmpty(s)).ToList();
            foreach (var assemblyName in additionalAssemblyReferences)
            {
                referencedProjects.Add(assemblyNameToProjectFile[assemblyName]);
            }
            var projectsToBuild = new List<string>();
            foreach (var liveReference in referencedProjects)
            {
                if (!File.Exists(liveReference.TargetPath))
                {
                    Log.LogMessage(MessageImportance.Normal, "Reference '{0}' doesn't exist. It will be built.", liveReference.TargetPath);
                    projectsToBuild.Add(liveReference.Project);
                }
                else
                {
                    Log.LogMessage(MessageImportance.Normal, "Reference '{0}' exists", liveReference.TargetPath);
                }
            }
            BuildEngineResult result = BuildEngine3.BuildProjectFilesInParallel(
                projectsToBuild.ToArray(),
                "Build",
                new Dictionary<string, string>(),
                new[] {"CustomAfterBuildCommonTargets"},
                null,
                false
            );
            if (!result.Result)
            {
                Log.LogError("Failed to resolve references.");
                return false;
            }
            referencePaths.AddRange(referencedProjects.Select(p => new TaskItem(p.TargetPath)));
            ReferencePaths = referencePaths.ToArray();
            return true;
        }
    }
}
