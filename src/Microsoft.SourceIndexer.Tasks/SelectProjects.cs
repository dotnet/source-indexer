using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.SourceIndexer.Tasks
{
    public class SelectProjects : Task
    {
        [Required]
        public ITaskItem[] Repositories { get; set; }

        [Output]
        public ITaskItem[] SelectedProjects { get; set; }

        public override bool Execute()
        {
            try
            {
                ExecuteCore();
                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, true);
                return false;
            }
        }

        private static Type FileMatcher { get; } = Type.GetType("Microsoft.Build.Shared.FileMatcher, Microsoft.Build.Tasks.Core, Version=14.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");

        private static Func<string, string, string[]> GetFiles { get; } =
            (Func<string, string, string[]>)FileMatcher.GetTypeInfo()
                .GetMethod("GetFiles", BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] {typeof(string), typeof(string)}, new ParameterModifier[0])
                .CreateDelegate(typeof(Func<string, string, string[]>));

        private IEnumerable<string> EvaluateItemInclude(string localPath, string include)
        {
            return include
                .Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .SelectMany(i => GetFiles(localPath, i))
                .Select(relative => Path.GetFullPath(Path.Combine(localPath, relative)));
        }

        private void ExecuteCore()
        {
            var selectedProjects = new List<ITaskItem>();
            foreach (var repository in Repositories)
            {
                var localPath = repository.GetMetadata("LocalPath");
                var projects = EvaluateItemInclude(localPath, repository.GetMetadata("Projects"));
                var excludedProjects = EvaluateItemInclude(localPath, repository.GetMetadata("ExcludeProjects"));
                foreach (var project in projects.Except(excludedProjects))
                {
                    selectedProjects.Add(new TaskItem(project));
                }
            }
            SelectedProjects = selectedProjects.OrderBy(i => i.GetMetadata("Identity")).ToArray();
            Log.LogMessage("Selected Projects:");
            foreach (var project in SelectedProjects)
            {
                Log.LogMessage(project.GetMetadata("FullPath"));
            }
        }
    }
}
