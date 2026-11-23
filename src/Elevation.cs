// Elevation.cs —— 按设置决定是否以管理员身份启动
//
// 背景：
//   连点器要把点击注入到游戏里，而 Windows 的 UIPI 会拦截低完整性进程向高完整性
//   窗口注入输入。很多游戏以管理员权限运行，所以连点器通常需要提权（见 README）。
//   但"总是提权"和"从不提权"都不理想：
//     · 总是提权 → 只想改改设置的用户也要过 UAC
//     · 从不提权 → 游戏里点击无效
//   因此做成配置项 runAsAdmin（默认开启，与旧版行为一致）。
//
// 为什么清单里用的是 asInvoker（不强制提权）：
//   requestedExecutionLevel 是"编译期"固定的，一旦强制提权，
//   这个 exe 就再也无法以普通权限启动，配置项也就失去了意义。
//   改用 asInvoker + 运行时自我提权，两种行为都能覆盖。
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GameClicker
{
    internal static class Elevation
    {
        /// <summary>UAC 被用户取消时 ShellExecute 抛出的错误码。</summary>
        private const int ERROR_CANCELLED = 1223;

        /// <summary>当前进程是否已具备管理员权限。</summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(
                        System.Security.Principal.WindowsIdentity.GetCurrent());
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 以管理员身份重新启动自己。
        /// 成功启动提权进程返回 true（调用方随后应退出自己）；UAC 被取消返回 false 并给出原因。
        /// </summary>
        public static bool RelaunchElevated(string[] args, out string error)
        {
            error = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.ExePath;
                psi.UseShellExecute = true;      // runas 需要 ShellExecute
                psi.Verb = "runas";
                psi.Arguments = BuildArguments(args);
                psi.WorkingDirectory = AppPaths.BaseDir;   // 不提权时子进程可能落到 system32

                Process.Start(psi);
                AppPaths.Log("已请求提权启动（UAC）");
                return true;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ERROR_CANCELLED)
                {
                    error = "用户在 UAC 提示中选择了「否」";
                    AppPaths.Log("提权被取消，将以普通权限继续运行");
                }
                else
                {
                    error = ex.Message;
                    AppPaths.Log("提权失败: " + ex.Message + "（错误码 " + ex.NativeErrorCode + "）");
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppPaths.Log("提权异常: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 拼接子进程命令行：保留原参数，但去掉提权相关标记，避免无限递归。
        /// 供测试直接验证，故为 internal。
        /// </summary>
        internal static string BuildArguments(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("--elevated-restart");

            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.IsNullOrEmpty(a)) continue;
                    if (a.Equals("--elevated-restart", StringComparison.OrdinalIgnoreCase)) continue;
                    if (a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase)) continue;

                    sb.Append(' ');
                    if (a.IndexOf(' ') >= 0) sb.Append('"').Append(a).Append('"');
                    else sb.Append(a);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 判断是否需要提权后重启自己。
        /// 已提权、或未开启该设置、或带 --no-elevate、或是自检模式，都不需要。
        /// </summary>
        public static bool ShouldElevate(AppConfig cfg, string[] args, bool selfTest)
        {
            if (IsElevated) return false;               // 已经是管理员
            if (selfTest) return false;                 // 自检不打扰用户

            if (args != null)
            {
                foreach (string a in args)
                {
                    if (a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase)) return false;
                }
            }

            return cfg != null && cfg.RunAsAdmin;
        }
    }
}
