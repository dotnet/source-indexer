using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    public interface IFileSystem
    {
        bool DirectoryExists(string name);
        IEnumerable<string> ListFiles(string dirName);
        bool FileExists(string name);
        Stream OpenSequentialReadStream(string name);
        Task CopyBytesToAsync(string name, long offset, int length, Stream destination);
        IEnumerable<string> ReadLines(string name);
    }
}