using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FolderLock
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 언어 강제: FolderLock.exe --lang=en  /  --lang=ko  (기본: Windows 표시 언어)
            foreach (var a in args)
            {
                if (a.Equals("--lang=en", StringComparison.OrdinalIgnoreCase)) L.Ko = false;
                else if (a.Equals("--lang=ko", StringComparison.OrdinalIgnoreCase)) L.Ko = true;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(L.T("예기치 못한 오류가 났습니다.\n\n", "An unexpected error occurred.\n\n") + e.Exception.Message, "FolderLock", MessageBoxButtons.OK, MessageBoxIcon.Error);

            // 이전에 강제 종료돼서 PC 에 남은 임시 해독본 정리
            TempManager.CleanupStale();

            var baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            try
            {
                while (true)
                {
                    Vault vault;
                    using (var start = new StartForm(baseDir))
                    {
                        if (start.ShowDialog() != DialogResult.OK || start.OpenedVault == null) break;
                        vault = start.OpenedVault;
                    }

                    // 같은 금고를 두 창에서 동시에 열면 목록이 서로 덮어써지므로 막는다
                    using (var mutex = new Mutex(false, "Local\\FolderLock_" + Hash(vault.Root.ToUpperInvariant())))
                    {
                        bool owned;
                        try { owned = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { owned = true; }
                        if (!owned)
                        {
                            vault.Dispose();
                            MessageBox.Show(L.T("이 금고는 이미 다른 FolderLock 창에서 열려 있습니다.", "This vault is already open in another FolderLock window."), "FolderLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            continue;
                        }

                        CloseKind result;
                        try
                        {
                            using (var main = new MainForm(vault))
                            {
                                main.ShowDialog();
                                result = main.Result;
                            }
                        }
                        finally
                        {
                            vault.Dispose();
                            TempManager.EndSession();
                            mutex.ReleaseMutex();
                        }

                        if (result == CloseKind.Removed)
                            MessageBox.Show(L.T("USB(금고 폴더)가 분리되어 금고를 잠갔습니다.\nPC에 남아 있던 임시 해독본도 지웠습니다.", "The USB drive (vault folder) was removed, so the vault was locked.\nDecrypted temp copies on this PC were deleted."), "FolderLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        else if (result == CloseKind.IdleLocked)
                            MessageBox.Show(L.T("오랫동안 사용하지 않아 금고를 자동으로 잠갔습니다.", "The vault was locked automatically after a period of inactivity."), "FolderLock", MessageBoxButtons.OK, MessageBoxIcon.Information);

                        if (result == CloseKind.Exit) break;
                    }
                }
            }
            finally
            {
                TempManager.EndSession();
            }
        }

        private static string Hash(string s)
        {
            using (var sha = SHA256.Create())
                return Crypto.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Substring(0, 32);
        }
    }
}
