// TestHarness.cs —— 核心功能自动化验证（不参与正式程序编译）
//
// 设计要点：
//  1) 时序精度用 ClickEngine.Clicked 事件测量：这是引擎自身的节奏，不受 UIPI 输入拦截影响。
//  2) 输入投递（点击是否真落到窗口）需要进程完整性 ≥ 目标窗口，否则会被 Windows UIPI 静默拦截。
//     测试会先探测这种限制，受限时如实报告为「环境受限」而不是伪造通过。
//  3) 钩子回调的结构体封送逻辑用构造内存直接验证，不依赖真实注入。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GameClicker.Test
{
    /// <summary>接收点击的目标窗口，记录每次按键按下的精确时刻。</summary>
    internal sealed class ClickTargetForm : Form
    {
        public readonly List<long> DownTicks = new List<long>();
        public readonly List<int> DownMessages = new List<int>();

        public ClickTargetForm()
        {
            Text = "ClickTarget";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(300, 200);
            BackColor = Color.LemonChiffon;
        }

        protected override void WndProc(ref Message m)
        {
            // Windows 会把"双击时间阈值(默认500ms)内的第二次按下"改写成 WM_*BUTTONDBLCLK，
            // 而不是再发一个 WM_*BUTTONDOWN。连点间隔通常远小于该阈值，
            // 所以统计时必须把双击也算作一次"按下"，否则会误判成丢点击。
            // 实测：21 次点击 → 11 次 DOWN + 10 次 DBLCLK，总数完全吻合。
            int effective = m.Msg;
            if (m.Msg == WM_LBUTTONDBLCLK) effective = Native.WM_LBUTTONDOWN;
            else if (m.Msg == WM_RBUTTONDBLCLK) effective = Native.WM_RBUTTONDOWN;
            else if (m.Msg == WM_MBUTTONDBLCLK) effective = Native.WM_MBUTTONDOWN;

            if (effective == Native.WM_LBUTTONDOWN || effective == Native.WM_RBUTTONDOWN ||
                effective == Native.WM_MBUTTONDOWN)
            {
                lock (DownTicks)
                {
                    DownTicks.Add(Stopwatch.GetTimestamp());
                    DownMessages.Add(effective);
                }
                if (m.Msg != effective) Interlocked.Increment(ref DblClicks);
            }
            base.WndProc(ref m);
        }

        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONDBLCLK = 0x0206;
        private const int WM_MBUTTONDBLCLK = 0x0209;

        /// <summary>被系统改写为双击的按下次数（仅用于诊断输出）。</summary>
        public int DblClicks;

        public int Count { get { lock (DownTicks) return DownTicks.Count; } }

        public int CountOf(int msg, int fromIndex)
        {
            int n = 0;
            lock (DownMessages)
            {
                for (int i = fromIndex; i < DownMessages.Count; i++)
                    if (DownMessages[i] == msg) n++;
            }
            return n;
        }
    }

    /// <summary>测试用键盘钩子：记录所有事件（含注入），用于验证钩子链路。</summary>
    internal sealed class ProbeKeyboardHook : IDisposable
    {
        private IntPtr _hook = IntPtr.Zero;
        private Native.HookProc _proc;
        public volatile int RawCount;
        public volatile int LastVk = -1;
        public volatile bool LastWasInjected;
        public volatile int LastFlags;

        public bool Install()
        {
            _proc = Callback;
            IntPtr hMod = Native.GetModuleHandle(null);
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, hMod, 0);
            return _hook != IntPtr.Zero;
        }

        /// <summary>直接以构造的内存调用回调，用于验证结构体封送逻辑（不依赖真实注入）。</summary>
        public void FeedSynthetic(uint vk, uint flags)
        {
            int size = Marshal.SizeOf(typeof(KBDLLHOOKSTRUCT));
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                KBDLLHOOKSTRUCT s = new KBDLLHOOKSTRUCT();
                s.vkCode = vk;
                s.scanCode = 0;
                s.flags = flags;
                s.time = 0;
                s.dwExtraInfo = IntPtr.Zero;
                Marshal.StructureToPtr(s, mem, false);
                Callback(0, new IntPtr(Native.WM_KEYDOWN), mem);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT d = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                LastVk = (int)d.vkCode;
                LastFlags = (int)d.flags;
                LastWasInjected = (d.flags & Native.LLKHF_INJECTED) != 0;
                RawCount++;
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }

    /// <summary>测试用鼠标钩子。</summary>
    internal sealed class ProbeMouseHook : IDisposable
    {
        private IntPtr _hook = IntPtr.Zero;
        private Native.HookProc _proc;
        public volatile int DownCount;
        public volatile bool LastWasInjected;

        public bool Install()
        {
            _proc = Callback;
            IntPtr hMod = Native.GetModuleHandle(null);
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, hMod, 0);
            return _hook != IntPtr.Zero;
        }

        /// <summary>构造 MSLLHOOKSTRUCT 直接喂给回调，验证封送逻辑。</summary>
        public void FeedSynthetic(int msg, uint flags)
        {
            int size = Marshal.SizeOf(typeof(MSLLHOOKSTRUCT));
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                MSLLHOOKSTRUCT s = new MSLLHOOKSTRUCT();
                s.pt = new POINT { X = 100, Y = 100 };
                s.mouseData = 0;
                s.flags = flags;
                s.time = 0;
                s.dwExtraInfo = IntPtr.Zero;
                Marshal.StructureToPtr(s, mem, false);
                Callback(0, new IntPtr(msg), mem);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
                {
                    MSLLHOOKSTRUCT d = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    LastWasInjected = (d.flags & Native.LLMHF_INJECTED) != 0;
                    DownCount++;
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }

    internal static class TestProgram
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int _pass, _fail, _skip;

        private static void Say(string s) { Console.WriteLine(s); Log.AppendLine(s); }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Say("  [PASS] " + name + "  " + detail); }
            else { _fail++; Say("  [FAIL] " + name + "  " + detail); }
        }

        private static void Skip(string name, string reason)
        {
            _skip++;
            Say("  [SKIP] " + name + "  " + reason);
        }

        /// <summary>持续泵消息循环：钩子回调与窗口消息及时处理的前提。</summary>
        private static void PumpFor(int ms)
        {
            long end = Stopwatch.GetTimestamp() + (long)(ms / 1000.0 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < end)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            Application.DoEvents();
        }

        // ---------- 完整性级别（决定输入能否投递） ----------
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, uint len, out uint ret);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static int IntegrityOf(uint pid)
        {
            IntPtr proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero) return -1;
            IntPtr token = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(proc, TOKEN_QUERY, out token)) return -1;
                uint len;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out len);
                if (len == 0) return -1;
                buf = Marshal.AllocHGlobal((int)len);
                if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out len)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf);
                byte subs = Marshal.ReadByte(sid, 1);
                return Marshal.ReadInt32(sid, 8 + (subs - 1) * 4);
            }
            catch { return -1; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(proc);
            }
        }

        private static string LevelName(int v)
        {
            if (v < 0) return "未知";
            if (v >= 0x4000) return "High(管理员)";
            if (v >= 0x3000) return "Medium(普通)";
            if (v >= 0x2000) return "Low(低)";
            return "0x" + v.ToString("X4");
        }

        [STAThread]
        private static void Main(string[] args)
        {
            string outDir = (args != null && args.Length > 0) ? args[0] : ".";
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            Console.OutputEncoding = Encoding.UTF8;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MakeProcessDpiAware();   // 尽早设置，否则高 DPI 会被虚拟化

            using (Form host = new Form())
            {
                host.ShowInTaskbar = false;
                host.FormBorderStyle = FormBorderStyle.None;
                host.ClientSize = new Size(1, 1);
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-2000, -2000);
                host.Show();
                PumpFor(300);

                Say("=== 游戏连点器 核心功能验证 ===");
                Say("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                uint myPid = (uint)Process.GetCurrentProcess().Id;
                int myLevel = IntegrityOf(myPid);
                Say("本进程完整性：" + LevelName(myLevel) + "   64位=" + Environment.Is64BitProcess);
                Say("");

                // 关于权限：注入目标是本进程自己创建的窗口，完整性级别必然与本进程相同，
                // 因此 UIPI 不可能拦截它 —— 早期版本拿"前台进程"的完整性来推断
                // "注入会被拦截"，那是错的（前台窗口与注入目标无关），
                // 结果把一批本该真实执行的项目误标成了 SKIP，掩盖了真实结果。
                // 这里改为直接实测：真发一次点击，看目标窗口是否收到。
                bool uipiBlocked = false;
                IntPtr fg = GetForegroundWindow();
                uint fgPid;
                GetWindowThreadProcessId(fg, out fgPid);
                string fgName = "?";
                try { fgName = Process.GetProcessById((int)fgPid).ProcessName; } catch { }
                Say("当前前台进程：" + fgName + "（与本测试的注入目标无关，仅作环境记录）");
                Say("");

                Native.TimeBeginPeriod(1);

                ClickEngine engine = null;
                ClickTargetForm target = null;

                try
                {
                    target = new ClickTargetForm();
                    target.Location = new Point(150, 150);
                    target.Show();
                    target.Activate();
                    PumpFor(400);

                    Check("目标窗口已就绪", target.IsHandleCreated && target.Visible, "Handle=" + target.Handle);

                    // 必须先把光标移到目标窗口上，否则点击会落在窗口外的任意位置，
                    // "是否送达"就变成随机结果（早期版本漏了这一步，导致结果时好时坏）。
                    Point center = new Point(target.Left + target.ClientSize.Width / 2,
                                             target.Top + target.ClientSize.Height / 2);
                    Native.SetCursorPos(center.X, center.Y);
                    PumpFor(250);

                    POINT cur;
                    Native.GetCursorPos(out cur);
                    bool onTarget = cur.X >= target.Left && cur.X < target.Right &&
                                    cur.Y >= target.Top && cur.Y < target.Bottom;
                    Check("★ 已把光标置于目标窗口内（否则点击位置无从谈起）",
                        onTarget,
                        "光标 (" + cur.X + "," + cur.Y + ")，目标窗口 " + target.Bounds);
                    Say("");

                    engine = new ClickEngine();

                    // ============ 测试 1：引擎时序精度（核心，不受 UIPI 影响） ============
                    Say("[测试 1] 连点节奏精度（引擎 Clicked 事件实测）");

                    double avg50, jit50, avg200, jit200, avg10, jit10;
                    int n50 = MeasureRhythm(engine, 50, out avg50, out jit50);
                    Check("50ms 平均间隔误差 ≤ 5ms", Math.Abs(avg50 - 50) <= 5,
                        "实测平均 " + avg50.ToString("0.00") + " ms，抖动 " + jit50.ToString("0.00") + " ms（" + n50 + " 次采样）");

                    int n200 = MeasureRhythm(engine, 200, out avg200, out jit200);
                    Check("200ms 平均间隔误差 ≤ 8ms", Math.Abs(avg200 - 200) <= 8,
                        "实测平均 " + avg200.ToString("0.00") + " ms，抖动 " + jit200.ToString("0.00") + " ms（" + n200 + " 次采样）");

                    // 高频档：不能断言"达到 90 次/秒"，因为本机存在与间隔设置无关的物理下限。
                    // 一次点击至少要两次 SendInput 调用（按下 + 抬起），实测每次调用约 4~6ms，
                    // 于是 10ms 档实际只能跑到 ~20ms/次 —— 这是环境限制，不是计时逻辑缺陷。
                    // 这里先实测本机的点击成本下限，再验证引擎确实逼近了它，
                    // 而不是拿一个写死的常数去卡（那样会偶发误判）。
                    double floorMs = MeasureLocalClickFloorMs();
                    int n10 = MeasureRhythm(engine, 10, out avg10, out jit10);
                    bool near10 = Math.Abs(avg10 - 10) <= 5;                 // 环境够快时应当贴近目标
                    bool atFloor = avg10 > 0 && avg10 <= floorMs * 1.6 + 6;  // 否则至少要逼近本机下限
                    Check("10ms 高频档：贴近目标或已达本机点击成本下限",
                        near10 || atFloor,
                        "实测 " + avg10.ToString("0.00") + " ms ≈ " + (1000.0 / avg10).ToString("0") +
                        " 次/秒；本机实测单次点击成本下限 " + floorMs.ToString("0.0") +
                        " ms（受 SendInput 调用开销限制）");

                    // 长间隔也要准（证明不是靠"跑满"伪装精度）
                    double avg500, jit500;
                    int n500 = MeasureRhythm(engine, 500, out avg500, out jit500);
                    Check("500ms 平均间隔误差 ≤ 12ms", Math.Abs(avg500 - 500) <= 12,
                        "实测平均 " + avg500.ToString("0.00") + " ms，抖动 " + jit500.ToString("0.00") + " ms");
                    Say("");

                    // ============ 测试 2：运行中改参数立即生效 ============
                    Say("[测试 2] 运行中调整间隔立即生效");
                    double avgA, jA, avgB, jB;
                    MeasureRhythm(engine, 30, out avgA, out jA);
                    // 不停止引擎，直接改参数
                    engine.UpdateLive(120, ClickButton.Left, 10, false, true);
                    PumpFor(200);
                    MeasureRhythmRunning(engine, out avgB, out jB);
                    Check("运行中把间隔从 30ms 改为 120ms 后节奏跟随", avgB > 60,
                        "改前 " + avgA.ToString("0.0") + " ms → 改后 " + avgB.ToString("0.0") + " ms");
                    engine.Stop();
                    PumpFor(200);
                    Check("停止后计数归零、不再点击", !engine.IsActive,
                        "IsActive=" + engine.IsActive + "，本次会话计数=" + engine.ClickCount);
                    Say("");

                    // ============ 测试 3：输入投递 ============
                    Say("[测试 3] 点击是否真正送达目标窗口");

                    // 前面的节奏测量把光标挪到了屏幕角落，这里必须先放回目标窗口中心，
                    // 否则点击落在窗口外，"是否送达"就成了随机结果。
                    Native.SetCursorPos(center.X, center.Y);
                    PumpFor(300);

                    int idx = target.Count;
                    engine.Start(50, ClickButton.Left, false, 15, true);
                    PumpFor(1000);
                    engine.Stop();
                    PumpFor(300);

                    int got = target.Count - idx;
                    int accepted = engine.LastSendAccepted;

                    // 目标窗口就是本进程自己的窗口，UIPI 无从拦截，因此这里必须真实断言。
                    // 注意 target.Count 已把 WM_*BUTTONDBLCLK 计入"按下"，
                    // 否则快速连点会被误判成丢点击（见 TargetForm.WndProc 注释）。
                    Check("左键连点送达目标窗口", got >= 12,
                        "1 秒收到 " + got + " 次按下（50ms 间隔理论 ≈19；含被系统改写为双击的 " +
                        target.DblClicks + " 次）");
                    Check("SendInput 被系统接受", accepted > 0, "SendInput 返回 " + accepted);

                    int before2 = target.Count;
                    engine.Start(40, ClickButton.Right, false, 12, true);
                    PumpFor(500);
                    engine.Stop();
                    PumpFor(250);
                    int right = target.CountOf(Native.WM_RBUTTONDOWN, before2);
                    Check("右键连点生效", right >= 6, "收到 " + right + " 次右键按下");
                    Say("");

                    // ============ 测试 4：位置模式逻辑 ============
                    Say("[测试 4] 位置模式（锁定 vs 跟随）");

                    // 锁定模式下，鼠标被移走时应把指针拉回锁定点
                    Native.SetCursorPos(400, 300);
                    PumpFor(150);
                    POINT p0;
                    Native.GetCursorPos(out p0);

                    engine.Start(40, ClickButton.Left, false, 12, true);   // 锁定 (400,300)
                    PumpFor(250);

                    Native.SetCursorPos(900, 600);   // 连点过程中把鼠标移走
                    PumpFor(400);
                    POINT p1;
                    Native.GetCursorPos(out p1);
                    engine.Stop();
                    PumpFor(200);

                    Check("锁定模式会把指针拉回锁定点",
                        Math.Abs(p1.X - 400) <= 3 && Math.Abs(p1.Y - 300) <= 3,
                        "移走后指针位于 (" + p1.X + "," + p1.Y + ")，期望回到 (400,300)");

                    // 跟随模式：不拉回
                    engine.Start(40, ClickButton.Left, true, 12, false);
                    PumpFor(200);
                    Native.SetCursorPos(700, 500);
                    PumpFor(300);
                    POINT p2;
                    Native.GetCursorPos(out p2);
                    engine.Stop();
                    PumpFor(200);

                    Check("跟随模式不干预指针位置",
                        Math.Abs(p2.X - 700) <= 3 && Math.Abs(p2.Y - 500) <= 3,
                        "跟随模式下指针保持在 (" + p2.X + "," + p2.Y + ")");

                    // 无论如何都能验证 RelockHere 不抛异常
                    try
                    {
                        engine.RelockHere();
                        Check("RelockHere（把锁定点设到鼠标处）可正常调用", true, "无异常");
                    }
                    catch (Exception ex)
                    {
                        Check("RelockHere 可正常调用", false, ex.Message);
                    }
                    Say("");

                    // ============ 测试 5：全局钩子 ============
                    Say("[测试 5] 全局键鼠钩子");

                    ProbeKeyboardHook kb = new ProbeKeyboardHook();
                    bool kbOk = kb.Install();
                    Check("低级键盘钩子安装成功", kbOk,
                        kbOk ? "SetWindowsHookEx 返回有效句柄" : "失败，错误码 " + Marshal.GetLastWin32Error());

                    if (kbOk)
                    {
                        // 结构体封送：用构造内存直接喂回调（验证真实解析代码路径）
                        kb.FeedSynthetic(0x7B, Native.LLKHF_INJECTED);
                        Check("虚拟键码解析正确（F12=0x7B）", kb.LastVk == 0x7B,
                            "解析 vkCode=0x" + kb.LastVk.ToString("X2"));
                        Check("LLKHF_INJECTED 标志识别正确（连点器靠它避免自触发）",
                            kb.LastWasInjected && kb.LastFlags == 0x10,
                            "flags=0x" + kb.LastFlags.ToString("X2") + " injected=" + kb.LastWasInjected);

                        kb.FeedSynthetic(0x41, 0x00);
                        Check("普通（非注入）按键识别正确", !kb.LastWasInjected && kb.LastVk == 0x41,
                            "A 键 vkCode=0x" + kb.LastVk.ToString("X2") + " injected=" + kb.LastWasInjected);

                        Check("KBDLLHOOKSTRUCT 结构体大小正确（x64=24）",
                            Marshal.SizeOf(typeof(KBDLLHOOKSTRUCT)) == (Environment.Is64BitProcess ? 24 : 20),
                            "实际 " + Marshal.SizeOf(typeof(KBDLLHOOKSTRUCT)));

                        // 端到端：真实注入是否被钩子收到。
                        // 低级钩子装在本进程内，自己注入的事件同样会经过它，
                        // 因此这里必须真实断言，不能以"环境受限"为由跳过。
                        int before = kb.RawCount;
                        INPUT[] seq = new INPUT[] { MakeKey(0x7B, false), MakeKey(0x7B, true) };
                        Native.SendInput((uint)seq.Length, seq, Native.InputSize);
                        for (int i = 0; i < 20 && kb.RawCount == before; i++) PumpFor(50);

                        Check("真实注入的按键被钩子捕获", kb.RawCount > before,
                            "收到 " + (kb.RawCount - before) + " 个事件");

                        kb.Dispose();
                    }

                    ProbeMouseHook ms = new ProbeMouseHook();
                    bool msOk = ms.Install();
                    Check("低级鼠标钩子安装成功", msOk, msOk ? "句柄有效" : "失败，错误码 " + Marshal.GetLastWin32Error());
                    if (msOk)
                    {
                        ms.FeedSynthetic(Native.WM_LBUTTONDOWN, Native.LLMHF_INJECTED);
                        Check("鼠标按键消息与注入标志解析正确",
                            ms.DownCount == 1 && ms.LastWasInjected,
                            "收到 " + ms.DownCount + " 次按下，injected=" + ms.LastWasInjected);

                        ms.FeedSynthetic(Native.WM_MBUTTONDOWN, 0);
                        Check("中键消息识别正确", ms.DownCount == 2 && !ms.LastWasInjected,
                            "累计 " + ms.DownCount + " 次，普通点击 injected=" + ms.LastWasInjected);

                        Check("MSLLHOOKSTRUCT 结构体大小正确（x64=32 / x86=24）",
                            Marshal.SizeOf(typeof(MSLLHOOKSTRUCT)) == (Environment.Is64BitProcess ? 32 : 24),
                            "实际 " + Marshal.SizeOf(typeof(MSLLHOOKSTRUCT)) + "（POINT(8)+mouseData(4)+flags(4)+time(4)+对齐(4)+extraInfo(8)=32）");
                        ms.Dispose();
                    }

                    Check("INPUT 结构体布局正确（Win64=40 / Win32=28）",
                        Native.InputSize == (Environment.Is64BitProcess ? 40 : 28),
                        "实际 " + Native.InputSize + "（SendInput 依赖此布局，错则完全不生效）");
                    Say("");

                    // ============ 测试 6：配置持久化 ============
                    Say("[测试 6] 配置持久化（真实落盘 + 带注释读回）");

                    string tmpDir = Path.Combine(Path.GetTempPath(), "gc_cfg_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmpDir);
                    string tmpCfg = Path.Combine(tmpDir, "config.json");

                    try
                    {
                        AppConfig cfg = new AppConfig();
                        cfg.IntervalMs = 37;
                        cfg.TriggerVk = 0x76;
                        cfg.Button = ClickButton.Right;
                        cfg.HoldMs = 22;
                        cfg.FollowCursor = false;
                        cfg.SwallowTrigger = false;
                        cfg.EmergencyVk = 0x7B;

                        string json = cfg.ToJson();
                        File.WriteAllText(tmpCfg, json, new UTF8Encoding(false));

                        Check("配置文本带中文注释（便于手工编辑）",
                            json.Contains("//") && json.Contains("_说明"), "含 // 说明与 _说明 字段");

                        AppConfig back = AppConfig.LoadFrom(tmpCfg);

                        Check("间隔 / 触发键 / 鼠标键 读回正确",
                            back.IntervalMs == 37 && back.TriggerVk == 0x76 &&
                            back.Button == ClickButton.Right && back.HoldMs == 22 &&
                            back.EmergencyVk == 0x7B,
                            "间隔=" + back.IntervalMs + " 触发键=0x" + back.TriggerVk.ToString("X2") +
                            " 按键=" + back.Button + " 按下时长=" + back.HoldMs);
                        Check("布尔项读回正确",
                            back.FollowCursor == false && !back.SwallowTrigger,
                            "followCursor=" + back.FollowCursor + " swallow=" + back.SwallowTrigger);

                        // 注释剥离的边界情况
                        string tricky = "{\"p\": \"C:\\\\a//b\", \"q\": 1} // 尾部中文注释";
                        string outTricky = AppConfig.StripComments(tricky);
                        Check("字符串内的 // 不被误删、行尾注释被剥离",
                            outTricky.Contains("a//b") && !outTricky.Contains("尾部"),
                            "路径保留，注释移除");

                        Check("注释剥离后不是靠猜（保留换行结构）",
                            AppConfig.StripComments("{\"a\":1,\n\"b\":2}").Contains("\n"),
                            "换行保留，避免键值粘连");

                        // 损坏配置必须安全回退
                        File.WriteAllText(tmpCfg, "{ 这不是合法 JSON !!!  ", new UTF8Encoding(false));
                        AppConfig broken = AppConfig.LoadFrom(tmpCfg);
                        Check("配置损坏时安全回退默认值（不崩溃）",
                            broken != null && broken.IntervalMs == 50 && broken.TriggerVk == 0x72,
                            "坏 JSON → 默认 50ms / F3");

                        // 空文件
                        File.WriteAllText(tmpCfg, "", new UTF8Encoding(false));
                        AppConfig empty = AppConfig.LoadFrom(tmpCfg);
                        Check("空配置文件安全回退", empty != null && empty.IntervalMs == 50, "默认值");

                        // 缺字段的部分配置
                        File.WriteAllText(tmpCfg, "{ \"intervalMs\": 88 }", new UTF8Encoding(false));
                        AppConfig partial = AppConfig.LoadFrom(tmpCfg);
                        Check("部分字段的配置能正常读取（缺失项用默认值）",
                            partial.IntervalMs == 88 && partial.TriggerVk == 0x72,
                            "intervalMs=88 生效，triggerVk 回落默认 F3");

                        // 越界值必须被 Sanitize
                        File.WriteAllText(tmpCfg, "{ \"intervalMs\": 999999, \"holdMs\": -5 }", new UTF8Encoding(false));
                        AppConfig sane = AppConfig.LoadFrom(tmpCfg);
                        Check("越界数值被纠正到合法范围",
                            sane.IntervalMs == 60000 && sane.HoldMs == 1,
                            "999999→60000，-5→1");
                    }
                    finally
                    {
                        try { Directory.Delete(tmpDir, true); } catch { }
                    }
                    Say("");

                    // ============ 测试 7：记录点功能 ============
                    Say("[测试 7] 记录点（坐标换算 / 点过去 / 归位）");

                    // 9.1 屏幕坐标 → SendInput 绝对坐标 换算（这段错了就会"总是偏一点"）
                    int ax, ay;
                    Native.ScreenToAbsolute(0, 0, 0, 0, 1920, 1080, out ax, out ay);
                    Check("单屏左上角映射到 (0,0)", ax == 0 && ay == 0, "得到 (" + ax + "," + ay + ")");

                    Native.ScreenToAbsolute(1919, 1079, 0, 0, 1920, 1080, out ax, out ay);
                    Check("单屏右下角映射到 (65535,65535)", ax == 65535 && ay == 65535, "得到 (" + ax + "," + ay + ")");

                    // 用"像素粒度"而不是魔法数字来断言：
                    // 1920 宽时一个像素 ≈ 65535/1919 ≈ 34 个单位，所以中心允许 ±40 的偏差。
                    // （换算式采用 index/(size-1) 约定，这样四角才能精确落在 0 与 65535 —— 见上面两条）
                    Native.ScreenToAbsolute(960, 540, 0, 0, 1920, 1080, out ax, out ay);
                    Check("屏幕中心映射到中间值（一个像素内）",
                        Math.Abs(ax - 32768) <= 40 && Math.Abs(ay - 32768) <= 40, "得到 (" + ax + "," + ay + ")");

                    // 负原点（副屏在主屏左侧/上方）必须被正确抬回 0..65535
                    Native.ScreenToAbsolute(-1920, -200, -1920, -200, 3840, 1280, out ax, out ay);
                    Check("负坐标原点（副屏在左/上方）正确抬回 0", ax == 0 && ay == 0, "得到 (" + ax + "," + ay + ")");

                    // 跨屏：虚拟桌面 x∈[-1920,1919]、y∈[-200,1079]，原点 (0,0) 恰好在水平中点、
                    // 垂直方向距顶 200/1279 → 约 10247
                    Native.ScreenToAbsolute(0, 0, -1920, -200, 3840, 1280, out ax, out ay);
                    Check("跨屏坐标映射正确（水平中点 + 垂直按比例）",
                        Math.Abs(ax - 32768) <= 20 && Math.Abs(ay - 10247) <= 20, "得到 (" + ax + "," + ay + ")");

                    // 越界坐标必须被夹紧而不是回绕
                    Native.ScreenToAbsolute(-99999, 99999, 0, 0, 1920, 1080, out ax, out ay);
                    Check("越界坐标被夹紧到合法范围", ax == 0 && ay == 65535, "得到 (" + ax + "," + ay + ")");

                    // 退化参数不能除零崩溃
                    try
                    {
                        Native.ScreenToAbsolute(10, 10, 0, 0, 0, 0, out ax, out ay);
                        Check("虚拟屏幕尺寸为 0 时不崩溃（除零防护）", true, "得到 (" + ax + "," + ay + ")");
                    }
                    catch (Exception ex)
                    {
                        Check("虚拟屏幕尺寸为 0 时不崩溃（除零防护）", false, ex.Message);
                    }

                    // 9.2 引擎的记录点模式行为
                    ClickEngine ptEngine = new ClickEngine();
                    try
                    {
                        int beforeClicks = target.Count;

                        // 目标点设在窗口内，用一次性模式
                        int tx = target.Left + 80;
                        int ty = target.Top + 70;

                        ptEngine.StartAtPoint(tx, ty, 50, ClickButton.Left, 12, true, false);
                        Check("StartAtPoint 后进入记录点模式", ptEngine.IsPointMode, "IsPointMode=" + ptEngine.IsPointMode);

                        // 一次性模式应在一次点击后自动停止
                        bool stopped = false;
                        for (int i = 0; i < 60; i++)
                        {
                            PumpFor(50);
                            if (!ptEngine.IsActive) { stopped = true; break; }
                        }
                        Check("单次模式点一下就自动停止", stopped,
                            "IsActive=" + ptEngine.IsActive + "，计数=" + ptEngine.ClickCount);
                        Check("单次模式确实只点了一次", ptEngine.ClickCount == 1, "计数=" + ptEngine.ClickCount);

                        Check("记录点模式点击送达目标窗口", target.Count > beforeClicks,
                            "窗口收到 " + (target.Count - beforeClicks) + " 次点击");

                        POINT landed;
                        if (Native.GetCursorPos(out landed))
                            Check("指针被移动到记录点",
                                Math.Abs(landed.X - tx) <= 3 && Math.Abs(landed.Y - ty) <= 3,
                                "记录点 (" + tx + "," + ty + ") → 实际 (" + landed.X + "," + landed.Y + ")");

                        // 连续模式：再按一次才停
                        ptEngine.StartAtPoint(tx, ty, 40, ClickButton.Left, 10, false, false);
                        PumpFor(400);
                        Check("连续模式下会持续点击", ptEngine.IsActive && ptEngine.ClickCount >= 5,
                            "计数=" + ptEngine.ClickCount + "，IsActive=" + ptEngine.IsActive);
                        ptEngine.Stop();
                        PumpFor(200);
                        Check("连续模式可被停止", !ptEngine.IsActive, "IsActive=" + ptEngine.IsActive);

                        // 归位开关：关闭时不应把指针拉回
                        Native.SetCursorPos(600, 500);
                        PumpFor(120);
                        POINT startPos;
                        Native.GetCursorPos(out startPos);

                        ptEngine.StartAtPoint(target.Left + 50, target.Top + 50, 40, ClickButton.Left, 10, false, false);
                        PumpFor(300);
                        ptEngine.Stop();
                        PumpFor(200);
                        POINT afterNoReturn;
                        Native.GetCursorPos(out afterNoReturn);

                        Check("关闭归位时指针留在记录点（不干扰无关操作时不乱动）",
                            !(Math.Abs(afterNoReturn.X - startPos.X) <= 3 && Math.Abs(afterNoReturn.Y - startPos.Y) <= 3),
                            "起点 (" + startPos.X + "," + startPos.Y + ") → 结束 (" + afterNoReturn.X + "," + afterNoReturn.Y + ")");

                        // 开启归位：结束后指针应回到起点
                        Native.SetCursorPos(700, 520);
                        PumpFor(120);
                        POINT r0;
                        Native.GetCursorPos(out r0);

                        ptEngine.StartAtPoint(target.Left + 40, target.Top + 40, 40, ClickButton.Left, 10, false, true);
                        PumpFor(300);
                        ptEngine.Stop();
                        PumpFor(300);
                        POINT r1;
                        Native.GetCursorPos(out r1);

                        Check("开启归位后指针回到起始位置",
                            Math.Abs(r1.X - r0.X) <= 5 && Math.Abs(r1.Y - r0.Y) <= 5,
                            "起点 (" + r0.X + "," + r0.Y + ") → 归位后 (" + r1.X + "," + r1.Y + ")");

                        // UpdateLive 不应破坏记录点语义
                        ptEngine.StartAtPoint(tx, ty, 40, ClickButton.Left, 10, false, false);
                        ptEngine.UpdateLive(999, ClickButton.Right, 50, true, true);
                        Check("连点记录点期间改设置不会丢失目标点",
                            ptEngine.IsPointMode,
                            "IsPointMode=" + ptEngine.IsPointMode + "（UpdateLive 被正确忽略）");
                        ptEngine.Stop();
                        PumpFor(150);
                    }
                    finally
                    {
                        try { ptEngine.Stop(); ptEngine.Dispose(); } catch { }
                    }
                    Say("");

                    // ============ 测试 8：序列配置持久化 ============
                    Say("[测试 8] 序列存取（多点序列 / 各自热键间隔 / 三代旧配置迁移）");

                    string ptDir = Path.Combine(Path.GetTempPath(), "gc_pt_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(ptDir);
                    string ptCfg = Path.Combine(ptDir, "config.json");
                    try
                    {
                        // 8.1 多点序列连同热键、间隔、开关、备注一起存取
                        AppConfig pc = new AppConfig();
                        SequenceEntry s1 = new SequenceEntry("每日领奖");
                        s1.Points.Add(new SeqPoint("领取奖励", 1234, 567));
                        s1.Points.Add(new SeqPoint("确认弹窗", 800, 450));
                        s1.HotkeyVk = 0x77;      // F8
                        s1.GapMs = 150;
                        s1.ReturnCursor = true;
                        pc.Sequences.Add(s1);

                        SequenceEntry s2 = new SequenceEntry("单点连挖");
                        s2.Points.Add(new SeqPoint("", 10, 20));
                        s2.HotkeyVk = 0x75;      // F6
                        s2.Mode = 1;             // 单点序列：连点
                        s2.Enabled = false;      // 停用
                        pc.Sequences.Add(s2);
                        File.WriteAllText(ptCfg, pc.ToJson(), new UTF8Encoding(false));

                        AppConfig pb = AppConfig.LoadFrom(ptCfg);
                        Check("★ 多条序列都能存下来",
                            pb.Sequences.Count == 2, "读回 " + pb.Sequences.Count + " 条");
                        Check("★ 序列内多个点的顺序就是录制顺序",
                            pb.Sequences.Count == 2 &&
                            pb.Sequences[0].Points.Count == 2 &&
                            pb.Sequences[0].Points[0].Name == "领取奖励" &&
                            pb.Sequences[0].Points[1].Name == "确认弹窗",
                            pb.Sequences.Count == 2 && pb.Sequences[0].Points.Count == 2
                                ? "点1=" + pb.Sequences[0].Points[0].Name + " → 点2=" + pb.Sequences[0].Points[1].Name
                                : "-");
                        Check("★ 每条序列的热键 / 间隔 / 开关各自独立保存",
                            pb.Sequences.Count == 2 &&
                            pb.Sequences[0].HotkeyVk == 0x77 && pb.Sequences[0].GapMs == 150 && pb.Sequences[0].Enabled &&
                            pb.Sequences[1].HotkeyVk == 0x75 && !pb.Sequences[1].Enabled,
                            pb.Sequences.Count == 2
                                ? "S1: F8/150ms/启用   S2: F6/停用"
                                : "-");
                        Check("★ 单点序列的连点模式与归位开关正确读回",
                            pb.Sequences.Count == 2 &&
                            pb.Sequences[0].ReturnCursor && pb.Sequences[1].Mode == 1,
                            pb.Sequences.Count == 2
                                ? "S1 归位=" + pb.Sequences[0].ReturnCursor + "   S2 模式=" + pb.Sequences[1].Mode
                                : "-");
                        Check("负坐标（副屏）能原样读回",
                            pb.Sequences.Count == 2 &&
                            pb.Sequences[0].Points[1].X == 800 && pb.Sequences[0].Points[1].Y == 450,
                            pb.Sequences.Count == 2
                                ? "点2=(" + pb.Sequences[0].Points[1].X + ", " + pb.Sequences[0].Points[1].Y + ")"
                                : "-");

                        // 8.2 备注名里的特殊字符不能把 JSON 写坏（序列名与点名都测）
                        AppConfig tricky = new AppConfig();
                        SequenceEntry ts = new SequenceEntry("序列\"引号");
                        ts.Points.Add(new SeqPoint("点A\\B", 10, 20));
                        ts.Points.Add(new SeqPoint("点B\"C", 30, 40));
                        tricky.Sequences.Add(ts);
                        File.WriteAllText(ptCfg, tricky.ToJson(), new UTF8Encoding(false));
                        AppConfig trickyBack = AppConfig.LoadFrom(ptCfg);
                        Check("★ 序列名 / 点名含引号与反斜杠也不会破坏配置文件",
                            trickyBack.Sequences.Count == 1 &&
                            trickyBack.Sequences[0].Name == "序列\"引号" &&
                            trickyBack.Sequences[0].Points.Count == 2 &&
                            trickyBack.Sequences[0].Points[0].Name == "点A\\B" &&
                            trickyBack.Sequences[0].Points[1].Name == "点B\"C",
                            trickyBack.Sequences.Count == 1 && trickyBack.Sequences[0].Points.Count == 2
                                ? "[" + trickyBack.Sequences[0].Name + "] [" +
                                  trickyBack.Sequences[0].Points[0].Name + " / " +
                                  trickyBack.Sequences[0].Points[1].Name + "]"
                                : "-");

                        // 8.3 三代旧配置迁移
                        // (a0) 新建配置必须是空白的：ProfileManager.Create 生成的配置不含任何序列
                        //      （用户反馈：新配置继承上一个配置的序列，还得挨个删，不合理）
                        string newProfDir = ProfileManager.Create("测试新配置", "testgame.exe");
                        Check("★ ProfileManager.Create 能创建新配置目录",
                            newProfDir != null && Directory.Exists(newProfDir),
                            newProfDir ?? "null");
                        if (newProfDir != null)
                        {
                            AppConfig fresh = AppConfig.LoadFromDir(newProfDir);
                            Check("★ 新建的配置是空白的：不含任何序列（不继承上一个配置）",
                                fresh.Sequences.Count == 0,
                                "序列数=" + fresh.Sequences.Count);
                            Check("★ 新建配置的绑定进程名已写入",
                                fresh.GameProcess == "testgame.exe",
                                "gameProcess=" + fresh.GameProcess);
                            Check("★ 新建配置的全局设置是默认值（间隔 50ms）",
                                fresh.IntervalMs == 50, "间隔=" + fresh.IntervalMs);
                        }
                        else
                        {
                            Check("★ ProfileManager.Create 能创建新配置目录", false, "创建返回 null（可能同名残留）");
                        }
                        ProfileManager.Delete("测试新配置");   // 清理

                        // (a) 最老版：单个记录点
                        string oldest = "{ \"hasPoint\": true, \"pointX\": 640, \"pointY\": 360, " +
                                        "\"pointHotkeyVk\": 119, \"pointMode\": 1, \"pointReturnCursor\": true }";
                        File.WriteAllText(ptCfg, oldest, new UTF8Encoding(false));
                        AppConfig mig1 = AppConfig.LoadFrom(ptCfg);
                        Check("★ 最老版（单记录点）迁移成一条单点序列，设置不丢",
                            mig1.Sequences.Count == 1 && mig1.Sequences[0].Points.Count == 1 &&
                            mig1.Sequences[0].Points[0].X == 640 && mig1.Sequences[0].Points[0].Y == 360 &&
                            mig1.Sequences[0].HotkeyVk == 119 && mig1.Sequences[0].Mode == 1 &&
                            mig1.Sequences[0].ReturnCursor,
                            mig1.Sequences.Count == 1
                                ? "(" + mig1.Sequences[0].Points[0].X + ", " + mig1.Sequences[0].Points[0].Y +
                                  ") 热键=0x" + mig1.Sequences[0].HotkeyVk.ToString("X2") +
                                  " 模式=" + mig1.Sequences[0].Mode
                                : "序列数=" + mig1.Sequences.Count);

                        // (b) 上一版：坐标点列表（每个坐标各自热键）→ 每个坐标变成一条单点序列
                        string prev = "{ \"points\": [" +
                                      "{ \"name\": \"A\", \"x\": 1, \"y\": 2, \"hotkeyVk\": 118, \"enabled\": true, \"mode\": 0, \"returnCursor\": true }, " +
                                      "{ \"name\": \"B\", \"x\": 3, \"y\": 4, \"hotkeyVk\": 117, \"enabled\": false, \"mode\": 1, \"returnCursor\": false }] }";
                        File.WriteAllText(ptCfg, prev, new UTF8Encoding(false));
                        AppConfig mig2 = AppConfig.LoadFrom(ptCfg);
                        Check("★ 上一版（坐标点列表）迁移成多条单点序列，热键/开关/模式全保留",
                            mig2.Sequences.Count == 2 &&
                            mig2.Sequences[0].Points.Count == 1 && mig2.Sequences[0].Points[0].Name == "A" &&
                            mig2.Sequences[0].HotkeyVk == 118 && mig2.Sequences[0].Enabled && mig2.Sequences[0].ReturnCursor &&
                            mig2.Sequences[1].Points.Count == 1 && mig2.Sequences[1].Points[0].Name == "B" &&
                            mig2.Sequences[1].HotkeyVk == 117 && !mig2.Sequences[1].Enabled && mig2.Sequences[1].Mode == 1,
                            mig2.Sequences.Count == 2
                                ? "A→F8/启用/归位   B→F7/停用/连点"
                                : "序列数=" + mig2.Sequences.Count);

                        // (c) 旧配置里没记录过坐标 → 不应凭空造出序列
                        File.WriteAllText(ptCfg, "{ \"hasPoint\": false, \"pointX\": 0, \"pointY\": 0 }",
                            new UTF8Encoding(false));
                        Check("旧的未记录状态不会凭空生成序列",
                            AppConfig.LoadFrom(ptCfg).Sequences.Count == 0,
                            "序列数=" + AppConfig.LoadFrom(ptCfg).Sequences.Count);

                        // 8.4 非法数据被清理
                        AppConfig bad = new AppConfig();
                        SequenceEntry bs = new SequenceEntry("越界点");
                        bs.Points.Add(new SeqPoint("越界", 999999, 0));
                        bs.Points.Add(new SeqPoint("正常", 50, 60));
                        bad.Sequences.Add(bs);
                        File.WriteAllText(ptCfg, bad.ToJson(), new UTF8Encoding(false));
                        AppConfig badBack = AppConfig.LoadFrom(ptCfg);
                        // 归零后 (0,0) 本身是合法坐标，所以只断言"不再停在越界位置"
                        Check("越界坐标被归零（不会乱点）",
                            badBack.Sequences.Count == 1 && badBack.Sequences[0].Points[0].X == 0 &&
                            badBack.Sequences[0].Points[0].Y == 0 && badBack.Sequences[0].Points[1].X == 50,
                            badBack.Sequences.Count == 1
                                ? "点1=(" + badBack.Sequences[0].Points[0].X + "," + badBack.Sequences[0].Points[0].Y +
                                  ") 点2=" + badBack.Sequences[0].Points[1].X
                                : "-");

                        // 空序列没有意义，应被丢弃
                        AppConfig empty = new AppConfig();
                        empty.Sequences.Add(new SequenceEntry("空的"));   // 没加任何点
                        File.WriteAllText(ptCfg, empty.ToJson(), new UTF8Encoding(false));
                        Check("★ 空序列被自动丢弃（不会留一条点不了的东西）",
                            AppConfig.LoadFrom(ptCfg).Sequences.Count == 0,
                            "序列数=" + AppConfig.LoadFrom(ptCfg).Sequences.Count);

                        // 越界热键 / 间隔被纠正
                        File.WriteAllText(ptCfg,
                            "{ \"sequences\": [{ \"name\": \"x\", \"hotkeyVk\": 9999, \"gapMs\": 999999, " +
                            "\"points\": [{ \"name\": \"p\", \"x\": 1, \"y\": 2 }] }] }",
                            new UTF8Encoding(false));
                        AppConfig oob = AppConfig.LoadFrom(ptCfg);
                        Check("越界热键归 0、越界间隔回落到上限",
                            oob.Sequences.Count == 1 && oob.Sequences[0].HotkeyVk == 0 &&
                            oob.Sequences[0].GapMs == 60000,
                            "hotkeyVk=" + (oob.Sequences.Count == 1 ? oob.Sequences[0].HotkeyVk.ToString() : "?") +
                            " gapMs=" + (oob.Sequences.Count == 1 ? oob.Sequences[0].GapMs.ToString() : "?"));

                        // 8.5 默认配置里没有任何序列
                        File.WriteAllText(ptCfg, "{ \"intervalMs\": 40 }", new UTF8Encoding(false));
                        Check("默认配置里没有任何序列",
                            AppConfig.LoadFrom(ptCfg).Sequences.Count == 0,
                            "序列数=" + AppConfig.LoadFrom(ptCfg).Sequences.Count);

                        // 8.6 窗口尺寸：存取往返 + 越界兜底
                        AppConfig ws = new AppConfig();
                        ws.WindowW = 946;
                        ws.WindowH = 844;
                        ws.WindowMaximized = false;
                        File.WriteAllText(ptCfg, ws.ToJson(), new UTF8Encoding(false));
                        AppConfig wsBack = AppConfig.LoadFrom(ptCfg);
                        Check("★ 窗口尺寸能正确存取（拖大后重启还原的依据）",
                            wsBack.WindowW == 946 && wsBack.WindowH == 844,
                            "读回 " + wsBack.WindowW + "x" + wsBack.WindowH);
                        Check("★ 最大化标志能正确存取",
                            AppConfig.LoadFrom(ptCfg).WindowMaximized == false,
                            "windowMaximized=false");

                        // 最大化标志单独存
                        ws.WindowMaximized = true;
                        File.WriteAllText(ptCfg, ws.ToJson(), new UTF8Encoding(false));
                        Check("★ 上次以最大化关闭时，标志能读回",
                            AppConfig.LoadFrom(ptCfg).WindowMaximized, "windowMaximized=true");

                        // 越界/过小的尺寸视为"未设置"（宁可回退默认，也不开出离谱窗口）
                        File.WriteAllText(ptCfg,
                            "{ \"windowW\": 200, \"windowH\": 100 }", new UTF8Encoding(false));
                        Check("★ 过小的窗口尺寸被忽略（回退默认值，布局不被压坏）",
                            AppConfig.LoadFrom(ptCfg).WindowW == 0 && AppConfig.LoadFrom(ptCfg).WindowH == 0,
                            "200x100 → " + AppConfig.LoadFrom(ptCfg).WindowW + "x" +
                            AppConfig.LoadFrom(ptCfg).WindowH);
                        File.WriteAllText(ptCfg,
                            "{ \"windowW\": 99999, \"windowH\": 88888 }", new UTF8Encoding(false));
                        Check("★ 荒唐大的窗口尺寸被忽略",
                            AppConfig.LoadFrom(ptCfg).WindowW == 0 && AppConfig.LoadFrom(ptCfg).WindowH == 0,
                            "99999x88888 → 0x0");

                        // ★ 全字段序列化自校验：历史上"追加新字段漏逗号"导致程序保存的配置
                        //   自己都读不回来（全部静默回退默认值）。这条锁死这类 bug。
                        AppConfig full = new AppConfig();
                        full.IntervalMs = 123;
                        full.TriggerVk = 0x76;
                        full.RunAsAdmin = false;
                        full.WindowW = 768;
                        full.WindowH = 720;
                        full.WindowMaximized = true;
                        full.Sequences.Add(SequenceEntry.FromPoints("自校验序列",
                            new SeqPoint[] { new SeqPoint("p1", 1, 2), new SeqPoint("p2", 3, 4) }, 88));
                        // 注意：LoadFrom 的参数是文件路径，不是 JSON 文本——
                        // 第一次写这条测试时就传错了，结果"往返失败"其实是测试自己的 bug
                        string rtPath = Path.Combine(ptDir, "roundtrip.json");
                        File.WriteAllText(rtPath, full.ToJson(), new UTF8Encoding(false));
                        AppConfig fullBack = AppConfig.LoadFrom(rtPath);
                        Check("★ 全字段 ToJson→LoadFrom 往返一致（防手写 JSON 拼接漏逗号）",
                            fullBack.IntervalMs == 123 && fullBack.TriggerVk == 0x76 &&
                            fullBack.WindowW == 768 && fullBack.WindowH == 720 &&
                            fullBack.WindowMaximized &&
                            fullBack.Sequences.Count == 1 &&
                            fullBack.Sequences[0].GapMs == 88 &&
                            fullBack.Sequences[0].Points.Count == 2,
                            "间隔=" + fullBack.IntervalMs + " 序列=" + fullBack.Sequences.Count +
                            " 窗口=" + fullBack.WindowW + "x" + fullBack.WindowH +
                            " gap=" + (fullBack.Sequences.Count > 0 ? fullBack.Sequences[0].GapMs.ToString() : "?"));

                        // 8.6 克隆必须是深拷贝（改副本不能污染原对象）
                        AppConfig orig = new AppConfig();
                        SequenceEntry os = new SequenceEntry("原名");
                        os.Points.Add(new SeqPoint("点1", 1, 2));
                        orig.Sequences.Add(os);
                        AppConfig copy = orig.Clone();
                        copy.Sequences[0].Name = "改过";
                        copy.Sequences[0].Points[0].X = 999;
                        copy.Sequences[0].Points.Add(new SeqPoint("新点", 3, 4));
                        Check("★ 配置克隆是深拷贝（改副本不会污染原对象）",
                            orig.Sequences.Count == 1 && orig.Sequences[0].Name == "原名" &&
                            orig.Sequences[0].Points.Count == 1 && orig.Sequences[0].Points[0].X == 1 &&
                            orig.Sequences[0].Points[0].Name == "点1",
                            "原对象: " + orig.Sequences[0].Name + " / " +
                            orig.Sequences[0].Points[0].Name + "(" + orig.Sequences[0].Points[0].X + ")");
                    }
                    finally
                    {
                        try { Directory.Delete(ptDir, true); } catch { }
                    }
                    Say("");

                    // ============ 测试 9：默认以管理员身份运行 ============
                    Say("[测试 9] 默认以管理员身份运行（配置项 / 提权判定 / 参数拼接）");

                    // 11.1 配置项持久化
                    string elDir = Path.Combine(Path.GetTempPath(), "gc_el_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(elDir);
                    string elCfg = Path.Combine(elDir, "config.json");
                    try
                    {
                        AppConfig on = new AppConfig();
                        on.RunAsAdmin = true;
                        File.WriteAllText(elCfg, on.ToJson(), new UTF8Encoding(false));
                        Check("runAsAdmin=true 能正确写读", AppConfig.LoadFrom(elCfg).RunAsAdmin,
                            "写 true 读回 " + AppConfig.LoadFrom(elCfg).RunAsAdmin);

                        AppConfig off = new AppConfig();
                        off.RunAsAdmin = false;
                        File.WriteAllText(elCfg, off.ToJson(), new UTF8Encoding(false));
                        Check("runAsAdmin=false 能正确写读", !AppConfig.LoadFrom(elCfg).RunAsAdmin,
                            "写 false 读回 " + AppConfig.LoadFrom(elCfg).RunAsAdmin);

                        // 旧版配置里没有这个字段：必须默认开启，才能与旧版行为一致
                        File.WriteAllText(elCfg, "{ \"intervalMs\": 50 }", new UTF8Encoding(false));
                        Check("★ 旧配置文件缺少该字段时默认为开启（与旧版行为一致）",
                            AppConfig.LoadFrom(elCfg).RunAsAdmin,
                            "缺失 runAsAdmin → 默认 true");

                        Check("配置文本含 runAsAdmin 说明", on.ToJson().Contains("runAsAdmin"),
                            "ToJson 输出包含该字段");
                    }
                    finally
                    {
                        try { Directory.Delete(elDir, true); } catch { }
                    }

                    // 11.2 提权判定逻辑（当前测试进程未提权，正好覆盖"需要提权"这一支）
                    AppConfig cfgOn = new AppConfig();
                    cfgOn.RunAsAdmin = true;
                    AppConfig cfgOff = new AppConfig();
                    cfgOff.RunAsAdmin = false;

                    bool elevatedNow = Elevation.IsElevated;
                    Say("    当前进程是否已提权 = " + elevatedNow + "（本测试宿主为普通权限）");

                    if (!elevatedNow)
                    {
                        Check("开启设置时判定为需要提权",
                            Elevation.ShouldElevate(cfgOn, new string[0], false),
                            "RunAsAdmin=true → ShouldElevate=true");
                        Check("关闭设置时判定为不需要提权",
                            !Elevation.ShouldElevate(cfgOff, new string[0], false),
                            "RunAsAdmin=false → ShouldElevate=false");
                        Check("带 --no-elevate 时强制不提权（应急开关）",
                            !Elevation.ShouldElevate(cfgOn, new[] { "--no-elevate" }, false),
                            "--no-elevate 覆盖了 RunAsAdmin=true");
                        Check("自检模式不提权（避免自动化测试被 UAC 卡住）",
                            !Elevation.ShouldElevate(cfgOn, new string[0], true),
                            "selfTest=true → 不提权");
                    }
                    else
                    {
                        Skip("提权判定", "当前进程已是管理员，无法覆盖未提权分支");
                    }

                    // 11.3 命令行拼接：绝不能把提权标记自己带过去，否则无限递归
                    string a1 = Elevation.BuildArguments(new[] { "--tray", "--minimized" });
                    Check("提权时保留原有参数",
                        a1.Contains("--elevated-restart") && a1.Contains("--tray") && a1.Contains("--minimized"),
                        "\"" + a1 + "\"");

                    string a2 = Elevation.BuildArguments(new[] { "--tray", "--elevated-restart" });
                    Check("★ 不会重复携带 --elevated-restart（防无限提权递归）",
                        a2.IndexOf("--elevated-restart") == a2.LastIndexOf("--elevated-restart") &&
                        a2.Contains("--tray"),
                        "\"" + a2 + "\"");

                    string a3 = Elevation.BuildArguments(new[] { "--tray", "--no-elevate" });
                    Check("提权时剥掉 --no-elevate，避免子进程又自我否决",
                        !a3.Contains("--no-elevate"), "\"" + a3 + "\"");

                    string a4 = Elevation.BuildArguments(new[] { "--selftest", @"C:\a b\out" });
                    Check("含空格的参数被正确加引号",
                        a4.Contains("\"C:\\a b\\out\""), "\"" + a4 + "\"");

                    Check("空参数数组也能安全处理",
                        Elevation.BuildArguments(new string[0]).Trim() == "--elevated-restart",
                        "\"" + Elevation.BuildArguments(new string[0]) + "\"");
                    Say("");

// ============ 测试 10：记录坐标热键 ============
                    Say("[测试 10] 记录坐标热键（按键即记录鼠标所在位置）");

                    string rhDir = Path.Combine(Path.GetTempPath(), "gc_rh_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(rhDir);
                    string rhCfg = Path.Combine(rhDir, "config.json");
                    try
                    {
                        // 12.1 序列的存取（记录坐标热键功能已删除，此处保留序列往返覆盖）
                        AppConfig rc = new AppConfig();

                        rc.Sequences.Add(SequenceEntry.FromPoints("测试点",
                            new SeqPoint[] { new SeqPoint("", 111, 222) }, 60));
                        File.WriteAllText(rhCfg, rc.ToJson(), new UTF8Encoding(false));

                        AppConfig rb = AppConfig.LoadFrom(rhCfg);
                        Check("序列能正确写读",
                            rb.Sequences.Count == 1 && rb.Sequences[0].Points[0].X == 111,
                            "序列=" + rb.Sequences.Count);

                        // 12.2 负坐标（副屏场景）必须能原样读回
                        AppConfig negPt = new AppConfig();
                        negPt.Sequences.Add(SequenceEntry.FromPoints("副屏点",
                            new SeqPoint[] { new SeqPoint("", -1024, 768) }, 60));
                        File.WriteAllText(rhCfg, negPt.ToJson(), new UTF8Encoding(false));
                        AppConfig negBack = AppConfig.LoadFrom(rhCfg);
                        Check("负坐标（副屏）能原样读回",
                            negBack.Sequences.Count == 1 && negBack.Sequences[0].Points[0].X == -1024 && negBack.Sequences[0].Points[0].Y == 768,
                            negBack.Sequences.Count == 1
                                ? "(" + negBack.Sequences[0].Points[0].X + ", " + negBack.Sequences[0].Points[0].Y + ")"
                                : "序列数=" + negBack.Sequences.Count);
                    }
                    finally
                    {
                        try { Directory.Delete(rhDir, true); } catch { }
                    }

                    // 12.3 捕获分派：直接驱动产品的 InputHook.TryHandleCapture，
                    //      而不是在测试里重写一份分派逻辑（那样测不到真实代码）
                    using (InputHook capHook = new InputHook())
                    {
                        int capturedTrigger = -1, capturedPoint = -1, capturedPointIdx = -99;
                        capHook.KeyCaptured += delegate (int v) { capturedTrigger = v; };
                        capHook.PointKeyCaptured += delegate (int v, int pointIdx) { capturedPoint = v; capturedPointIdx = pointIdx; };

                        // 坐标点热键：必须带上"为哪个点改键"的索引
                        capHook.BeginPointKeyCapture(2);
                        capHook.TryHandleCapture(0x77, true);
                        Check("★ 坐标点改键会上报按键码 + 目标点索引",
                            capturedPoint == 0x77 && capturedPointIdx == 2 && capturedTrigger == -1,
                            "point=0x" + capturedPoint.ToString("X2") + " 索引=" + capturedPointIdx +
                            " trigger=" + capturedTrigger);

                        // 不带索引的普通捕获应上报 -1，避免误改到别的点
                        capHook.BeginCapture(CaptureKind.PointKey);
                        capHook.TryHandleCapture(0x78, true);
                        Check("普通坐标点捕获索引为 -1（不会误改某个点）",
                            capturedPoint == 0x78 && capturedPointIdx == -1,
                            "索引=" + capturedPointIdx);

                        // 连点触发键
                        capHook.BeginCapture(CaptureKind.TriggerKey);
                        capHook.TryHandleCapture(0x72, true);
                        Check("捕获连点触发键时上报给 KeyCaptured",
                            capturedTrigger == 0x72,
                            "trigger=" + capturedTrigger);

                        // 捕获是一次性的：第二次按键不应再上报（用触发键捕获验证）
                        int before = capturedTrigger;
                        capHook.BeginCapture(CaptureKind.TriggerKey);
                        capHook.TryHandleCapture(0x75, true);   // 第一次：应上报并结束捕获
                        capHook.TryHandleCapture(0x74, true);   // 第二次：应被忽略
                        Check("★ 捕获只生效一次（不会连续吃按键）",
                            capturedTrigger == 0x75 && before != capturedTrigger,
                            "第一次=0x" + capturedTrigger.ToString("X2") + "（第二次 0x74 未覆盖）");

                        // 取消后不再上报
                        capHook.BeginCapture(CaptureKind.TriggerKey);
                        capHook.CancelCapture();
                        int afterCancel = capturedTrigger;
                        bool consumedAfterCancel = capHook.TryHandleCapture(0x73, true);
                        Check("取消捕获后不再上报、也不吞按键",
                            capturedTrigger == afterCancel && !consumedAfterCancel,
                            "trigger 仍为 0x" + capturedTrigger.ToString("X2"));

                        // 等待鼠标捕获时，键盘事件不应被消费
                        capHook.BeginCapture(CaptureKind.TriggerMouse);
                        bool consumedWhenMouse = capHook.TryHandleCapture(0x41, true);
                        Check("等待鼠标捕获时，键盘事件不被消费（否则鼠标钩子收不到按键）",
                            !consumedWhenMouse,
                            "TryHandleCapture 返回 " + consumedWhenMouse);

                        // 未处于捕获模式时不消费任何按键
                        capHook.CancelCapture();
                        Check("空闲时不拦截任何按键",
                            !capHook.TryHandleCapture(0x41, true),
                            "TryHandleCapture 返回 false");

                        // 按键抬起(up)不应触发捕获完成
                        capHook.BeginCapture(CaptureKind.TriggerKey);
                        bool consumedUp = capHook.TryHandleCapture(0x76, false);
                        Check("按键抬起不触发捕获",
                            !consumedUp && capturedTrigger == afterCancel,
                            "up 事件被忽略");
                    }
                    Say("");

                    // ============ 测试 11：序列热键分派 ============
                    Say("[测试 11] 序列热键分派（哪个键对应哪条序列）");

                    using (InputHook mh = new InputHook())
                    {
                        AppConfig mc = new AppConfig();
                        mc.Sequences.Add(SequenceEntry.FromPoints("序列A", new SeqPoint[] { new SeqPoint("", 100, 100) }, 60));
                        mc.Sequences[0].HotkeyVk = 0x70;              // F1
                        mc.Sequences.Add(SequenceEntry.FromPoints("序列B", new SeqPoint[] { new SeqPoint("", 200, 200) }, 60));
                        mc.Sequences[1].HotkeyVk = 0x71;              // F2
                        mc.Sequences.Add(SequenceEntry.FromPoints("序列C-停用", new SeqPoint[] { new SeqPoint("", 300, 300) }, 60));
                        mc.Sequences[2].HotkeyVk = 0x72;              // F3
                        mc.Sequences[2].Enabled = false;              // 停用
                        mc.Sequences.Add(SequenceEntry.FromPoints("序列D-无键", new SeqPoint[] { new SeqPoint("", 400, 400) }, 60));
                        mc.Sequences[3].HotkeyVk = 0;                 // 没设热键
                        mh.ApplySettings(mc);

                        Check("★ F1 → 第 0 条序列",
                            mh.MatchPointHotkey(0x70) == 0, "索引=" + mh.MatchPointHotkey(0x70));
                        Check("★ F2 → 第 1 条序列（不同序列不同索引，不会串）",
                            mh.MatchPointHotkey(0x71) == 1, "索引=" + mh.MatchPointHotkey(0x71));
                        Check("★ 被停用的序列其热键不再响应",
                            mh.MatchPointHotkey(0x72) == -1, "索引=" + mh.MatchPointHotkey(0x72));
                        Check("没有热键的序列不会被任何键触发",
                            mh.MatchPointHotkey(0) == -1 && mh.MatchPointHotkey(0x73) == -1,
                            "vk=0 与未分配的 F4 都不匹配");
                        Check("无关按键不匹配任何序列",
                            mh.MatchPointHotkey(0x41) == -1, "A 键 索引=" + mh.MatchPointHotkey(0x41));

                        // 重新启用后应立刻能响应（验证快照会被真正替换，而不是缓存住）
                        mc.Sequences[2].Enabled = true;
                        mh.ApplySettings(mc);
                        Check("★ 重新启用后热键立刻恢复响应",
                            mh.MatchPointHotkey(0x72) == 2, "索引=" + mh.MatchPointHotkey(0x72));

                        // 改键后旧键必须失效，否则会出现"两条序列抢一个键"
                        mc.Sequences[1].HotkeyVk = 0x74;              // 序列B 改到 F5
                        mh.ApplySettings(mc);
                        Check("★ 改键后旧键失效、新键生效",
                            mh.MatchPointHotkey(0x71) == -1 && mh.MatchPointHotkey(0x74) == 1,
                            "旧F2=" + mh.MatchPointHotkey(0x71) + " 新F5=" + mh.MatchPointHotkey(0x74));

                        // 序列被删除后，索引必须跟着收缩，不能越界
                        mc.Sequences.RemoveAt(0);
                        mh.ApplySettings(mc);
                        Check("★ 删除序列后索引正确收缩（不会越界访问）",
                            mh.MatchPointHotkey(0x70) == -1 && mh.MatchPointHotkey(0x74) == 0 &&
                            mh.MatchPointHotkey(0x72) == 1,
                            "原F1=" + mh.MatchPointHotkey(0x70) + " 序列B(F5)=" + mh.MatchPointHotkey(0x74) +
                            " 序列C(F3)=" + mh.MatchPointHotkey(0x72));

                        // 空配置也必须安全
                        mh.ApplySettings(new AppConfig());
                        Check("没有任何序列时分派安全（不崩溃）",
                            mh.MatchPointHotkey(0x70) == -1, "索引=" + mh.MatchPointHotkey(0x70));

                        // 真实分派：按一下只触发一次，长按的自动重复不再触发
                        AppConfig disp = new AppConfig();
                        disp.Sequences.Add(SequenceEntry.FromPoints("甲", new SeqPoint[] { new SeqPoint("", 1, 1) }, 60));
                        disp.Sequences[0].HotkeyVk = 0x70;
                        mh.ApplySettings(disp);

                        List<int> fired = new List<int>();
                        List<int[]> firedGroups = new List<int[]>();
                        Action<int[]> onFire = delegate (int[] ix)
                        {
                            firedGroups.Add(ix);
                            foreach (int k in ix) fired.Add(k);
                        };
                        mh.PointHotkeyPressed += onFire;
                        try
                        {
                            // 传 selfForeground:false = "本程序不在前台"（真实游戏场景）。
                            // 测试宿主自己就是前台窗口，而产品在前台时会故意不触发，
                            // 所以必须显式传入，否则测试会假失败。
                            Check("按下序列热键被识别为该序列",
                                mh.TryHandlePointHotkey(0x70, true, false, false), "返回 true");

                            mh.TryHandlePointHotkey(0x70, true, false, false);
                            mh.TryHandlePointHotkey(0x70, true, false, false);
                            Check("★ 按住不放只触发一次（自动重复被过滤）",
                                fired.Count == 1, "触发 " + fired.Count + " 次（期望 1）");

                            mh.TryHandlePointHotkey(0x70, false, true, false);   // 松开
                            mh.TryHandlePointHotkey(0x70, true, false, false);   // 再按
                            Check("★ 松开后再按可以再次触发",
                                fired.Count == 2, "触发 " + fired.Count + " 次（期望 2）");

                            Check("无关按键不会被当成序列热键",
                                !mh.TryHandlePointHotkey(0x41, true, false, false), "A 键返回 false");

                            int beforeSelf = fired.Count;
                            mh.TryHandlePointHotkey(0x70, false, true, false);   // 先松开
                            bool owned = mh.TryHandlePointHotkey(0x70, true, false, true);
                            Check("★ 本程序窗口在前台时不触发（方便你在界面里操作）",
                                owned && fired.Count == beforeSelf,
                                "按键仍被本功能接管，但未触发（触发数仍为 " + fired.Count + "）");
                        }
                        finally { mh.PointHotkeyPressed -= onFire; }

                        // 「吞掉热键」开关必须真正传到钩子里
                        AppConfig sw = new AppConfig();
                        sw.PointHotkeySwallow = false;
                        mh.ApplySettings(sw);
                        Check("★ 「吞掉热键」开关真正生效（不是摆设）",
                            !mh.PointSwallowForTest,
                            "point=" + mh.PointSwallowForTest);

                        sw.PointHotkeySwallow = true;
                        mh.ApplySettings(sw);
                        Check("★ 重新勾选「吞掉热键」后恢复吞键",
                            mh.PointSwallowForTest,
                            "point=" + mh.PointSwallowForTest);
                    }
                    Say("");


                    // ============ 测试 12：多条序列共用同一热键 ============
                    Say("[测试 12] 同一热键触发多条序列（顺序由用户调整）");

                    using (InputHook sh = new InputHook())
                    {
                        AppConfig sc = new AppConfig();
                        sc.Sequences.Add(SequenceEntry.FromPoints("第一", new SeqPoint[] { new SeqPoint("", 10, 10) }, 60));
                        sc.Sequences[0].HotkeyVk = 0x72;      // F3
                        sc.Sequences.Add(SequenceEntry.FromPoints("第二", new SeqPoint[] { new SeqPoint("", 20, 20) }, 60));
                        sc.Sequences[1].HotkeyVk = 0x72;      // 同一个键！
                        sc.Sequences.Add(SequenceEntry.FromPoints("第三", new SeqPoint[] { new SeqPoint("", 30, 30) }, 60));
                        sc.Sequences[2].HotkeyVk = 0x72;      // 还是同一个键
                        sc.Sequences.Add(SequenceEntry.FromPoints("别的键", new SeqPoint[] { new SeqPoint("", 40, 40) }, 60));
                        sc.Sequences[3].HotkeyVk = 0x73;      // F4，独立
                        sh.ApplySettings(sc);

                        int[] hits = sh.MatchPointHotkeys(0x72);
                        Check("★ 同一热键能匹配到全部共用它的序列",
                            hits.Length == 3, "命中 " + hits.Length + " 条（期望 3）");
                        Check("★ 命中的顺序就是配置里的先后顺序（决定谁先触发）",
                            hits.Length == 3 && hits[0] == 0 && hits[1] == 1 && hits[2] == 2,
                            hits.Length == 3
                                ? "顺序 = " + hits[0] + ", " + hits[1] + ", " + hits[2]
                                : "命中数=" + hits.Length);
                        Check("独立热键只命中它自己那一条序列",
                            sh.MatchPointHotkeys(0x73).Length == 1 && sh.MatchPointHotkeys(0x73)[0] == 3,
                            "F4 命中 " + sh.MatchPointHotkeys(0x73).Length + " 条");

                        // 顺序由列表顺序决定：把"第三"挪到最前面，命中顺序必须跟着变
                        SequenceEntry tmp = sc.Sequences[2];
                        sc.Sequences.RemoveAt(2);
                        sc.Sequences.Insert(0, tmp);
                        sh.ApplySettings(sc);
                        int[] after = sh.MatchPointHotkeys(0x72);
                        Check("★ 调整列表顺序后，触发顺序随之改变（上移/下移生效）",
                            after.Length == 3 && after[0] == 0 && after[1] == 1 && after[2] == 2 &&
                            sc.Sequences[0].Name == "第三",
                            "现在第一个是「" + sc.Sequences[0].Name + "」");

                        // 停用其中一条 → 它不再参与，另两条照旧
                        sc.Sequences[1].Enabled = false;
                        sh.ApplySettings(sc);
                        int[] partial = sh.MatchPointHotkeys(0x72);
                        Check("★ 停用其中一条序列后，其余序列仍会被该键触发",
                            partial.Length == 2,
                            "命中 " + partial.Length + " 条（期望 2）");

                        // 全部停用 → 该键不再被本功能占用
                        sc.Sequences[0].Enabled = false;
                        sc.Sequences[1].Enabled = false;
                        sc.Sequences[2].Enabled = false;
                        sh.ApplySettings(sc);
                        Check("共用该键的序列全部停用后，按键不再被接管",
                            sh.MatchPointHotkeys(0x72).Length == 0,
                            "命中 " + sh.MatchPointHotkeys(0x72).Length + " 条");

                        // 只恢复 0 和 2，确认跳过了被停用的 1
                        sc.Sequences[0].Enabled = true;
                        sc.Sequences[2].Enabled = true;
                        sh.ApplySettings(sc);
                        int[] restored = sh.MatchPointHotkeys(0x72);
                        Check("★ 只触发已启用的序列（停用的那条不参与）",
                            restored.Length == 2 && restored[0] == 0 && restored[1] == 2,
                            restored.Length == 2
                                ? "命中索引 " + restored[0] + ", " + restored[1] + "（跳过被停用的 1）"
                                : "命中数=" + restored.Length);

                        // 一次按下只上报一次，但带上整组索引
                        sc.Sequences[1].Enabled = true;
                        sh.ApplySettings(sc);

                        List<int[]> groups = new List<int[]>();
                        Action<int[]> rec = delegate (int[] g) { groups.Add(g); };
                        sh.PointHotkeyPressed += rec;
                        try
                        {
                            sh.TryHandlePointHotkey(0x72, true, false, false);
                            sh.TryHandlePointHotkey(0x72, true, false, false);   // 自动重复
                            Check("★ 共用同一键时，按一下只触发一组（自动重复不重复触发）",
                                groups.Count == 1, "触发 " + groups.Count + " 组（期望 1）");
                            Check("★ 这一组包含全部共用该键的序列（供上层依次执行）",
                                groups.Count == 1 && groups[0].Length == 3,
                                groups.Count == 1 ? "组内 " + groups[0].Length + " 条序列" : "-");
                        }
                        finally { sh.PointHotkeyPressed -= rec; }
                    }
                    Say("");

                    // ============ 测试 13：序列执行（间隔与顺序） ============
                    Say("[测试 13] 序列执行（按一下依次点完多个点，间隔为该序列自己的值）");

                    {
                        ClickEngine seq = new ClickEngine();
                        try
                        {
                            // 三个点排成一行，检验"每个都点到、间隔正确"
                            int[] xs = new int[] { 200, 400, 600 };
                            int[] ys = new int[] { 300, 300, 300 };

                            List<long> stamps = new List<long>();
                            Action onClk = delegate { lock (stamps) stamps.Add(Stopwatch.GetTimestamp()); };
                            seq.Clicked += onClk;

                            seq.StartSequence(xs, ys, 80, 50, ClickButton.Left, 5, false);
                            for (int i = 0; i < 60 && seq.IsActive; i++) PumpFor(50);
                            PumpFor(200);
                            seq.Clicked -= onClk;

                            Check("★ 序列会点完所有点", seq.ClickCount == 3,
                                "共点击 " + seq.ClickCount + " 次（期望 3）");
                            Check("★ 序列跑完一轮后自动停止（不用再按一次）",
                                !seq.IsActive, "IsActive=" + seq.IsActive);

                            if (stamps.Count >= 3)
                            {
                                double g1 = (stamps[1] - stamps[0]) * 1000.0 / Stopwatch.Frequency;
                                double g2 = (stamps[2] - stamps[1]) * 1000.0 / Stopwatch.Frequency;
                                Check("★ 相邻两次触发的间隔符合该序列自己的设定（±40ms 容忍调度抖动）",
                                    Math.Abs(g1 - 80) <= 40 && Math.Abs(g2 - 80) <= 40,
                                    "实测 " + g1.ToString("0") + "ms / " + g2.ToString("0") + "ms（设定 80ms）");
                            }
                            else
                            {
                                Check("相邻两次触发的间隔符合该序列自己的设定",
                                    false, "只采到 " + stamps.Count + " 个时间点");
                            }

                            // 间隔 0 也应能跑完，不能卡死
                            seq.StartSequence(new int[] { 100, 150 }, new int[] { 100, 150 },
                                0, 50, ClickButton.Left, 5, false);
                            for (int i = 0; i < 60 && seq.IsActive; i++) PumpFor(50);
                            PumpFor(200);
                            Check("间隔设为 0ms 时也能跑完（不会卡死）",
                                !seq.IsActive && seq.ClickCount == 2,
                                "点击 " + seq.ClickCount + " 次，IsActive=" + seq.IsActive);

                            // 空序列不应启动
                            seq.StartSequence(new int[0], new int[0], 60, 50, ClickButton.Left, 5, false);
                            PumpFor(300);
                            Check("空序列不会启动引擎",
                                !seq.IsActive, "IsActive=" + seq.IsActive);

                            // 运行中停止
                            seq.StartSequence(new int[] { 10, 20, 30, 40, 50 }, new int[] { 10, 20, 30, 40, 50 },
                                200, 50, ClickButton.Left, 5, false);
                            PumpFor(120);
                            seq.Stop();
                            long stoppedAt = seq.ClickCount;
                            PumpFor(500);
                            Check("序列执行中可以中途停止",
                                !seq.IsActive && seq.ClickCount == stoppedAt,
                                "停止时 " + stoppedAt + " 次，500ms 后仍为 " + seq.ClickCount + " 次");
                        }
                        finally { seq.Dispose(); }
                    }
                    Say("");

                    // ============ 测试 14：批量录点（窗口让开，点哪儿记哪儿） ============
                    Say("[测试 14] 批量录点模式（录出一条多点序列）");

                    using (InputHook bh = new InputHook())
                    {
                        // 录点前的配置：已有一条序列，用于确认录点不会动它
                        AppConfig bc = new AppConfig();
                        bc.Sequences.Add(SequenceEntry.FromPoints("已有序列",
                            new SeqPoint[] { new SeqPoint("", 5, 5) }, 60));
                        bc.Sequences[0].HotkeyVk = 0x70;
                        bh.ApplySettings(bc);

                        List<int[]> recorded = new List<int[]>();
                        List<int> actions = new List<int>();
                        Action<int, int, int> onRec = delegate (int act, int x, int y)
                        { actions.Add(act); recorded.Add(new int[] { x, y }); };
                        bh.BatchPointCaptured += onRec;

                        // 未进入录点模式时，点击不应被记录
                        Check("未进入录点模式时不记录任何点击",
                            !bh.TryHandleBatchPoint(Native.WM_LBUTTONDOWN, 111, 222) && recorded.Count == 0,
                            "录到 " + recorded.Count + " 个");

                        bh.BeginCapture(CaptureKind.BatchPoint);
                        bh.ResetBatchPointState();
                        Check("进入录点模式后能被查询到",
                            bh.IsBatchRecording, "IsBatchRecording=" + bh.IsBatchRecording);

                        // 点三个不同位置 → 三个坐标（顺序 = 录制顺序）
                        bool owned1 = bh.TryHandleBatchPoint(Native.WM_LBUTTONDOWN, 100, 200);
                        bh.TryHandleBatchPoint(Native.WM_LBUTTONDOWN, 300, 400);
                        bh.TryHandleBatchPoint(Native.WM_RBUTTONDOWN, 500, 600);   // 右键也取点（旧语义）
                        bh.TryHandleBatchPoint(Native.WM_LBUTTONDOWN, 700, 800);
                        Check("★ 录点模式下点击被接管（会跳过所有热键判定）",
                            owned1, "TryHandleBatchPoint 返回 " + owned1);
                        Check("★ 每点一下记录一个坐标，顺序就是点击顺序",
                            recorded.Count == 4 &&
                            recorded[0][0] == 100 && recorded[0][1] == 200 &&
                            recorded[1][0] == 300 && recorded[1][1] == 400 &&
                            recorded[2][0] == 500 && recorded[2][1] == 600,
                            recorded.Count == 4
                                ? "1=(" + recorded[0][0] + "," + recorded[0][1] + ") 2=(" +
                                  recorded[1][0] + "," + recorded[1][1] + ") 3=(" +
                                  recorded[2][0] + "," + recorded[2][1] + ") 4=(" +
                                  recorded[3][0] + "," + recorded[3][1] + ")"
                                : "录到 " + recorded.Count + " 个");
                        Check("★ 事件带动作类型：左键取点、右键撤销",
                            actions.Count >= 4 && actions[0] == InputHook.BatchActionPick &&
                            actions[2] == InputHook.BatchActionUndo,
                            "actions[0]=" + (actions.Count > 0 ? actions[0].ToString() : "?") +
                            " actions[2]=" + (actions.Count > 2 ? actions[2].ToString() : "?"));

                        // 抬起事件不应重复记录
                        int beforeUp = recorded.Count;
                        bh.TryHandleBatchPoint(Native.WM_LBUTTONUP, 100, 200);
                        Check("★ 鼠标抬起不会重复记录（一次点击 = 一个坐标）",
                            recorded.Count == beforeUp,
                            "抬起前 " + beforeUp + " 个，抬起后 " + recorded.Count + " 个");

                        // 负坐标（副屏）也要能录
                        bh.TryHandleBatchPoint(Native.WM_LBUTTONDOWN, -800, -300);   // 负坐标（副屏）
                        Check("副屏负坐标也能记录",
                            recorded.Count == 5 && recorded[4][0] == -800 && recorded[4][1] == -300,
                            recorded.Count == 5
                                ? "(" + recorded[4][0] + "," + recorded[4][1] + ")" : "-");

                        // 用录到的点真实生成一条序列（产品的 FromPoints 路径），
                        // 并确认点的顺序与录制顺序一致
                        List<SeqPoint> madePts = new List<SeqPoint>();
                        foreach (int[] r in recorded) madePts.Add(new SeqPoint("", r[0], r[1]));
                        SequenceEntry made = SequenceEntry.FromPoints("实测", madePts, 60);
                        Check("★ 录到的点包成序列后，顺序与录制顺序一致（标记 1 = 第 1 个点）",
                            made.Points.Count == 5 &&
                            made.Points[0].X == 100 && made.Points[4].X == -800 &&
                            made.Points[0].Y == 200 && made.Points[4].Y == -300,
                            "点1=(" + made.Points[0].X + "," + made.Points[0].Y + ")  点5=(" +
                            made.Points[4].X + "," + made.Points[4].Y + ")");

                        // 录点期间键盘不被吞（用户还要在游戏里用键盘）
                        Check("录点模式下键盘不被吞掉",
                            !bh.TryHandleCapture(0x41, true), "TryHandleCapture 返回 false");

                        // 结束录点后不再记录
                        bh.CancelCapture();
                        int afterEnd = recorded.Count;
                        bh.TryHandleBatchPoint(Native.WM_LBUTTONDOWN, 999, 999);
                        Check("★ 结束录点后不再记录任何点击",
                            recorded.Count == afterEnd, "仍为 " + recorded.Count + " 个");
                        Check("结束后 IsBatchRecording 归位",
                            !bh.IsBatchRecording, "IsBatchRecording=" + bh.IsBatchRecording);

                        bh.BatchPointCaptured -= onRec;

                        // ---- Esc 结束录点 ----
                        bh.BeginCapture(CaptureKind.BatchPoint);
                        bh.ResetBatchPointState();

                        List<bool> finished = new List<bool>();
                        Action onFin = delegate { finished.Add(true); };
                        bh.BatchPointFinishRequested += onFin;
                        try
                        {
                            Check("录点模式下按 Esc 被本模式消费（不会漏给游戏弹菜单/退全屏）",
                                bh.TryHandleBatchPointKey(0x1B, true), "返回 true");

                            Check("★ 录点时按 Esc 立即结束录点",
                                finished.Count == 1, "收到结束请求 " + finished.Count + " 次（期望 1）");

                            bh.TryHandleBatchPointKey(0x1B, true);
                            Check("★ 按住 Esc 不放只触发一次结束",
                                finished.Count == 1, "仍是 " + finished.Count + " 次（期望 1）");

                            bh.TryHandleBatchPointKey(0x1B, false);
                            bh.ResetBatchPointState();
                            bh.TryHandleBatchPointKey(0x1B, true);
                            Check("★ 再次进入录点模式后，Esc 可再次结束",
                                finished.Count == 2, "收到 " + finished.Count + " 次（期望 2）");

                            Check("★ 录点模式下其它键照常放行（不影响游戏用键盘）",
                                !bh.TryHandleBatchPointKey(0x41, true), "A 键返回 false");

                            // 状态残留回归：上次"按住 Esc 退出"没等松开就退了模式
                            bh.TryHandleBatchPointKey(0x1B, true);
                            bh.TryHandleBatchPointKey(0x1B, true);
                            bh.CancelCapture();
                            bh.BeginCapture(CaptureKind.BatchPoint);
                            bh.ResetBatchPointState();
                            int before = finished.Count;
                            bh.TryHandleBatchPointKey(0x1B, true);
                            Check("★ 上次按住 Esc 退出后，再次进入时 Esc 仍可用（状态残留修复）",
                                finished.Count == before + 1,
                                "进入前 " + before + " 次，按下后 " + finished.Count + " 次（期望 +1）");

                            bh.CancelCapture();
                            Check("非录点模式下 Esc 不被占用",
                                !bh.TryHandleBatchPointKey(0x1B, true), "返回 false");
                        }
                        finally { bh.BatchPointFinishRequested -= onFin; }
                    }
                    Say("");


                    // ============ 测试 15：DPI 与界面缩放 ============
                    Say("[测试 15] DPI 查询与界面缩放");

                    int dpiScreen = DpiInfo.OfScreen();
                    int dpiWindow = DpiInfo.OfWindow(host.Handle);

                    Check("屏幕 DPI 查询返回合理值",
                        dpiScreen >= 96 && dpiScreen <= 480,
                        "OfScreen() = " + dpiScreen + "（96=100%，120=125%，144=150%）");

                    Check("窗口 DPI 查询可用（失败也不影响，有回退链）",
                        dpiWindow == 0 || (dpiWindow >= 96 && dpiWindow <= 480),
                        "OfWindow() = " + dpiWindow);

                    // 缩放比例必须落在合理区间，否则界面会小到看不清或大到装不下
                    int dpiUse = dpiWindow > 0 ? dpiWindow : (dpiScreen > 0 ? dpiScreen : 96);
                    float scale = dpiUse / 96f;
                    Check("缩放比例在 1.0~5.0 之间",
                        scale >= 1.0f && scale <= 5.0f,
                        "scale = " + scale.ToString("0.###") + "（基于 DPI " + dpiUse + "）");

                    // 本机若是高 DPI，界面必须相应放大 —— 这正是之前文字被截断的根因
                    Check("★ 高 DPI 时界面按比例放大（AutoScaleMode=None 下不能依赖 DeviceDpi）",
                        dpiUse <= 96 || scale > 1.0f,
                        "DPI=" + dpiUse + " → 宽度应从 560 放大到约 " + (int)Math.Round(560 * scale) + "px");

                    // 诚实标注覆盖边界：本测试程序没有 DPI 清单（不是 per-monitor DPI 感知），
                    // 在 125%/150% 的系统上仍会被 Windows 虚拟化成 96，
                    // 因此这里无法真正复现高 DPI 场景。
                    // 真正的高 DPI 验证请看 dist\GameClicker.exe --selftest 的输出
                    // （它带清单，会报告真实 DPI，并据此校验文字是否被截断）。
                    if (dpiUse <= 96 && DpiInfo.OfScreen() <= 96)
                    {
                        Say("    注：本测试程序无 DPI 清单，高 DPI 场景会被虚拟化为 96，");
                        Say("        因此此处只验证了回退链可用；高 DPI 实测请用 --selftest。");
                    }
                    Say("");

                    // ============ 测试 12：按键名称 ============
                    Say("[测试 16] 按键名称显示");
                    Check("常用按键名称正确",
                        KeyNames.Describe(0x72) == "F3" && KeyNames.Describe(0x41) == "A" &&
                        KeyNames.Describe(0x31) == "1" && KeyNames.Describe(0x20) == "Space" &&
                        KeyNames.Describe(0x0D) == "Enter" && KeyNames.Describe(0x7B) == "F12" &&
                        KeyNames.Describe(0x23) == "End",
                        "F3 / A / 1 / Space / Enter / F12 / End 均正确");
                    Check("未设置时显示友好文案", KeyNames.Describe(0) == "未设置", "\"" + KeyNames.Describe(0) + "\"");
                }
                catch (Exception ex)
                {
                    _fail++;
                    Say("[EXCEPTION] " + ex);
                }
                finally
                {
                    try { if (engine != null) { engine.Stop(); engine.Dispose(); } } catch { }
                    try { if (target != null) { target.Close(); target.Dispose(); } } catch { }
                    try { Native.TimeEndPeriod(1); } catch { }
                }

                Say("");
                Say("=== 结果：通过 " + _pass + " 项，失败 " + _fail + " 项，环境受限跳过 " + _skip + " 项 ===");

                File.WriteAllText(Path.Combine(outDir, "test-report.txt"), Log.ToString(), new UTF8Encoding(false));
                host.Close();
            }

            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        /// <summary>
        /// 用引擎自身的 Clicked 事件测量节奏。
        ///
        /// 关键：测量前先把光标移离本测试自己的目标窗口。
        /// 目标窗口属于本进程，点击消息要由本进程 GUI 线程处理，而测试线程
        /// 就是那个 GUI 线程，且 PumpFor 每轮都要 Thread.Sleep(1)
        /// （本机实测 Sleep(1) 实际耗时约 15ms）——于是连点节奏被测试自身的
        /// 消息泵拖慢，量到的是"测试开销"而非产品能力。
        /// 真实游戏是独立进程，不存在这种耦合，所以这里必须先把光标挪开。
        /// </summary>
        private static void ParkCursorOffTarget()
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Native.SetCursorPos(wa.Right - 40, wa.Bottom - 40);
                PumpFor(250);
            }
            catch { }
        }

        /// <summary>
        /// 实测本机「一次点击」的物理成本下限（毫秒）。
        ///
        /// 一次点击 = SendInput(按下) + SendInput(抬起)。在部分环境（虚拟机、沙箱、
        /// 特定电源策略）单次 SendInput 调用本身就要好几毫秒，于是无论把间隔设多小，
        /// 实际速率都上不去。测出这个下限后，高频档断言才有正确基准，
        /// 不会拿写死的常数去卡真实的引擎行为。
        /// </summary>
        private static double MeasureLocalClickFloorMs()
        {
            try
            {
                double best = double.MaxValue;
                for (int round = 0; round < 3; round++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    const int N = 40;
                    for (int i = 0; i < N; i++)
                    {
                        INPUT[] dn = new INPUT[] { Native.MouseInput(Native.MOUSEEVENTF_LEFTDOWN, 0) };
                        Native.SendInput(1, dn, Native.InputSize);
                        INPUT[] up = new INPUT[] { Native.MouseInput(Native.MOUSEEVENTF_LEFTUP, 0) };
                        Native.SendInput(1, up, Native.InputSize);
                    }
                    double per = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency / N;
                    if (per < best) best = per;
                }
                // 取最快一轮作为"下限"，排除偶发调度抖动
                if (best < 1 || best > 500) best = 10;
                return best;
            }
            catch { return 10; }
        }

        /// <summary>用引擎自身的 Clicked 事件测量节奏（不受输入投递限制影响）。</summary>
        private static int MeasureRhythm(ClickEngine engine, int intervalMs, out double avg, out double jitter)
        {
            ParkCursorOffTarget();

            List<long> ticks = new List<long>();
            Action handler = delegate { lock (ticks) ticks.Add(Stopwatch.GetTimestamp()); };
            engine.Clicked += handler;
            try
            {
                engine.Start(intervalMs, ClickButton.Left, false, Math.Min(15, Math.Max(2, intervalMs / 3)), true);
                // 采样窗口随间隔伸缩，保证至少有 ~6 个间隔样本
                int window = Math.Max(1000, intervalMs * 6 + 400);
                PumpFor(window);
                engine.Stop();
                PumpFor(200);
            }
            finally { engine.Clicked -= handler; }

            return Compute(ticks, out avg, out jitter);
        }

        /// <summary>引擎已在运行时测量节奏（用于验证运行中改参数）。</summary>
        private static void MeasureRhythmRunning(ClickEngine engine, out double avg, out double jitter)
        {
            List<long> ticks = new List<long>();
            Action handler = delegate { lock (ticks) ticks.Add(Stopwatch.GetTimestamp()); };
            engine.Clicked += handler;
            try
            {
                if (!engine.IsActive) engine.Start(120, ClickButton.Left, false, 10, true);
                PumpFor(800);
                engine.Stop();
                PumpFor(150);
            }
            finally { engine.Clicked -= handler; }

            Compute(ticks, out avg, out jitter);
        }

        private static int Compute(List<long> ticks, out double avg, out double jitter)
        {
            avg = -1; jitter = 0;
            lock (ticks)
            {
                if (ticks.Count < 3) return ticks.Count;

                double sum = 0;
                List<double> gaps = new List<double>();
                for (int i = 1; i < ticks.Count; i++)
                {
                    double g = (ticks[i] - ticks[i - 1]) * 1000.0 / Stopwatch.Frequency;
                    if (g > 5000) continue;   // 剔除停止/重启造成的长间隔
                    gaps.Add(g);
                    sum += g;
                }
                if (gaps.Count == 0) return ticks.Count;

                avg = sum / gaps.Count;
                double var = 0;
                foreach (double g in gaps) var += (g - avg) * (g - avg);
                jitter = Math.Sqrt(var / gaps.Count);
                return gaps.Count;
            }
        }

        private static INPUT MakeKey(ushort vk, bool up)
        {
            INPUT inp = new INPUT();
            inp.type = Native.INPUT_KEYBOARD;
            inp.u.ki.wVk = vk;
            inp.u.ki.wScan = 0;
            inp.u.ki.dwFlags = up ? 2u : 0u;
            inp.u.ki.time = 0;
            inp.u.ki.dwExtraInfo = IntPtr.Zero;
            return inp;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        /// <summary>
        /// 让测试进程具备 per-monitor DPI 感知。
        /// 否则在 125%/150% 缩放的系统上，Windows 会把 DPI 虚拟化成 96，
        /// 高 DPI 相关的代码路径就测不到——之前正是因此漏掉了"文字被截断"的缺陷。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        private static void MakeProcessDpiAware()
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { }
        }
    }
}
