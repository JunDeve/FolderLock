using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FolderLock
{
    internal enum CloseKind { Exit, Locked, Removed, IdleLocked }

    /// <summary>금고 전용 탐색기.</summary>
    internal sealed class MainForm : Form
    {
        private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
        private const long ShowProgressAbove = 4L * 1024 * 1024; // 이보다 큰 파일을 열 때만 진행 창

        private sealed class Opened
        {
            public string VaultPath;
            public string TempPath;
            public DateTime LastWrite;
            public long Length;
        }

        private readonly Vault _v;
        private string _folder = "";
        private readonly ListView _list;
        private readonly Label _pathLabel;
        private readonly ToolStripStatusLabel _status, _tempStatus;
        private readonly ToolStripButton _upBtn;
        private readonly ShellIcons _icons;
        private readonly Timer _timer;
        private readonly List<Opened> _opened = new List<Opened>();
        private Task _syncTask;
        private bool _closing;
        private bool _busy;

        public CloseKind Result { get; private set; } = CloseKind.Exit;

        public MainForm(Vault vault)
        {
            _v = vault;
            Text = vault.DisplayName + " — FolderLock";
            Icon = Theme.AppIcon;
            Font = Theme.Base;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(920, 580);
            MinimumSize = new Size(660, 420);
            BackColor = Color.White;
            KeyPreview = true;

            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            _icons = new ShellIcons(scale);

            // ---------- 툴바 ----------
            var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8, 4, 8, 4), RenderMode = ToolStripRenderMode.System, BackColor = Theme.Surface };
            ToolStripButton Btn(string text, Action a, string tip)
            {
                var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(4, 2, 4, 2), ToolTipText = tip };
                b.Click += (s, e) => a();
                tools.Items.Add(b);
                return b;
            }
            _upBtn = Btn(L.T("↑ 위로", "↑ Up"), GoUp, L.T("상위 폴더 (Backspace)", "Parent folder (Backspace)"));
            tools.Items.Add(new ToolStripSeparator());
            Btn(L.T("+ 파일 넣기", "+ Add files"), AddFilesDialog, L.T("파일을 암호화해서 넣기 (끌어다 놓아도 됩니다)", "Encrypt files into the vault (drag and drop works too)"));
            Btn(L.T("+ 폴더 넣기", "+ Add folder"), AddFolderDialog, L.T("폴더째 암호화해서 넣기", "Encrypt a whole folder into the vault"));
            Btn(L.T("새 폴더", "New folder"), NewFolder, L.T("새 폴더", "New folder"));
            tools.Items.Add(new ToolStripSeparator());
            Btn(L.T("꺼내기", "Export"), ExportSelected, L.T("선택한 항목을 복호화해서 다른 곳에 저장", "Decrypt the selected items and save them elsewhere"));
            Btn(L.T("이름 바꾸기", "Rename"), RenameSelected, L.T("이름 바꾸기 (F2)", "Rename (F2)"));
            Btn(L.T("삭제", "Delete"), DeleteSelected, L.T("금고에서 영구 삭제 (Delete)", "Permanently delete from the vault (Delete)"));
            var lockBtn = new ToolStripButton(L.T("잠그기", "Lock"))
            {
                Alignment = ToolStripItemAlignment.Right, Font = Theme.Bold, ForeColor = Theme.Accent, Padding = new Padding(8, 2, 8, 2),
                ToolTipText = L.T("임시파일을 지우고 금고를 잠급니다", "Delete temp files and lock the vault"),
            };
            lockBtn.Click += (s, e) => LockNow(CloseKind.Locked);
            var pwBtn = new ToolStripButton(L.T("비밀번호 변경", "Change password")) { Alignment = ToolStripItemAlignment.Right };
            pwBtn.Click += (s, e) => ChangePassword();
            tools.Items.Add(lockBtn);
            tools.Items.Add(pwBtn);

            // ---------- 경로 표시 ----------
            var pathPanel = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(14, 8, 14, 4), BackColor = Color.White };
            _pathLabel = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Font = Theme.Bold };
            pathPanel.Controls.Add(_pathLabel);

            // ---------- 목록 ----------
            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, LabelEdit = true,
                HideSelection = false, AllowDrop = true, BorderStyle = BorderStyle.None, SmallImageList = _icons.Images,
            };
            _list.Columns.Add(L.T("이름", "Name"), 420);
            _list.Columns.Add(L.T("수정한 날짜", "Date modified"), 150);
            _list.Columns.Add(L.T("종류", "Type"), 150);
            _list.Columns.Add(L.T("크기", "Size"), 90, HorizontalAlignment.Right);
            _list.ItemActivate += (s, e) => OpenSelected();
            _list.KeyDown += ListKeyDown;
            _list.BeforeLabelEdit += (s, e) => { if (_busy) e.CancelEdit = true; };
            _list.AfterLabelEdit += AfterLabelEdit;
            _list.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
            _list.DragDrop += (s, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) BeginInvoke(new Action(() => AddPaths(paths))); };
            _list.MouseUp += (s, e) => { if (e.Button == MouseButtons.XButton1) GoUp(); };
            _list.ContextMenuStrip = BuildContextMenu();

            // ---------- 상태줄 ----------
            var status = new StatusStrip { SizingGrip = true, BackColor = Theme.Surface };
            _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _tempStatus = new ToolStripStatusLabel { ForeColor = Color.FromArgb(180, 83, 9) };
            var idle = new ToolStripStatusLabel(L.T($"{IdleLimit.TotalMinutes:0}분 동안 입력이 없으면 자동 잠금", $"Auto-locks after {IdleLimit.TotalMinutes:0} min of inactivity")) { ForeColor = Theme.Muted };
            status.Items.AddRange(new ToolStripItem[] { _status, _tempStatus, idle });

            Controls.Add(_list);
            Controls.Add(pathPanel);
            Controls.Add(tools);
            Controls.Add(status);

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += Tick;
            Load += (s, e) => { RefreshList(); _timer.Start(); };
        }

        // ======================= 목록 =======================

        private void RefreshList(string selectPath = null)
        {
            if (!_v.FolderExists(_folder)) _folder = "";
            _list.BeginUpdate();
            _list.Items.Clear();
            var items = _v.List(_folder);
            foreach (var e in items)
            {
                var it = new ListViewItem(e.Name, _icons.Key(e.Name, e.IsDir)) { Tag = e };
                it.SubItems.Add(e.IsDir ? "" : e.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                it.SubItems.Add(e.IsDir ? L.T("파일 폴더", "File folder") : _icons.TypeName(e.Name, false));
                it.SubItems.Add(e.IsDir ? "" : Fmt.Size(e.Size));
                _list.Items.Add(it);
                if (selectPath != null && string.Equals(e.Path, selectPath, StringComparison.OrdinalIgnoreCase))
                {
                    it.Selected = true;
                    it.Focused = true;
                }
            }
            _list.EndUpdate();
            if (_list.SelectedItems.Count > 0) _list.SelectedItems[0].EnsureVisible();

            var crumbs = new List<string> { _v.DisplayName };
            if (_folder.Length > 0) crumbs.AddRange(_folder.Split('/'));
            _pathLabel.Text = string.Join("  ›  ", crumbs);
            _upBtn.Enabled = _folder.Length > 0;

            long size = items.Where(x => !x.IsDir).Sum(x => x.Size);
            _status.Text = items.Count == 0
                ? L.T("비어 있음 — 파일이나 폴더를 이 창으로 끌어다 놓으면 암호화되어 들어갑니다.", "Empty — drag files or folders onto this window to encrypt them into the vault.")
                : L.T($"항목 {items.Count:N0}개 ({Fmt.Size(size)})   ·   전체 파일 {_v.FileCount:N0}개",
                      $"{items.Count:N0} item(s) ({Fmt.Size(size)})   ·   {_v.FileCount:N0} file(s) in vault");
            UpdateTempStatus();
        }

        private void UpdateTempStatus()
        {
            _tempStatus.Text = _opened.Count == 0 ? ""
                : L.T($"PC 임시폴더에 열린 파일 {_opened.Count}개 (잠그면 삭제)", $"{_opened.Count} file(s) open in PC temp folder (deleted on lock)");
        }

        private List<Entry> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag).ToList();

        private void Navigate(string folder, string select = null)
        {
            _folder = folder;
            RefreshList(select);
        }

        private void GoUp()
        {
            if (_folder.Length == 0) return;
            var from = _folder;
            Navigate(Vault.ParentOf(_folder), from);
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var m = new ContextMenuStrip();
            var open = m.Items.Add(L.T("열기", "Open"), null, (s, e) => OpenSelected());
            open.Font = Theme.Bold;
            var export = m.Items.Add(L.T("꺼내기...", "Export..."), null, (s, e) => ExportSelected());
            var rename = m.Items.Add(L.T("이름 바꾸기", "Rename"), null, (s, e) => RenameSelected());
            var del = m.Items.Add(L.T("삭제", "Delete"), null, (s, e) => DeleteSelected());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(L.T("새 폴더", "New folder"), null, (s, e) => NewFolder());
            m.Items.Add(L.T("파일 넣기...", "Add files..."), null, (s, e) => AddFilesDialog());
            m.Opening += (s, e) =>
            {
                int n = _list.SelectedItems.Count;
                open.Enabled = n == 1;
                export.Enabled = del.Enabled = n > 0;
                rename.Enabled = n == 1;
            };
            return m;
        }

        private void ListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Back || (e.Alt && e.KeyCode == Keys.Left)) { GoUp(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { RefreshList(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem i in _list.Items) i.Selected = true; e.Handled = true; }
        }

        // ======================= 열기 =======================

        private void OpenSelected()
        {
            if (_busy || _list.SelectedItems.Count == 0) return;
            var e = (Entry)_list.SelectedItems[0].Tag;
            if (e.IsDir) { Navigate(e.Path); return; }
            try
            {
                if (PreviewForm.CanPreview(e)) OpenInMemory(e);
                else OpenExternal(e);
            }
            catch (Exception ex) { Msg.Error(this, L.T("파일을 열 수 없습니다.\n\n", "Cannot open the file.\n\n") + ex.Message); }
        }

        private void OpenInMemory(Entry e)
        {
            var data = e.Size > ShowProgressAbove
                ? ProgressForm.Run(this, L.T("복호화 중", "Decrypting"), p => { p.Begin(1, e.Size); return _v.ReadAll(e, p); })
                : _v.ReadAll(e);
            var path = e.Path;
            PreviewForm pf = null;
            pf = new PreviewForm(e, data, bytes =>
            {
                try
                {
                    _v.ReplaceContent(path, bytes);
                    RefreshList(path);
                    return true;
                }
                catch (Exception ex) { Msg.Error(pf, L.T("저장하지 못했습니다.\n\n", "Could not save.\n\n") + ex.Message); return false; }
            });
            Array.Clear(data, 0, data.Length);
            pf.Owner = this;
            pf.Show();
        }

        private void OpenExternal(Entry e)
        {
            var existing = _opened.FirstOrDefault(o => string.Equals(o.VaultPath, e.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(o.TempPath));
            string temp;
            if (existing != null)
            {
                temp = existing.TempPath;
            }
            else
            {
                temp = TempManager.NewFilePath(e.Name);
                if (e.Size > ShowProgressAbove)
                {
                    if (!ProgressForm.Run(this, L.T("복호화 중", "Decrypting"), p => { p.Begin(1, e.Size); _v.ExtractTo(e, temp, p); }, cancellable: true))
                    {
                        TempManager.TryDelete(temp);
                        return;
                    }
                }
                else _v.ExtractTo(e, temp);
                var fi = new FileInfo(temp);
                _opened.Add(new Opened { VaultPath = e.Path, TempPath = temp, LastWrite = fi.LastWriteTimeUtc, Length = fi.Length });
                UpdateTempStatus();
            }
            try
            {
                Process.Start(new ProcessStartInfo(temp) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                // 연결된 프로그램이 없음 → "연결 프로그램 선택" 창
                Process.Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL \"" + temp + "\"");
            }
            _status.Text = L.T($"'{e.Name}' 을(를) 열었습니다. 수정 후 저장하면 자동으로 금고에 암호화되어 반영됩니다.",
                               $"Opened '{e.Name}'. When you save changes, they are encrypted back into the vault automatically.");
        }

        // ======================= 주기 점검: USB 제거 / 자동 잠금 / 임시파일 반영 =======================

        private bool VaultPresent()
        {
            try { return Directory.Exists(_v.Root) && File.Exists(Path.Combine(_v.Root, Vault.LockFile)); }
            catch { return false; }
        }

        private void Tick(object sender, EventArgs e)
        {
            if (_closing || _busy) return; // 작업 중(진행 창 표시 중)에는 작업이 끝난 뒤 다음 tick 에서 처리
            if (!VaultPresent()) { EmergencyLock(); return; }
            if (Native.IdleTime() > IdleLimit) { LockNow(CloseKind.IdleLocked, interactive: false); return; }
            if (_syncTask == null || _syncTask.IsCompleted) StartBackgroundSync();
        }

        private List<Opened> ChangedTemps()
        {
            var changed = new List<Opened>();
            foreach (var o in _opened)
            {
                try
                {
                    var fi = new FileInfo(o.TempPath);
                    if (!fi.Exists) continue;
                    if (fi.LastWriteTimeUtc != o.LastWrite || fi.Length != o.Length) changed.Add(o);
                }
                catch { }
            }
            return changed;
        }

        private void StartBackgroundSync()
        {
            var changed = ChangedTemps();
            if (changed.Count == 0) return;
            _syncTask = Task.Run(() => SyncTemps(changed)).ContinueWith(t =>
            {
                var saved = t.Result;
                if (saved.Count > 0 && !_closing && IsHandleCreated)
                    BeginInvoke(new Action(() =>
                    {
                        if (_closing) return;
                        RefreshList();
                        var names = string.Join("', '", saved);
                        _status.Text = $"{DateTime.Now:HH:mm:ss}  " + L.T($"'{names}' 변경 내용을 금고에 암호화 저장했습니다.", $"Saved changes to '{names}' into the vault (encrypted).");
                    }));
            });
        }

        /// <summary>바뀐 임시파일을 금고에 다시 암호화. 다른 프로그램이 쓰는 중이면 다음 기회에.</summary>
        private List<string> SyncTemps(List<Opened> changed)
        {
            var saved = new List<string>();
            foreach (var o in changed)
            {
                try
                {
                    var fi = new FileInfo(o.TempPath);
                    var w = fi.LastWriteTimeUtc;
                    var len = fi.Length;
                    if (_v.Get(o.VaultPath) == null) continue; // 금고에서 삭제됨
                    _v.ReplaceContent(o.VaultPath, o.TempPath);
                    o.LastWrite = w;
                    o.Length = len;
                    saved.Add(Vault.NameOf(o.VaultPath));
                }
                catch (IOException) { }               // 저장 중/잠김 → 다음 tick 재시도
                catch (UnauthorizedAccessException) { }
            }
            return saved;
        }

        /// <summary>잠그기 전 정리. false = 사용자가 취소.</summary>
        private bool PrepareLock(bool interactive)
        {
            // 1) 미리보기 창 닫기
            foreach (var f in OwnedForms.OfType<PreviewForm>().ToList())
            {
                if (!interactive) f.ForceClose = true;
                f.Close();
                if (!f.IsDisposed && f.Visible) return false; // 저장 여부에서 취소
            }

            // 2) 진행 중인 반영 끝내고 마지막 변경분 반영
            try { _syncTask?.Wait(); } catch { }
            SyncTemps(ChangedTemps());

            // 3) 임시파일 삭제 (다른 프로그램이 잡고 있으면 안내)
            while (true)
            {
                var stuck = _opened.Where(o => !TempManager.TryDelete(o.TempPath)).ToList();
                _opened.Clear();
                _opened.AddRange(stuck);
                if (stuck.Count == 0 || !interactive) break;

                var names = string.Join("\n", stuck.Select(o => "  • " + Vault.NameOf(o.VaultPath)));
                var r = MessageBox.Show(this, L.T(
                    "아래 파일이 아직 다른 프로그램에서 열려 있어 임시본을 지울 수 없습니다:\n\n" + names +
                    "\n\n그 프로그램에서 저장하고 닫은 뒤 [다시 시도]를 누르세요.\n" +
                    "[취소]를 누르면 지금 잠급니다. 이후 그 프로그램에서 저장한 내용은 금고에 반영되지 않고,\n" +
                    "남은 임시본은 FolderLock 을 다음에 실행할 때 지워집니다.",
                    "These files are still open in another program, so their temp copies cannot be deleted:\n\n" + names +
                    "\n\nSave and close them in that program, then press [Retry].\n" +
                    "[Cancel] locks now. Anything saved in that program afterwards will NOT reach the vault,\n" +
                    "and the leftover temp copies are deleted the next time FolderLock runs."),
                    "FolderLock", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.Retry) break;
                SyncTemps(ChangedTemps());
            }
            UpdateTempStatus();
            return true;
        }

        private void LockNow(CloseKind kind, bool interactive = true)
        {
            if (_closing || _busy) return;
            _timer.Stop();
            if (!PrepareLock(interactive)) { _timer.Start(); return; }
            _closing = true;
            Result = kind;
            Close();
        }

        /// <summary>USB 가 뽑힘: 반영은 불가능하므로 PC 쪽 흔적만 지우고 즉시 종료.</summary>
        private void EmergencyLock()
        {
            _closing = true;
            _timer.Stop();
            foreach (var f in OwnedForms.OfType<PreviewForm>().ToList()) { f.ForceClose = true; f.Close(); }
            foreach (var o in _opened) TempManager.TryDelete(o.TempPath);
            _opened.Clear();
            Result = CloseKind.Removed;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closing)
            {
                if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
                _timer.Stop();
                bool interactive = e.CloseReason == CloseReason.UserClosing;
                if (!PrepareLock(interactive)) { e.Cancel = true; _timer.Start(); return; }
                _closing = true;
                Result = CloseKind.Exit;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _icons.Images.Dispose(); }
            base.Dispose(disposing);
        }

        // ======================= 넣기 =======================

        private void AddFilesDialog()
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("금고에 넣을 파일 선택", "Select files to add to the vault") })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
        }

        private void AddFolderDialog()
        {
            using (var dlg = new FolderBrowserDialog { Description = L.T("금고에 넣을 폴더 선택 (폴더째 암호화)", "Select a folder to add (encrypted as a whole)"), ShowNewFolderButton = false })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dlg.SelectedPath });
        }

        private void AddPaths(string[] paths)
        {
            if (_busy || paths == null || paths.Length == 0) return;
            var root = _v.Root.TrimEnd('\\') + "\\";
            if (paths.Any(p => Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(p).TrimEnd('\\'), _v.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                Msg.Warn(this, L.T("금고 폴더 자체의 파일은 넣을 수 없습니다.", "Files inside the vault folder itself cannot be added."));
                return;
            }
            var r = RunBusy(L.T("암호화해서 넣는 중", "Encrypting into vault"), p => _v.AddPaths(paths, _folder, p), cancellable: true);
            RefreshList(Vault.Combine(_folder, Path.GetFileName(paths[0].TrimEnd('\\'))));
            if (r == BusyResult.Done)
                _status.Text = L.T($"{paths.Length}개 항목을 암호화해서 넣었습니다. 원본은 그대로 남아 있습니다.", $"Encrypted {paths.Length} item(s) into the vault. The originals were left as they are.");
            else if (r == BusyResult.Canceled)
                _status.Text = L.T("넣기를 중간에 멈췄습니다. 그때까지 넣은 파일은 금고에 있습니다.", "Adding was stopped. Files added up to that point are in the vault.");
        }

        private void NewFolder()
        {
            if (_busy) return;
            try
            {
                var path = _v.CreateFolder(_folder, L.T("새 폴더", "New folder"));
                RefreshList(path);
                if (_list.SelectedItems.Count == 1) _list.SelectedItems[0].BeginEdit();
            }
            catch (Exception ex) { Msg.Error(this, ex.Message); }
        }

        // ======================= 꺼내기 / 이름 / 삭제 =======================

        private void ExportSelected()
        {
            var sel = Selected();
            if (_busy || sel.Count == 0) return;
            var exportTitle = L.T("꺼내는 중", "Exporting");
            if (sel.Count == 1 && !sel[0].IsDir)
            {
                using (var dlg = new SaveFileDialog { FileName = sel[0].Name, Title = L.T("꺼낼 위치 (복호화된 파일이 저장됩니다)", "Export to (the decrypted file will be saved here)") })
                {
                    var ext = Path.GetExtension(sel[0].Name);
                    dlg.Filter = (ext.Length > 0 ? L.T($"{ext} 파일|*{ext}|", $"{ext} files|*{ext}|") : "") + L.T("모든 파일|*.*", "All files|*.*");
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var target = dlg.FileName;
                    if (RunBusy(exportTitle, p => { p.Begin(1, sel[0].Size); _v.ExtractTo(sel[0], target, p); p.FileDone(); }, cancellable: true) == BusyResult.Done)
                        _status.Text = L.T("꺼냈습니다: ", "Exported: ") + target;
                }
                return;
            }
            using (var dlg = new FolderBrowserDialog { Description = L.T("꺼낼 위치를 선택하세요 (복호화된 파일이 저장됩니다)", "Choose where to export (decrypted files will be saved there)"), ShowNewFolderButton = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var dest = dlg.SelectedPath;
                var clash = sel.Where(x => File.Exists(Path.Combine(dest, x.Name)) || Directory.Exists(Path.Combine(dest, x.Name))).ToList();
                if (clash.Count > 0 && !Msg.Confirm(this, L.T($"꺼낼 위치에 같은 이름이 {clash.Count}개 있습니다. 덮어쓸까요?", $"{clash.Count} item(s) with the same name already exist there. Overwrite?"))) return;
                var r = RunBusy(exportTitle, p => _v.ExtractMany(sel, dest, p), cancellable: true);
                if (r == BusyResult.Done) _status.Text = L.T($"{sel.Count}개 항목을 꺼냈습니다: {dest}", $"Exported {sel.Count} item(s) to {dest}");
                else if (r == BusyResult.Canceled) _status.Text = L.T("꺼내기를 중간에 멈췄습니다.", "Export was stopped.");
            }
        }

        private void RenameSelected()
        {
            if (_busy || _list.SelectedItems.Count != 1) return;
            _list.SelectedItems[0].BeginEdit();
        }

        private void AfterLabelEdit(object sender, LabelEditEventArgs e)
        {
            e.CancelEdit = true; // 목록은 직접 새로 그린다
            if (e.Label == null) return;
            var entry = (Entry)_list.Items[e.Item].Tag;
            var newName = e.Label.Trim();
            if (newName == entry.Name) return;
            if (!Vault.IsValidName(newName)) { Msg.Warn(this, L.T("이름에 다음 문자는 쓸 수 없습니다:  \\ / : * ? \" < > |", "Names cannot contain:  \\ / : * ? \" < > |")); return; }
            var oldPath = entry.Path;
            BeginInvoke(new Action(() =>
            {
                try
                {
                    _v.Rename(entry, newName);
                    var newPath = Vault.Combine(Vault.ParentOf(oldPath), newName);
                    foreach (var o in _opened)
                    {
                        if (string.Equals(o.VaultPath, oldPath, StringComparison.OrdinalIgnoreCase)) o.VaultPath = newPath;
                        else if (o.VaultPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)) o.VaultPath = newPath + o.VaultPath.Substring(oldPath.Length);
                    }
                    RefreshList(newPath);
                }
                catch (Exception ex) { Msg.Error(this, ex.Message); }
            }));
        }

        private void DeleteSelected()
        {
            var sel = Selected();
            if (_busy || sel.Count == 0) return;
            string what = sel.Count == 1
                ? (sel[0].IsDir ? L.T($"'{sel[0].Name}' 폴더와 그 안의 모든 파일", $"the folder '{sel[0].Name}' and everything in it") : $"'{sel[0].Name}'")
                : L.T($"선택한 {sel.Count}개 항목", $"the {sel.Count} selected items");
            if (!Msg.Confirm(this, L.T(what + "을(를) 금고에서 영구 삭제합니다.\n휴지통으로 가지 않으며 되돌릴 수 없습니다.\n\n삭제할까요?",
                                       "This permanently deletes " + what + " from the vault.\nIt does not go to the Recycle Bin and cannot be undone.\n\nDelete?"), danger: true)) return;
            RunBusy(L.T("삭제 중", "Deleting"), p => _v.Delete(sel, p));
            foreach (var o in _opened.Where(o => sel.Any(x => string.Equals(o.VaultPath, x.Path, StringComparison.OrdinalIgnoreCase)
                                                              || o.VaultPath.StartsWith(x.Path + "/", StringComparison.OrdinalIgnoreCase))).ToList())
            {
                TempManager.TryDelete(o.TempPath);
                _opened.Remove(o);
            }
            RefreshList();
        }

        private void ChangePassword()
        {
            if (_busy) return;
            using (var pf = new PasswordForm(PasswordMode.Change, _v.DisplayName))
            {
                if (pf.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    ProgressForm.Run(this, L.T("비밀번호 바꾸는 중", "Changing password"), _ => _v.ChangePassword(pf.CurrentPassword, pf.Password));
                    Msg.Info(this, L.T("비밀번호를 바꿨습니다. 다음부터 새 비밀번호로 여세요.", "Password changed. Use the new password from now on."));
                }
                catch (WrongPasswordException) { Msg.Warn(this, L.T("현재 비밀번호가 틀렸습니다. 바뀌지 않았습니다.", "The current password is wrong. Nothing was changed.")); }
                catch (Exception ex) { Msg.Error(this, L.T("비밀번호를 바꾸지 못했습니다.\n\n", "Could not change the password.\n\n") + ex.Message); }
            }
        }

        private enum BusyResult { Done, Canceled, Failed }

        private BusyResult RunBusy(string title, Action<OpProgress> work, bool cancellable = false)
        {
            _busy = true;
            try
            {
                try { _syncTask?.Wait(); } catch { }
                return ProgressForm.Run(this, title, work, cancellable) ? BusyResult.Done : BusyResult.Canceled;
            }
            catch (Exception ex)
            {
                if (VaultPresent()) // USB 가 빠진 경우는 다음 tick 에서 긴급 잠금
                    Msg.Error(this, L.T("작업 중 오류가 났습니다.\n\n", "An error occurred.\n\n") + ex.Message);
                return BusyResult.Failed;
            }
            finally { _busy = false; }
        }
    }
}
