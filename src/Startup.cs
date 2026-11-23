// Startup.cs —— 开机自启注册
//
// 为什么优先用任务计划程序而不是注册表 Run 键：
//   连点器通常需要管理员权限才能在更高完整性的游戏里生效。
//   而注册表 Run 键启动的程序会触发 UAC 弹窗，用户每次开机都得点"是"。
//   因此优先使用「任务计划程序」：以最高权限创建登录触发任务，开机静默启动、不弹 UAC。
//   只有在创建任务失败（例如非管理员）时才降级到注册表 Run 键。
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace GameClicker
{
    internal static class Startup
    {
        private const string TaskName = "GameClickerAutoStart";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "GameClicker";
        /// <summary>历史任务名：早期版本用守护进程实现开机自启，改名后需清理，避免残留。</summary>
        private const string LegacyTaskName = "GameClickerDaemon";

        /// <summary>当前进程是否以管理员身份运行（统一走 Elevation，避免两份实现漂移）。</summary>
        public static bool IsElevated
        {
            get { return Elevation.IsElevated; }
        }

        /// <summary>应用开机自启设置。返回是否成功。</summary>
        public static bool Apply(bool enabled, out string detail)
        {
            detail = "";
            if (enabled)
            {
                // 首选：任务计划程序（静默、免 UAC）
                if (IsElevated && CreateScheduledTask(out detail))
                {
                    string ignoredKey;
                    RemoveRunKey(out ignoredKey);   // 避免注册表与计划任务重复启动
                    detail = "已通过任务计划程序设置开机自启（后台静默启动，不会弹 UAC）";
                    return true;
                }

                // 降级：注册表 Run 键
                string why = detail;
                if (WriteRunKey(out detail))
                {
                    detail = "已设置开机自启。" + Environment.NewLine +
                             "注意：当前方式在开机时会弹出一次 UAC 确认。" + Environment.NewLine +
                             "若希望完全静默，请以管理员身份运行本程序后再次保存设置。" +
                             (string.IsNullOrEmpty(why) ? "" : Environment.NewLine + "（计划任务失败原因：" + why + "）");
                    return true;
                }
                return false;
            }

            // 关闭自启：两种方式都要清掉
            string d1, d2;
            bool a = RemoveScheduledTask(out d1);
            bool b = RemoveRunKey(out d2);
            detail = "已取消开机自启。";
            return a || b || true;
        }

        /// <summary>查询当前是否已注册开机自启（任一种方式）。</summary>
        public static bool IsRegistered()
        {
            try
            {
                string outp;
                if (RunSchtasks("/query /tn \"" + TaskName + "\"", out outp))
                {
                    if (outp.IndexOf(TaskName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key != null && key.GetValue(RunValueName) != null) return true;
                }
            }
            catch { }

            return false;
        }

        // ---------------- 任务计划程序 ----------------

        private static bool CreateScheduledTask(out string error)
        {
            error = "";

            // 先删掉同名旧任务，保证参数是新的
            string ignored;
            RunSchtasks("/delete /tn \"" + TaskName + "\" /f", out ignored);

            // 早期版本以守护进程实现，任务名不同；一并清掉，避免开机启动两个实例
            RunSchtasks("/delete /tn \"" + LegacyTaskName + "\" /f", out ignored);

            string tr = "\"" + AppPaths.ExePath + "\" --tray";

            // /RL HIGHEST       以最高权限运行 → 游戏在更高完整性时也能生效
            // /SC ONLOGON       登录时自动启动
            // /F                覆盖同名任务
            // /DELAY 0000:10    登录后延迟 10 秒，避开开机高峰
            string args =
                "/create /tn \"" + TaskName + "\" " +
                "/tr " + QuoteArg(tr) + " " +
                "/sc ONLOGON /rl HIGHEST /f /delay 0000:10";

            string output;
            bool ok = RunSchtasks(args, out output);

            if (!ok)
            {
                error = LastMeaningfulLine(output);
                AppPaths.Log("创建计划任务失败: " + output);
                return false;
            }

            if (output.IndexOf("成功", StringComparison.Ordinal) < 0 &&
                output.IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) < 0)
            {
                error = LastMeaningfulLine(output);
                AppPaths.Log("创建计划任务返回可疑: " + output);
                return false;
            }

            AppPaths.Log("已创建计划任务 " + TaskName);
            return true;
        }

        private static bool RemoveScheduledTask(out string detail)
        {
            detail = "";
            string output;
            bool ok = RunSchtasks("/delete /tn \"" + TaskName + "\" /f", out output);
            if (ok) AppPaths.Log("已删除计划任务 " + TaskName);
            else detail = LastMeaningfulLine(output);
            return ok;
        }

        /// <summary>schtasks 参数里带空格/引号的路径需要再包一层转义。</summary>
        private static string QuoteArg(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        private static bool RunSchtasks(string args, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    output = (stdout + "\n" + stderr).Trim();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private static string LastMeaningfulLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string[] lines = text.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string s = lines[i].Trim();
                if (s.Length > 0 && !s.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase)) return s;
            }
            return text.Trim();
        }

        // ---------------- 注册表 Run 键（降级方案） ----------------

        private static bool WriteRunKey(out string error)
        {
            error = "";
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) { error = "无法打开注册表 Run 键"; return false; }
                    key.SetValue(RunValueName, "\"" + AppPaths.ExePath + "\" --tray", RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool RemoveRunKey(out string detail)
        {
            detail = "";
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return false;
                    if (key.GetValue(RunValueName) != null)
                    {
                        key.DeleteValue(RunValueName, false);
                        return true;
                    }
                }
            }
            catch (Exception ex) { detail = ex.Message; }
            return false;
        }
    }
}
