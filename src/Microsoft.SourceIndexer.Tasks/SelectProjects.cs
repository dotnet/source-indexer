using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ClassLibrary
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

        private void ExecuteCore()
        {
            var matcherType = Type.GetType("Microsoft.Build.Shared.FileMatcher, Microsoft.Build.Tasks.Core, Version=14.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
            var getFilesFunction = matcherType.GetTypeInfo()
                    .GetMethod("GetFiles", BindingFlags.NonPublic | BindingFlags.Static, null, new Type[]{typeof(string), typeof(string)}, new ParameterModifier[0]);

            var selectedProjects = new List<ITaskItem>();
            foreach (var repository in Repositories)
            {
                var projects = repository.GetMetadata("Projects");
                var localPath = repository.GetMetadata("LocalPath");
                foreach (var projectItemSpec in projects.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = projectItemSpec.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        var itemSpec = trimmed;
                        var files = (string[])getFilesFunction.Invoke(null, new object[] {localPath, itemSpec});
                        foreach (var file in files)
                        {
                        selectedProjects.Add(new TaskItem(localPath + file));
                        }
                    }
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
