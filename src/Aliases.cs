using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace SongRequestMod
{
    /// <summary>
    /// 别名(社区昵称)搜索。
    ///
    /// 数据文件: Mods\SongRequestMod\aliases.txt —— 制表符分隔, 你能直接手改, 改完刷页面即生效:
    ///     独サカリ	咬	甘噛
    ///     15001	咬
    ///   (第一列 = 曲名 或 曲目 id; 后面每列一个别名; # 开头是注释)
    ///
    /// 匹配方式和 SongSearch 一样朴素: 小写子串匹配曲名 / 艺术家 / id / 别名,
    /// 空格分词 AND。不做拼音/假名罗马音转换。
    /// </summary>
    internal static class Aliases
    {
        private static readonly Dictionary<string, List<string>> _byTitle =
            new Dictionary<string, List<string>>();
        private static readonly Dictionary<int, List<string>> _byId =
            new Dictionary<int, List<string>>();
        private static DateTime _stamp;
        private static string _loadedPath;
        private static int _aliasCount;
        private static int _songCount;
        private static bool _usedEmbedded;
        /// <summary>成功解析过至少一次(磁盘或内嵌)。首次读取必须解析, 不然计数永远是 0</summary>
        private static bool _parsed;

        internal static string FilePath
        {
            get
            {
                try
                {
                    string dir = Web.GameDir;
                    string p = Path.Combine(dir, "Mods", "SongRequestMod", "aliases.txt");
                    if (File.Exists(p))
                    {
                        return p;
                    }
                    string p2 = Path.Combine(dir, "Mods", "aliases.txt");
                    if (File.Exists(p2))
                    {
                        return p2;
                    }
                    return p;
                }
                catch
                {
                    return "aliases.txt";
                }
            }
        }

        /// <summary>别名条数。首次读取会无条件解析一次 —— 计数跟曲库没有任何关系
        /// (曲库为空时 Build() 根本不会调 For(), 老代码因此永远显示 0 条)</summary>
        internal static int Count
        {
            get { Ensure(); return _aliasCount; }
        }

        /// <summary>有别名可用的曲目数(按 id 索引的 + 按曲名索引的)</summary>
        internal static int SongCount
        {
            get { Ensure(); return _songCount; }
        }

        /// <summary>启动后主动解析一次(Mod.OnLateInitializeMelon 里调), 让 /api/status 一开始就有真数字</summary>
        internal static void Preload()
        {
            Ensure();
        }

        /// <summary>文件没变就不重读; 第一次读无条件解析</summary>
        internal static void Ensure()
        {
            try
            {
                string p = FilePath;
                if (!File.Exists(p))
                {
                    // 磁盘上没有 aliases.txt -> 用内嵌的那份(只丢一个 dll 也能用)
                    if (!_usedEmbedded)
                    {
                        string emb = Web.ReadEmbedded("SongRequestMod.aliases.txt");
                        if (emb != null)
                        {
                            _usedEmbedded = true;
                            Parse(emb.Split(new char[] { '\n' }));
                            _loadedPath = "(内嵌)";
                            ModLog.Info("[SongRequest] 别名库用内嵌版本");
                        }
                    }
                    return;
                }
                DateTime t = File.GetLastWriteTimeUtc(p);
                if (_parsed && _loadedPath == p && t == _stamp)
                {
                    return;
                }
                Parse(File.ReadAllLines(p, Encoding.UTF8));
                _loadedPath = p;
                _stamp = t;
                ModLog.Info("[SongRequest] 别名库已载入: " + _aliasCount + " 条 (" + p + ")");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 读别名库失败: " + e.Message);
            }
        }

        private static void Parse(string[] lines)
        {
            _byTitle.Clear();
            _byId.Clear();
            _aliasCount = 0;
            _songCount = 0;
            int songs = 0;
            foreach (string raw in lines)
            {
                if (raw == null)
                {
                    continue;
                }
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                string[] parts = line.Split('\t');
                if (parts.Length < 2)
                {
                    continue;
                }
                string key = parts[0].Trim();
                if (key.Length == 0)
                {
                    continue;
                }
                List<string> list = new List<string>();
                for (int i = 1; i < parts.Length; i++)
                {
                    string a = parts[i].Trim();
                    if (a.Length > 0)
                    {
                        list.Add(a);
                    }
                }
                if (list.Count == 0)
                {
                    continue;
                }
                int id;
                if (int.TryParse(key, out id) && id > 0)
                {
                    _byId[id] = list;
                }
                else
                {
                    _byTitle[Norm(key)] = list;
                }
                _aliasCount += list.Count;
                songs++;
            }
            _songCount = _byTitle.Count + _byId.Count;
            _parsed = true;
            ModLog.Info("[SongRequest] 别名库已解析: " + songs + " 首 / " + _aliasCount + " 条");
        }

        /// <summary>aliases.txt 的 mtime 变过没(曲目表要不要因此重出)</summary>
        internal static bool StampChanged()
        {
            try
            {
                string p = FilePath;
                if (!File.Exists(p))
                {
                    return false;
                }
                return File.GetLastWriteTimeUtc(p) != _stamp;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>取该曲的别名(按 id 和曲名两路都查, 合起来去重)</summary>
        internal static List<string> For(int id, string title)
        {
            Ensure();
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> byId;
            if (_byId.TryGetValue(id, out byId))
            {
                foreach (string a in byId)
                {
                    if (seen.Add(a))
                    {
                        result.Add(a);
                    }
                }
            }
            if (!string.IsNullOrEmpty(title))
            {
                List<string> byTitle;
                if (_byTitle.TryGetValue(Norm(title), out byTitle))
                {
                    foreach (string a in byTitle)
                    {
                        if (seen.Add(a))
                        {
                            result.Add(a);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>曲名归一: 去空白 + 转小写(全角空格也算空白)</summary>
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == ' ' || c == '\u3000' || c == '\t')
                {
                    continue;
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
