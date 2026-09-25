using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Manager;
using MelonLoader;
using MAI2.Util;

namespace SongRequestMod
{
    /// <summary>
    /// 把游戏自己的曲目表导出成网页用的 JSON。
    ///
    /// 注意: DataManager.GetMusics() / GetMusic(id) 会被别的 mod(MuNet 的云海/Collection 钩子)
    /// 用 Harmony postfix 换成服务器下发的几首 —— 直接读就只剩 7 首。
    /// 所以这里读 DataManager 的私有字段 _musics, 那才是本地全量表(1600+ 首)。
    /// 每首带 5 个难度, 等级字符串取游戏自己的 MusicLevel 表(所以 "13+" 会原样出来)。
    /// </summary>
    internal static class SongTable
    {
        internal static readonly string[] DiffNames =
            { "BASIC", "ADVANCED", "EXPERT", "MASTER", "Re:MASTER" };

        private static readonly Dictionary<int, Manager.MaiStudio.MusicData> _byId =
            new Dictionary<int, Manager.MaiStudio.MusicData>();

        private static FieldInfo _fMusics;
        private static string _json;
        private static int _jsonCount = -1;
        /// <summary>曲目表版本号: 每重建一次 +1, 网页靠它知道要不要重新拉列表</summary>
        internal static int Rev;
        private static float _timer;
        private static bool _wasInSelect;

        /// <summary>主线程每 5 秒看一眼游戏曲目表有没有变(热导入新歌后要重出)</summary>
        internal static void Tick(float dt)
        {
            _timer += dt;
            if (_timer < 2f)
            {
                return;
            }
            _timer = 0f;
            int n = RawCount();
            // 曲目数变了(热导入) 或 刚进选曲界面(这时才拿得到 STD/DX 真值) -> 重出
            bool enteredSelect = SelectDriver.InSelect && !_wasInSelect;
            _wasInSelect = SelectDriver.InSelect;
            // 别名文件被改过(改了 aliases.txt)也要重出, 不然别名要等曲库变化才生效
            bool aliasChanged = Aliases.StampChanged();
            if (_json == null || n != _jsonCount || enteredSelect || aliasChanged)
            {
                Json(true);
            }
        }

        private static Dictionary<int, int> _types;
        private static int _typesRev = -1;

        /// <summary>该曲的 STD/DX 类型: (std, dx)。优先用游戏选曲数据里的 existStandardScore/existDeluxeScore,
        /// 拿不到就按 id 约定(id>=10000 是 DX, 这个私服/转换器都这么发号)。</summary>
        internal static void TypeOf(int id, out bool std, out bool dx)
        {
            try
            {
                if (_types == null || _typesRev != Rev)
                {
                    _types = SelectDriver.ScoreTypeMap();
                    _typesRev = Rev;
                }
                int v;
                if (_types != null && _types.TryGetValue(id, out v))
                {
                    std = (v & 1) != 0;
                    dx = (v & 2) != 0;
                    return;
                }
            }
            catch
            {
            }
            dx = id >= 10000;
            std = !dx;
        }

        internal static string TypeLabel(int id)
        {
            bool std, dx;
            TypeOf(id, out std, out dx);
            if (dx && std)
            {
                return "DX+STD";
            }
            return dx ? "DX" : "STD";
        }

        internal static string DifficultyName(int d)
        {
            return (d >= 0 && d < DiffNames.Length) ? DiffNames[d] : "?";
        }

        private static FieldInfo MusicsField
        {
            get
            {
                if (_fMusics == null)
                {
                    _fMusics = AccessTools.Field(typeof(Manager.DataManager), "_musics");
                    if (_fMusics == null)
                    {
                        MelonLogger.Warning("[SongRequest] 找不到 DataManager._musics, 退回 GetMusics()"
                            + "(可能只有被过滤后的曲目)");
                    }
                }
                return _fMusics;
            }
        }

