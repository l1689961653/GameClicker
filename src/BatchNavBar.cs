// BatchNavBar.cs —— 批量录点时屏幕上的迷你导航条
//
// 三个操作：暂停/继续（停止取点但保留已取的点与标记）、完成（结束并保存）。
//
// 设计要点：
//  - TopMost + WS_EX_NOACTIVATE：绝不抢全屏游戏的焦点，否则游戏会被切到后台
//  - 点击导航条本身不能被记成坐标：由 MainForm 在收到取点事件时检查
//    光标是否落在导航条范围内（钩子层不感知 UI）
//  - 尽可能小：单行 3 个小按钮，贴屏幕右上角
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameClicker
{
    internal sealed class BatchNavBar : Form
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly Button _btnPause;
        private readonly Button _btnDone;
        private readonly Label _lblCount;
        private bool _paused;

        /// <summary>用户点了「暂停/继续」。</summary>
        public event Action<bool> PauseToggled;      // true = 现在处于暂停
        /// <summary>用户点了「完成」。</summary>
        public event Action DoneClicked;
        public BatchNavBar()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(32, 38, 48);

            int w = 236, h = 34;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(wa.Right - w - 12, wa.Top + 8, w, h);

            _lblCount = new Label();
            _lblCount.Text = "0 点";
            _lblCount.ForeColor = Color.FromArgb(140, 220, 180);
            _lblCount.SetBounds(8, 8, 52, 18);
            Controls.Add(_lblCount);

            _btnPause = MakeBtn("暂停", 60, 5, 56);
            _btnPause.Click += delegate
            {
                _paused = !_paused;
                _btnPause.Text = _paused ? "继续" : "暂停";
                _btnPause.BackColor = _paused ? Color.FromArgb(170, 110, 40) : Color.FromArgb(52, 62, 78);
                if (PauseToggled != null) PauseToggled(_paused);
            };
            Controls.Add(_btnPause);

            _btnDone = MakeBtn("完成", 120, 5, 56);
            _btnDone.BackColor = Color.FromArgb(24, 148, 88);
            _btnDone.Click += delegate { if (DoneClicked != null) DoneClicked(); };
            Controls.Add(_btnDone);

            Button esc = MakeBtn("Esc 退出", 180, 5, 50);
            esc.Click += delegate { if (DoneClicked != null) DoneClicked(); };
            Controls.Add(esc);

            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        private static Button MakeBtn(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 24);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(52, 62, 78);
            b.ForeColor = Color.White;
            b.TabStop = false;
            return b;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;   // 双保险：绝不抢焦点
                return cp;
            }
        }

        /// <summary>刷新点计数显示。</summary>
        public void SetCount(int n) { _lblCount.Text = n + " 点"; }

        /// <summary>暂停期间让计数变灰，提示当前不在取点。</summary>
        public void SetPausedVisual(bool paused)
        {
            _lblCount.ForeColor = paused ? Color.FromArgb(170, 110, 40) : Color.FromArgb(140, 220, 180);
        }

        /// <summary>光标是否落在导航条范围内（MainForm 用它过滤取点）。</summary>
        public bool ContainsPoint(int x, int y)
        {
            Rectangle wa = Bounds;
            return x >= wa.Left && x <= wa.Right && y >= wa.Top && y <= wa.Bottom;
        }
    }
}
