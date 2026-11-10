using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FolderLock
{
    /// <summary>
    /// 오래 걸리는 작업을 백그라운드로 돌리며 진행률·처리량·남은 시간을 보여주는 모달 창. (선택) 취소 버튼.
    /// </summary>
    internal sealed class ProgressForm : Form
    {
        private readonly Label _title, _file, _stats, _eta;
        private readonly ProgressBar _bar;
        private readonly Button _cancel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly OpProgress _p;
        private readonly Func<OpProgress, object> _work;
        private readonly System.Windows.Forms.Timer _timer;
        private Exception _error;
        private object _result;
        private bool _finished;

        private ProgressForm(string title, Func<OpProgress, object> work, bool cancellable)
        {
            _work = work;
            _p = new OpProgress(_cts.Token);
            Text = "FolderLock";
            Icon = Theme.AppIcon;
            Font = Theme.Base;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;

            const int W = 460;
            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(22, 18, 22, 16) };
            _title = new Label { Text = title, Font = Theme.Bold, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            _file = new Label { Text = L.T("준비 중...", "Preparing..."), AutoSize = false, Width = W, Height = 22, AutoEllipsis = true, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 6) };
            _bar = new ProgressBar { Width = W, Height = 14, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Maximum = 1000, Margin = new Padding(0, 0, 0, 8) };
            _stats = new Label { AutoSize = false, Width = W, Height = 22, Text = "" };
            _eta = new Label { AutoSize = false, Width = W, Height = 22, Text = "", ForeColor = Theme.Muted };
            root.Controls.Add(_title);
            root.Controls.Add(_file);
            root.Controls.Add(_bar);
            root.Controls.Add(_stats);
            root.Controls.Add(_eta);

            if (cancellable)
            {
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
                _cancel = Theme.SecondaryButton(L.T("취소", "Cancel"));
                _cancel.Click += (s, e) =>
                {
                    _cancel.Enabled = false;
                    _cancel.Text = L.T("멈추는 중...", "Stopping...");
                    _cts.Cancel();
                };
                buttons.Controls.Add(_cancel);
                root.Controls.Add(buttons);
            }
            Controls.Add(root);

            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += (s, e) => UpdateView();
        }

        /// <summary>작업 실행. 끝까지 하면 true, 취소하면 false. 다른 예외는 그대로 던진다.</summary>
        public static bool Run(IWin32Window owner, string title, Action<OpProgress> work, bool cancellable = false)
        {
            using (var f = new ProgressForm(title, p => { work(p); return null; }, cancellable))
            {
                f.ShowDialog(owner);
                if (f._error is OperationCanceledException) return false;
                if (f._error != null) throw f._error;
                return true;
            }
        }

        /// <summary>결과를 돌려주는 작업 (취소 없음).</summary>
        public static T Run<T>(IWin32Window owner, string title, Func<OpProgress, T> work)
        {
            using (var f = new ProgressForm(title, p => work(p), false))
            {
                f.ShowDialog(owner);
                if (f._error != null) throw f._error;
                return (T)f._result;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer.Start();
            Task.Run(() =>
            {
                try { _result = _work(_p); }
                catch (Exception ex) { _error = ex; }
            }).ContinueWith(_ => BeginInvoke(new Action(() => { _finished = true; _timer.Stop(); Close(); })));
        }

        private void UpdateView()
        {
            if (!_p.HasTotals) return;
            long total = _p.TotalBytes, done = _p.Bytes;
            int files = _p.Files, totalFiles = _p.TotalFiles;

            if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
            double frac = total > 0 ? (double)done / total : (totalFiles > 0 ? (double)files / totalFiles : 0);
            _bar.Value = Math.Max(0, Math.Min(1000, (int)(frac * 1000)));

            if (!string.IsNullOrEmpty(_p.Current)) _file.Text = _p.Current;

            var parts = $"{files:N0} / {totalFiles:N0}" + L.T("개", " files");
            if (total > 0) parts += $"   ·   {Fmt.Size(done)} / {Fmt.Size(total)}";
            var rate = _p.BytesPerSecond;
            if (rate > 0 && total > 0) parts += $"   ·   {Fmt.Size((long)rate)}/s";
            _stats.Text = parts;

            var rem = _p.Remaining;
            _eta.Text = rem.HasValue
                ? L.T("남은 시간 ", "Time left: ") + Fmt.Duration(rem.Value) + L.T($"   (경과 {Fmt.Duration(_p.Elapsed, false)})", $"   (elapsed {Fmt.Duration(_p.Elapsed, false)})")
                : (total > 0 ? L.T("남은 시간 계산 중...", "Estimating time left...") : "");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_finished && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; // 작업 중에는 닫기 금지 (취소 버튼 사용)
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _cts.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
