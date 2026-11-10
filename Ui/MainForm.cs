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
            ClientSize = new Size(900, 580);
            MinimumSize = new Size(640, 420);
            BackColor = Color.White;
            KeyPreview = true;

            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            _icons = new ShellIcons(scale);

            // ---------- 툴바 ----------
            var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8, 4, 8, 4), RenderMode = ToolStripRenderMode.System, BackColor = Theme.Surface };
            ToolStripButton Btn(string text, Action a, string tip = null)
            {
                var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(4, 2, 4, 2), ToolTipText = tip ?? text };
                b.Click += (s, e) => a();
                tools.Items.Add(b);
                return b;
            }
            _upBtn = Btn("↑ 위로", GoUp, "상위 폴더 (Backspace)");
            tools.Items.Add(new ToolStripSeparator());
            Btn("+ 파일 넣기", AddFilesDialog, "파일을 암호화해서 넣기 (끌어다 놓아도 됩니다)");
            Btn("+ 폴더 넣기", AddFolderDialog, "폴더째 암호화해서 넣기");
            Btn("새 폴더", NewFolder);
            tools.Items.Add(new ToolStripSeparator());
            Btn("꺼내기", ExportSelected, "선택한 항목을 복호화해서 다른 곳에 저장");
            Btn("이름 바꾸기", RenameSelected, "이름 바꾸기 (F2)");
            Btn("삭제", DeleteSelected, "금고에서 영구 삭제 (Delete)");
            var lockBtn = new ToolStripButton("잠그기") { Alignment = ToolStripItemAlignment.Right, Font = Theme.Bold, ForeColor = Theme.Accent, Padding = new Padding(8, 2, 8, 2), ToolTipText = "임시파일을 지우고 금고를 잠급니다" };
            lockBtn.Click += (s, e) => LockNow(CloseKind.Locked);
            var pwBtn = new ToolStripButton("비밀번호 변경") { Alignment = ToolStripItemAlignment.Right };
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
            _list.Columns.Add("이름", 420);
            _list.Columns.Add("수정한 날짜", 150);
            _list.Columns.Add("종류", 150);
            _list.Columns.Add("크기", 90, HorizontalAlignment.Right);
            _list.ItemActivate += (s, e) => OpenSelected();
            _list.KeyDown += ListKeyDown;
            _list.BeforeLabelEdit += (s, e) => { if (_busy) e.CancelEdit = true; };
            _list.AfterLabelEdit += AfterLabelEdit;
            _list.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
            _list.DragDrop += (s, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); };
            _list.MouseUp += (s, e) => { if (e.Button == MouseButtons.XButton1) GoUp(); };
            _list.ContextMenuStrip = BuildContextMenu();

            // ---------- 상태줄 ----------
            var status = new StatusStrip { SizingGrip = true, BackColor = Theme.Surface };
            _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _tempStatus = new ToolStripStatusLabel { ForeColor = Color.FromArgb(180, 83, 9) };
            var idle = new ToolStripStatusLabel($"{IdleLimit.TotalMinutes:0}분 동안 입력이 없으면 자동 잠금") { ForeColor = Theme.Muted };
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
                it.SubItems.Add(e.IsDir ? "파일 폴더" : _icons.TypeName(e.Name, false));
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
                ? "비어 있음 — 파일이나 폴더를 이 창으로 끌어다 놓으면 암호화되어 들어갑니다."
                : $"항목 {items.Count}개 ({Fmt.Size(size)})   ·   전체 파일 {_v.FileCount}개";
            UpdateTempStatus();
        }

        private void UpdateTempStatus()
        {
            _tempStatus.Text = _opened.Count == 0 ? "" : $"PC 임시폴더에 열린 파일 {_opened.Count}개 (잠그면 삭제)";
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
            var open = m.Items.Add("열기", null, (s, e) => OpenSelected());
            open.Font = Theme.Bold;
            var export = m.Items.Add("꺼내기...", null, (s, e) => ExportSelected());
            var rename = m.Items.Add("이름 바꾸기", null, (s, e) => RenameSelected());
            var del = m.Items.Add("삭제", null, (s, e) => DeleteSelected());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("새 폴더", null, (s, e) => NewFolder());
            m.Items.Add("파일 넣기...", null, (s, e) => AddFilesDialog());
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
            catch (Exception ex) { Msg.Error(this, "파일을 열 수 없습니다.\n\n" + ex.Message); }
        }

        private void OpenInMemory(Entry e)
        {
            var data = e.Size > 4 * 1024 * 1024 ? ProgressForm.Run(this, "복호화 중", _ => _v.ReadAll(e)) : _v.ReadAll(e);
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
                catch (Exception ex) { Msg.Error(pf, "저장하지 못했습니다.\n\n" + ex.Message); return false; }
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
                if (e.Size > 4 * 1024 * 1024) ProgressForm.Run(this, "복호화 중", _ => _v.ExtractTo(e, temp));
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
            _status.Text = $"'{e.Name}' 을(를) 열었습니다. 수정 후 저장하면 자동으로 금고에 암호화되어 반영됩니다.";
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
                        _status.Text = $"{DateTime.Now:HH:mm:ss}  '{string.Join("', '", saved)}' 변경 내용을 금고에 암호화 저장했습니다.";
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
                var r = MessageBox.Show(this,
                    "아래 파일이 아직 다른 프로그램에서 열려 있어 임시본을 지울 수 없습니다:\n\n" + names +
                    "\n\n그 프로그램에서 저장하고 닫은 뒤 [다시 시도]를 누르세요.\n" +
                    "[취소]를 누르면 지금 잠급니다. 이후 그 프로그램에서 저장한 내용은 금고에 반영되지 않고,\n" +
                    "남은 임시본은 FolderLock 을 다음에 실행할 때 지워집니다.",
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
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "금고에 넣을 파일 선택" })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
        }

        private void AddFolderDialog()
        {
            using (var dlg = new FolderBrowserDialog { Description = "금고에 넣을 폴더 선택 (폴더째 암호화)", ShowNewFolderButton = false })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dlg.SelectedPath });
        }

        private void AddPaths(string[] paths)
        {
            if (_busy || paths == null || paths.Length == 0) return;
            var root = _v.Root.TrimEnd('\\') + "\\";
            if (paths.Any(p => Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(p).TrimEnd('\\'), _v.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                Msg.Warn(this, "금고 폴더 자체의 파일은 넣을 수 없습니다.");
                return;
            }
            RunBusy("암호화해서 넣는 중", p => _v.AddPaths(paths, _folder, p));
            RefreshList(Vault.Combine(_folder, Path.GetFileName(paths[0].TrimEnd('\\'))));
            _status.Text = $"{paths.Length}개 항목을 암호화해서 넣었습니다. 원본은 그대로 남아 있습니다.";
        }

        private void NewFolder()
        {
            if (_busy) return;
            try
            {
                var path = _v.CreateFolder(_folder, "새 폴더");
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
            if (sel.Count == 1 && !sel[0].IsDir)
            {
                using (var dlg = new SaveFileDialog { FileName = sel[0].Name, Title = "꺼낼 위치 (복호화된 파일이 저장됩니다)" })
                {
                    var ext = Path.GetExtension(sel[0].Name);
                    dlg.Filter = (ext.Length > 0 ? $"{ext} 파일|*{ext}|" : "") + "모든 파일|*.*";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    RunBusy("꺼내는 중", _ => _v.ExtractTo(sel[0], dlg.FileName));
                    _status.Text = "꺼냈습니다: " + dlg.FileName;
                }
                return;
            }
            using (var dlg = new FolderBrowserDialog { Description = "꺼낼 위치를 선택하세요 (복호화된 파일이 저장됩니다)", ShowNewFolderButton = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var dest = dlg.SelectedPath;
                var clash = sel.Where(x => File.Exists(Path.Combine(dest, x.Name)) || Directory.Exists(Path.Combine(dest, x.Name))).ToList();
                if (clash.Count > 0 && !Msg.Confirm(this, $"꺼낼 위치에 같은 이름이 {clash.Count}개 있습니다. 덮어쓸까요?")) return;
                RunBusy("꺼내는 중", p =>
                {
                    int i = 0;
                    foreach (var x in sel)
                    {
                        p(i++, sel.Count, x.Name);
                        if (x.IsDir) _v.ExtractFolderTo(x, dest);
                        else _v.ExtractTo(x, Path.Combine(dest, x.Name));
                    }
                });
                _status.Text = $"{sel.Count}개 항목을 꺼냈습니다: {dest}";
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
            if (!Vault.IsValidName(newName)) { Msg.Warn(this, "이름에 다음 문자는 쓸 수 없습니다:  \\ / : * ? \" < > |"); return; }
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
            var what = sel.Count == 1 ? $"'{sel[0].Name}'" + (sel[0].IsDir ? " 폴더와 그 안의 모든 파일" : "") : $"선택한 {sel.Count}개 항목";
            if (!Msg.Confirm(this, what + "을(를) 금고에서 영구 삭제합니다.\n휴지통으로 가지 않으며 되돌릴 수 없습니다.\n\n삭제할까요?", danger: true)) return;
            RunBusy("삭제 중", _ => _v.Delete(sel));
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
                    ProgressForm.Run(this, "비밀번호 바꾸는 중", _ => _v.ChangePassword(pf.CurrentPassword, pf.Password));
                    Msg.Info(this, "비밀번호를 바꿨습니다. 다음부터 새 비밀번호로 여세요.");
                }
                catch (WrongPasswordException) { Msg.Warn(this, "현재 비밀번호가 틀렸습니다. 바뀌지 않았습니다."); }
                catch (Exception ex) { Msg.Error(this, "비밀번호를 바꾸지 못했습니다.\n\n" + ex.Message); }
            }
        }

        private void RunBusy(string title, Action<Action<int, int, string>> work)
        {
            _busy = true;
            try
            {
                try { _syncTask?.Wait(); } catch { }
                ProgressForm.Run(this, title, work);
            }
            catch (Exception ex)
            {
                if (!VaultPresent()) return; // USB 제거 → 다음 tick 에서 긴급 잠금
                Msg.Error(this, title.Replace(" 중", "") + " 작업 중 오류가 났습니다.\n\n" + ex.Message);
            }
            finally { _busy = false; }
        }
    }
}
