using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    public class StaticFileSystem : IFileSystem
    {
        private readonly string rootPath;

        public StaticFileSystem(string rootPath)
        {
            this.rootPath = rootPath;
        }
        public bool DirectoryExists(string name)
        {
            return Directory.Exists(Path.Combine(rootPath, name));
        }

        public IEnumerable<string> ListFiles(string dirName)
        {
            return Directory.GetFiles(Path.Combine(rootPath, dirName));
        }

        public bool FileExists(string name)
        {
            return File.Exists(Path.Combine(rootPath, name));
        }

        public Stream OpenSequentialReadStream(string name)
        {
            var path = Path.Combine(rootPath, name);
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                262144,
                FileOptions.SequentialScan);
        }

        public async Task CopyBytesToAsync(string name, long offset, int length, Stream destination)
        {
            var path = Path.Combine(rootPath, name);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, 81920));
            try
            {
                long position = offset;
                int remaining = length;
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, buffer.Length);
                    int n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, chunk), position).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                    position += n;
                    remaining -= n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public IEnumerable<string> ReadLines(string name)
        {
            return File.ReadLines(Path.Combine(rootPath, name));
        }
    }
}