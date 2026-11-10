using System;
using System.Drawing;
using System.Windows.Forms;

namespace FolderLock
{
    internal enum PasswordMode { Open, Create, Change }

    internal sealed class PasswordForm : Form
    {
        private const int MinLength = 6;
        private readonly PasswordMode _mode;
        private readonly TextBox _current, _pw1, _pw2;
        private readonly Label _error;

        public string CurrentPassword => _current?.Text;
        public string Password => _pw1.Text;

        public PasswordForm(PasswordMode mode, string vaultName)
        {
            _mode = mode;
            Text = mode == PasswordMode.Open ? L.T("금고 열기", "Open vault") : mode == PasswordMode.Create ? L.T("새 비밀번호 설정", "Set a new password") : L.T("비밀번호 변경", "Change password");
            Icon = Theme.AppIcon;
            Font = Theme.Base;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;
            ShowInTaskbar = false;

            var root = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(22, 18, 22, 16), Dock = DockStyle.Fill,
            };
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                Text = vaultName, Font = new Font("Malgun Gothic", 12f, FontStyle.Bold),
                AutoSize = true, Margin = new Padding(0, 0, 0, 10),
            });

            if (mode == PasswordMode.Create)
            {
                root.Controls.Add(new Label
                {
                    Text = L.T("비밀번호를 잊으면 누구도(만든 사람도) 복구할 수 없습니다.\n잘 기억할 수 있는 긴 문장을 추천합니다.",
                             "If you forget the password, nobody (not even you) can recover the files.\nA long sentence you can remember is recommended."),
                    ForeColor = Theme.Danger, AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 10),
                });
            }

            TextBox AddField(string label)
            {
                root.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 4, 0, 2) });
                var tb = new TextBox { UseSystemPasswordChar = true, Width = 380, Margin = new Padding(0, 0, 0, 6), Font = new Font("Malgun Gothic", 11f) };
                root.Controls.Add(tb);
                return tb;
            }

            if (mode == PasswordMode.Change) _current = AddField(L.T("현재 비밀번호", "Current password"));
            _pw1 = AddField(mode == PasswordMode.Open ? L.T("비밀번호", "Password") : L.T($"새 비밀번호 ({MinLength}자 이상)", $"New password (at least {MinLength} characters)"));
            if (mode != PasswordMode.Open) _pw2 = AddField(L.T("새 비밀번호 확인", "Confirm new password"));

            var show = new CheckBox { Text = L.T("비밀번호 보기", "Show password"), AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 2, 0, 4) };
            show.CheckedChanged += (s, e) =>
            {
                foreach (var tb in new[] { _current, _pw1, _pw2 }) if (tb != null) tb.UseSystemPasswordChar = !show.Checked;
            };
            root.Controls.Add(show);

            _error = new Label { AutoSize = true, ForeColor = Theme.Danger, Text = "", Margin = new Padding(0, 2, 0, 6), MaximumSize = new Size(380, 0) };
            root.Controls.Add(_error);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            var ok = Theme.PrimaryButton(mode == PasswordMode.Open ? L.T("열기", "Open") : L.T("확인", "OK"));
            var cancel = Theme.SecondaryButton(L.T("취소", "Cancel"));
            ok.Click += (s, e) => TryAccept();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons);

            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (s, e) => (_current ?? _pw1).Focus();
        }

        public void ShowError(string text) => _error.Text = text;

        private void TryAccept()
        {
            if (_mode == PasswordMode.Open)
            {
                if (_pw1.Text.Length == 0) { _error.Text = L.T("비밀번호를 입력하세요.", "Enter the password."); return; }
            }
            else
            {
                if (_mode == PasswordMode.Change && _current.Text.Length == 0) { _error.Text = L.T("현재 비밀번호를 입력하세요.", "Enter the current password."); return; }
                if (_pw1.Text.Length < MinLength) { _error.Text = L.T($"비밀번호는 {MinLength}자 이상이어야 합니다.", $"The password must be at least {MinLength} characters."); _pw1.Focus(); return; }
                if (_pw1.Text != _pw2.Text) { _error.Text = L.T("두 비밀번호가 서로 다릅니다.", "The two passwords do not match."); _pw2.SelectAll(); _pw2.Focus(); return; }
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var tb in new[] { _current, _pw1, _pw2 }) if (tb != null && DialogResult != DialogResult.OK) tb.Text = "";
            base.OnFormClosed(e);
        }
    }
}
