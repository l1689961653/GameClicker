// ProfileManager.cs —— 按游戏管理的多套配置
//
// 目录布局：
//   %APPDATA%\GameClicker\config.json            ← 根配置（未绑定游戏的默认套）
//   %APPDATA%\GameClicker\Profiles\<名称>\config.json   ← 每个游戏一套
//
// 为什么配置里存的是"游戏进程名"而不是把对应关系放在根配置里：
//   对应关系跟着配置走——复制一个 Profiles 目录给朋友，绑定关系也一起带过去了，
//   不需要再解释"你还得在根配置里加一行映射"。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GameClicker
{
    internal static class ProfileManager
    {
        /// <summary>所有 Profile 的存放根目录。</summary>
        public static string ProfilesRoot
        {
            get { return Path.Combine(AppPaths.DataDir, "Profiles"); }
        }

        /// <summary>一个 Profile 的描述：名字 + 目录 + 绑定的游戏进程。</summary>
        public sealed class Item
        {
            public string Name;         // 显示名（= 目录名）
            public string Dir;          // 配置目录
            public string GameProcess;  // 绑定的游戏进程名（可空）
        }

        /// <summary>
        /// 保证至少存在一个 Profile；一个都没有时创建 "配置1"。
        /// 返回应激活的 Profile 名。程序不再有「默认/根配置」概念，
        /// 所有配置平等，启动时加载上一次使用的那个（由界面记忆）。
        /// </summary>
        public static string EnsureAny()
        {
            List<Item> all = List();
            if (all.Count > 0) return all[0].Name;
            Create("配置1", "");
            return "配置1";
        }

        /// <summary>名字是否合法（作为目录名用，拒绝路径非法字符）。</summary>
        public static bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Trim() != name) return false;
            if (name.Length > 60) return false;
            foreach (char c in Path.GetInvalidFileNameChars())
                if (name.IndexOf(c) >= 0) return false;
            return true;
        }

        /// <summary>列出全部 Profile（按名字排序）。目录存在但没有 config.json 的也算（新初始化的）。</summary>
        public static List<Item> List()
        {
            List<Item> list = new List<Item>();
            try
            {
                if (!Directory.Exists(ProfilesRoot)) return list;
                foreach (string dir in Directory.GetDirectories(ProfilesRoot))
                {
                    string name = Path.GetFileName(dir);
                    string game = "";
                    try
                    {
                        AppConfig c = AppConfig.LoadFromDir(dir);
                        game = c.GameProcess;
                    }
                    catch { }
                    list.Add(new Item { Name = name, Dir = dir, GameProcess = game });
                }
                list.Sort(delegate (Item a, Item b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch { }
            return list;
        }

        /// <summary>按名字取 Profile，不存在返回 null。</summary>
        public static Item Find(string name)
        {
            foreach (Item it in List())
                if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase))
                    return it;
            return null;
        }

        /// <summary>新建一个 Profile（目录 + 空配置），返回其目录；失败返回 null。</summary>
        public static string Create(string name, string gameProcess)
        {
            if (!ValidName(name)) return null;
            try
            {
                string dir = Path.Combine(ProfilesRoot, name);
                if (Directory.Exists(dir)) return null;   // 已存在

                AppConfig cfg = new AppConfig();
                cfg.GameProcess = (gameProcess ?? "").Trim();
                AppConfig.SaveToDir(dir, cfg);
                return dir;
            }
            catch { return null; }
        }

        /// <summary>删除一个 Profile（整个目录）。返回是否删除了东西。</summary>
        public static bool Delete(string name)
        {
            try
            {
                string dir = Path.Combine(ProfilesRoot, name);
                if (!Directory.Exists(dir)) return false;
                Directory.Delete(dir, true);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 重命名 Profile（改目录名）。目标名已存在或源不存在时失败。
        /// 失败原因通过 error 返回（供界面提示）。
        /// </summary>
        public static bool Rename(string oldName, string newName, out string error)
        {
            error = "";
            if (!ValidName(newName)) { error = "新名称不合法"; return false; }
            if (Find(newName) != null) { error = "同名配置已存在"; return false; }

            try
            {
                string oldDir = Path.Combine(ProfilesRoot, oldName);
                string newDir = Path.Combine(ProfilesRoot, newName);
                if (!Directory.Exists(oldDir)) { error = "原配置不存在"; return false; }
                Directory.Move(oldDir, newDir);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 根据前台进程名找出应激活的 Profile；找不到返回 null。
        /// 进程名比较不区分大小写（用户填 "StarRail" 还是 "starrail.exe" 都能对上）。
        /// </summary>
        public static Item MatchByProcess(string foregroundProcess)
        {
            if (string.IsNullOrEmpty(foregroundProcess)) return null;
            string fg = foregroundProcess.Trim();
            if (fg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                fg = fg.Substring(0, fg.Length - 4);

            foreach (Item it in List())
            {
                string g = (it.GameProcess ?? "").Trim();
                if (g.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    g = g.Substring(0, g.Length - 4);
                if (g.Length > 0 && string.Equals(g, fg, StringComparison.OrdinalIgnoreCase))
                    return it;
            }
            return null;
        }
    }
}