        /// <summary>本地全量表(绕开别的 mod 的过滤钩子)</summary>
        private static object RawTable()
        {
            try
            {
                var dm = Singleton<Manager.DataManager>.Instance;
                if (dm == null)
                {
                    return null;
                }
                FieldInfo f = MusicsField;
                if (f != null)
                {
                    object v = f.GetValue(dm);
                    if (v != null)
                    {
                        return v;
                    }
                }
                return dm.GetMusics();
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 读曲目表失败: " + e.Message);
                return null;
            }
        }

        /// <summary>遍历本地全量表: (id, MusicData)</summary>
        private static IEnumerable<KeyValuePair<int, Manager.MaiStudio.MusicData>> Each()
        {
            object table = RawTable();
            if (table == null)
            {
                yield break;
            }
            var typed = table as IEnumerable<KeyValuePair<int, Manager.MaiStudio.MusicData>>;
            if (typed != null)
            {
                foreach (var kv in typed)
                {
                    yield return kv;
                }
                yield break;
            }
            var loose = table as IEnumerable;
            if (loose == null)
            {
                yield break;
            }
            foreach (object item in loose)
            {
                if (item == null)
                {
                    continue;
                }
                System.Type t = item.GetType();
                PropertyInfo pk = t.GetProperty("Key");
                PropertyInfo pv = t.GetProperty("Value");
                if (pk == null || pv == null)
                {
                    continue;
                }
                object k = pk.GetValue(item, null);
                object v = pv.GetValue(item, null);
                if (k is int && v is Manager.MaiStudio.MusicData)
                {
                    yield return new KeyValuePair<int, Manager.MaiStudio.MusicData>((int)k,
                        (Manager.MaiStudio.MusicData)v);
                }
            }
        }

        internal static int RawCount()
        {
            int n = 0;
            foreach (var kv in Each())
            {
                n++;
            }
            return n;
        }

        internal static int Count
        {
            get { return _jsonCount > 0 ? _jsonCount : RawCount(); }
        }

        internal static Manager.MaiStudio.MusicData GetMusic(int id)
        {
            try
            {
                if (_byId.Count == 0)
                {
                    Json(true);
                }
                Manager.MaiStudio.MusicData md;
                if (_byId.TryGetValue(id, out md))
                {
                    return md;
                }
                if (id >= 10000 && _byId.TryGetValue(id % 10000, out md))
                {
                    return md;
                }
                var dm = Singleton<Manager.DataManager>.Instance;
                return dm == null ? null : dm.GetMusic(id);
            }
            catch
            {
                return null;
            }
        }

        internal static string NameOf(int id)
        {
            try
            {
                var md = GetMusic(id);
                if (md == null || md.name == null || string.IsNullOrEmpty(md.name.str))
                {
                    return "music " + id;
                }
                return md.name.str;
            }
            catch
            {
                return "music " + id;
            }
        }

        /// <summary>曲绘资源名 —— 直接用游戏数据里的 jacketFile / thumbnailName</summary>
        internal static string JacketAssetName(int id, bool thumb)
        {
            try
            {
                var md = GetMusic(id);
                if (md != null)
                {
                    if (thumb && !string.IsNullOrEmpty(md.thumbnailName))
                    {
                        return md.thumbnailName;
                    }
                    if (!string.IsNullOrEmpty(md.jacketFile))
                    {
                        return md.jacketFile;
                    }
                    if (!string.IsNullOrEmpty(md.thumbnailName))
                    {
                        return md.thumbnailName;
                    }
                }
            }
            catch
            {
            }
            return "Jacket/UI_Jacket_" + (id % 10000).ToString("D6") + ".png";
        }

        /// <summary>某曲某难度的等级显示串("13+"), 没有则空</summary>
        internal static string LevelOf(int musicId, int diff)
        {
            try
            {
                var md = GetMusic(musicId);
                if (md == null || md.notesData == null || diff < 0)
                {
                    return "";
                }
                int idx = 0;
                foreach (var nt in md.notesData)
                {
                    if (idx == diff)
                    {
                        return nt.isEnable ? LevelStr(nt) : "";
                    }
                    idx++;
                }
            }
            catch
            {
            }
            return "";
        }

