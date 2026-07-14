using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    // Reads the packed reference output produced by the HtmlGenerator: a single references.pack holding
    // every per-symbol reference fragment back-to-back, plus a references.index describing each fragment's
    // byte range. Fragments are returned verbatim (preamble and all) so they are byte-identical to the
    // individual .html files the server used to serve. Positioned reads via RandomAccess are thread-safe,
    // so a single handle serves concurrent requests without locking.
    public sealed class ReferencePack : IDisposable
    {
        private readonly SafeHandleRecord[] _records;
        private readonly Dictionary<string, int> _index;
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _packHandle;

        private readonly struct SafeHandleRecord
        {
            public readonly long Offset;
            public readonly int Length;

            public SafeHandleRecord(long offset, int length)
            {
                Offset = offset;
                Length = length;
            }
        }

        private ReferencePack(Microsoft.Win32.SafeHandles.SafeFileHandle packHandle, Dictionary<string, int> index, SafeHandleRecord[] records)
        {
            _packHandle = packHandle;
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

            Dictionary<string, int> index;
            SafeHandleRecord[] records;

            using (var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan))
            using (var reader = new BinaryReader(stream))
            {
                int count = reader.ReadInt32();
                index = new Dictionary<string, int>(count, StringComparer.Ordinal);
                records = new SafeHandleRecord[count];

                var idBytes = new byte[16];
                for (int i = 0; i < count; i++)
                {
                    if (reader.Read(idBytes, 0, 16) != 16)
                    {
                        return false;
                    }

                    var id = Encoding.ASCII.GetString(idBytes);
                    long offset = reader.ReadInt64();
                    int length = reader.ReadInt32();

                    records[i] = new SafeHandleRecord(offset, length);
                    index[id] = i;
                }
            }

            var handle = File.OpenHandle(packPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            pack = new ReferencePack(handle, index, records);
            return true;
        }

        public bool TryGetFragment(string symbolId, out byte[] fragment)
        {
            if (!_index.TryGetValue(symbolId, out int recordIndex))
            {
                fragment = null;
                return false;
            }

            var record = _records[recordIndex];
            fragment = new byte[record.Length];

            int read = 0;
            while (read < record.Length)
            {
                int n = System.IO.RandomAccess.Read(_packHandle, fragment.AsSpan(read), record.Offset + read);
                if (n == 0)
                {
                    fragment = null;
                    return false;
                }
                read += n;
            }

            return true;
        }

        public void Dispose()
        {
            _packHandle.Dispose();
        }
    }
}
