// InputHook.cs —— 全局低级键鼠钩子：触发键、紧急停止、按键捕获
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameClicker
{
    /// <summary>
    /// 按键捕获的目标。
    /// 用显式枚举而不是多个布尔量（_capturing/_captureMouse/_capturePoint…）：
    /// 热键已经有"连点触发键/记录点热键/记录坐标热键"三种，用布尔量互相推断
    /// 很容易漏掉组合分支，改起来也容易出错。
    /// </summary>
    internal enum CaptureKind
    {
        None = 0,
        TriggerKey = 1,      // 连点触发键（键盘）
        TriggerMouse = 2,    // 连点触发键（鼠标）
        PointKey = 3,        // 「点记录点」热键
        RecordKey = 4,       // 「记录当前鼠标位置」热键
        BatchPoint = 5       // 批量录点：鼠标左键点哪儿就把哪儿记成坐标
    }

    internal sealed class InputHook : IDisposable
    {
        private IntPtr _kbHook = IntPtr.Zero;
        private IntPtr _msHook = IntPtr.Zero;

        // 必须保持委托引用，否则会被 GC 回收导致钩子失效
        private Native.HookProc _kbProc;
        private Native.HookProc _msProc;

        // 当前生效的触发设置
        private volatile bool _enabled = true;
        private volatile int _triggerKind;
        private volatile int _triggerVk;
        private volatile int _triggerMouse;
        private volatile int _triggerMode;      // 0=toggle 1=hold
        private volatile bool _swallow;
        private volatile int _emergencyVk;

        // 记录点热键（可多个，每个坐标点一个）
        // 用快照数组存放：钩子回调在系统线程上跑，替换数组引用是原子的，
        // 避免在回调里遍历一个正在被界面线程修改的 List。
        private volatile int[] _pointVks = new int[0];
        private volatile bool[] _pointEnabled = new bool[0];
        private volatile bool _pointHotkeySwallow;
        private bool[] _pointDown = new bool[0];

        // 记录坐标热键（把当前鼠标位置记下来）

        // 当前正在捕获哪种按键
        private volatile int _capture = (int)CaptureKind.None;

        // 正在为哪个坐标点改键（仅 CaptureKind.PointKey 有效）
        private volatile int _capturePointIndex = -1;

        // 按键物理状态，用于过滤长按自动重复
        private bool _triggerDown;
        private bool _emergencyDown;

        private readonly uint _selfPid;

        public event Action<int> KeyCaptured;
        public event Action<int> MouseCaptured;

        /// <summary>
        /// 坐标点热键的捕获结果：(按键码, 该点在配置中的索引)。
        /// 索引为 -1 表示这次改键不属于某个坐标点。
        /// </summary>
        public event Action<int, int> PointKeyCaptured;

        /// <summary>记录坐标热键的捕获结果。</summary>

        public event Action TriggerDown;
        public event Action TriggerUp;
        public event Action TriggerToggle;
        public event Action EmergencyPressed;

        /// <summary>
        /// 某个热键被按下：参数是共用这个键的**所有**坐标点索引（按配置顺序）。
        /// 只有一个点时常量长度 1；多个点共用时按顺序依次触发。
        /// </summary>
        public event Action<int[]> PointHotkeyPressed;

        /// <summary>记录坐标热键被按下：把当前鼠标位置记录为坐标点。</summary>

        public InputHook()
        {
            _selfPid = (uint)Process.GetCurrentProcess().Id;
        }

        public bool Installed { get { return _kbHook != IntPtr.Zero; } }
        public string InstallError { get; private set; }

        public void Install()
        {
            try
            {
                _kbProc = KeyboardProc;
                _msProc = MouseProc;

                IntPtr hMod = Native.GetModuleHandle(null);
                _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0);
                if (_kbHook == IntPtr.Zero)
                {
                    InstallError = "键盘钩子安装失败，错误码 " + Marshal.GetLastWin32Error();
                    AppPaths.Log(InstallError);
                }

                _msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, hMod, 0);
                if (_msHook == IntPtr.Zero)
                {
                    string msg = "鼠标钩子安装失败，错误码 " + Marshal.GetLastWin32Error();
                    InstallError = (InstallError == null) ? msg : (InstallError + "；" + msg);
                    AppPaths.Log(msg);
                }
            }
            catch (Exception ex)
            {
                InstallError = ex.Message;
                AppPaths.Log("Install 异常: " + ex);
            }
        }

        public void ApplySettings(AppConfig cfg)
        {
            _triggerKind = (int)cfg.TriggerKind;
            _triggerVk = cfg.TriggerVk;
            _triggerMouse = cfg.TriggerMouse;
            _triggerMode = (int)cfg.Mode;
            _swallow = cfg.SwallowTrigger;
            _emergencyVk = cfg.EmergencyVk;


            // 序列热键与「记录坐标热键」共用同一个"吞键"开关
            // （界面上一个复选框同时管这两类键）。
            _pointHotkeySwallow = cfg.PointHotkeySwallow;

            // 序列热键：生成快照数组后整体替换（钩子回调只读，不会被中途改坏）。
            // 注意"同一个键可以被多条序列共用"：那样按一下会依次触发这几条序列。
            int n = cfg.Sequences != null ? cfg.Sequences.Count : 0;
            int[] vks = new int[n];
            bool[] ons = new bool[n];
            for (int i = 0; i < n; i++)
            {
                SequenceEntry s = cfg.Sequences[i];
                vks[i] = s.HotkeyVk;
                ons[i] = s.Enabled && s.HotkeyVk > 0;
            }
            _pointVks = vks;
            _pointEnabled = ons;
            _pointDown = new bool[n];

            // 触发键被改掉时复位物理状态，避免残留
            _triggerDown = false;
        }

        /// <summary>
        /// 坐标点热键的判定与分派；返回 true 表示这个按键归某个坐标点管。
        ///
        /// 单独抽出来是为了让自动化测试能直接驱动**真实**的分派与去重逻辑。
        /// 注意：不能靠"注入一个按键再观察"来测——产品为避免自触发，
        /// 会主动丢弃带 LLKHF_INJECTED 标记的按键，注入的测试按键根本到不了这里。
        /// </summary>
        internal bool TryHandlePointHotkey(int vk, bool down, bool up)
        {
            return TryHandlePointHotkey(vk, down, up, IsSelfForeground());
        }

        /// <summary>
        /// 分派核心；把"本程序是否在前台"作为显式参数传入，
        /// 这样测试可以分别验证两种情形，而不必去操纵真实前台窗口（那样不稳定）。
        /// </summary>
        internal bool TryHandlePointHotkey(int vk, bool down, bool up, bool selfForeground)
        {
            int[] hits = MatchPointHotkeys(vk);
            if (hits.Length == 0) return false;

            // 用第一个索引作为"这个键的物理状态"，因为它代表这一组
            int pi = hits[0];

            if (_enabled && !selfForeground)
            {
                bool[] downs = _pointDown;
                // 只在"按下的那一刻"触发一次；长按产生的自动重复不会反复触发
                if (down && (pi >= downs.Length || !downs[pi]))
                {
                    if (pi < downs.Length) downs[pi] = true;
                    int[] idx = hits;   // 闭包捕获本次命中的全部索引
                    Raise(() => { Action<int[]> h = PointHotkeyPressed; if (h != null) h(idx); });
                }
                else if (up && pi < downs.Length)
                {
                    downs[pi] = false;   // 松开后才允许下一次触发
                }
            }

            return true;
        }

        /// <summary>
        /// 找出被按下的**所有**序列索引。同一热键可以被多条序列共用
        /// （此时按一下会依次触发这几条），顺序即配置里的先后顺序。
        /// 没有匹配返回空数组。
        /// </summary>
        internal int[] MatchPointHotkeys(int vk)
        {
            if (vk <= 0) return new int[0];
            int[] vks = _pointVks;
            bool[] ons = _pointEnabled;
            if (vks == null || ons == null) return new int[0];

            int count = Math.Min(vks.Length, ons.Length);
            List<int> hits = null;
            for (int i = 0; i < count; i++)
            {
                if (ons[i] && vks[i] == vk)
                {
                    if (hits == null) hits = new List<int>(2);
                    hits.Add(i);
                }
            }
            return hits == null ? new int[0] : hits.ToArray();
        }

        /// <summary>
        /// 找出被按下的坐标点索引；没有匹配返回 -1。
        /// 同一按键被多个点共用时返回第一个（详细信息用 MatchPointHotkeys）。
        /// </summary>
        internal int MatchPointHotkey(int vk)
        {
            int[] all = MatchPointHotkeys(vk);
            return all.Length > 0 ? all[0] : -1;
        }

        /// <summary>供自动化测试读取「吞掉热键」是否真正生效（避免开关只是摆设）。</summary>
        internal bool PointSwallowForTest { get { return _pointHotkeySwallow; } }


        /// <summary>
        /// 批量录点的鼠标事件处理；返回 true 表示"当前处于录点模式，该事件已被本分支处理"。
        ///
        /// 事件语义：左键 = 取点，右键 = 删除上一个点（界面据此处理）。
        /// 单独抽出来是为了让自动化测试能直接驱动**真实**的录点判定：
        /// 注入的点击会被产品按设计丢弃（防自触发），所以无法靠"注入一个点击再观察"来测。
        /// 传入的坐标就是真实钩子里取到的 MSLLHOOKSTRUCT.pt。
        /// </summary>
        internal bool TryHandleBatchPoint(int mouseMsg, int x, int y)
        {
            if (_capture != (int)CaptureKind.BatchPoint) return false;

            // 只在"按下"时处理：按下+抬起算一次动作，否则会重复触发两遍
            if (IsDownMessage(mouseMsg))
            {
                // 左键 = 取点；右键 = 撤销上一个点
                bool isRight = (mouseMsg == Native.WM_RBUTTONDOWN);
                int action = isRight ? BatchActionUndo : BatchActionPick;
                Raise(() =>
                {
                    Action<int, int, int> h = BatchPointCaptured;
                    if (h != null) h(action, x, y);
                });
            }

            return true;   // 录点模式下所有鼠标事件都不再走热键判定
        }

        /// <summary>批量录点的鼠标动作类型。</summary>
        internal const int BatchActionPick = 0;   // 左键：取点
        internal const int BatchActionUndo = 1;   // 右键：撤销上一个点

        /// <summary>当前是否处于批量录点模式（供界面/测试查询）。</summary>
        internal bool IsBatchRecording { get { return _capture == (int)CaptureKind.BatchPoint; } }

        /// <summary>批量录点时按下 Esc 表示"录完了"，界面据此回到确认流程。</summary>
        public event Action BatchPointFinishRequested;

        /// <summary>批量录点时的鼠标动作：参数为（动作类型, 屏幕X, 屏幕Y）。左键取点、右键撤销。</summary>
        public event Action<int, int, int> BatchPointCaptured;

        /// <summary>Esc 的物理状态，避免长按重复触发结束。</summary>
        private bool _batchEscDown;

        /// <summary>
        /// 进入批量录点模式时调用：复位 Esc 的物理状态。
        /// 否则上一次"按住 Esc 退出"没等到松开就退了模式，
        /// 下次进入后第一次按 Esc 会被当成"还按着"而不触发——表现为"Esc 失灵"。
        /// </summary>
        public void ResetBatchPointState()
        {
            _batchEscDown = false;
        }


        /// <summary>
        /// 批量录点期间的键盘处理；返回 true 表示该键已被本模式消费（钩子应吞掉）。
        ///
        /// 单独抽出来是为了让自动化测试能直接驱动**真实**的分支逻辑：
        /// 注入的按键会被产品按设计丢弃（防自触发），无法靠"注入 Esc 再观察"来测。
        /// </summary>
        internal bool TryHandleBatchPointKey(int vk, bool down)
        {
            if (_capture != (int)CaptureKind.BatchPoint) return false;

            // Esc = 录完了，回到界面确认。
            // 故意吞掉这个键：它在本模式里是"结束录点"的控制键，
            // 再漏给游戏的话，游戏可能同时弹出菜单或退出全屏，反而干扰。
            if (vk == 0x1B)
            {
                if (down && !_batchEscDown)
                {
                    _batchEscDown = true;
                    AppPaths.Log("[诊断] Esc down 触发结束录点");
                    Raise(() => { Action h = BatchPointFinishRequested; if (h != null) h(); });
                }
                else if (!down)
                {
                    _batchEscDown = false;   // 松开后才允许下次再触发
                }
                return true;
            }

            // 其它键一律放行：用户可能还要在游戏里用键盘
            return false;
        }

        public void SetArmed(bool armed) { _enabled = armed; }

        public void BeginCapture(CaptureKind kind)
        {
            _capture = (int)kind;
            _capturePointIndex = -1;
        }

        /// <summary>为指定索引的坐标点改键。捕获结果通过 PointKeyCaptured 上报。</summary>
        public void BeginPointKeyCapture(int pointIndex)
        {
            _capture = (int)CaptureKind.PointKey;
            _capturePointIndex = pointIndex;
        }

        public void CancelCapture()
        {
            _capture = (int)CaptureKind.None;
            _capturePointIndex = -1;
        }

        /// <summary>当前是否正在等待某个按键（供界面查询）。</summary>
        public bool IsCapturing { get { return _capture != (int)CaptureKind.None; } }

        /// <summary>
        /// 按键捕获分派：处于捕获模式时把按键送达对应事件，返回 true 表示钩子应吞掉该键。
        /// 单独抽出来是为了让自动化测试能直接驱动**真实**的分派逻辑，
        /// 而不是在测试里照抄一份（那样测试通过了也不能说明产品代码正确）。
        /// </summary>
        internal bool TryHandleCapture(int vk, bool down)
        {
            if (!down) return false;

            int cap = _capture;
            if (cap == (int)CaptureKind.None) return false;
            if (cap == (int)CaptureKind.TriggerMouse) return false;   // 等鼠标，不消费键盘
            // 批量录点等的是鼠标点击，键盘不该被吃掉（否则用户没法在游戏里用键盘）
            if (cap == (int)CaptureKind.BatchPoint) return false;

            _capture = (int)CaptureKind.None;    // 捕获是一次性的

            if (cap == (int)CaptureKind.PointKey)
            {
                int idx = _capturePointIndex;
                _capturePointIndex = -1;
                Raise(() => { Action<int, int> h = PointKeyCaptured; if (h != null) h(vk, idx); });
            }
            else
                Raise(() => { Action<int> h = KeyCaptured; if (h != null) h(vk); });

            return true;
        }

        // ---------------- 键盘钩子 ----------------

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);

            try
            {
                KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if ((data.flags & Native.LLKHF_INJECTED) != 0)
                    return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                bool down = (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN);
                bool up = (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP);
                int vk = (int)data.vkCode;

                // 1) 捕获模式（键盘类目标）—— 走与自动化测试相同的分派逻辑
                if (TryHandleCapture(vk, down))
                    return new IntPtr(1);

                // 2) 紧急停止键（只在首次按下时触发一次）
                if (_emergencyVk > 0 && vk == _emergencyVk)
                {
                    if (down)
                    {
                        if (!_emergencyDown)
                        {
                            _emergencyDown = true;
                            Raise(() => { Action h = EmergencyPressed; if (h != null) h(); });
                        }
                        return new IntPtr(1);
                    }
                    if (up)
                    {
                        _emergencyDown = false;
                        return new IntPtr(1);
                    }
                }

                // 批量录点期间：Esc = 结束录点（吞掉，避免同时漏给游戏弹出菜单/退全屏）；
                // 其它键一律放行，用户在游戏里照常用键盘。
                // 紧急停止在上方分支已处理，仍可作为安全出口。
                if (_capture == (int)CaptureKind.BatchPoint)
                {
                    if (TryHandleBatchPointKey(vk, down))
                        return new IntPtr(1);   // Esc 被本模式消费

                    return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
                }

                // 3) 坐标点热键（可多个）：与普通触发键互相独立，
                //    连点进行中也允许用它切到某个坐标点（或停止该点连点）。
                //    放在触发键之前判定，避免两个键设成同一个时行为含糊。
                if (TryHandlePointHotkey(vk, down, up))
                {
                    if (_pointHotkeySwallow) return new IntPtr(1);
                    return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
                }

                // 3b) 记录坐标热键：把当前鼠标位置记下来。
                //     与记录点热键同样互相独立，连点进行中也允许随时记录新坐标。


                // 4) 触发键
                if (_enabled && _triggerKind == 0 && vk == _triggerVk && !IsSelfForeground())
                {
                    if (_triggerMode == 1)   // 按住模式
                    {
                        if (down && !_triggerDown)
                        {
                            _triggerDown = true;
                            Raise(() => { Action h = TriggerDown; if (h != null) h(); });
                        }
                        else if (up && _triggerDown)
                        {
                            _triggerDown = false;
                            Raise(() => { Action h = TriggerUp; if (h != null) h(); });
                        }
                        // 自动重复（down 且已按下）不重复触发，但照样吞掉
                    }
                    else                     // 切换模式
                    {
                        if (down && !_triggerDown)
                        {
                            _triggerDown = true;
                            Raise(() => { Action h = TriggerToggle; if (h != null) h(); });
                        }
                        else if (up)
                        {
                            _triggerDown = false;
                        }
                    }

                    if (_swallow) return new IntPtr(1);
                }
                else if (vk == _triggerVk && up)
                {
                    _triggerDown = false;
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log("KeyboardProc 异常: " + ex.Message);
            }

            return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        // ---------------- 鼠标钩子 ----------------

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

            try
            {
                MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if ((data.flags & Native.LLMHF_INJECTED) != 0)
                    return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                int code = MouseButtonCode(msg, data.mouseData);

                // 0) 批量录点模式：你点屏幕哪儿，就把哪儿记成一个坐标。
                //    这里**不吞掉**点击（走 CallNextHookEx），
                //    因为用户往往就是要点游戏里的按钮来确认位置，吞了反而看不到反应。
                //    同时必须跳过下面的各种热键判定：若触发键恰好是鼠标键，
                //    "录点时的点击"会被当成触发而开始连点，那就乱套了。
                //    坐标提取走真实的产品方法（也是自动化测试驱动的那一个）。
                if (TryHandleBatchPoint(msg, data.pt.X, data.pt.Y))
                {
                    return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
                }

                // 1) 捕获鼠标按键（仅当目标就是鼠标时）
                if (_capture == (int)CaptureKind.TriggerMouse && code >= 0 && IsDownMessage(msg))
                {
                    _capture = (int)CaptureKind.None;
                    Raise(() => { Action<int> h = MouseCaptured; if (h != null) h(code); });
                    return new IntPtr(1);
                }

                // 2) 鼠标触发键
                if (_enabled && _triggerKind == 1 && code >= 0 && code == _triggerMouse && !IsSelfForeground())
                {
                    bool down = IsDownMessage(msg);
                    if (_triggerMode == 1)
                    {
                        if (down && !_triggerDown)
                        {
                            _triggerDown = true;
                            Raise(() => { Action h = TriggerDown; if (h != null) h(); });
                        }
                        else if (!down && _triggerDown)
                        {
                            _triggerDown = false;
                            Raise(() => { Action h = TriggerUp; if (h != null) h(); });
                        }
                    }
                    else
                    {
                        if (down && !_triggerDown)
                        {
                            _triggerDown = true;
                            Raise(() => { Action h = TriggerToggle; if (h != null) h(); });
                        }
                        else if (!down)
                        {
                            _triggerDown = false;
                        }
                    }

                    if (_swallow) return new IntPtr(1);
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log("MouseProc 异常: " + ex.Message);
            }

            return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        private static bool IsDownMessage(int msg)
        {
            return msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                   msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN;
        }

        /// <summary>鼠标消息 → 按键编号：0左 1右 2中 3侧键X1 4侧键X2；非按键返回 -1。</summary>
        private static int MouseButtonCode(int msg, uint mouseData)
        {
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                case Native.WM_LBUTTONUP:
                    return 0;
                case Native.WM_RBUTTONDOWN:
                case Native.WM_RBUTTONUP:
                    return 1;
                case Native.WM_MBUTTONDOWN:
                case Native.WM_MBUTTONUP:
                    return 2;
                case Native.WM_XBUTTONDOWN:
                case Native.WM_XBUTTONUP:
                    uint which = (mouseData >> 16) & 0xFFFF;
                    return which == 2 ? 4 : 3;   // XBUTTON1=3, XBUTTON2=4
                default:
                    return -1;
            }
        }

        // ---------------- 辅助 ----------------

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        /// <summary>本程序窗口在前台时不做触发，方便用户在界面里正常操作设置。</summary>
        private bool IsSelfForeground()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return pid == _selfPid;
            }
            catch { return false; }
        }

        /// <summary>钩子回调里绝不能抛异常到系统，统一包一层。</summary>
        private static void Raise(Action action)
        {
            try { action(); }
            catch (Exception ex) { AppPaths.Log("钩子回调异常: " + ex.Message); }
        }

        public void Dispose()
        {
            try
            {
                if (_kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
                if (_msHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
            }
            catch { }
        }
    }
}
