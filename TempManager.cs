using System;
using System.IO;

namespace FolderLock
{
    /// <summary>
    /// 외부 프로그램(한글, 워드, 사진 뷰어 등)으로 열 때만 쓰는 PC 쪽 임시 폴더.
    ///   %TEMP%\FolderLock\{세션}\{무작위}\원래이름
    /// 세션 폴더에는 .session 잠금 파일을 열어둔다 → 실행 중인 다른 창의 임시폴더는 건드리지 않고,
    /// 강제 종료로 남은 이전 세션만 다음 실행 때 지운다.
    /// </summary>
    internal static class TempManager
    {
        private static readonly string BaseDir = Path.Combine(Path.GetTempPath(), "FolderLock");
        private static string _sessionDir;
        private static FileStream _sessionLock;

        public static int CleanupStale()
        {
            int removed = 0;
            if (!Directory.Exists(BaseDir)) return 0;
            foreach (var dir in Directory.GetDirectories(BaseDir))
            {
                if (_sessionDir != null && string.Equals(dir, _sessionDir, StringComparison.OrdinalIgnoreCase)) continue;
                var marker = Path.Combine(dir, ".session");
                try
                {
                    if (File.Exists(marker))
                    {
                        // 다른 실행 중인 창이 잡고 있으면 여기서 예외 → 건너뜀
                        using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    }
                    ForceDeleteDir(dir);
                    removed++;
                }
                catch { }
            }
            return removed;
        }

        private static string SessionDir
        {
            get
            {
                if (_sessionDir == null)
                {
                    var d = Path.Combine(BaseDir, Crypto.RandomId().Substring(0, 10));
                    Directory.CreateDirectory(d);
                    _sessionLock = new FileStream(Path.Combine(d, ".session"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    _sessionDir = d;
                }
                return _sessionDir;
            }
        }

        public static string NewFilePath(string originalName)
        {
            var d = Path.Combine(SessionDir, Crypto.RandomId().Substring(0, 8));
            Directory.CreateDirectory(d);
            return Path.Combine(d, originalName);
        }

        public static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                var parent = Path.GetDirectoryName(path);
                if (parent != null && Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                    Directory.Delete(parent);
                return true;
            }
            catch { return false; }
        }

        /// <summary>세션 종료: 임시 폴더 전체 삭제 시도. 남은 게 있으면 false (다음 실행 때 정리됨).</summary>
        public static bool EndSession()
        {
            if (_sessionDir == null) return true;
            var d = _sessionDir;
            try { _sessionLock?.Dispose(); } catch { }
            _sessionLock = null;
            _sessionDir = null;
            try { ForceDeleteDir(d); return true; }
            catch { return false; }
        }

        private static void ForceDeleteDir(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(dir, true);
        }
    }
}
