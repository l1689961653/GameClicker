// HotkeyPromptForm.cs —— 批量录点结束后弹出的热键设置对话框
//
// 交互：默认未捕获。用户点「设置热键」→ 进入捕获（主钩子 PointKey 通道）→
//       按任意键回显；再点可重设；「清除热键」清空；「确定」落盘；「跳过」不设。
// 界面不抢焦点（TopMost + WS_EX_NOACTIVATE），主窗体保持可见。
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameClicker
{
    internal sealed class HotkeyPromptForm : Form
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly Label _lblSummary;
        private readonly Label _lblHotkey;
        private readonly Button _btnCapture;
        private readonly Button _btnClear;
        private int _hotkeyVk = 0;

        /// <summary>用户点了「确定」。参数为热键 vk（0 = 不设热键）。</summary>
        public event Action<int> Confirmed;
        /// <summary>用户点「设置热键」→ 主窗体启动捕获。</summary>
        public event Action CaptureRequested;
        /// <summary>用户点「清除热键」→ 主窗体清掉该序列的热键。</summary>
        public event Action ClearRequested;

        public HotkeyPromptForm(string summary)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(32, 38, 48);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = 320, h = 176;
            Bounds = new Rectangle(wa.Right - w - 24, wa.Bottom - h - 24, w, h);

            Label title = new Label();
            title.Text = "设置热键";
            title.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.SetBounds(16, 12, 200, 22);
            Controls.Add(title);

            _lblSummary = new Label();
            _lblSummary.Text = summary;
            _lblSummary.ForeColor = Color.FromArgb(160, 200, 240);
            _lblSummary.SetBounds(16, 38, w - 32, 20);
            Controls.Add(_lblSummary);

            _lblHotkey = new Label();
            _lblHotkey.Text = "当前：未设置";
            _lblHotkey.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            _lblHotkey.ForeColor = Color.FromArgb(170, 178, 190);
            _lblHotkey.SetBounds(16, 64, w - 32, 26);
            Controls.Add(_lblHotkey);

            _btnCapture = MakeBtn("设置热键", 16, 98, 130);
            _btnCapture.BackColor = Color.FromArgb(24, 148, 88);
            _btnCapture.Click += delegate
            {
                _btnCapture.Text = "按键…";
                if (CaptureRequested != null) CaptureRequested();
            };
            Controls.Add(_btnCapture);

            _btnClear = MakeBtn("清除热键", 152, 98, 88);
            _btnClear.Click += delegate
            {
                SetHotkeyNone();
                if (ClearRequested != null) ClearRequested();
            };
            Controls.Add(_btnClear);

            Button ok = MakeBtn("确定", 246, 98, 58);
            ok.Click += delegate
            {
                if (Confirmed != null) Confirmed(_hotkeyVk);
                Close();
            };
            Controls.Add(ok);

            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        /// <summary>把热键文本解析回 vk（用于确定时回传）；解析不了返回 0。</summary>

        private static Button MakeBtn(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 28);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(52, 62, 78);
            b.ForeColor = Color.White;
            b.TabStop = false;
            return b;
        }

        /// <summary>主窗体捕获被取消（Esc/冲突）后恢复按钮。</summary>
        public void ResetCapture()
        {
            _btnCapture.Text = "设置热键";
        }

        /// <summary>主窗体捕获到热键后回显（vk + 文本）。</summary>
        public void SetHotkey(int vk, string describe)
        {
            _hotkeyVk = vk;
            _lblHotkey.Text = "当前：" + describe;
            _lblHotkey.ForeColor = Color.White;
            _btnCapture.Text = "重新设置";
        }

        /// <summary>清除热键回显。</summary>
        public void SetHotkeyNone()
        {
            _hotkeyVk = 0;
            _lblHotkey.Text = "当前：未设置";
            _lblHotkey.ForeColor = Color.FromArgb(170, 178, 190);
            _btnCapture.Text = "设置热键";
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
    }
}
