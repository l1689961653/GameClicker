// StatusBanner.cs —— 触发键启停时的屏幕顶部状态条
//
// 两种形态：
//   已启动 —— 绿色、常驻（连点期间一直在屏幕上方，随时能看到状态）
//   已关闭 —— 灰色、只显示约 2 秒后自动淡出（不需要常驻提醒"没在连点"）
//
// 与 BatchNavBar 相同的无焦点设计：绝不把全屏游戏切到后台。
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameClicker
{
    internal sealed class StatusBanner : Form
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        /// <summary>「已关闭」形态停留多久后自动关闭（毫秒）。</summary>
        private const int OffLifeMs = 2000;

        private readonly Label _lbl;

        public StatusBanner()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 148, 88);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = 220, h = 40;
            Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + 8, w, h);

            _lbl = new Label();
            _lbl.Text = "已启动";
            _lbl.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            _lbl.ForeColor = Color.White;
            _lbl.Dock = DockStyle.Fill;
            _lbl.TextAlign = ContentAlignment.MiddleCenter;
            _lbl.BackColor = Color.Transparent;
            Controls.Add(_lbl);

            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        private System.Windows.Forms.Timer _offTimer;

        /// <summary>显示「已启动」（绿色常驻）。</summary>
        public void ShowOn(string triggerText)
        {
            StopLifeTimer();
            BackColor = Color.FromArgb(24, 148, 88);
            _lbl.Text = "连点中 · " + triggerText;
            if (!Visible) Show();
            Refresh();
        }

        /// <summary>显示「已关闭」（灰色，约 2 秒后自动消失）。</summary>
        public void ShowOff()
        {
            StopLifeTimer();
            BackColor = Color.FromArgb(70, 78, 90);
            _lbl.Text = "已关闭";
            if (!Visible) Show();
            Refresh();

            if (_offTimer == null)
            {
                _offTimer = new System.Windows.Forms.Timer { Interval = OffLifeMs };
                _offTimer.Tick += delegate
                {
                    _offTimer.Stop();
                    Hide();
                };
            }
            _offTimer.Start();
        }

        private void StopLifeTimer()
        {
            if (_offTimer != null) _offTimer.Stop();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            StopLifeTimer();
            if (_offTimer != null) { try { _offTimer.Dispose(); } catch { } }
        }
    }
}
