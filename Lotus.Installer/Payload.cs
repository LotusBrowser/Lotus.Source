using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Lotus.Installer
{
    // The build script appends  [payload.zip][int64 length]["LOTUSPAY"]  to the
    // setup exe. Keeping the payload as a trailer (instead of an embedded
    // resource) lets Uninstall.exe be a copy of just the small exe part.
    internal static class Payload
    {
        const string Magic = "LOTUSPAY";

        public static string SelfPath => Process.GetCurrentProcess().MainModule.FileName;

        static bool TryGetRange(FileStream fs, out long start, out long length)
        {
            start = 0; length = 0;
            if (fs.Length <= 16) return false;
            var tail = new byte[16];
            fs.Seek(-16, SeekOrigin.End);
            int read = 0;
            while (read < 16) { int n = fs.Read(tail, read, 16 - read); if (n <= 0) return false; read += n; }
            if (Encoding.ASCII.GetString(tail, 8, 8) != Magic) return false;
            length = BitConverter.ToInt64(tail, 0);
            start = fs.Length - 16 - length;
            return length > 0 && start > 0;
        }

        // Opens the payload zip, or returns null if there is none.
        public static Stream Open(string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                return File.OpenRead(overridePath);

            string self = SelfPath;
            var fs = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start, length;
            if (TryGetRange(fs, out start, out length))
                return new SubStream(fs, start, length);
            fs.Dispose();

            string sibling = Path.Combine(Path.GetDirectoryName(self), "payload.zip");
            return File.Exists(sibling) ? File.OpenRead(sibling) : null;
        }

        // Size of the exe without the payload trailer (what Uninstall.exe contains).
        public static long StubLength(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long s, l;
                return TryGetRange(fs, out s, out l) ? s : fs.Length;
            }
        }
    }

    internal sealed class SubStream : Stream
    {
        readonly Stream _inner; readonly long _start, _length; long _pos;
        public SubStream(Stream inner, long start, long length) { _inner = inner; _start = start; _length = length; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get { return _pos; } set { _pos = value; } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _pos;
            if (remaining <= 0) return 0;
            if (count > remaining) count = (int)remaining;
            _inner.Position = _start + _pos;
            int n = _inner.Read(buffer, offset, count);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: _pos = offset; break;
                case SeekOrigin.Current: _pos += offset; break;
                default: _pos = _length + offset; break;
            }
            return _pos;
        }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
