using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FolderLock
{
    /// <summary>오래 걸리는 작업을 백그라운드로 돌리며 진행률을 보여주는 모달 창.</summary>
    internal sealed class ProgressForm : Form
    {
        private readonly Label _label;
        private readonly ProgressBar _bar;
        private readonly Func<Action<int, int, string>, object> _work;
        private Exception _error;
        private object _result;
        private bool _finished;
        private long _lastUpdate;

        private ProgressForm(string title, Func<Action<int, int, string>, object> work)
        {
            _work = work;
            Text = title;
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

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(22, 18, 22, 20) };
            _label = new Label { Text = title + "...", AutoSize = false, Width = 420, Height = 22, AutoEllipsis = true, Margin = new Padding(0, 0, 0, 8) };
            _bar = new ProgressBar { Width = 420, Height = 14, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            root.Controls.Add(_label);
            root.Controls.Add(_bar);
            Controls.Add(root);
        }

        public static void Run(IWin32Window owner, string title, Action<Action<int, int, string>> work) =>
            Run<object>(owner, title, p => { work(p); return null; });

        /// <summary>작업이 예외를 던지면 그대로 다시 던진다.</summary>
        public static T Run<T>(IWin32Window owner, string title, Func<Action<int, int, string>, T> work)
        {
            using (var f = new ProgressForm(title, p => work(p)))
            {
                f.ShowDialog(owner);
                if (f._error != null) throw f._error;
                return (T)f._result;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Task.Run(() =>
            {
                try { _result = _work(Report); }
                catch (Exception ex) { _error = ex; }
            }).ContinueWith(_ => BeginInvoke(new Action(() => { _finished = true; Close(); })));
        }

        private void Report(int done, int total, string name)
        {
            long now = Environment.TickCount;
            if (done < total && now - _lastUpdate < 80) return; // UI 과부하 방지
            _lastUpdate = now;
            if (!IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (total > 0)
                {
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Maximum = total;
                    _bar.Value = Math.Min(done, total);
                    _label.Text = $"{Text}  ({done}/{total})  {name}";
                }
            }));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_finished && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; // 작업 중에는 닫기 금지
            base.OnFormClosing(e);
        }
    }
}
