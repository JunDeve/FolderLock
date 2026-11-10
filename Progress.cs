using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FolderLock
{
    /// <summary>
    /// 오래 걸리는 작업의 진행 상황 + 취소. 작업 스레드가 값을 올리고, 진행 창이 주기적으로 읽는다.
    /// 남은 시간 = 남은 바이트 / (지금까지 처리한 바이트 / 경과 시간) → 검증·목록 저장 시간까지 자연스럽게 포함.
    /// </summary>
    internal sealed class OpProgress
    {
        private readonly CancellationToken _token;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _bytes, _totalBytes;
        private int _files, _totalFiles;
        private volatile string _current = "";

        public OpProgress(CancellationToken token = default) { _token = token; }

        public static readonly OpProgress None = new OpProgress();

        public int Files => Volatile.Read(ref _files);
        public int TotalFiles => Volatile.Read(ref _totalFiles);
        public long Bytes => Interlocked.Read(ref _bytes);
        public long TotalBytes => Interlocked.Read(ref _totalBytes);
        public string Current => _current;
        public TimeSpan Elapsed => _clock.Elapsed;
        public bool HasTotals => TotalFiles > 0 || TotalBytes > 0;

        public void Begin(int totalFiles, long totalBytes)
        {
            Volatile.Write(ref _totalFiles, totalFiles);
            Interlocked.Exchange(ref _totalBytes, totalBytes);
            Volatile.Write(ref _files, 0);
            Interlocked.Exchange(ref _bytes, 0);
            _clock.Restart();
        }

        public void StartFile(string name) { _current = name ?? ""; Check(); }
        public void FileDone() => Interlocked.Increment(ref _files);
        public void AddBytes(long n) { Interlocked.Add(ref _bytes, n); Check(); }

        public void Check() => _token.ThrowIfCancellationRequested();

        /// <summary>초당 처리 바이트 (2초 이상 지나야 의미 있음).</summary>
        public double BytesPerSecond
        {
            get
            {
                var s = _clock.Elapsed.TotalSeconds;
                return s < 0.5 ? 0 : Bytes / s;
            }
        }

        public TimeSpan? Remaining
        {
            get
            {
                var rate = BytesPerSecond;
                long total = TotalBytes, done = Bytes;
                if (rate <= 0 || total <= 0 || _clock.Elapsed.TotalSeconds < 2) return null;
                return TimeSpan.FromSeconds(Math.Max(0, total - done) / rate);
            }
        }
    }

    /// <summary>읽거나 쓴 바이트를 OpProgress 에 더하는 스트림 (취소 시 다음 블록에서 중단). 내부 스트림은 닫지 않는다.</summary>
    internal sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly OpProgress _p;

        public ProgressStream(Stream inner, OpProgress p) { _inner = inner; _p = p ?? OpProgress.None; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            if (n > 0) _p.AddBytes(n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _p.AddBytes(count);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
    }
}
