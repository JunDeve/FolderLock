using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FolderLock
{
    /// <summary>시작 화면: USB 안의 금고 목록 + 열기 / 새로 잠그기.</summary>
    internal sealed class StartForm : Form
    {
        private readonly ListView _list;
        private readonly Button _openBtn;
        private readonly string _baseDir;

        public Vault OpenedVault { get; private set; }

        public StartForm(string baseDir)
        {
            _baseDir = baseDir;
            Text = "FolderLock";
            Icon = Theme.AppIcon;
            Font = Theme.Base;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(520, 400);
            MinimumSize = new Size(480, 360);
            BackColor = Color.White;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24, 20, 24, 18) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var header = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            header.Controls.Add(new PictureBox { Image = Theme.AppImage(48), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), Margin = new Padding(0, 0, 10, 0) });
            header.Controls.Add(new Label { Text = "FolderLock", Font = Theme.Title, AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
            root.Controls.Add(header);

            root.Controls.Add(new Label
            {
                Text = "폴더를 비밀번호로 잠급니다. USB 안에는 항상 암호화된 상태로만 저장되므로\n그냥 뽑거나 PC가 꺼져도 안전합니다.",
                AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 10, 0, 14),
            });

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.FixedSingle, HideSelection = false,
            };
            _list.Columns.Add("금고", 300);
            _list.Columns.Add("위치", 160);
            _list.DoubleClick += (s, e) => OpenSelected();
            _list.SelectedIndexChanged += (s, e) => _openBtn.Enabled = _list.SelectedItems.Count == 1;
            _list.Resize += (s, e) => { if (_list.Columns.Count == 2) _list.Columns[0].Width = Math.Max(120, _list.ClientSize.Width - _list.Columns[1].Width - 4); };
            root.Controls.Add(_list);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 14, 0, 0) };
            _openBtn = Theme.PrimaryButton("열기");
            _openBtn.Enabled = false;
            _openBtn.Click += (s, e) => OpenSelected();
            var otherBtn = Theme.SecondaryButton("다른 금고 열기...");
            otherBtn.Click += (s, e) => OpenOther();
            var createBtn = Theme.SecondaryButton("폴더 잠그기 (금고 만들기)...");
            createBtn.Click += (s, e) => CreateVault();
            buttons.Controls.Add(_openBtn);
            buttons.Controls.Add(otherBtn);
            buttons.Controls.Add(createBtn);
            root.Controls.Add(buttons);

            AcceptButton = _openBtn;
            Load += (s, e) => Reload();
        }

        private void Reload()
        {
            _list.Items.Clear();
            foreach (var dir in FindVaults())
            {
                var it = new ListViewItem(Path.GetFileName(dir)) { Tag = dir };
                it.SubItems.Add(ShortLocation(dir));
                _list.Items.Add(it);
            }
            if (_list.Items.Count == 0)
            {
                var it = new ListViewItem("(아직 금고가 없습니다 — '폴더 잠그기'로 만드세요)") { ForeColor = Theme.Muted };
                _list.Items.Add(it);
            }
            else
            {
                _list.Items[0].Selected = true;
                _list.Focus();
            }
        }

        private string ShortLocation(string dir)
        {
            var parent = Path.GetDirectoryName(dir) ?? "";
            return parent.Length > 30 ? "..." + parent.Substring(parent.Length - 28) : parent;
        }

        /// <summary>exe 가 있는 폴더와 그 드라이브 루트에서 2단계까지 금고 탐색.</summary>
        private IEnumerable<string> FindVaults()
        {
            var roots = new List<string> { _baseDir };
            var driveRoot = Path.GetPathRoot(_baseDir);
            if (!string.IsNullOrEmpty(driveRoot) && !string.Equals(driveRoot.TrimEnd('\\'), _baseDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                roots.Add(driveRoot);
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots)
            {
                if (Vault.IsVault(r)) found.Add(r.TrimEnd('\\'));
                foreach (var d in SafeDirs(r))
                {
                    if (Vault.IsVault(d)) { found.Add(d); continue; }
                    foreach (var d2 in SafeDirs(d)) if (Vault.IsVault(d2)) found.Add(d2);
                }
            }
            return found;
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try
            {
                return Directory.GetDirectories(dir).Where(d =>
                {
                    try { return (new DirectoryInfo(d).Attributes & (FileAttributes.System | FileAttributes.Hidden)) == 0; }
                    catch { return false; }
                }).ToArray();
            }
            catch { return new string[0]; }
        }

        // ------------------------------------------------------------

        private void OpenSelected()
        {
            if (_list.SelectedItems.Count != 1 || !(_list.SelectedItems[0].Tag is string dir)) return;
            OpenVault(dir);
        }

        private void OpenOther()
        {
            using (var dlg = new FolderBrowserDialog { Description = "열 금고 폴더를 선택하세요.", SelectedPath = _baseDir, ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!Vault.IsVault(dlg.SelectedPath))
                {
                    if (Msg.Confirm(this, "이 폴더는 아직 금고가 아닙니다.\n이 폴더를 잠그시겠습니까 (금고 만들기)?"))
                        CreateVaultAt(dlg.SelectedPath);
                    return;
                }
                OpenVault(dlg.SelectedPath);
            }
        }

        private void OpenVault(string dir)
        {
            var name = Path.GetFileName(dir.TrimEnd('\\'));
            while (true)
            {
                string pw;
                using (var pf = new PasswordForm(PasswordMode.Open, name))
                {
                    if (pf.ShowDialog(this) != DialogResult.OK) return;
                    pw = pf.Password;
                }
                try
                {
                    var v = ProgressForm.Run(this, "비밀번호 확인 중", _ => Vault.Open(dir, pw));
                    if (!OfferEncryptLooseFiles(v)) { v.Dispose(); return; }
                    OpenedVault = v;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                catch (WrongPasswordException)
                {
                    Msg.Warn(this, "비밀번호가 틀렸습니다.");
                }
                catch (Exception ex)
                {
                    Msg.Error(this, "금고를 열 수 없습니다.\n\n" + ex.Message);
                    return;
                }
            }
        }

        /// <summary>탐색기로 금고 폴더에 직접 넣은(평문) 파일이 있으면 암호화 제안.</summary>
        private bool OfferEncryptLooseFiles(Vault v)
        {
            List<string> plain;
            try { plain = v.FindPlainFiles(); } catch { return true; }
            if (plain.Count == 0) return true;
            long size = plain.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            var ans = MessageBox.Show(this,
                $"금고 폴더 안에 잠기지 않은 파일이 {plain.Count}개({Fmt.Size(size)}) 있습니다.\n" +
                "(탐색기로 직접 넣었거나, 이전 작업이 중간에 끊긴 경우)\n\n" +
                "지금 암호화해서 금고에 넣고 원본은 지울까요?",
                "FolderLock", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel) return false;
            if (ans == DialogResult.Yes)
            {
                try { ProgressForm.Run(this, "암호화 중", p => v.EncryptPlainFilesInPlace(p)); }
                catch (Exception ex) { Msg.Error(this, "일부 파일을 암호화하지 못했습니다. 원본은 그대로 남아 있습니다.\n\n" + ex.Message); }
            }
            return true;
        }

        private void CreateVault()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = "잠글 폴더를 선택하세요. 안의 파일은 암호화되고 원본은 삭제됩니다.\n(빈 폴더를 새로 만들어도 됩니다)",
                SelectedPath = _baseDir, ShowNewFolderButton = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                CreateVaultAt(dlg.SelectedPath);
            }
        }

        private void CreateVaultAt(string dir)
        {
            dir = Path.GetFullPath(dir).TrimEnd('\\');
            if (Vault.IsVault(dir)) { OpenVault(dir); return; }

            var exeDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
            if (Path.GetPathRoot(dir).TrimEnd('\\').Equals(dir, StringComparison.OrdinalIgnoreCase)
                || exeDir.Equals(dir, StringComparison.OrdinalIgnoreCase)
                || exeDir.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                Msg.Warn(this, "드라이브 전체나 FolderLock.exe 가 들어 있는 폴더는 금고로 만들 수 없습니다.\n하위 폴더를 선택하세요.");
                return;
            }
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (dir.StartsWith(sys, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            {
                Msg.Warn(this, "시스템 폴더는 잠글 수 없습니다.");
                return;
            }

            List<string> plain;
            try { plain = Vault.FindPlainFiles(dir); }
            catch (Exception ex) { Msg.Error(this, "폴더를 읽을 수 없습니다.\n\n" + ex.Message); return; }

            var name = Path.GetFileName(dir);
            if (plain.Count > 0)
            {
                long size = plain.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                if (!Msg.Confirm(this,
                        $"'{name}' 폴더의 파일 {plain.Count}개({Fmt.Size(size)})를 암호화하고 원본은 삭제합니다.\n\n" +
                        "• 각 파일은 암호화본 검증이 끝난 뒤에만 원본을 지웁니다.\n" +
                        "• 처음이라면 중요한 파일은 다른 곳에 백업해 두고 진행하세요.\n" +
                        "• 플래시 메모리 특성상, 지운 원본이 복구 프로그램으로 일부 되살아날 수 있습니다.\n\n" +
                        "계속할까요?", danger: true))
                    return;
            }

            string pw;
            using (var pf = new PasswordForm(PasswordMode.Create, name))
            {
                if (pf.ShowDialog(this) != DialogResult.OK) return;
                pw = pf.Password;
            }

            Vault v = null;
            try
            {
                v = ProgressForm.Run(this, "금고 만드는 중", _ => Vault.Create(dir, pw));
                if (plain.Count > 0)
                    ProgressForm.Run(this, "암호화 중", p => v.EncryptPlainFilesInPlace(p));
            }
            catch (Exception ex)
            {
                Msg.Error(this, "금고 작업 중 오류가 났습니다. 암호화되지 않은 원본은 지워지지 않았습니다.\n\n" + ex.Message);
                if (v == null) return;
            }
            OpenedVault = v;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
