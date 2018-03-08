using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks;
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

        private string[] GetFiles(string localPath, string glob)
        {
            var createItemTask = new CreateItem
            {
                BuildEngine = BuildEngine,
                Include = new[]
                {
                    new TaskItem(Path.Combine(localPath, glob))
                }
            };

            if (!createItemTask.Execute())
            {
                throw new Exception(
                    $"Failed to create items with localPath '{localPath}', glob '{glob}'");
            }

            return createItemTask.Include.Select(item => item.ItemSpec).ToArray();
        }


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