        internal static bool DifficultyEnabled(int musicId, int d)
        {
            try
            {
                var md = GetMusic(musicId);
                if (md == null || md.notesData == null)
                {
                    return false;
                }
                int idx = 0;
                foreach (var nt in md.notesData)
                {
                    if (idx == d)
                    {
                        return nt.isEnable;
                    }
                    idx++;
                }
            }
            catch
            {
            }
            return false;
        }

        internal static int HighestEnabled(int musicId)
        {
            try
            {
                var md = GetMusic(musicId);
                if (md == null || md.notesData == null)
                {
                    return -1;
                }
                int best = -1;
                int idx = 0;
                foreach (var nt in md.notesData)
                {
                    if (nt.isEnable && idx <= 4 && idx > best)
                    {
                        best = idx;
                    }
                    idx++;
                }
                return best;
            }
            catch
            {
                return -1;
            }
        }

        internal static List<int> RandomPool(int difficulty)
        {
            List<int> ids = new List<int>();
            foreach (var kv in Each())
            {
                var md = kv.Value;
                if (md == null || md.IsDisable())
                {
                    continue;
                }
                int id = md.GetID();
                if (difficulty >= 0 && difficulty <= 4)
                {
                    if (DifficultyEnabled(id, difficulty))
                    {
                        ids.Add(id);
                    }
                }
                else if (HighestEnabled(id) >= 0)
                {
                    ids.Add(id);
                }
            }
            return ids;
        }

        private static string LevelStr(Manager.MaiStudio.Notes nt)
        {
            try
            {
                var dm = Singleton<Manager.DataManager>.Instance;
                if (dm != null && nt.musicLevelID > 0)
                {
                    var lv = dm.GetMusicLevel(nt.musicLevelID);
                    if (lv != null && !string.IsNullOrEmpty(lv.levelNum))
                    {
                        return lv.levelNum;
                    }
                }
            }
            catch
            {
            }
            return nt.level > 0 ? nt.level.ToString() : "-";
        }

        /// <summary>曲目表 JSON(缓存; force=true 或曲目数变了才重建)</summary>
        internal static string Json(bool force)
        {
            if (!force && _json != null)
            {
                return _json;
            }
            _json = Build();
            return _json;
        }

