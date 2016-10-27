using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace Microsoft.SourceIndexer.Tasks
{
    public class LiveReferenceData
    {
        public string Project { get; set; }
        public string TargetPath { get; set; }
    }

    public class GenerateLiveReferenceCache : Task
    {
        [Required]
        public ITaskItem[] CandidateReferenceProjects { get; set; }

        public string SetProperties { get; set; } = string.Empty;

        public string UndefineProperties { get; set; } = string.Empty;

        [Required]
        public string LiveReferenceCacheFile { get; set; }

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
            var properties = new Dictionary<string, string>();
            foreach (var prop in SetProperties.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var key = prop.Substring(0, prop.IndexOf('=')).Trim();
                var value = prop.Substring(prop.IndexOf('=') + 1).Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    properties[key] = value;
                }
            }
            var projectFiles = CandidateReferenceProjects
                .Select(p => p.GetMetadata("FullPath"))
                .ToArray();
            var propertyArray = new IDictionary[projectFiles.Length];
            for (int i = 0; i < projectFiles.Length; i++)
            {
                propertyArray[i] = properties;
            }
            var removePropertiesArray = Enumerable.Repeat((IList<string>)UndefineProperties.Split(new[] { ';' }).Select(p => p.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray(), propertyArray.Length).ToArray();
            BuildEngineResult result = BuildEngine3.BuildProjectFilesInParallel(
                projectFiles,
                Enumerable.Repeat("GetTargetPath", projectFiles.Length).ToArray(),
                propertyArray,
                removePropertiesArray,
                new string[projectFiles.Length],
                true
            );
            if (!result.Result)
            {
                Log.LogError("Building 'GetTargetPath' Failed.");
                return false;
            }
            var assemblyNameToProject = new Dictionary<string, LiveReferenceData>();
            for (int i = 0; i < projectFiles.Length; i++)
            {
                string projectFile = projectFiles[i];
                IDictionary<string, ITaskItem[]> targetOutputs = result.TargetOutputsPerProject[i];
                ITaskItem targetPath = targetOutputs["GetTargetPath"].First();
                string assemblyName = targetPath.GetMetadata("FileName");
                assemblyNameToProject[assemblyName] = new LiveReferenceData
                {
                    Project = projectFile,
                    TargetPath = targetPath.GetMetadata("FullPath")
                };
            }
            File.WriteAllText(LiveReferenceCacheFile, JsonConvert.SerializeObject(assemblyNameToProject, Formatting.Indented));
            return true;
        }
    }
}