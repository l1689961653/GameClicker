// Config.cs —— 配置模型与持久化（config.json 存放在用户目录 %APPDATA%\GameClicker）
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GameClicker
{
    internal enum TriggerKind
    {
        Keyboard = 0,
        Mouse = 1
    }

    internal enum TriggerMode
    {
        Toggle = 0,   // 按一下开始，再按一下停止
        Hold = 1      // 按住时连点，松开停止
    }

    /// <summary>序列里的一个坐标点：只有位置和备注名，触发顺序就是它在列表里的先后。</summary>
    internal sealed class SeqPoint
    {
        public string Name = "";   // 备注名，例如"领取奖励"
        public int X = 0;
        public int Y = 0;

        public SeqPoint() { }

        public SeqPoint(string name, int x, int y)
        {
            Name = name;
            X = x;
            Y = y;
        }

        public SeqPoint Clone()
        {
            return (SeqPoint)MemberwiseClone();
        }

        /// <summary>显示用名称：没写备注时给个兜底，避免编辑框里出现空白。</summary>
        public string DisplayName(int index)
        {
            if (!string.IsNullOrEmpty(Name)) return Name;
            return "点 " + (index + 1);
        }

        public bool HasValidCoord
        {
            get { return X >= -100000 && X <= 100000 && Y >= -100000 && Y <= 100000; }
        }
    }

    /// <summary>
    /// 一条「序列」：按录制顺序排列的一组坐标，共用一个热键，按一下依次点完一轮。
    ///
    /// 单点的序列就是以前的一个坐标点：仍可选「按一次点一次 / 按一下开始再按停止」。
    /// 多点的序列固定为"依次点完一轮"，点与点之间等 GapMs。
    /// </summary>
    internal sealed class SequenceEntry
    {
        public string Name = "";                  // 序列备注名，例如"每日领奖流程"
        public List<SeqPoint> Points = new List<SeqPoint>();
        public int HotkeyVk = 0;                  // 0 = 未设置，不响应
        public bool Enabled = true;               // 是否启用该热键
        public int GapMs = 60;                    // 多点序列：相邻两个点之间等多久
        public int Mode = 0;                      // 仅单点序列有意义：0=点一次 1=连点
        public bool ReturnCursor = false;         // 跑完（或停止）后把鼠标送回原处

        public SequenceEntry() { }

        public SequenceEntry(string name)
        {
            Name = name;
        }

        /// <summary>把批量录点攒下的一组坐标包成一条序列。</summary>
        public static SequenceEntry FromPoints(string name, IList<SeqPoint> pts, int gapMs)
        {
            SequenceEntry s = new SequenceEntry(name);
            foreach (SeqPoint p in pts) s.Points.Add(p.Clone());
            s.GapMs = gapMs;
            return s;
        }

        public SequenceEntry Clone()
        {
            SequenceEntry c = (SequenceEntry)MemberwiseClone();
            c.Points = new List<SeqPoint>();
            foreach (SeqPoint p in Points) c.Points.Add(p.Clone());
            return c;
        }

        /// <summary>显示用名称：没写备注时按点数给个兜底。</summary>
        public string DisplayName(int index)
        {
            if (!string.IsNullOrEmpty(Name)) return Name;
            return "序列 " + (index + 1);
        }

        /// <summary>状态栏/提示用：几个点。</summary>
        public string SizeText
        {
            get { return Points.Count + " 个点"; }
        }

        public bool HasValidCoords
        {
            get
            {
                foreach (SeqPoint p in Points) if (!p.HasValidCoord) return false;
                return Points.Count > 0;
            }
        }
    }

    internal sealed class AppConfig
    {
        // ---- 触发 ----
        public TriggerKind TriggerKind = TriggerKind.Keyboard;
        public int TriggerVk = 0x72;        // 默认 F3
        public int TriggerMouse = 3;        // 0左 1右 2中 3侧键X1 4侧键X2
        public TriggerMode Mode = TriggerMode.Toggle;
        public int EmergencyVk = 0x7B;      // 默认 F12；0 表示禁用
        public bool SwallowTrigger = true;  // 吞掉触发键，避免游戏同时响应

        // ---- 点击 ----
        public int IntervalMs = 50;
        public ClickButton Button = ClickButton.Left;
        public int HoldMs = 15;
        public bool FollowCursor = true;    // true=跟随鼠标 / false=锁定起始位置
        public bool TrackPixels = true;     // 锁定模式下把被移动的指针拉回

        // ---- 序列（批量录点的产物：一组坐标共用一个热键，按录制顺序依次触发） ----
        public List<SequenceEntry> Sequences = new List<SequenceEntry>();

        /// <summary>「记录坐标」热键：按下把当前鼠标位置记为新的坐标点。</summary>

        /// <summary>
        /// 多个坐标共用同一个热键时，相邻两个坐标之间等待多久（毫秒）。
        /// 这就是"谁先触发、隔多久触发下一个"里的间隔。
        /// </summary>
        public int SequenceGapMs = 60;

        // ---- 旧版单坐标字段（仅为兼容读取旧配置，迁移后不再使用） ----
        public bool HasPoint = false;
        public int PointX = 0;
        public int PointY = 0;
        public int PointHotkeyVk = 0x77;    // 旧默认 F8
        public bool PointHotkeySwallow = true;
        public int PointMode = 0;
        public bool PointReturnCursor = false;
        /// <summary>旧配置里存在单坐标字段时为 true，用于一次性迁移到 Points。</summary>
        public bool HadLegacyPoint = false;


        // ---- 程序行为 ----
        public bool AutoStartWithWindows = false;
        public bool MinimizeToTrayOnClose = true;
        public bool StartMinimized = false;
        public bool ShowBalloonTips = true;
        public bool RunAsAdmin = true;      // 启动时自动请求管理员权限（默认开，与旧版行为一致）

        /// <summary>
        /// 这套配置绑定的游戏进程名（如 "starrail.exe"，不含路径，小写比较）。
        /// 为空 = 不绑定，不参与"检测到游戏自动切换"。
        /// </summary>
        public string GameProcess = "";

        /// <summary>这套配置在界面下拉框里显示的名字（运行时由 ProfileManager 填，不写盘）。</summary>
        public string ProfileName = "";

        // ---- 主界面窗口尺寸（用户拖拽调节后保存） ----
        public int WindowW = 0;             // 0 = 未设置（用按内容计算的默认值）
        public int WindowH = 0;             // 0 = 未设置
        public bool WindowMaximized = false; // 上次关闭时是否最大化

        public static string ConfigPath
        {
            get { return Path.Combine(ActiveConfigDir, "config.json"); }
        }

        /// <summary>
        /// 把旧版「程序目录\config.json」迁移到新的用户目录（仅当新位置还没有配置时）。
        /// 迁移后不删除旧文件，留作用户备份；返回 true 表示确实迁移了。
        /// </summary>
        public static bool MigrateLegacyConfig()
        {
            try
            {
                string legacy = AppPaths.LegacyConfigPath;
                if (string.Equals(legacy, ConfigPath, StringComparison.OrdinalIgnoreCase)) return false;
                if (File.Exists(ConfigPath)) return false;   // 新位置已有配置，不覆盖
                if (!File.Exists(legacy)) return false;

                File.Copy(legacy, ConfigPath, false);
                return true;
            }
            catch { return false; }
        }

        public AppConfig Clone()
        {
            AppConfig c = (AppConfig)MemberwiseClone();
            // 浅拷贝会共享同一个 List，必须深拷贝，否则改动副本会污染原对象
            c.Sequences = new List<SequenceEntry>();
            foreach (SequenceEntry s in Sequences) c.Sequences.Add(s.Clone());
            return c;
        }

        // ---------------- 读取 ----------------

        /// <summary>
        /// 剥掉 JSON 里的 // 行注释，让配置文件既能带中文说明、又能被标准解析器读取。
        /// 字符串内部的 //（例如路径 "C:\\a\\b" 或 URL）不会被误伤。
        /// </summary>
        internal static string StripComments(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("//", StringComparison.Ordinal) < 0) return text;

            StringBuilder sb = new StringBuilder(text.Length);
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (inString)
                {
                    sb.Append(c);
                    if (escaped) { escaped = false; }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"') { inString = false; }
                    continue;
                }

                if (c == '"') { inString = true; sb.Append(c); continue; }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    // 跳到行尾，整段注释丢弃
                    while (i < text.Length && text[i] != '\n') i++;
                    if (i < text.Length) sb.Append('\n');
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>当前生效的配置目录（默认 = 根目录；切换 Profile 后指向 Profiles\<名称>）。</summary>
        public static string ActiveConfigDir
        {
            get { return string.IsNullOrEmpty(_activeDir) ? AppPaths.DataDir : _activeDir; }
            set { _activeDir = value; }
        }
        private static string _activeDir = "";

        /// <summary>从指定目录读 config.json（ProfileManager 用）。</summary>
        public static AppConfig LoadFromDir(string dir)
        {
            return LoadFrom(Path.Combine(dir, "config.json"));
        }

        public static void SaveToDir(string dir, AppConfig cfg)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string old = ConfigPath;
                string json = cfg.ToJson();
                if (!cfg.RoundTripSelfCheck())
                {
                    AppPaths.Log("配置序列化自校验失败，本次不保存（保留原文件）");
                    return;
                }
                File.WriteAllText(Path.Combine(dir, "config.json"), json, new UTF8Encoding(false));
            }
            catch { }
        }

        public static AppConfig Load()
        {
            // 旧版把配置放在程序目录，那里会被重新构建/清理动作波及。
            // 首次以新版启动时把老配置搬到用户目录，用户的设置就不会再"变回初始值"。
            if (MigrateLegacyConfig()) AppPaths.Log("已把旧配置迁移到 " + ConfigPath);

            return LoadFrom(ConfigPath);
        }

        /// <summary>从指定路径读取配置（便于自动化测试）。任何异常都安全回退到默认值。</summary>
        public static AppConfig LoadFrom(string path)
        {
            AppConfig cfg = new AppConfig();
            try
            {
                if (!File.Exists(path)) return cfg;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text.Trim())) return cfg;

                return LoadFromText(text);
            }
            catch
            {
                // 配置损坏时回退到默认值，不影响程序启动。
                // 但必须留下日志：否则用户手工改坏（或程序写坏）配置后，
                // 表现是"所有设置莫名回到默认"，完全无从排查。
                try { AppPaths.Log("配置读取异常，已回退默认值: " + "IO/格式错误"); } catch { }
            }
            return cfg;
        }

        /// <summary>从文本解析配置（供 LoadFrom 与序列化自校验共用，保证两者行为一致）。</summary>
        private static AppConfig LoadFromText(string text)
        {
            AppConfig cfg = new AppConfig();
            try
            {
                if (string.IsNullOrEmpty(text.Trim())) return cfg;

                text = StripComments(text);   // 配置文件带 // 说明注释，解析前必须先剥掉

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 4 * 1024 * 1024;
                Dictionary<string, object> map = ser.Deserialize<Dictionary<string, object>>(text);
                if (map == null) return cfg;

                cfg.TriggerKind = (TriggerKind)GetInt(map, "triggerKind", (int)cfg.TriggerKind);
                cfg.TriggerVk = GetInt(map, "triggerVk", cfg.TriggerVk);
                cfg.TriggerMouse = GetInt(map, "triggerMouse", cfg.TriggerMouse);
                cfg.Mode = (TriggerMode)GetInt(map, "mode", (int)cfg.Mode);
                cfg.EmergencyVk = GetInt(map, "emergencyVk", cfg.EmergencyVk);
                cfg.SwallowTrigger = GetBool(map, "swallowTrigger", cfg.SwallowTrigger);

                cfg.IntervalMs = GetInt(map, "intervalMs", cfg.IntervalMs);
                cfg.Button = (ClickButton)GetInt(map, "button", (int)cfg.Button);
                cfg.HoldMs = GetInt(map, "holdMs", cfg.HoldMs);
                cfg.FollowCursor = GetBool(map, "followCursor", cfg.FollowCursor);
                cfg.TrackPixels = GetBool(map, "trackPixels", cfg.TrackPixels);

                // 旧版单坐标字段照旧读入，稍后统一迁移进 Points（保证老用户设置不丢）
                cfg.HasPoint = GetBool(map, "hasPoint", cfg.HasPoint);
                cfg.PointX = GetInt(map, "pointX", cfg.PointX);
                cfg.PointY = GetInt(map, "pointY", cfg.PointY);
                cfg.PointHotkeyVk = GetInt(map, "pointHotkeyVk", cfg.PointHotkeyVk);
                cfg.PointHotkeySwallow = GetBool(map, "pointHotkeySwallow", cfg.PointHotkeySwallow);
                cfg.PointMode = GetInt(map, "pointMode", cfg.PointMode);
                cfg.PointReturnCursor = GetBool(map, "pointReturnCursor", cfg.PointReturnCursor);
                cfg.HadLegacyPoint = map.ContainsKey("pointX") || map.ContainsKey("hasPoint");


                // 新版序列列表（内含兼容旧版坐标点列表的读取）
                cfg.Sequences = ParseSequences(map);



                cfg.AutoStartWithWindows = GetBool(map, "autoStartWithWindows", cfg.AutoStartWithWindows);
                cfg.MinimizeToTrayOnClose = GetBool(map, "minimizeToTrayOnClose", cfg.MinimizeToTrayOnClose);
                cfg.StartMinimized = GetBool(map, "startMinimized", cfg.StartMinimized);
                cfg.ShowBalloonTips = GetBool(map, "showBalloonTips", cfg.ShowBalloonTips);
                cfg.RunAsAdmin = GetBool(map, "runAsAdmin", cfg.RunAsAdmin);
                cfg.WindowW = GetInt(map, "windowW", cfg.WindowW);
                cfg.WindowH = GetInt(map, "windowH", cfg.WindowH);
                cfg.WindowMaximized = GetBool(map, "windowMaximized", cfg.WindowMaximized);
                object gp;
                if (map.TryGetValue("gameProcess", out gp) && gp != null)
                    cfg.GameProcess = Convert.ToString(gp).Trim();
            }
            catch (Exception ex)
            {
                // 配置损坏时回退到默认值，不影响程序启动。
                // 但必须留下日志：否则用户手工改坏（或程序写坏）配置后，
                // 表现是"所有设置莫名回到默认"，完全无从排查。
                try { AppPaths.Log("配置解析失败，已回退默认值: " + ex.Message); } catch { }
            }
            return Sanitize(cfg);
        }

        /// <summary>
        /// 解析序列列表，并兼容读取旧版「坐标点列表」。
        ///
        /// 三种历史格式，按优先级处理：
        ///   1. sequences   —— 当前格式（一条序列含多个点）
        ///   2. points      —— 上一版（每个坐标各自一个热键）→ 每个坐标转成一条单点序列
        ///   3. pointX/Y…   —— 最老版（只有一个记录点）→ 转成一条单点序列
        ///
        /// JavaScriptSerializer 会把 JSON 数组解析成 ArrayList，元素是 Dictionary，
        /// 所以这里按 IEnumerable 遍历并逐个做类型转换。
        /// </summary>
        private static List<SequenceEntry> ParseSequences(Dictionary<string, object> map)
        {
            List<SequenceEntry> list = new List<SequenceEntry>();

            // 1) 当前格式：sequences
            object raw;
            if (map.TryGetValue("sequences", out raw) && raw != null)
            {
                IEnumerable seq = raw as IEnumerable;
                if (seq != null && !(raw is string))
                {
                    foreach (object item in seq)
                    {
                        Dictionary<string, object> d = item as Dictionary<string, object>;
                        if (d == null) continue;

                        SequenceEntry s = new SequenceEntry();
                        object v;
                        if (d.TryGetValue("name", out v) && v != null) s.Name = Convert.ToString(v);
                        s.HotkeyVk = GetInt(d, "hotkeyVk", 0);
                        s.Enabled = GetBool(d, "enabled", true);
                        s.GapMs = GetInt(d, "gapMs", 60);
                        s.Mode = GetInt(d, "mode", 0);
                        s.ReturnCursor = GetBool(d, "returnCursor", false);
                        s.Points = ParseSeqPoints(d);
                        list.Add(s);
                    }
                }
            }
            if (list.Count > 0) return list;

            // 2) 上一版：points（每个坐标各自一个热键）
            object legacyPoints;
            if (map.TryGetValue("points", out legacyPoints) && legacyPoints != null)
            {
                IEnumerable seq = legacyPoints as IEnumerable;
                if (seq != null && !(legacyPoints is string))
                {
                    foreach (object item in seq)
                    {
                        Dictionary<string, object> d = item as Dictionary<string, object>;
                        if (d == null) continue;

                        SeqPoint p = new SeqPoint();
                        object v;
                        if (d.TryGetValue("name", out v) && v != null) p.Name = Convert.ToString(v);
                        p.X = GetInt(d, "x", 0);
                        p.Y = GetInt(d, "y", 0);

                        // 每个旧坐标变成一条单点序列，热键/开关/点击方式/归位全部保留
                        SequenceEntry s = new SequenceEntry(p.Name);
                        s.Points.Add(p);
                        s.HotkeyVk = GetInt(d, "hotkeyVk", 0);
                        s.Enabled = GetBool(d, "enabled", true);
                        s.Mode = GetInt(d, "mode", 0);
                        s.ReturnCursor = GetBool(d, "returnCursor", false);
                        s.GapMs = 60;
                        list.Add(s);
                    }
                }
            }
            if (list.Count > 0) return list;

            // 3) 最老版：单个记录点（pointX/pointY）
            if (map.ContainsKey("pointX") || map.ContainsKey("hasPoint"))
            {
                bool has = GetBool(map, "hasPoint", false);
                int lx = GetInt(map, "pointX", 0);
                int ly = GetInt(map, "pointY", 0);
                if (has && lx >= -100000 && lx <= 100000 && ly >= -100000 && ly <= 100000)
                {
                    SequenceEntry s = new SequenceEntry("旧记录点");
                    s.Points.Add(new SeqPoint("旧记录点", lx, ly));
                    s.HotkeyVk = GetInt(map, "pointHotkeyVk", 0);
                    s.Mode = GetInt(map, "pointMode", 0);
                    s.ReturnCursor = GetBool(map, "pointReturnCursor", false);
                    list.Add(s);
                }
            }

            return list;
        }

        /// <summary>解析一条序列内的点集（"points" 子数组）。</summary>
        private static List<SeqPoint> ParseSeqPoints(Dictionary<string, object> seqMap)
        {
            List<SeqPoint> pts = new List<SeqPoint>();
            object raw;
            if (!seqMap.TryGetValue("points", out raw) || raw == null) return pts;

            IEnumerable arr = raw as IEnumerable;
            if (arr == null || raw is string) return pts;

            foreach (object item in arr)
            {
                Dictionary<string, object> d = item as Dictionary<string, object>;
                if (d == null) continue;

                SeqPoint p = new SeqPoint();
                object v;
                if (d.TryGetValue("name", out v) && v != null) p.Name = Convert.ToString(v);
                p.X = GetInt(d, "x", 0);
                p.Y = GetInt(d, "y", 0);
                pts.Add(p);
            }
            return pts;
        }

        public static AppConfig Sanitize(AppConfig cfg)
        {
            if (cfg.IntervalMs < 1) cfg.IntervalMs = 1;
            if (cfg.IntervalMs > 60000) cfg.IntervalMs = 60000;
            if (cfg.HoldMs < 1) cfg.HoldMs = 1;
            if (cfg.HoldMs > 1000) cfg.HoldMs = 1000;
            if (cfg.TriggerVk < 0 || cfg.TriggerVk > 255) cfg.TriggerVk = 0x72;
            if (cfg.EmergencyVk < 0 || cfg.EmergencyVk > 255) cfg.EmergencyVk = 0;
            if (cfg.TriggerMouse < 0 || cfg.TriggerMouse > 4) cfg.TriggerMouse = 3;
            if (cfg.PointHotkeyVk < 0 || cfg.PointHotkeyVk > 255) cfg.PointHotkeyVk = 0x77;
            if (cfg.PointMode < 0 || cfg.PointMode > 1) cfg.PointMode = 0;

            if (cfg.Sequences == null) cfg.Sequences = new List<SequenceEntry>();

            // 逐条校验：清空空序列、纠正越界值，保证引擎拿到的永远是合法数据
            for (int i = cfg.Sequences.Count - 1; i >= 0; i--)
            {
                SequenceEntry s = cfg.Sequences[i];
                if (s == null) { cfg.Sequences.RemoveAt(i); continue; }
                if (s.Name == null) s.Name = "";
                if (s.Points == null) s.Points = new List<SeqPoint>();
                if (s.HotkeyVk < 0 || s.HotkeyVk > 255) s.HotkeyVk = 0;
                if (s.Mode < 0 || s.Mode > 1) s.Mode = 0;
                if (s.GapMs < 0) s.GapMs = 0;
                if (s.GapMs > 60000) s.GapMs = 60000;

                foreach (SeqPoint p in s.Points)
                {
                    if (p == null) continue;
                    if (p.Name == null) p.Name = "";
                    if (!p.HasValidCoord) { p.X = 0; p.Y = 0; }
                }
                s.Points.RemoveAll(delegate (SeqPoint p) { return p == null; });

                // 一个点都没有的序列没有意义，丢弃
                if (s.Points.Count == 0) cfg.Sequences.RemoveAt(i);
            }

            // 窗口尺寸：越界/过小一律视为"未设置"，回退到按内容计算的默认值。
            // 最小宽度要保证三页标签 + 按钮行能正常显示（低于它布局会被压坏）。
            if (cfg.WindowW < 480 || cfg.WindowW > 10000) cfg.WindowW = 0;
            if (cfg.WindowH < 360 || cfg.WindowH > 10000) cfg.WindowH = 0;

            // 旧字段本身越界时视为未记录（保持原有兜底行为）
            if (cfg.HasPoint)
            {
                if (cfg.PointX < -100000 || cfg.PointX > 100000 ||
                    cfg.PointY < -100000 || cfg.PointY > 100000)
                {
                    cfg.HasPoint = false;
                    cfg.PointX = 0;
                    cfg.PointY = 0;
                }
            }
            return cfg;
        }

        private static int GetInt(Dictionary<string, object> map, string key, int fallback)
        {
            object raw;
            if (!map.TryGetValue(key, out raw) || raw == null) return fallback;
            try
            {
                if (raw is int) return (int)raw;
                if (raw is long) return (int)(long)raw;
                if (raw is double) return (int)Math.Round((double)raw);
                if (raw is decimal) return (int)Math.Round((decimal)raw);
                int parsed;
                if (int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out parsed)) return parsed;
            }
            catch { }
            return fallback;
        }

        private static bool GetBool(Dictionary<string, object> map, string key, bool fallback)
        {
            object raw;
            if (!map.TryGetValue(key, out raw) || raw == null) return fallback;
            if (raw is bool) return (bool)raw;
            string s = Convert.ToString(raw);
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            return fallback;
        }

        // ---------------- 保存 ----------------

        public void Save()
        {
            try
            {
                // 写到用户目录（%APPDATA%\GameClicker），不要写到程序目录：
                // 程序目录会被重新构建/清理动作波及，放那里用户设置会莫名丢失。
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // 保存前自校验：生成的 JSON 必须能原样读回。
                // 写坏的内容一旦覆盖旧文件，用户所有设置就永久丢了——宁可这次不保存。
                if (!RoundTripSelfCheck())
                {
                    AppPaths.Log("配置序列化自校验失败，本次不保存（保留原文件）");
                    return;
                }

                File.WriteAllText(ConfigPath, ToJson(), new UTF8Encoding(false));
            }
            catch { }
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"_说明\": \"连点器配置。可用记事本手工修改，程序每次启动读取。\",");
            sb.AppendLine("  \"triggerKind\": " + (int)TriggerKind + ",          // 0=键盘按键  1=鼠标按键");
            sb.AppendLine("  \"triggerVk\": " + TriggerVk + ",              // 键盘虚拟键码，见文档附录");
            sb.AppendLine("  \"triggerMouse\": " + TriggerMouse + ",            // 0=左键 1=右键 2=中键 3=侧键X1 4=侧键X2");
            sb.AppendLine("  \"mode\": " + (int)Mode + ",                  // 0=切换(按一下开/再按关)  1=按住连点");
            sb.AppendLine("  \"emergencyVk\": " + EmergencyVk + ",            // 紧急停止键虚拟键码，0=禁用");
            sb.AppendLine("  \"swallowTrigger\": " + Bool(SwallowTrigger) + ",      // true=吞掉触发键，游戏不会收到该键");
            sb.AppendLine();
            sb.AppendLine("  \"intervalMs\": " + IntervalMs + ",             // 两次点击间隔(毫秒)");
            sb.AppendLine("  \"button\": " + (int)Button + ",                 // 0=左键 1=右键 2=中键");
            sb.AppendLine("  \"holdMs\": " + HoldMs + ",                 // 每次按下保持时长(毫秒)");
            sb.AppendLine("  \"followCursor\": " + Bool(FollowCursor) + ",       // true=跟随鼠标当前位置 false=锁定启动时的位置");
            sb.AppendLine("  \"trackPixels\": " + Bool(TrackPixels) + ",        // 锁定模式下，指针被移动后自动拉回");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("  // 序列列表：批量录点的一条采集 = 一条序列（一组坐标共用一个热键）");
            sb.AppendLine("  // 按一下热键 → 按下面点的先后顺序依次点一遍，相邻两个点之间等 gapMs 毫秒");
            sb.AppendLine("  // 只有一个点的序列：mode 才有意义（0=按一次点一次  1=按一下开始/再按停止）");
            sb.AppendLine("  // hotkeyVk=0 表示该序列没设热键（只能在界面上点\"点过去\"）");
            sb.AppendLine("  \"sequences\": [");
            for (int i = 0; i < Sequences.Count; i++)
            {
                SequenceEntry s = Sequences[i];
                sb.AppendLine("    {");
                sb.AppendLine("      \"name\": " + JsonString(s.Name) + ",");
                sb.AppendLine("      \"hotkeyVk\": " + s.HotkeyVk + ",");
                sb.AppendLine("      \"enabled\": " + Bool(s.Enabled) + ",");
                sb.AppendLine("      \"gapMs\": " + s.GapMs + ",");
                sb.AppendLine("      \"mode\": " + s.Mode + ",");
                sb.AppendLine("      \"returnCursor\": " + Bool(s.ReturnCursor) + ",");
                sb.AppendLine("      \"points\": [");
                for (int j = 0; j < s.Points.Count; j++)
                {
                    SeqPoint p = s.Points[j];
                    sb.AppendLine("        {");
                    sb.AppendLine("          \"name\": " + JsonString(p.Name) + ",");
                    sb.AppendLine("          \"x\": " + p.X + ",");
                    sb.AppendLine("          \"y\": " + p.Y);
                    sb.AppendLine("        }" + (j < s.Points.Count - 1 ? "," : ""));
                }
                sb.AppendLine("      ]");
                sb.AppendLine("    }" + (i < Sequences.Count - 1 ? "," : ""));
            }
            sb.AppendLine("  ],");
            sb.AppendLine();

            sb.AppendLine("  \"autoStartWithWindows\": " + Bool(AutoStartWithWindows) + ",   // 开机自动后台启动");
            sb.AppendLine("  \"minimizeToTrayOnClose\": " + Bool(MinimizeToTrayOnClose) + ",  // 关闭窗口时最小化到托盘");
            sb.AppendLine("  \"startMinimized\": " + Bool(StartMinimized) + ",       // 启动后直接隐藏到托盘");
            sb.AppendLine("  \"showBalloonTips\": " + Bool(ShowBalloonTips) + ",");
            sb.AppendLine("  \"runAsAdmin\": " + Bool(RunAsAdmin) + ",           // 启动时自动请求管理员权限(游戏里点击无效时可开这个)");
            sb.AppendLine("  \"windowW\": " + WindowW + ",                 // 主界面宽度(0=用默认值,由程序按内容计算)");
            sb.AppendLine("  \"windowH\": " + WindowH + ",                 // 主界面高度(0=用默认值)");
            sb.AppendLine("  \"windowMaximized\": " + Bool(WindowMaximized) + ",      // 上次关闭时是否最大化");
            sb.AppendLine("  \"gameProcess\": " + JsonString(GameProcess) + "     // 绑定的游戏进程名(如 starrail.exe)，留空=不自动切换");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// 自检：ToJson 的输出必须能被自己的解析器原样读回。
        ///
        /// 背景：字段是手写 JSON 拼接的，历史上出现过"追加新字段时上一行漏逗号"，
        /// 结果程序保存的配置自己都读不回来（全部静默回退默认值，用户设置莫名丢失）。
        /// 所以保存前先做一次往返自校验，发现写坏了就保留旧文件、绝不用坏内容覆盖。
        /// </summary>
        private bool RoundTripSelfCheck()
        {
            try
            {
                AppConfig back = LoadFromText(ToJson());
                // 抽查几个关键字段：任何一项对不上都说明序列化坏了
                return back.IntervalMs == IntervalMs &&
                       back.TriggerVk == TriggerVk &&
                       back.RunAsAdmin == RunAsAdmin &&
                       back.WindowW == WindowW &&
                       back.WindowH == WindowH &&
                       back.WindowMaximized == WindowMaximized &&
                       back.Sequences.Count == Sequences.Count;
            }
            catch { return false; }
        }


        /// <summary>把任意文本转成安全的 JSON 字符串字面量（备注名可能含引号 / 反斜杠 / 中文）。</summary>
        internal static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string Bool(bool v)
        {
            return v ? "true" : "false";
        }
    }

    internal static class AppPaths
    {
        /// <summary>程序所在目录（exe 旁边）。</summary>
        public static string BaseDir
        {
            get
            {
                try
                {
                    string p = System.Reflection.Assembly.GetEntryAssembly().Location;
                    string d = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(d)) return d;
                }
                catch { }
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        /// <summary>
        /// 存放配置与日志的目录：%APPDATA%\GameClicker。
        ///
        /// 为什么不放在程序目录：早期版本把 config.json 放在 exe 旁边，
        /// 结果每次重新构建、清理 dist 目录都会把用户设置一起删掉
        /// （表现为"改完配置又变回初始值"）。放到用户目录后，
        /// 程序怎么更新、移动到哪，设置都不会丢。
        /// 若该目录不可用（极端受限环境），回退到程序目录，功能不受影响。
        /// </summary>
        public static string DataDir
        {
            get
            {
                try
                {
                    string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    if (!string.IsNullOrEmpty(root))
                    {
                        string dir = Path.Combine(root, "GameClicker");
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        return dir;
                    }
                }
                catch { }
                return BaseDir;   // 回退：极少数取不到 %APPDATA% 的环境
            }
        }

        /// <summary>历史配置位置（程序目录），仅用于一次性迁移。</summary>
        public static string LegacyConfigPath
        {
            get { return Path.Combine(BaseDir, "config.json"); }
        }

        public static string ExePath
        {
            get
            {
                try { return System.Reflection.Assembly.GetEntryAssembly().Location; }
                catch { return Path.Combine(BaseDir, "GameClicker.exe"); }
            }
        }

        public static string LogPath
        {
            get { return Path.Combine(DataDir, "gameclicker.log"); }
        }

        public static void Log(string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
                File.AppendAllText(LogPath, line, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