        private static string Build()
        {
            StringBuilder sb = new StringBuilder(1 << 21);
            sb.Append('[');
            int n = 0;
            try
            {
                _byId.Clear();
                bool first = true;
                foreach (var kv in Each())
                {
                    Manager.MaiStudio.MusicData md = kv.Value;
                    if (md == null)
                    {
                        continue;
                    }
                    _byId[md.GetID()] = md;
                    if (md.IsDisable())
                    {
                        continue;   // 游戏里禁用的曲目不进点歌台
                    }
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    AppendMusic(sb, md);
                    n++;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[SongRequest] 导出曲目表失败: " + e.Message);
            }
            sb.Append(']');
            _jsonCount = n;
            Rev++;
            // 曲库变了 -> 曲绘缓存也作废(热导入换过封面的曲子不会继续给旧图)
            Jackets.ClearCache();
            _types = null;
            ModLog.Info("[SongRequest] 曲目表已导出: " + n + " 首 (rev " + Rev + ")");
            return sb.ToString();
        }

        private static void AppendMusic(StringBuilder sb, Manager.MaiStudio.MusicData md)
        {
            int id = md.GetID();
            sb.Append("{\"id\":").Append(id);
            sb.Append(",\"name\":");
            Esc(sb, md.name != null ? md.name.str : "");
            sb.Append(",\"artist\":");
            Esc(sb, md.artistName != null ? md.artistName.str : "");
            sb.Append(",\"genre\":");
            Esc(sb, GenreName(md));
            sb.Append(",\"bpm\":").Append(md.bpm);
            sb.Append(",\"version\":");
            Esc(sb, md.AddVersion != null ? md.AddVersion.str : "");
            bool isStd, isDx;
            TypeOf(id, out isStd, out isDx);
            sb.Append(",\"type\":\"").Append(isDx && isStd ? "DX+STD" : (isDx ? "DX" : "STD")).Append('"');
            sb.Append(",\"std\":").Append(isStd ? "true" : "false");
            sb.Append(",\"dx\":").Append(isDx ? "true" : "false");

            string[] levelStr = new string[5];
            int[] levelNum = new int[5];
            bool[] enable = new bool[5];
            int maxLevel = -1;
            if (md.notesData != null)
            {
                // 难度看 notesData 的位置(0=Basic .. 4=Re:Master), 不是 notesType ——
                // 这个私服包里所有 Notes 的 notesType 都是 0, 位置才对应谱面文件(015001_03.ma2 = Master)。
                int pos = 0;
                foreach (var nt in md.notesData)
                {
                    int d = pos;
                    pos++;
                    if (d < 0 || d > 4)
                    {
                        continue;
                    }
                    enable[d] = nt.isEnable;
                    levelStr[d] = LevelStr(nt);
                    levelNum[d] = ParseLevel(levelStr[d], nt.level);
                    if (nt.isEnable && levelNum[d] > maxLevel)
                    {
                        maxLevel = levelNum[d];
                    }
                }
            }
            // 别名(社区昵称): 单独一个数组给网页做搜索, 为空就不输出这个字段
            List<string> aliases = Aliases.For(id, md.name != null ? md.name.str : "");
            if (aliases.Count > 0)
            {
                sb.Append(",\"alias\":[");
                for (int i = 0; i < aliases.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    Esc(sb, aliases[i]);
                }
                sb.Append(']');
            }
            sb.Append(",\"maxLevel\":").Append(maxLevel < 0 ? 0 : maxLevel);
            // enable = XML 声明 且 游戏认为可玩(选曲数据的 isExistsScore)。
            // 只信 XML 会出现"点了 14+ 跳到 14"—— 谱面文件缺失/版本没同步时游戏自己会降档。
            bool[] playable = new bool[5];
            for (int d = 0; d < 5; d++)
            {
                bool? gp = SelectDriver.GamePlayable(id, d);
                playable[d] = gp.HasValue ? gp.Value : enable[d];
                if (enable[d] && !playable[d])
                {
                    enable[d] = false;
                }
            }
            sb.Append(",\"difficulty\":[");
            for (int d = 0; d < 5; d++)
            {
                if (d > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"type\":").Append(d);
                sb.Append(",\"name\":\"").Append(DiffNames[d]).Append('"');
                sb.Append(",\"level\":").Append(levelNum[d]);
                sb.Append(",\"levelStr\":");
                Esc(sb, levelStr[d] == null ? "-" : levelStr[d]);
                sb.Append(",\"enable\":").Append(enable[d] ? "true" : "false");
                sb.Append(",\"playable\":").Append(playable[d] ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("]}");
        }

        /// <summary>"13+" -> 13 (给网页排序用)</summary>
        private static int ParseLevel(string s, int fallback)
        {
            if (string.IsNullOrEmpty(s))
            {
                return fallback;
            }
            int v;
            if (int.TryParse(s.Trim().TrimEnd('+').Trim(), out v))
            {
                return v;
            }
            return fallback;
        }

        private static string GenreName(Manager.MaiStudio.MusicData md)
        {
            try
            {
                if (md.genreName != null)
                {
                    if (!string.IsNullOrEmpty(md.genreName.str))
                    {
                        return md.genreName.str;
                    }
                    var dm = Singleton<Manager.DataManager>.Instance;
                    if (dm != null && md.genreName.id > 0)
                    {
                        var g = dm.GetMusicGenre(md.genreName.id);
                        if (g != null && g.name != null && !string.IsNullOrEmpty(g.name.str))
                        {
                            return g.name.str;
                        }
                    }
                }
            }
            catch
            {
            }
            return "";
        }

        private static void Esc(StringBuilder sb, string s)
        {
            sb.Append('"').Append(Escape(s)).Append('"');
        }

        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            StringBuilder sb = new StringBuilder(s.Length + 8);
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
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
