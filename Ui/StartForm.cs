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
            ClientSize = new Size(560, 400);
            MinimumSize = new Size(500, 360);
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
                Text = L.T("폴더를 비밀번호로 잠급니다. USB 안에는 항상 암호화된 상태로만 저장되므로\n그냥 뽑거나 PC가 꺼져도 안전합니다.",
                           "Locks a folder with a password. Files on the USB are always stored encrypted,\nso pulling the drive out or a power-off is safe."),
                AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 10, 0, 14),
            });

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.FixedSingle, HideSelection = false,
            };
            _list.Columns.Add(L.T("금고", "Vault"), 300);
            _list.Columns.Add(L.T("위치", "Location"), 180);
            _list.DoubleClick += (s, e) => OpenSelected();
            _list.SelectedIndexChanged += (s, e) => _openBtn.Enabled = _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is string;
            _list.Resize += (s, e) => { if (_list.Columns.Count == 2) _list.Columns[0].Width = Math.Max(120, _list.ClientSize.Width - _list.Columns[1].Width - 4); };
            root.Controls.Add(_list);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 14, 0, 0) };
            _openBtn = Theme.PrimaryButton(L.T("열기", "Open"));
            _openBtn.Enabled = false;
            _openBtn.Click += (s, e) => OpenSelected();
            var otherBtn = Theme.SecondaryButton(L.T("다른 위치의 금고 열기...", "Open vault elsewhere..."));
            otherBtn.Click += (s, e) => OpenOther();
            var createBtn = Theme.SecondaryButton(L.T("폴더 잠그기 (금고 만들기)...", "Lock a folder (new vault)..."));
            createBtn.Click += (s, e) => CreateVault();
            var tips = new ToolTip();
            tips.SetToolTip(_openBtn, L.T("목록에서 선택한 금고를 엽니다", "Open the vault selected in the list"));
            tips.SetToolTip(otherBtn, L.T("목록에 없는 금고 폴더(다른 드라이브 등)를 직접 골라 엽니다", "Pick a vault folder that is not in the list (e.g. on another drive)"));
            tips.SetToolTip(createBtn, L.T("일반 폴더를 골라 새 금고로 만듭니다 (안의 파일은 암호화)", "Turn a normal folder into a new vault (its files get encrypted)"));
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
                var it = new ListViewItem(L.T("(아직 금고가 없습니다 — '폴더 잠그기'로 만드세요)", "(No vaults yet — create one with 'Lock a folder')")) { ForeColor = Theme.Muted };
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
            using (var dlg = new FolderBrowserDialog { Description = L.T("열 금고 폴더를 선택하세요.", "Select the vault folder to open."), SelectedPath = _baseDir, ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!Vault.IsVault(dlg.SelectedPath))
                {
                    if (Msg.Confirm(this, L.T("이 폴더는 아직 금고가 아닙니다.\n이 폴더를 잠그시겠습니까 (금고 만들기)?",
                                              "This folder is not a vault yet.\nDo you want to lock it (create a vault)?")))
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
                    var v = ProgressForm.Run(this, L.T("비밀번호 확인 중", "Checking password"), _ => Vault.Open(dir, pw));
                    if (!OfferEncryptLooseFiles(v)) { v.Dispose(); return; }
                    OpenedVault = v;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                catch (WrongPasswordException)
                {
                    Msg.Warn(this, L.T("비밀번호가 틀렸습니다.", "Wrong password."));
                }
                catch (Exception ex)
                {
                    Msg.Error(this, L.T("금고를 열 수 없습니다.\n\n", "Cannot open the vault.\n\n") + ex.Message);
                    return;
                }
            }
        }

        /// <summary>탐색기로 금고 폴더에 직접 넣은(평문) 파일이 있으면 암호화 제안.</summary>
        private bool OfferEncryptLooseFiles(Vault v)
        {
            List<FileInfo> plain;
            try { plain = v.FindPlainFiles(); } catch { return true; }
            if (plain.Count == 0) return true;
            long size = plain.Sum(f => f.Length);
            var ans = MessageBox.Show(this,
                L.T($"금고 폴더 안에 잠기지 않은 파일이 {plain.Count:N0}개({Fmt.Size(size)}) 있습니다.\n" +
                    "(탐색기로 직접 넣었거나, 이전 작업이 중간에 끊긴 경우)\n\n" +
                    "지금 암호화해서 금고에 넣고 원본은 지울까요?",
                    $"The vault folder contains {plain.Count:N0} unlocked file(s) ({Fmt.Size(size)}).\n" +
                    "(Copied in with Explorer, or a previous run was interrupted.)\n\n" +
                    "Encrypt them into the vault now and delete the originals?"),
                "FolderLock", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel) return false;
            if (ans == DialogResult.Yes) EncryptWithProgress(v);
            return true;
        }

        /// <summary>폴더 안 평문 파일 암호화 (취소 가능). 결과를 안내한다.</summary>
        private void EncryptWithProgress(Vault v)
        {
            try
            {
                bool done = ProgressForm.Run(this, L.T("암호화 중", "Encrypting"), p => v.EncryptPlainFilesInPlace(p), cancellable: true);
                if (!done)
                {
                    int left = 0;
                    try { left = v.FindPlainFiles().Count; } catch { }
                    Msg.Info(this, L.T($"중간에 멈췄습니다. 처리된 파일은 금고에 안전하게 들어갔고,\n남은 {left:N0}개는 원본 그대로 있습니다. 다음에 금고를 열 때 이어서 할 수 있습니다.",
                                       $"Stopped. Processed files are safely in the vault and\nthe remaining {left:N0} file(s) are untouched. You can continue the next time you open the vault."));
                }
            }
            catch (Exception ex)
            {
                Msg.Error(this, L.T("일부 파일을 암호화하지 못했습니다. 처리되지 않은 원본은 그대로 남아 있습니다.\n\n",
                                    "Some files could not be encrypted. Unprocessed originals are untouched.\n\n") + ex.Message);
            }
        }

        private void CreateVault()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = L.T("잠글 폴더를 선택하세요. 안의 파일은 암호화되고 원본은 삭제됩니다.\n(빈 폴더를 새로 만들어도 됩니다)",
                                  "Select the folder to lock. Its files will be encrypted and the originals deleted.\n(You can also create a new empty folder.)"),
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
                Msg.Warn(this, L.T("드라이브 전체나 FolderLock.exe 가 들어 있는 폴더는 금고로 만들 수 없습니다.\n하위 폴더를 선택하세요.",
                                   "A whole drive or the folder containing FolderLock.exe cannot become a vault.\nPlease pick a subfolder."));
                return;
            }
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (dir.StartsWith(sys, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            {
                Msg.Warn(this, L.T("시스템 폴더는 잠글 수 없습니다.", "System folders cannot be locked."));
                return;
            }

            List<FileInfo> plain;
            try { plain = ProgressForm.Run(this, L.T("파일 목록 확인 중", "Scanning files"), _ => Vault.FindPlainFiles(dir)); }
            catch (Exception ex) { Msg.Error(this, L.T("폴더를 읽을 수 없습니다.\n\n", "Cannot read the folder.\n\n") + ex.Message); return; }

            var name = Path.GetFileName(dir);
            if (plain.Count > 0)
            {
                long size = plain.Sum(f => f.Length);
                if (!Msg.Confirm(this, L.T(
                        $"'{name}' 폴더의 파일 {plain.Count:N0}개({Fmt.Size(size)})를 암호화하고 원본은 삭제합니다.\n\n" +
                        "• 각 파일은 암호화본 검증이 끝난 뒤에만 원본을 지웁니다.\n" +
                        "• 진행 중 취소하거나 USB가 빠져도, 처리 안 된 원본은 그대로 남습니다.\n" +
                        "• 처음이라면 중요한 파일은 다른 곳에 백업해 두고 진행하세요.\n" +
                        "• 플래시 메모리 특성상, 지운 원본이 복구 프로그램으로 일부 되살아날 수 있습니다.\n\n" +
                        "계속할까요?",
                        $"{plain.Count:N0} file(s) ({Fmt.Size(size)}) in '{name}' will be encrypted and the originals deleted.\n\n" +
                        "• Each original is deleted only after its encrypted copy has been verified.\n" +
                        "• If you cancel or the USB is pulled out, unprocessed originals stay untouched.\n" +
                        "• If this is your first time, back up important files elsewhere first.\n" +
                        "• Due to how flash memory works, deleted originals may be partly recoverable with recovery tools.\n\n" +
                        "Continue?"), danger: true))
                    return;
            }

            string pw;
            using (var pf = new PasswordForm(PasswordMode.Create, name))
            {
                if (pf.ShowDialog(this) != DialogResult.OK) return;
                pw = pf.Password;
            }

            Vault v;
            try { v = ProgressForm.Run(this, L.T("금고 만드는 중", "Creating vault"), _ => Vault.Create(dir, pw)); }
            catch (Exception ex)
            {
                Msg.Error(this, L.T("금고를 만들지 못했습니다. 원본은 그대로입니다.\n\n", "Could not create the vault. Originals are untouched.\n\n") + ex.Message);
                return;
            }
            if (plain.Count > 0) EncryptWithProgress(v);
            OpenedVault = v;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
