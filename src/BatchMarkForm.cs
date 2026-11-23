// BatchMarkForm.cs —— 批量录点时在点击位置显示的序号标记
//
// 为什么是独立窗口而不是在主窗体上画：录点时主窗体已经隐藏，
// 而且用户此刻可能正全屏在游戏里，标记必须"始终在最上层"才能看见。
//
// 为什么要 WS_EX_NOACTIVATE：标记窗弹出时不能把焦点从游戏抢走，
// 否则全屏游戏会被切到后台（有些游戏甚至会最小化），录点就没法继续了。
//
// 标记会一直留在屏幕上，直到本次录点结束（按 Esc / 双击托盘）才统一清掉。
// 这样采集完回到连点器时，能对照屏幕上的序号知道"我先点的哪个"。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameClicker
{
    internal sealed class BatchMarkForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_NOOWNERZORDER = 0x0200;

        private readonly int _index;      // 第几个点（显示序号）
        private readonly string _coord;   // 坐标文本

        // 本次录点产生的所有标记，结束录点时统一关闭
        private static readonly List<BatchMarkForm> _live = new List<BatchMarkForm>();

        public BatchMarkForm(int x, int y, int index)
        {
            _index = index;
            _coord = x + ", " + y;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Bounds = new Rectangle(x - 21, y - 21, 42, 42);

            // 关键：不抢焦点 + 不出现在 Alt+Tab
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER | 0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/);

            // 透明：整个窗体只有圆圈是可见的，四周不留灰色底
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 外圈：醒目的青绿色实线圆，一眼能看出"刚才点的是这儿"
            using (Pen p = new Pen(Color.FromArgb(255, 0, 200, 120), 3f))
                g.DrawEllipse(p, 5, 5, 30, 30);

            // 内点：中心一个小点，标记精确的点击位置
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 0, 200, 120)))
                g.FillEllipse(b, 18, 18, 6, 6);

            // 序号：第几个记录的坐标（1 起），回到连点器后按这个对号
            using (Font f = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold))
            using (SolidBrush t = new SolidBrush(Color.FromArgb(255, 255, 255)))
            {
                SizeF ts = g.MeasureString(_index.ToString(), f);
                g.DrawString(_index.ToString(), f, t, 21 - ts.Width / 2, 21 - ts.Height / 2);
            }

            // 深色描边提高对比度：亮背景/暗背景都看得清
            using (Pen q = new Pen(Color.FromArgb(255, 20, 30, 40), 1f))
                g.DrawEllipse(q, 4, 4, 32, 32);
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

        /// <summary>
        /// 在指定位置弹一个序号标记（不抢焦点）。
        /// 标记会一直留在屏幕上，直到 CloseAll() 被调用（即本次录点结束）。
        /// </summary>
        public static void ShowMark(int x, int y, int index)
        {
            BatchMarkForm f = new BatchMarkForm(x, y, index);
            lock (_live)
            {
                _live.Add(f);
                f.Show();
            }
        }

        /// <summary>移除最后一个标记（批量录点时右键撤销用）。</summary>
        public static void RemoveLast()
        {
            lock (_live)
            {
                if (_live.Count == 0) return;
                BatchMarkForm f = _live[_live.Count - 1];
                _live.RemoveAt(_live.Count - 1);
                try { f.Close(); f.Dispose(); } catch { }
            }
        }

        /// <summary>关闭本次录点产生的全部标记（结束采集时调用）。</summary>
        public static void CloseAll()
        {
            lock (_live)
            {
                foreach (BatchMarkForm f in _live)
                {
                    try { f.Close(); f.Dispose(); } catch { }
                }
                _live.Clear();
            }
        }

        /// <summary>当前屏幕上还剩几个标记（供诊断/测试）。</summary>
        public static int LiveCount
        {
            get { lock (_live) { return _live.Count; } }
        }
    }
}
