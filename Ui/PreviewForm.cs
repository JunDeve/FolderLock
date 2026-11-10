using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FolderLock
{
    /// <summary>
    /// 사진·텍스트를 디스크에 풀지 않고 메모리에서 바로 보여주는 창.
    /// 텍스트는 편집 후 Ctrl+S 로 금고에 바로 암호화 저장.
    /// </summary>
    internal sealed class PreviewForm : Form
    {
        public static readonly HashSet<string> ImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico" };

        public static readonly HashSet<string> TextExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".xml", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".toml",
            ".sql", ".cs", ".py", ".js", ".ts", ".java", ".c", ".cpp", ".h", ".html", ".css", ".bat", ".cmd", ".ps1", ".sh", ".env",
        };

        public const long MaxImageBytes = 80L * 1024 * 1024;
        public const long MaxTextBytes = 8L * 1024 * 1024;

        public static bool CanPreview(Entry e)
        {
            var ext = Path.GetExtension(e.Name);
            return (ImageExt.Contains(ext) && e.Size <= MaxImageBytes) || (TextExt.Contains(ext) && e.Size <= MaxTextBytes);
        }

        private readonly MemoryStream _imageStream;
        private readonly Image _image;
        private readonly TextBox _text;
        private readonly Encoding _encoding;
        private readonly bool _bom;
        private readonly Func<byte[], bool> _save;
        private bool _dirty;
        private readonly string _name;

        public PreviewForm(Entry e, byte[] data, Func<byte[], bool> save)
        {
            _name = e.Name;
            _save = save;
            Icon = Theme.AppIcon;
            Font = Theme.Base;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(900, 680);
            ShowInTaskbar = true;
            KeyPreview = true;

            var ext = Path.GetExtension(e.Name);
            if (ImageExt.Contains(ext))
            {
                _imageStream = new MemoryStream(data, false);
                _image = Image.FromStream(_imageStream);
                ExifRotate(_image);
                var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = _image, BackColor = Color.FromArgb(30, 30, 30) };
                Controls.Add(pb);
                Text = $"{e.Name}  ({_image.Width}×{_image.Height}, {Fmt.Size(e.Size)}) — FolderLock";
                KeyDown += (s, a) => { if (a.KeyCode == Keys.Escape) Close(); };
            }
            else
            {
                _encoding = DetectEncoding(data, out _bom, out int skip);
                _text = new TextBox
                {
                    Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                    Font = new Font("Consolas", 10.5f), AcceptsTab = true, MaxLength = 0,
                    Text = _encoding.GetString(data, skip, data.Length - skip),
                };
                _text.TextChanged += (s, a) => { if (!_dirty) { _dirty = true; UpdateTitle(); } };
                var strip = new StatusStrip();
                strip.Items.Add(new ToolStripStatusLabel("Ctrl+S: 금고에 암호화 저장   |   인코딩: " + (_encoding.CodePage == 949 ? "ANSI(CP949)" : "UTF-8")));
                Controls.Add(_text);
                Controls.Add(strip);
                UpdateTitle();
                KeyDown += (s, a) =>
                {
                    if (a.Control && a.KeyCode == Keys.S) { SaveText(); a.SuppressKeyPress = true; }
                    else if (a.Control && a.KeyCode == Keys.A) { _text.SelectAll(); a.SuppressKeyPress = true; }
                };
                Shown += (s, a) => { _text.SelectionStart = 0; _text.SelectionLength = 0; };
            }
        }

        private void UpdateTitle() => Text = (_dirty ? "● " : "") + _name + " — FolderLock (메모리에서 열림)";

        private bool SaveText()
        {
            if (_text == null || !_dirty) return true;
            var pre = _bom ? _encoding.GetPreamble() : new byte[0];
            var body = _encoding.GetBytes(_text.Text);
            var all = new byte[pre.Length + body.Length];
            Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
            Buffer.BlockCopy(body, 0, all, pre.Length, body.Length);
            if (!_save(all)) return false;
            _dirty = false;
            UpdateTitle();
            return true;
        }

        /// <summary>강제로 닫을 때(잠금 등) 저장 여부 묻지 않음.</summary>
        public bool ForceClose { get; set; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty && !ForceClose && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this, "변경 내용을 금고에 저장할까요?", "FolderLock", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes && !SaveText()) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _image?.Dispose();
                _imageStream?.Dispose();
                if (_text != null) _text.Text = "";
            }
            base.Dispose(disposing);
        }

        private static Encoding DetectEncoding(byte[] d, out bool bom, out int skip)
        {
            bom = false; skip = 0;
            if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) { bom = true; skip = 3; return new UTF8Encoding(true); }
            if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) { bom = true; skip = 2; return new UnicodeEncoding(false, true); }
            try
            {
                new UTF8Encoding(false, true).GetString(d);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(949); // 한국어 Windows 메모장 ANSI
            }
        }

        private static void ExifRotate(Image img)
        {
            const int OrientationId = 0x0112;
            try
            {
                if (Array.IndexOf(img.PropertyIdList, OrientationId) < 0) return;
                var o = img.GetPropertyItem(OrientationId).Value[0];
                var r = o == 3 ? RotateFlipType.Rotate180FlipNone : o == 6 ? RotateFlipType.Rotate90FlipNone : o == 8 ? RotateFlipType.Rotate270FlipNone : RotateFlipType.RotateNoneFlipNone;
                if (r != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(r);
            }
            catch { }
        }
    }
}
