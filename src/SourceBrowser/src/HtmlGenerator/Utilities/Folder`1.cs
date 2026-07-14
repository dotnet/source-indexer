using System;
using System.Collections.Generic;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>Distinguishes a plain physical-folder node in the Solution Explorer tree from the
    /// Repo/Solution grouping nodes introduced when a merged site spans more than one repo (see
    /// Program.IndexSolutionsAsync), so the Html/UI layers can style them differently.</summary>
    public enum FolderKind
    {
        Plain,
        Repo,
        Solution
    }

    public class Folder<T>
    {
        public string Name { get; set; }
        public FolderKind Kind { get; set; } = FolderKind.Plain;

        /// <summary>Repo tag for Repo/Solution grouping nodes (see Program.IndexSolutionsAsync),
        /// so the Solution Explorer's client-side repo filter can hide the whole group -- rather
        /// than leaving an empty, expandable shell behind -- when it doesn't match. Unset for
        /// plain physical-folder nodes.</summary>
        public string RepoName { get; set; }

        public SortedList<string, Folder<T>> Folders { get; set; }
        public List<T> Items { get; set; }

        public void Add(T item)
        {
            if (Items == null)
            {
                Items = new List<T>();
            }

            Items.Add(item);
        }

        public void Sort(Comparison<T> comparison)
        {
            if (Items != null)
            {
                Items.Sort((l, r) => comparison(l, r));
            }

            if (Folders != null)
            {
                foreach (var subfolder in Folders.Values)
                {
                    subfolder.Sort(comparison);
                }
            }
        }

        public Folder<T> GetOrCreateFolder(string folderName)
        {
            if (Folders == null)
            {
                Folders = new SortedList<string, Folder<T>>(StringComparer.OrdinalIgnoreCase);
            }

            if (!Folders.TryGetValue(folderName, out Folder<T> result))
            {
                result = new Folder<T> { Name = folderName };
                Folders.Add(folderName, result);
            }

            return result;
        }
    }
}
