// Native.cs —— Win32 互操作层（全局钩子 / SendInput / 高精度计时）
using System;
using System.Runtime.InteropServices;

namespace GameClicker
{
    // ---------------- 结构体 ----------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    // ---------------- 常量与调用 ----------------

    internal static class Native
    {
        // 钩子
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        // 前台窗口查询（游戏配置自动切换用）
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // 键盘 / 鼠标消息
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_XBUTTONUP = 0x020C;

        // LL 钩子 flags
        public const uint LLKHF_INJECTED = 0x10;
        public const uint LLKHF_UP = 0x80;
        public const uint LLMHF_INJECTED = 0x01;

        // SendInput
        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080;
        public const uint MOUSEEVENTF_XUP = 0x0100;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        // 虚拟键
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;   // Alt
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        // GetSystemMetrics
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        /// <summary>标记由本程序注入的事件，避免自触发。</summary>
        public static readonly IntPtr MAGIC_EXTRA_INFO = new IntPtr(unchecked((int)0x1CE0BEEF));

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint TimeEndPeriod(uint uMilliseconds);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>抓取窗口位图（自检截图用）。flags=2 表示 PW_RENDERFULLCONTENT。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        /// <summary>LOGPIXELSX：屏幕水平 DPI。</summary>
        public const int LOGPIXELSX = 88;

        public static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>构造一个鼠标输入事件。</summary>
        public static INPUT MouseInput(uint flags, uint mouseData)
        {
            INPUT inp = new INPUT();
            inp.type = INPUT_MOUSE;
            inp.u.mi.dx = 0;
            inp.u.mi.dy = 0;
            inp.u.mi.mouseData = mouseData;
            inp.u.mi.dwFlags = flags;
            inp.u.mi.time = 0;
            inp.u.mi.dwExtraInfo = MAGIC_EXTRA_INFO;
            return inp;
        }

        /// <summary>
        /// 屏幕坐标 → SendInput 绝对坐标（0..65535）的换算。
        /// 抽成纯函数（虚拟桌面参数由调用方传入）以便单元测试：
        /// 这段换算涉及多显示器、负坐标原点等边界，是"点击总是偏一点"这类问题的常见来源。
        /// </summary>
        public static void ScreenToAbsolute(int x, int y,
            int vx, int vy, int vw, int vh, out int nx, out int ny)
        {
            if (vw <= 1) vw = 2;
            if (vh <= 1) vh = 2;

            long ax = ((long)(x - vx) * 65535L) / (vw - 1);
            long ay = ((long)(y - vy) * 65535L) / (vh - 1);

            if (ax < 0) ax = 0; else if (ax > 65535) ax = 65535;
            if (ay < 0) ay = 0; else if (ay > 65535) ay = 65535;

            nx = (int)ax;
            ny = (int)ay;
        }

        /// <summary>把屏幕坐标转换为虚拟桌面绝对坐标（0..65535）。</summary>
        public static INPUT MouseMoveAbsolute(int x, int y)
        {
            int nx, ny;
            ScreenToAbsolute(x, y,
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN),
                GetSystemMetrics(SM_CYVIRTUALSCREEN),
                out nx, out ny);

            INPUT inp = new INPUT();
            inp.type = INPUT_MOUSE;
            inp.u.mi.dx = nx;
            inp.u.mi.dy = ny;
            inp.u.mi.mouseData = 0;
            inp.u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            inp.u.mi.time = 0;
            inp.u.mi.dwExtraInfo = MAGIC_EXTRA_INFO;
            return inp;
        }

        public static int InputSize { get { return Marshal.SizeOf(typeof(INPUT)); } }
    }

    /// <summary>DPI 查询：界面缩放必须依据真实 DPI，否则高分屏上文字会被切掉。</summary>
    internal static class DpiInfo
    {
        /// <summary>当前窗口所在显示器的 DPI（Win10 1607+）。失败返回 0。</summary>
        public static int OfWindow(IntPtr hwnd)
        {
            try
            {
                uint dpi = Native.GetDpiForWindow(hwnd);
                return (int)dpi;
            }
            catch { return 0; }
        }

        /// <summary>屏幕水平 DPI（老 API，所有系统可用）。</summary>
        public static int OfScreen()
        {
            IntPtr dc = IntPtr.Zero;
            try
            {
                dc = Native.GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) return 0;
                return Native.GetDeviceCaps(dc, Native.LOGPIXELSX);
            }
            catch { return 0; }
            finally
            {
                if (dc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, dc);
            }
        }
    }

    // ---------------- 按键名称 ----------------
    internal static class KeyNames
    {
        /// <summary>把虚拟键码转成便于阅读的名称。</summary>
        public static string Describe(int vk)
        {
            if (vk <= 0) return "未设置";

            switch (vk)
            {
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x13: return "Pause";
                case 0x14: return "CapsLock";
                case 0x1B: return "Esc";
                case 0x20: return "Space";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2C: return "PrintScreen";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x5D: return "Menu";
                case 0x90: return "NumLock";
                case 0x91: return "ScrollLock";
                case 0xC0: return "` ~";
                case 0xBA: return "; :";
                case 0xBB: return "= +";
                case 0xBC: return ", <";
                case 0xBD: return "- _";
                case 0xBE: return ". >";
                case 0xBF: return "/ ?";
                case 0xDB: return "[ {";
                case 0xDC: return "\\ |";
                case 0xDD: return "] }";
                case 0xDE: return "' \"";
            }

            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);          // F1..F24
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();       // 0..9
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();       // A..Z
            if (vk >= 0x60 && vk <= 0x69) return "小键盘" + (vk - 0x60);      // NumPad0..9
            if (vk == 0x6A) return "小键盘 *";
            if (vk == 0x6B) return "小键盘 +";
            if (vk == 0x6D) return "小键盘 -";
            if (vk == 0x6E) return "小键盘 .";
            if (vk == 0x6F) return "小键盘 /";

            try
            {
                System.Windows.Forms.Keys k = (System.Windows.Forms.Keys)vk;
                return k.ToString();
            }
            catch
            {
                return "VK" + vk.ToString("X2");
            }
        }
    }
}
