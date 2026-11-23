// ClickEngine.cs —— 连点引擎（后台线程）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace GameClicker
{
    internal enum ClickButton
    {
        Left = 0,
        Right = 1,
        Middle = 2
    }

    /// <summary>连点引擎：在独立高优先级线程里按设定间隔发送鼠标点击。</summary>
    internal sealed class ClickEngine
    {
        private readonly Thread _thread;
        private volatile bool _stop;

        // 运行参数（GUI 线程写，引擎线程读）
        private volatile bool _active;
        private volatile int _intervalMs = 50;
        private volatile int _button = 0;
        private volatile int _holdMs = 15;
        private volatile bool _followCursor = true;
        private volatile bool _trackPixels = true;
        private volatile int _lockX;
        private volatile int _lockY;

        // ---- 记录点模式：固定点过去点击 ----
        private volatile bool _useTarget;      // true=忽略鼠标位置，直接点 (_targetX,_targetY)
        private volatile int _targetX;
        private volatile int _targetY;
        private volatile bool _returnCursor;   // 结束后把鼠标送回原处
        private volatile bool _oneShot;        // 只点一次就自动停止
        private int _savedX;                   // 会话开始时鼠标所在位置
        private int _savedY;

        private long _count;

        /// <summary>每完成一次点击后触发（引擎线程）。UI 与自动化测试都靠它观测节奏。</summary>
        public event Action Clicked;

        /// <summary>最近一次 SendInput 实际接受的鼠标事件数，0 表示被系统拦截。</summary>
        public volatile int LastSendAccepted;

        /// <summary>已点击次数。</summary>
        public long ClickCount { get { return Interlocked.Read(ref _count); } }

        public bool IsActive { get { return _active; } }

        /// <summary>
        /// 当前正在连点哪个坐标点（配置里的索引）；-1 表示不是坐标点模式。
        /// 界面用它判断"再按一次热键"是停止当前点，还是切换到另一个点。
        /// </summary>
        public volatile int LastPointIndex = -1;

        // ---- 序列模式：一个热键依次点多个坐标 ----
        private volatile bool _useSequence;
        private volatile int[] _seqX = new int[0];
        private volatile int[] _seqY = new int[0];
        private volatile int _seqGapMs = 60;
        /// <summary>序列跑完后要回到哪个坐标点（用于界面显示/状态栏）；-1 表示无。</summary>
        public volatile int LastSequenceFirstPoint = -1;

        /// <summary>当前是否处于序列模式（一个热键依次点多个坐标）。</summary>
        public bool IsSequenceMode { get { return _active && _useSequence; } }

        /// <summary>本次序列共有几个坐标（供状态显示）。</summary>
        public int SequenceLength { get { return _seqX.Length; } }

        /// <summary>引擎线程异常信息，读取后自动清空。</summary>
        public string LastError;

        public ClickEngine()
        {
            _stop = false;
            LastError = null;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Name = "GameClicker.Engine";
            _thread.Start();
        }

        /// <summary>启动连点。followCursor=true 跟随鼠标，false 锁定当前位置。</summary>
        public void Start(int intervalMs, ClickButton button, bool followCursor, int holdMs, bool trackPixels)
        {
            POINT cur;
            if (!Native.GetCursorPos(out cur)) { cur.X = 0; cur.Y = 0; }

            _intervalMs = Clamp(intervalMs, 1, 60000);
            _button = (int)button;
            _followCursor = followCursor;
            _holdMs = Clamp(holdMs, 1, 1000);
            _trackPixels = trackPixels;

            _useTarget = false;
            _oneShot = false;
            _returnCursor = false;
            _savedX = cur.X;
            _savedY = cur.Y;
            _lockX = cur.X;
            _lockY = cur.Y;

            Interlocked.Exchange(ref _count, 0);
            _active = true;
        }

        /// <summary>
        /// 启动"点到记录的坐标"。
        /// oneShot=true 时点一次就自动停止；false 时按 intervalMs 持续点该坐标（再按热键停止）。
        /// returnCursor=true 时，在停止后把鼠标送回启动前的位置，不打断用户操作。
        /// </summary>
        public void StartAtPoint(int x, int y, int intervalMs, ClickButton button,
            int holdMs, bool oneShot, bool returnCursor)
        {
            POINT cur;
            if (!Native.GetCursorPos(out cur)) { cur.X = 0; cur.Y = 0; }

            _intervalMs = Clamp(intervalMs, 1, 60000);
            _button = (int)button;
            _holdMs = Clamp(holdMs, 1, 1000);

            _targetX = x;
            _targetY = y;
            _useTarget = true;
            _oneShot = oneShot;
            _returnCursor = returnCursor;
            _trackPixels = false;      // 目标点模式由 _targetX/Y 决定位置，不做拉回
            _followCursor = false;
            _savedX = cur.X;
            _savedY = cur.Y;
            _lockX = x;
            _lockY = y;

            Interlocked.Exchange(ref _count, 0);
            _active = true;
        }

        /// <summary>
        /// 启动"一个热键依次点多个坐标"。
        ///
        /// 这是给"多个坐标共用同一个热键"用的：按下热键后，按数组顺序
        /// 每个坐标点一次，相邻两次之间等待 gapMs，跑完一轮即自动结束。
        /// 顺序就是数组顺序（由界面上的"上移/下移"决定）。
        /// returnCursor=true 时，整轮结束后把鼠标送回启动前的位置。
        /// </summary>
        public void StartSequence(int[] xs, int[] ys, int gapMs,
            int intervalMs, ClickButton button, int holdMs, bool returnCursor)
        {
            POINT cur;
            if (!Native.GetCursorPos(out cur)) { cur.X = 0; cur.Y = 0; }

            _intervalMs = Clamp(intervalMs, 1, 60000);
            _button = (int)button;
            _holdMs = Clamp(holdMs, 1, 1000);
            _seqGapMs = Clamp(gapMs, 0, 60000);

            _seqX = xs ?? new int[0];
            _seqY = ys ?? new int[0];

            _useSequence = _seqX.Length > 0;
            _useTarget = false;
            _oneShot = false;
            _returnCursor = returnCursor;
            _trackPixels = false;
            _followCursor = false;
            _savedX = cur.X;
            _savedY = cur.Y;

            Interlocked.Exchange(ref _count, 0);
            _active = _useSequence;
        }

        /// <summary>停止后把鼠标送回本次会话开始前的位置（仅记录点模式且开启归位时生效）。</summary>
        private void RestoreCursorIfNeeded()
        {
            if (!_returnCursor) return;
            try
            {
                POINT cur;
                if (!Native.GetCursorPos(out cur)) return;
                if (Math.Abs(cur.X - _savedX) <= 1 && Math.Abs(cur.Y - _savedY) <= 1) return;

                INPUT mv = Native.MouseMoveAbsolute(_savedX, _savedY);
                INPUT[] arr = new INPUT[] { mv };
                Native.SendInput(1, arr, Native.InputSize);
            }
            catch { }
        }

        /// <summary>只更新参数，不重置锁定点与计数（运行中改设置用）。</summary>
        public void UpdateLive(int intervalMs, ClickButton button, int holdMs, bool followCursor, bool trackPixels)
        {
            _intervalMs = Clamp(intervalMs, 1, 60000);
            _button = (int)button;
            _holdMs = Clamp(holdMs, 1, 1000);

            // 记录点/序列热键正在跑时，不要被界面设置覆盖掉目标点语义
            if (_useTarget || _useSequence) return;

            _followCursor = followCursor;
            _trackPixels = trackPixels;

            // 从锁定模式切回跟随模式时无需处理；反向切换时重新锁定到当前位置
            if (!followCursor)
            {
                POINT cur;
                if (Native.GetCursorPos(out cur)) { _lockX = cur.X; _lockY = cur.Y; }
            }
        }

        /// <summary>当前是否在点记录点（而非跟随鼠标）。</summary>
        public bool IsPointMode { get { return _active && _useTarget; } }

        public void Stop()
        {
            bool wasActive = _active;
            bool wasSeq = _useSequence;
            _active = false;
            _useSequence = false;

            // 记录点/序列模式下若开启"归位"，停止时把鼠标送回原处。
            // 序列自然跑完时由 RunSequenceOnce 负责归位，这里负责"被中途打断"的情况
            // （RestoreCursorIfNeeded 内部会检查 _returnCursor 开关）。
            if (wasActive && (_useTarget || wasSeq)) RestoreCursorIfNeeded();

            LastPointIndex = -1;
            LastSequenceFirstPoint = -1;
        }

        /// <summary>把锁定点改到鼠标当前位置。</summary>
        public void RelockHere()
        {
            POINT cur;
            if (Native.GetCursorPos(out cur)) { _lockX = cur.X; _lockY = cur.Y; }
        }

        public void Dispose()
        {
            _active = false;
            _stop = true;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private void Loop()
        {
            while (!_stop)
            {
                if (!_active)
                {
                    Thread.Sleep(2);
                    continue;
                }

                // 序列模式：一趟跑完全部坐标，然后自动结束（不是持续循环）
                if (_useSequence)
                {
                    RunSequenceOnce();
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                try
                {
                    FireOnce();
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }

                // 单次模式：点一下就够了，立刻停下（并把鼠标归位）
                if (_oneShot)
                {
                    _active = false;
                    RestoreCursorIfNeeded();
                    _oneShot = false;
                    continue;
                }

                // 精确等待到下一个周期（高精度计时 + 末尾自旋补齐）
                int interval = _intervalMs;
                WaitUntil(started, interval);
            }
        }

        /// <summary>
        /// 依次点击本轮的每个坐标，相邻之间等 gapMs，全部完成后自动停止。
        /// 中途 Stop() 会立刻退出（每次等待都检查 _active）。
        /// </summary>
        private void RunSequenceOnce()
        {
            int[] xs = _seqX;
            int[] ys = _seqY;
            int n = Math.Min(xs.Length, ys.Length);

            for (int i = 0; i < n; i++)
            {
                if (!_active || _stop) break;

                long started = Stopwatch.GetTimestamp();
                try
                {
                    FireAtPoint(xs[i], ys[i]);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }

                // 最后一个点之后不再等待，避免白白拖长一轮的时间
                if (i < n - 1)
                {
                    WaitUntil(started, _seqGapMs);
                }
            }

            _active = false;
            _useSequence = false;
            RestoreCursorIfNeeded();
        }

        // 系统 Thread.Sleep 的实际粒度（毫秒）。
        // 很多环境（虚拟机、部分电源策略、沙箱）即使调用了 timeBeginPeriod(1)，
        // 睡眠粒度仍可能是 15.6ms。若在"间隔小于粒度"时还去 Sleep，
        // 必定睡过头 —— 于是 10ms 档实际只能跑到约 15.6ms，间隔形同虚设。
        // 启动时实测一次，用于决定"到底该不该睡"。
        private static double _sleepGranularityMs = -1;

        /// <summary>实测本机 Thread.Sleep 的最小实际粒度并缓存。</summary>
        private static double SleepGranularityMs
        {
            get
            {
                if (_sleepGranularityMs > 0) return _sleepGranularityMs;

                double best = double.MaxValue;
                try
                {
                    for (int round = 0; round < 3; round++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        Thread.Sleep(1);
                        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                        if (ms < best) best = ms;
                    }
                }
                catch { best = 16.0; }

                if (best < 0.5 || best > 100) best = 16.0;
                _sleepGranularityMs = best;
                return best;
            }
        }

        /// <summary>
        /// 忙等/睡眠混合，直到距 startTicks 达到 ms 毫秒。
        /// 只有当"剩余时间明显大于睡眠粒度"时才真的睡，否则纯自旋 ——
        /// 这是短间隔档（≤15ms）能贴近目标值的关键。
        /// </summary>
        private void WaitUntil(long startTicks, double ms)
        {
            double remain = ms - Elapsed(startTicks);

            // 只有确信睡下去不会超过目标时才睡（留 1 个粒度的余量）
            double granularity = SleepGranularityMs;
            if (remain > granularity + 1.0)
            {
                int sleep = (int)(remain - granularity);
                if (sleep > 0) Thread.Sleep(sleep);
            }

            // 余下时间用自旋补齐，保证精度
            int guard = 0;
            while (_active && !_stop)
            {
                if (Elapsed(startTicks) >= ms) break;
                Thread.SpinWait(30);
                if (++guard > 5000000) break;   // 极端情况兜底
            }
        }

        private static double Elapsed(long startTicks)
        {
            return (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        }

        private void FireOnce()
        {
            if (_useTarget) FireAtPoint(_targetX, _targetY);
            else FireAtCursor();
        }

        /// <summary>按当前模式点击鼠标所在（或锁定的）位置。</summary>
        private void FireAtCursor()
        {
            int x = 0, y = 0;
            bool needMove = _trackPixels;

            if (needMove)
            {
                if (_followCursor)
                {
                    POINT cur;
                    if (Native.GetCursorPos(out cur)) { x = cur.X; y = cur.Y; }
                    else { x = _lockX; y = _lockY; }
                }
                else
                {
                    x = _lockX;
                    y = _lockY;
                }
            }

            SendClick(needMove, x, y);
        }

        /// <summary>把指针移到指定坐标并点一次（序列模式与单点模式共用）。</summary>
        private void FireAtPoint(int x, int y)
        {
            SendClick(true, x, y);
        }

        /// <summary>
        /// 发送一次完整点击：可选先移动指针，再按下 → 保持 holdMs → 抬起。
        /// 计数与 Clicked 事件都在这里统一维护，保证任何模式下统计口径一致。
        /// </summary>
        private void SendClick(bool moveFirst, int x, int y)
        {
            int btn = _button;
            uint downFlag, upFlag;

            switch (btn)
            {
                case 1:
                    downFlag = Native.MOUSEEVENTF_RIGHTDOWN;
                    upFlag = Native.MOUSEEVENTF_RIGHTUP;
                    break;
                case 2:
                    downFlag = Native.MOUSEEVENTF_MIDDLEDOWN;
                    upFlag = Native.MOUSEEVENTF_MIDDLEUP;
                    break;
                default:
                    downFlag = Native.MOUSEEVENTF_LEFTDOWN;
                    upFlag = Native.MOUSEEVENTF_LEFTUP;
                    break;
            }

            List<INPUT> down = new List<INPUT>(3);
            if (moveFirst) down.Add(Native.MouseMoveAbsolute(x, y));
            down.Add(Native.MouseInput(downFlag, 0));

            INPUT[] downArr = down.ToArray();
            uint acceptedDown = Native.SendInput((uint)downArr.Length, downArr, Native.InputSize);
            LastSendAccepted = (int)acceptedDown;

            // 保持按下状态 holdMs —— 短按时用自旋保证精度，游戏才能识别到有效点击。
            // 注意：不能因为 hold 略大于几毫秒就去 Sleep，
            // 本机 Sleep 粒度约 15.6ms，那样会让 5ms 的按下实际持续 15ms 以上，
            // 拖慢整体节奏（与 WaitUntil 同一原因）。
            int hold = _holdMs;
            if (hold > 0)
            {
                long t0 = Stopwatch.GetTimestamp();
                double gran = SleepGranularityMs;
                if (hold > gran + 1.0)
                {
                    int sleep = (int)(hold - gran);
                    if (sleep > 0) Thread.Sleep(sleep);
                }
                int guard = 0;
                while (Elapsed(t0) < hold)
                {
                    Thread.SpinWait(30);
                    if (++guard > 5000000) break;
                }
            }

            INPUT[] upArr = new INPUT[] { Native.MouseInput(upFlag, 0) };
            Native.SendInput(1, upArr, Native.InputSize);

            Interlocked.Increment(ref _count);

            Action handler = Clicked;
            if (handler != null)
            {
                try { handler(); }
                catch { }
            }
        }
    }
}
