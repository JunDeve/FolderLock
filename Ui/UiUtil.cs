using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FolderLock
{
    internal static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentDark = Color.FromArgb(29, 78, 216);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Color Muted = Color.FromArgb(100, 116, 139);
        public static readonly Color Surface = Color.FromArgb(248, 250, 252);
        public static readonly Color Border = Color.FromArgb(226, 232, 240);

        public static Font Base => new Font("Malgun Gothic", 9.75f);
        public static Font Small => new Font("Malgun Gothic", 9f);
        public static Font Title => new Font("Malgun Gothic", 16f, FontStyle.Bold);
        public static Font Bold => new Font("Malgun Gothic", 9.75f, FontStyle.Bold);

        public static Button PrimaryButton(string text)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White,
                Padding = new Padding(14, 5, 14, 5), Margin = new Padding(6, 0, 0, 0), Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentDark;
            return b;
        }

        public static Button SecondaryButton(string text)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(12, 5, 12, 5), Margin = new Padding(6, 0, 0, 0), Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Surface;
            return b;
        }

        private static Icon _appIcon;

        /// <summary>자물쇠 아이콘 (런타임에 그림 → 별도 리소스 불필요)</summary>
        public static Icon AppIcon
        {
            get
            {
                if (_appIcon != null) return _appIcon;
                try
                {
                    using (var s = typeof(Theme).Assembly.GetManifestResourceStream("FolderLock.ico"))
                        if (s != null) return _appIcon = new Icon(s);
                }
                catch { /* 리소스 문제 시 아래 그림으로 대체 */ }
                using (var bmp = new Bitmap(64, 64))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        using (var pen = new Pen(Color.FromArgb(71, 85, 105), 7))
                            g.DrawArc(pen, 18, 6, 28, 32, 180, 180);
                        using (var pen = new Pen(Color.FromArgb(71, 85, 105), 7))
                        {
                            g.DrawLine(pen, 18, 22, 18, 30);
                            g.DrawLine(pen, 46, 22, 46, 30);
                        }
                        using (var path = RoundRect(new Rectangle(8, 28, 48, 32), 7))
                        using (var br = new SolidBrush(Accent))
                            g.FillPath(br, path);
                        using (var br = new SolidBrush(Color.White))
                        {
                            g.FillEllipse(br, 27, 36, 10, 10);
                            g.FillRectangle(br, 30, 42, 4, 10);
                        }
                    }
                    _appIcon = Icon.FromHandle(bmp.GetHicon());
                }
                return _appIcon;
            }
        }

        /// <summary>특정 크기의 앱 아이콘 이미지 (시작 화면 로고용).</summary>
        public static Image AppImage(int size)
        {
            try
            {
                using (var s = typeof(Theme).Assembly.GetManifestResourceStream("FolderLock.ico"))
                    if (s != null) using (var ic = new Icon(s, size, size)) return ic.ToBitmap();
            }
            catch { }
            return AppIcon.ToBitmap();
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal static class Msg
    {
        public static void Info(IWin32Window owner, string text) =>
            MessageBox.Show(owner, text, "FolderLock", MessageBoxButtons.OK, MessageBoxIcon.Information);

        public static void Warn(IWin32Window owner, string text) =>
            MessageBox.Show(owner, text, "FolderLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        public static void Error(IWin32Window owner, string text) =>
            MessageBox.Show(owner, text, "FolderLock", MessageBoxButtons.OK, MessageBoxIcon.Error);

        public static bool Confirm(IWin32Window owner, string text, bool danger = false) =>
            MessageBox.Show(owner, text, "FolderLock", MessageBoxButtons.YesNo,
                danger ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    internal static class Fmt
    {
        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes;
            string[] u = { "KB", "MB", "GB", "TB" };
            int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
            return (v >= 100 ? v.ToString("0") : v.ToString("0.#")) + " " + u[i];
        }
    }

    /// <summary>탐색기와 같은 파일 아이콘·종류 이름 (파일이 실제로 없어도 확장자로 조회).</summary>
    internal sealed class ShellIcons
    {
        public readonly ImageList Images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        private readonly Dictionary<string, string> _typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ShellIcons(float scale)
        {
            int s = (int)Math.Round(16 * scale);
            Images.ImageSize = new Size(s, s);
        }

        public string Key(string fileName, bool isDir)
        {
            var key = isDir ? "<dir>" : (System.IO.Path.GetExtension(fileName) is string ext && ext.Length > 0 ? ext.ToLowerInvariant() : "<none>");
            if (!Images.Images.ContainsKey(key)) Load(key, isDir);
            return key;
        }

        public string TypeName(string fileName, bool isDir)
        {
            var key = Key(fileName, isDir);
            return _typeNames.TryGetValue(key, out var n) ? n : "";
        }

        private void Load(string key, bool isDir)
        {
            var info = new Native.SHFILEINFO();
            string probe = isDir ? "folder" : ("file" + (key == "<none>" ? "" : key));
            uint attr = isDir ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
            uint flags = Native.SHGFI_ICON | Native.SHGFI_SMALLICON | Native.SHGFI_USEFILEATTRIBUTES | Native.SHGFI_TYPENAME;
            if (Images.ImageSize.Width > 20) flags = Native.SHGFI_ICON | Native.SHGFI_USEFILEATTRIBUTES | Native.SHGFI_TYPENAME; // 큰 DPI 는 32px 를 줄여 씀
            var r = Native.SHGetFileInfo(probe, attr, ref info, (uint)Marshal.SizeOf(info), flags);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                using (var ic = Icon.FromHandle(info.hIcon))
                    Images.Images.Add(key, new Bitmap(ic.ToBitmap(), Images.ImageSize));
                Native.DestroyIcon(info.hIcon);
            }
            else
            {
                Images.Images.Add(key, new Bitmap(Images.ImageSize.Width, Images.ImageSize.Height));
            }
            _typeNames[key] = info.szTypeName ?? "";
        }
    }

    internal static class Native
    {
        public const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_TYPENAME = 0x400;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        /// <summary>마지막 키보드/마우스 입력 이후 경과 시간.</summary>
        public static TimeSpan IdleTime()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
            uint now = (uint)Environment.TickCount;
            return TimeSpan.FromMilliseconds(unchecked(now - li.dwTime));
        }
    }
}
