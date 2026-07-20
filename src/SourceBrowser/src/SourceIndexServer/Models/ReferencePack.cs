using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    // Reads the packed reference output produced by the HtmlGenerator: a single references.pack holding
    // every per-symbol reference fragment back-to-back, plus a references.index describing each fragment's
    // byte range. Fragments are returned verbatim (preamble and all) so they are byte-identical to the
    // individual .html files the server used to serve. The index is read once into memory; fragments are
    // then fetched with positioned reads -- from a local file handle when the index is on disk, or ranged
    // GETs against blob storage when it is served from Azure (source.dot.net). Both are thread-safe, so a
    // single pack serves concurrent requests without locking.
    public sealed class ReferencePack : IDisposable
    {
        private readonly Record[] _records;
        private readonly Dictionary<string, int> _index;
        private readonly IPackData _data;

        private readonly struct Record
        {
            public readonly long Offset;
            public readonly int Length;

            public Record(long offset, int length)
            {
                Offset = offset;
                Length = length;
            }
        }

        // Backing store for the concatenated fragment bytes, hiding whether they live on local disk or blob.
        // Fragments are streamed straight to the response rather than buffered, so a heavily-referenced
        // symbol's (multi-MB) fragment doesn't force a large-object-heap allocation per request.
        private interface IPackData : IDisposable
        {
            Task WriteToAsync(long offset, int length, Stream destination);
        }

        private ReferencePack(IPackData data, Dictionary<string, int> index, Record[] records)
        {
            _data = data;
            _index = index;
            _records = records;
        }

        public static bool TryLoad(string referencesFolder, out ReferencePack pack)
        {
            pack = null;

            var packPath = Path.Combine(referencesFolder, Constants.ReferencePackFileName);
            var indexPath = Path.Combine(referencesFolder, Constants.ReferenceIndexFileName);

            if (!File.Exists(packPath) || !File.Exists(indexPath))
            {
                return false;
            }

            using (var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan))
            {
                ReadIndex(stream, out var index, out var records);
                var handle = File.OpenHandle(packPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
                pack = new ReferencePack(new FilePackData(handle), index, records);
            }

            return true;
        }

        // Loads the pack for an assembly whose index is served from blob storage: the small references.index
        // is read once into memory, and each fragment is then served with a ranged read of references.pack so
        // the multi-hundred-MB pack is never downloaded in full.
        public static bool TryLoadFromBlob(IFileSystem fs, string assembly, out ReferencePack pack)
        {
            pack = null;

            var packName = $"/{assembly}/{Constants.ReferencesFileName}/{Constants.ReferencePackFileName}";
            var indexName = $"/{assembly}/{Constants.ReferencesFileName}/{Constants.ReferenceIndexFileName}";

            if (!fs.FileExists(indexName) || !fs.FileExists(packName))
            {
                return false;
            }

            using (var stream = fs.OpenSequentialReadStream(indexName))
            {
                ReadIndex(stream, out var index, out var records);
                pack = new ReferencePack(new BlobPackData(fs, packName), index, records);
            }

            return true;
        }

        private static void ReadIndex(Stream stream, out Dictionary<string, int> index, out Record[] records)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            int count = reader.ReadInt32();
            index = new Dictionary<string, int>(count, StringComparer.Ordinal);
            records = new Record[count];

            for (int i = 0; i < count; i++)
            {
                var id = reader.ReadString();
                long offset = reader.ReadInt64();
                int length = reader.ReadInt32();

                records[i] = new Record(offset, length);
                index[id] = i;
            }
        }

        // Looks up a fragment's location in the pack. The bytes are streamed separately via
        // WriteFragmentAsync so the caller can set Content-Length before writing the body.
        public bool TryGetFragment(string symbolId, out long offset, out int length)
        {
            if (!_index.TryGetValue(symbolId, out int recordIndex))
            {
                offset = 0;
                length = 0;
                return false;
            }

            var record = _records[recordIndex];
            offset = record.Offset;
            length = record.Length;
            return true;
        }

        public Task WriteFragmentAsync(long offset, int length, Stream destination)
        {
            return _data.WriteToAsync(offset, length, destination);
        }

        public void Dispose()
        {
            _data.Dispose();
        }

        private sealed class FilePackData : IPackData
        {
            private readonly SafeFileHandle _handle;

            public FilePackData(SafeFileHandle handle)
            {
                _handle = handle;
            }

            public async Task WriteToAsync(long offset, int length, Stream destination)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, 81920));
                try
                {
                    long position = offset;
                    int remaining = length;
                    while (remaining > 0)
                    {
                        int chunk = Math.Min(remaining, buffer.Length);
                        int n = await RandomAccess.ReadAsync(_handle, buffer.AsMemory(0, chunk), position).ConfigureAwait(false);
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

            public void Dispose()
            {
                _handle.Dispose();
            }
        }

        private sealed class BlobPackData : IPackData
        {
            private readonly IFileSystem _fs;
            private readonly string _packName;

            public BlobPackData(IFileSystem fs, string packName)
            {
                _fs = fs;
                _packName = packName;
            }

            public Task WriteToAsync(long offset, int length, Stream destination)
            {
                return _fs.CopyBytesToAsync(_packName, offset, length, destination);
            }

            public void Dispose()
            {
            }
        }
    }
}
