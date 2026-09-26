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

        /// <summary>
        /// id -> MusicData。只在主线程 Build 里整张新建再换引用, 从不原地修改 ——
        /// 以前是 Clear() 再逐条塞, 主线程和网页线程同时重建时会把 Dictionary 写坏,
        /// 之后的查询可能死循环, 游戏直接卡死。
        /// </summary>
        private static volatile Dictionary<int, Manager.MaiStudio.MusicData> _byId =
            new Dictionary<int, Manager.MaiStudio.MusicData>();

        private static volatile string _json;
        private static int _jsonCount = -1;
        /// <summary>上次 Build 时的"原始条目数"。判断曲目表有没有变要跟它比, 不能跟 _jsonCount 比 ——
        /// _jsonCount 已经滤掉游戏禁用曲目, 两者不相等时会导致每 2 秒白重建一次。</summary>
        private static int _builtSnapCount = -1;
        /// <summary>曲目表版本号: 导出的内容真的变了才 +1, 网页靠它知道要不要重新拉列表</summary>
        internal static volatile int Rev;
        private static float _timer;
        private static bool _wasInSelect;
        private const float TickInterval = 5f;

        /// <summary>网页线程用: 主线程最近一次建好的曲目表 JSON(还没建过就是空数组)</summary>
        internal static string CachedJson
        {
            get { return _json ?? "[]"; }
        }

        /// <summary>主线程每 5 秒看一眼游戏曲目表有没有变(热导入新歌后要重出); 游玩中不看, 免得打歌掉帧</summary>
        internal static void Tick(float dt)
        {
            _timer += dt;
            if (_timer < TickInterval)
            {
                return;
            }
            _timer = 0f;
            if (_json != null && LiveState.State == "playing")
            {
                return;
            }
            int n = RawCount();
            // 曲目数变了(热导入) 或 刚进选曲界面(这时才拿得到 STD/DX 真值) -> 重出
            bool inSelect = SelectDriver.InSelect;
            bool enteredSelect = inSelect && !_wasInSelect;
            _wasInSelect = inSelect;
            // 别名文件被改过(改了 aliases.txt)也要重出, 不然别名要等曲库变化才生效
            bool aliasChanged = Aliases.StampChanged();
            if (_json == null || n != _builtSnapCount || enteredSelect || aliasChanged)
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

        // ── 曲目表数据源: 多路尝试 + 取条目最多的那一路 ─────────────────────────
        // 老实现写死 "先反射 DataManager._musics, 拿不到才退 GetMusics()", 而 Each() 只认
        // IEnumerable<KeyValuePair<int,MusicData>> 这一种形状, 别的情况一律静默 continue ——
        // 别人机器上只要字段名 / 包装类型 / 条目形状有一处不一样, 就变成 "0 首"(页面 曲库总量 0000),
        // 而且是无声的, 连日志都没有。现在改成:
        //   ① 选曲列表 SelectDriver 抓到的 CombineMusicDataList(游戏自己建好的可点歌曲表,
        //      完全绕开 DataManager 的字段/API; 离开选曲界面后用最后一次成功的快照兜住)
        //   ② 公开 API GetMusics()
        //   ③ 反射 DataManager 所有"像曲目表"的实例字段(不写死 _musics 这个名字)
        //   ④ 裸 MusicData 序列用 musicData.GetID() 当 key(老实现会整路跳过)
        //      字典类只要 "Key/Value 属性"、"嵌套可枚举"、"Values 属性" 都认
        // 每路都记条目数/形状/异常, 取条目最多的那一路(并列取靠前的 -> 选曲列表优先),
        // 明细给 /api/selfcheck。

        private const int MaxProbeItems = 100000;    // 单路条目上限, 防病态枚举把游戏卡住
        private const int MaxProbeSources = 48;      // 最多探几路字段
        private const int SnapTtlMs = 800;           // 快照缓存: 同一秒内的多次读取只探一次

        private static readonly object _snapLock = new object();
        private static readonly HashSet<int> _dedupe = new HashSet<int>();
        private static List<KeyValuePair<int, Manager.MaiStudio.MusicData>> _snap;
        private static int _snapMs;
        private static int _snapRaw;
        private static string _snapSource = "(还没探测)";
        private static string _probeReport = "[]";
        private static string _lastLoggedSource = "";
        private static bool _fallbackWarned;
        private static bool _zeroWarned;

        // ── 数据源①: 选曲列表(SelectDriver 抓到的 MusicSelectProcess.CombineMusicDataList) ──
        // 离开选曲界面时游戏会把 CombineMusicDataList 释放掉, 但我们不能因此把曲库掉回 0 首,
        // 所以最后一次成功的结果缓在这里, 直到下次进选曲界面刷新。
        private static List<KeyValuePair<int, Manager.MaiStudio.MusicData>> _selectCache;
        private static string _selectCacheAt = "";
        private static int _selectCacheItems;
        private static int _selectCacheCats;
        private static bool _selectLive;

        /// <summary>一路数据源的探测结果</summary>
        private sealed class Probe
        {
            public string Name = "";
            public string Shape = "";
            public string Error = "";
            /// <summary>这一路"现在根本不可用"(例如没进过选曲界面) —— 不算失败, 不报 Warning</summary>
            public bool Skipped;
            public int Count;
            public readonly List<KeyValuePair<int, Manager.MaiStudio.MusicData>> Items =
                new List<KeyValuePair<int, Manager.MaiStudio.MusicData>>();
        }

        /// <summary>真正的错误走 MelonLogger.Warning, 但日志本身不能把探测流程带崩</summary>
        private static void Warn(string msg)
        {
            try
            {
                MelonLogger.Warning(msg);
            }
            catch
            {
            }
        }

        /// <summary>当前曲目表快照(去重后、按 id 升序)。fresh=true 强制重探。</summary>
        private static List<KeyValuePair<int, Manager.MaiStudio.MusicData>> Snapshot(bool fresh)
        {
            lock (_snapLock)
            {
                int now = Environment.TickCount;
                if (fresh || _snap == null || unchecked(now - _snapMs) >= SnapTtlMs)
                {
                    ProbeAll();
                    _snapMs = now;
                }
                return _snap;
            }
        }

        /// <summary>
        /// 探所有数据源, 取条目最多的那一路(并列时靠前的赢 -> 选曲列表 > GetMusics() > 反射字段)。
        /// 调用方必须持有 _snapLock。
        /// </summary>
        private static void ProbeAll()
        {
            List<Probe> probes = new List<Probe>();

            // ① 选曲列表: 完全绕开 DataManager 的字段/API, 只要进过选曲界面就有
            Probe sel = new Probe();
            sel.Name = "select:CombineMusicDataList";
            probes.Add(sel);
            try
            {
                FillFromSelectList(sel);
            }
            catch (Exception e)
            {
                sel.Error = e.GetType().Name + ": " + e.Message;
            }

            Manager.DataManager dm = null;
            try
            {
                dm = Singleton<Manager.DataManager>.Instance;
            }
            catch (Exception e)
            {
                Warn("[SongRequest] 取 DataManager 实例失败: " + e.Message);
            }
            if (dm == null)
            {
                Probe p0 = new Probe();
                p0.Name = "Singleton<DataManager>.Instance";
                p0.Error = "实例为 null(游戏数据还没初始化完?)";
                probes.Add(p0);
            }
            else
            {
                // ② 公开 API
                Probe api = new Probe();
                api.Name = "api:GetMusics()";
                try
                {
                    Fill(api, dm.GetMusics());
                }
                catch (Exception e)
                {
                    api.Error = e.GetType().Name + ": " + e.Message;
                }
                probes.Add(api);
                // ③ 反射 DataManager 的所有实例字段(以前只认 _musics 一个名字)
                foreach (FieldInfo f in MusicTableFields())
                {
                    Probe p = new Probe();
                    p.Name = "field:" + f.Name;
                    try
                    {
                        Fill(p, f.GetValue(dm));
                    }
                    catch (Exception e)
                    {
                        p.Error = e.GetType().Name + ": " + e.Message;
                    }
                    probes.Add(p);
                }
            }

            Probe best = null;
            for (int i = 0; i < probes.Count; i++)
            {
                Probe p = probes[i];
                if (p.Count <= 0)
                {
                    continue;
                }
                if (best == null || p.Count > best.Count)
                {
                    best = p;      // 并列时靠前的赢
                }
            }

            _snap = best != null ? best.Items
                : new List<KeyValuePair<int, Manager.MaiStudio.MusicData>>();
            _snapRaw = best != null ? best.Count : 0;
            if (best != null)
            {
                _snapSource = best.Name + " [" + best.Shape + "]";
            }
            else
            {
                _snapSource = "(所有数据源都是 0 条)";
            }
            // 诊断串先算好: 下面那几个日志万一抛异常, 也不能把 /api/selfcheck 的关键信息丢了
            _probeReport = ProbeReportJson(probes, best);
            if (best != null)
            {
                if (probes.Count > 0 && !ReferenceEquals(best, probes[0]) && probes[0].Count <= 0
                    && !probes[0].Skipped && !_fallbackWarned)
                {
                    _fallbackWarned = true;
                    Warn("[SongRequest] 主数据源 " + probes[0].Name + " 读到 0 条"
                        + (probes[0].Error.Length > 0 ? " (" + probes[0].Error + ")" : "")
                        + ", 已自动改用 " + best.Name + "(" + best.Count + " 条)。明细: /api/selfcheck");
                }
            }
            else if (!_zeroWarned)
            {
                _zeroWarned = true;
                Warn("[SongRequest] 所有曲目数据源都读到 0 条 —— 点歌台会显示 0 首。"
                    + "各数据源明细见 /api/selfcheck 的 songTable.tried");
            }
            if (_lastLoggedSource != _snapSource)
            {
                _lastLoggedSource = _snapSource;
                ModLog.Info("[SongRequest] 曲目表来源: " + _snapSource + " / " + _snapRaw + " 条");
            }
        }

        /// <summary>把一路候选对象里的曲目条目抽出来(形状自适应)</summary>
        private static void Fill(Probe p, object table)
        {
            _dedupe.Clear();
            if (table == null)
            {
                p.Shape = "null";
                return;
            }
            string shape = "";
            try
            {
                Extract(table, p.Items, ref shape, 0);
            }
            catch (Exception e)
            {
                p.Error = "枚举异常 " + e.GetType().Name + ": " + e.Message;
            }
            p.Shape = shape.Length > 0 ? shape : table.GetType().Name;
            p.Count = p.Items.Count;
        }

        /// <summary>数据源①: 读 SelectDriver 抓到的选曲列表, 然后交给 FillSelectList。</summary>
        private static void FillFromSelectList(Probe p)
        {
            object live = null;
            try
            {
                live = SelectDriver.SelectList;      // 没进过选曲界面 -> null
            }
            catch
            {
            }
            FillSelectList(p, live);
        }

        /// <summary>
        /// 选曲列表 -> 曲目表。list = MusicSelectProcess.CombineMusicDataList
        /// (List&lt;ReadOnlyCollection&lt;CombineMusicSelectData&gt;&gt;; null = 现在没在选曲界面)。
        ///
        /// 每条 CombineMusicSelectData = 分类里的一格:
        ///   msDetailData.musicId          这一格是哪个 id
        ///   musicSelectData[STD/DX]       两个槽, 每槽 .MusicData 就是游戏的 MusicData 对象(曲名/艺术家/
        ///                                 BPM/谱面/版本都能直接用), .isExistsScore 是"这档能不能玩"的真值
        /// 同一首会出现在多个分类(本机 13 分类 2611 项 -> 1677 首), 所以按 MusicData.GetID() 精确去重;
        /// STD 和 DX 各有一条 id 的是**两首不同的条目**(本机 1677 个 id 里 1090 个 >=10000),
        /// 所以只合并"同 id 重复", 不做 x/x+10000 合并 —— 合并会把 DX 曲目从曲库里删掉。
        /// 离开选曲界面后 CombineMusicDataList 会被游戏释放 -> 用最后一次成功的快照兜住。
        /// </summary>
        private static void FillSelectList(Probe p, object list)
        {
            _dedupe.Clear();
            var cats = list
                as List<System.Collections.ObjectModel.ReadOnlyCollection<
                    Process.MusicSelectProcess.CombineMusicSelectData>>;
            int catCount = 0;
            int itemCount = 0;
            _selectLive = cats != null && cats.Count > 0;
            if (_selectLive)
            {
                catCount = cats.Count;
                for (int c = 0; c < catCount && p.Items.Count < MaxProbeItems; c++)
                {
                    var page = cats[c];
                    if (page == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < page.Count && p.Items.Count < MaxProbeItems; i++)
                    {
                        var item = page[i];
                        if (item == null)
                        {
                            continue;
                        }
                        itemCount++;
                        int detailId = 0;
                        try
                        {
                            if (item.msDetailData != null)
                            {
                                detailId = item.msDetailData.musicId;
                            }
                        }
                        catch
                        {
                        }
                        var slots = item.musicSelectData;
                        if (slots == null)
                        {
                            continue;
                        }
                        for (int s = 0; s < slots.Count; s++)
                        {
                            var slot = slots[s];
                            if (slot == null)
                            {
                                continue;
                            }
                            var md = slot.MusicData;      // Process.MusicSelectProcess.MusicSelectData.MusicData
                            if (md == null)
                            {
                                continue;
                            }
                            int id = IdOf(md);
                            if (id <= 0)
                            {
                                id = detailId;
                            }
                            AddId(id, md, p.Items);
                        }
                    }
                }
            }
            if (p.Items.Count > 0)
            {
                // 缓存活的结果: 等下离开选曲界面时 CombineMusicDataList 会被释放
                _selectCache = new List<KeyValuePair<int, Manager.MaiStudio.MusicData>>(p.Items);
                _selectCacheAt = DateTime.Now.ToString("HH:mm:ss");
                _selectCacheCats = catCount;
                _selectCacheItems = itemCount;
                p.Shape = "选曲列表 " + catCount + " 分类/" + itemCount + " 项 去重后";
            }
            else if (_selectCache != null)
            {
                // 没在选曲界面(或列表里没有 MusicData): 用最后一份快照, 别让曲库掉回 0 首
                p.Items.AddRange(_selectCache);
                p.Shape = "选曲列表缓存(" + _selectCacheCats + " 分类/" + _selectCacheItems + " 项, "
                    + _selectCacheAt + " 抓的; 现在没在选曲界面)";
            }
            else
            {
                p.Skipped = true;
                p.Shape = cats == null ? "选曲列表不可用(还没进过选曲界面)" : "选曲列表里没有 MusicData";
            }
            p.Count = p.Items.Count;
        }

        /// <summary>
        /// 从任意"像曲目表"的对象里抽 (id, MusicData)。认这些形状:
        ///   IEnumerable&lt;KeyValuePair&lt;int,MusicData&gt;&gt;  (Dictionary / Safe.ReadonlySortedDictionary 之类)
        ///   非泛型 IEnumerable 里的 KVP / 裸 MusicData(用 GetID() 当 key) / 带 Key+Value 属性的项
        ///   嵌套可枚举(如 SortedDictionary&lt;int,List&lt;MusicData&gt;&gt;), 以及只暴露 Values 属性的包装
        /// 单个 item 认不出来只跳过它, 不会把整路丢掉; 枚举中途出异常也保住已收到的条目。
        /// </summary>
        private static void Extract(object table,
            List<KeyValuePair<int, Manager.MaiStudio.MusicData>> sink, ref string shape, int depth)
        {
            if (table == null || sink.Count >= MaxProbeItems || depth > 3)
            {
                return;
            }
            var typed = table as IEnumerable<KeyValuePair<int, Manager.MaiStudio.MusicData>>;
            if (typed != null)
            {
                shape = Shape(shape, "kvp<int,MusicData>");
                foreach (var kv in typed)
                {
                    if (sink.Count >= MaxProbeItems)
                    {
                        break;
                    }
                    AddId(kv.Key, kv.Value, sink);
                }
                return;
            }
            var loose = table as IEnumerable;
            if (loose == null)
            {
                // 有包装类自己不实现 IEnumerable, 只暴露 Values
                PropertyInfo pv = table.GetType().GetProperty("Values");
                if (pv != null && pv.GetIndexParameters().Length == 0)
                {
                    object inner = null;
                    try
                    {
                        inner = pv.GetValue(table, null);
                    }
                    catch
                    {
                    }
                    if (inner != null && !ReferenceEquals(inner, table))
                    {
                        shape = Shape(shape, "values属性");
                        Extract(inner, sink, ref shape, depth + 1);
                        return;
                    }
                }
                shape = Shape(shape, "不认:" + table.GetType().Name);
                return;
            }
            shape = Shape(shape, "loose");
            IEnumerator e = loose.GetEnumerator();
            try
            {
                while (sink.Count < MaxProbeItems)
                {
                    object item;
                    try
                    {
                        if (!e.MoveNext())
                        {
                            break;
                        }
                        item = e.Current;
                    }
                    catch (Exception ex)
                    {
                        // 别的 mod 并发改表把枚举搞炸 -> 已收到的条目照用, 别整路归零
                        shape = Shape(shape, "枚举中断:" + ex.GetType().Name);
                        break;
                    }
                    if (item == null)
                    {
                        continue;
                    }
                    if (item is KeyValuePair<int, Manager.MaiStudio.MusicData>)
                    {
                        var kv = (KeyValuePair<int, Manager.MaiStudio.MusicData>)item;
                        AddId(kv.Key, kv.Value, sink);
                        shape = Shape(shape, "kvp");
                        continue;
                    }
                    var md = item as Manager.MaiStudio.MusicData;
                    if (md != null)
                    {
                        AddId(IdOf(md), md, sink);      // 裸 MusicData 序列: 用 GetID() 当 key
                        shape = Shape(shape, "裸MusicData");
                        continue;
                    }
                    if (AddByProperties(item, sink))
                    {
                        shape = Shape(shape, "key-value属性");
                        continue;
                    }
                    // 键值对但 Value 本身是个列表(DataManager._musicsByAddVersion 就是
                    // SortedDictionary<int, List<MusicData>>, key 是版本号不是曲目 id)
                    // -> 摊平进去, id 一律用 MusicData 自己的 GetID()
                    if (AddByNestedValue(item, sink, ref shape, depth))
                    {
                        continue;
                    }
                    var nested = item as IEnumerable;
                    if (nested != null && !(item is string))
                    {
                        shape = Shape(shape, "嵌套");
                        Extract(nested, sink, ref shape, depth + 1);
                        continue;
                    }
                    shape = Shape(shape, "跳过:" + item.GetType().Name);
                }
            }
            finally
            {
                var d = e as IDisposable;
                if (d != null)
                {
                    try
                    {
                        d.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>Key/Value 属性形状(自定义 KVP 结构 / DictionaryEntry / 老式字典条目)</summary>
        private static bool AddByProperties(object item,
            List<KeyValuePair<int, Manager.MaiStudio.MusicData>> sink)
        {
            System.Type t = item.GetType();
            PropertyInfo pk = t.GetProperty("Key");
            PropertyInfo pv = t.GetProperty("Value");
            if (pk == null || pv == null
                || pk.GetIndexParameters().Length > 0 || pv.GetIndexParameters().Length > 0)
            {
                return false;
            }
            object k, v;
            try
            {
                k = pk.GetValue(item, null);
                v = pv.GetValue(item, null);
            }
            catch
            {
                return false;
            }
            var md = v as Manager.MaiStudio.MusicData;
            if (md == null)
            {
                return false;
            }
            int id = 0;
            try
            {
                id = Convert.ToInt32(k);      // key 可能是 long/uint/枚举, 一律 Convert
            }
            catch
            {
                id = 0;
            }
            AddId(id, md, sink);
            return true;
        }

        /// <summary>
        /// 条目是 "键值对 + Value 是可枚举"(如 KeyValuePair&lt;int, List&lt;MusicData&gt;&gt;) 时摊平进去。
        /// DataManager._musicsByAddVersion 就是这个形状, 而它的 key 是版本号不是曲目 id,
        /// 所以里面的 MusicData 一律用 GetID() 拿 id。
        /// </summary>
        private static bool AddByNestedValue(object item,
            List<KeyValuePair<int, Manager.MaiStudio.MusicData>> sink, ref string shape, int depth)
        {
            PropertyInfo pv = item.GetType().GetProperty("Value");
            if (pv == null || pv.GetIndexParameters().Length > 0)
            {
                return false;
            }
            object v;
            try
            {
                v = pv.GetValue(item, null);
            }
            catch
            {
                return false;
            }
            if (v == null || v is Manager.MaiStudio.MusicData || v is string)
            {
                return false;
            }
            var inner = v as IEnumerable;
            if (inner == null)
            {
                return false;
            }
            shape = Shape(shape, "键值+嵌套");
            Extract(inner, sink, ref shape, depth + 1);
            return true;
        }

        private static void AddId(int id, Manager.MaiStudio.MusicData md,
            List<KeyValuePair<int, Manager.MaiStudio.MusicData>> sink)
        {
            if (md == null)
            {
                return;
            }
            if (id <= 0)
            {
                id = IdOf(md);      // key 缺失/不是整数 -> 用 MusicData 自己的 id
            }
            if (id <= 0)
            {
                return;
            }
            if (!_dedupe.Add(id))
            {
                return;             // 多路/嵌套列表里同一首会出现多次, 只算一条
            }
            sink.Add(new KeyValuePair<int, Manager.MaiStudio.MusicData>(id, md));
        }

        private static int IdOf(Manager.MaiStudio.MusicData md)
        {
            try
            {
                return md.GetID();
            }
            catch
            {
                return 0;
            }
        }

        private static string Shape(string cur, string add)
        {
            if (cur.Length == 0)
            {
                return add;
            }
            if (cur.IndexOf(add, StringComparison.Ordinal) >= 0)
            {
                return cur;
            }
            return cur.Length < 90 ? cur + "+" + add : cur;
        }

        /// <summary>DataManager 上所有"像曲目表"的实例字段(_musics 排最前)</summary>
        private static List<FieldInfo> MusicTableFields()
        {
            List<FieldInfo> hits = new List<FieldInfo>();
            try
            {
                FieldInfo[] all = typeof(Manager.DataManager).GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (FieldInfo f in all)
                {
                    if (f.IsStatic || !LooksLikeMusicTable(f))
                    {
                        continue;
                    }
                    if (hits.Count >= MaxProbeSources)
                    {
                        break;
                    }
                    hits.Add(f);
                }
                // _musics 排最前(常规机器上就是它, 省得每次都多枚举几路)
                hits.Sort(delegate (FieldInfo a, FieldInfo b)
                {
                    return Rank(a).CompareTo(Rank(b));
                });
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 枚举 DataManager 字段失败: " + e.Message);
            }
            return hits;
        }

        private static int Rank(FieldInfo f)
        {
            if (f.Name == "_musics")
            {
                return 0;
            }
            return DirectMusicValue(f.FieldType) ? 1 : 2;
        }

        private static bool DirectMusicValue(System.Type t)
        {
            if (t == null || !t.IsGenericType)
            {
                return false;
            }
            foreach (System.Type a in t.GetGenericArguments())
            {
                if (a == typeof(Manager.MaiStudio.MusicData))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>只看字段类型就知道要不要试 —— 避免把 DataManager 上几十张别的表都枚举一遍</summary>
        private static bool LooksLikeMusicTable(FieldInfo f)
        {
            System.Type t = f.FieldType;
            if (t == null)
            {
                return false;
            }
            if (t == typeof(Manager.MaiStudio.MusicData))
            {
                return true;
            }
            if (t.IsGenericType)
            {
                foreach (System.Type a in t.GetGenericArguments())
                {
                    if (a == typeof(Manager.MaiStudio.MusicData)
                        || a == typeof(List<Manager.MaiStudio.MusicData>))
                    {
                        return true;
                    }
                    if (a.IsGenericType)
                    {
                        foreach (System.Type b in a.GetGenericArguments())
                        {
                            if (b == typeof(Manager.MaiStudio.MusicData))
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
            if (!typeof(IEnumerable).IsAssignableFrom(t))
            {
                return false;
            }
            return t.Name.IndexOf("MusicData", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ProbeReportJson(List<Probe> probes, Probe best)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < probes.Count; i++)
            {
                Probe p = probes[i];
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"name\":\"").Append(Escape(p.Name)).Append('"');
                sb.Append(",\"count\":").Append(p.Count);
                sb.Append(",\"shape\":\"").Append(Escape(p.Shape)).Append('"');
                sb.Append(",\"error\":\"").Append(Escape(p.Error)).Append('"');
                sb.Append(",\"picked\":").Append(ReferenceEquals(p, best) ? "true" : "false");
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>遍历曲目表: (id, MusicData)</summary>
        private static IEnumerable<KeyValuePair<int, Manager.MaiStudio.MusicData>> Each()
        {
            var snap = Snapshot(false);
            for (int i = 0; i < snap.Count; i++)
            {
                yield return snap[i];
            }
        }

        internal static int RawCount()
        {
            return Snapshot(false).Count;
        }

        /// <summary>导出的曲目数(网页线程也会读, 所以只给缓存值, 不现场探测)</summary>
        internal static int Count
        {
            get { return _jsonCount > 0 ? _jsonCount : 0; }
        }

        /// <summary>自检用: 曲目表数据源明细(/api/selfcheck 里的 songTable 段)</summary>
        internal static string DiagJson()
        {
            try
            {
                lock (_snapLock)
                {
                    if (_snap == null)
                    {
                        ProbeAll();
                        _snapMs = Environment.TickCount;
                    }
                    StringBuilder sb = new StringBuilder(512);
                    sb.Append("{\"source\":\"").Append(Escape(_snapSource)).Append('"');
                    sb.Append(",\"entries\":").Append(_snapRaw);
                    sb.Append(",\"songs\":").Append(_jsonCount);
                    sb.Append(",\"rev\":").Append(Rev);
                    sb.Append(",\"byId\":").Append(_byId.Count);
                    sb.Append(",\"retryFails\":").Append(_retryFails);
                    sb.Append(",\"selectList\":{\"live\":").Append(_selectLive ? "true" : "false")
                        .Append(",\"cached\":").Append(_selectCache != null ? "true" : "false")
                        .Append(",\"cats\":").Append(_selectCacheCats)
                        .Append(",\"items\":").Append(_selectCacheItems)
                        .Append(",\"at\":\"").Append(Escape(_selectCacheAt)).Append("\"}");
                    sb.Append(",\"disableFilter\":\"").Append(_disableFilterOff
                        ? "off(整库被判禁用, 已自动关闭)" : "on").Append('"');
                    sb.Append(",\"tried\":").Append(_probeReport);
                    sb.Append(",\"alias\":{\"count\":").Append(Aliases.Count)
                        .Append(",\"songs\":").Append(Aliases.SongCount).Append('}');
                    sb.Append('}');
                    return sb.ToString();
                }
            }
            catch (Exception e)
            {
                return "{\"source\":\"诊断失败\",\"error\":\"" + Escape(e.Message) + "\"}";
            }
        }

        private static int _retryMs;
        private static bool _everRetried;
        private static int _retryFails;
        private const int RetryIntervalMs = 5000;
        private const int RetryMaxFails = 3;

        /// <summary>
        /// 表是空的时候补一次重建 —— 但要有节流: 老代码这里是无条件 Json(true),
        /// 表一直空(别人机器上反射读不到)时, 网页每轮询一次 Now Playing 就整表重建一次,
        /// rev 疯涨(报告里那个 382)+ 白耗 CPU。现在: 距上次重试 >=5 秒才试一次,
        /// 连续 3 次仍旧 0 首就彻底放弃并打一条 Warning(明细去 /api/selfcheck 看)。
        /// </summary>
        private static void EnsureTable()
        {
            if (!MainThread.IsMain)
            {
                return;   // 重建只在主线程做
            }
            if (_byId.Count > 0)
            {
                _retryFails = 0;
                return;
            }
            if (_retryFails >= RetryMaxFails)
            {
                return;
            }
            int now = Environment.TickCount;
            if (_everRetried && unchecked(now - _retryMs) < RetryIntervalMs)
            {
                return;
            }
            _everRetried = true;
            _retryMs = now;
            Json(true);
            if (_byId.Count > 0)
            {
                _retryFails = 0;
                ModLog.Info("[SongRequest] 曲目表重建成功: " + _byId.Count + " 首 (来源 " + _snapSource + ")");
                return;
            }
            _retryFails++;
            Warn("[SongRequest] 曲目表重建后仍是 0 首(第 " + _retryFails + "/"
                + RetryMaxFails + " 次, 数据源: " + _snapSource + ")"
                + (_retryFails >= RetryMaxFails ? " —— 停止自动重试, 明细见 /api/selfcheck" : ""));
        }

        internal static Manager.MaiStudio.MusicData GetMusic(int id)
        {
            try
            {
                if (_byId.Count == 0)
                {
                    EnsureTable();
                }
                var byId = _byId;
                Manager.MaiStudio.MusicData md;
                if (byId.TryGetValue(id, out md))
                {
                    return md;
                }
                if (id >= 10000 && byId.TryGetValue(id % 10000, out md))
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
                if (md == null || (!_disableFilterOff && IsDisabled(md)))
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

        /// <summary>曲目表 JSON(缓存; force=true 才重建)。重建只允许在主线程, 别的线程只拿缓存</summary>
        internal static string Json(bool force)
        {
            if ((!force && _json != null) || !MainThread.IsMain)
            {
                return CachedJson;
            }
            string built = Build();
            if (built != _json)
            {
                // 内容真的变了才换版本号: 以前每次重建都 +1, 两个以上网页互相看到 rev 变化就互相触发刷新,
                // 变成每几秒整表重建 + 清空封面缓存
                _json = built;
                Rev++;
            }
            return _json;
        }

        /// <summary>丢掉数据源快照缓存后重出(网页 /api/songs?refresh=1 经 MainThread.Run 在主线程调用)</summary>
        internal static string JsonFresh()
        {
            lock (_snapLock)
            {
                _snap = null;
            }
            return Json(true);
        }

        private static string Build()
        {
            StringBuilder sb = new StringBuilder(1 << 21);
            sb.Append('[');
            int n = 0;
            int prevCount = _jsonCount;
            // 别名文件整次导出只检查一次(以前每首歌都查一次文件时间, 1600 首就是 1600 次磁盘访问)
            Aliases.Ensure();
            // STD/DX 与"能不能玩"都按当前选曲列表重算(Rev 现在只在内容变了才 +1, 不能再靠它判断缓存过期)
            _types = null;
            SelectDriver.ResetPlayableCache();
            try
            {
                var byId = new Dictionary<int, Manager.MaiStudio.MusicData>();
                var snap = Snapshot(false);
                int raw = 0;
                for (int i = 0; i < snap.Count; i++)
                {
                    Manager.MaiStudio.MusicData md = snap[i].Value;
                    if (md == null)
                    {
                        continue;
                    }
                    raw++;
                    byId[md.GetID()] = md;
                }
                _byId = byId;
                n = AppendAll(sb, snap, !_disableFilterOff);
                if (n == 0 && raw > 0 && !_disableFilterOff)
                {
                    // 保险: 整库都被 disable 过滤清空了 —— 宁可多显示几首被禁用的歌, 也不能给用户 0 首
                    _disableFilterOff = true;
                    if (!_disableFilterWarned)
                    {
                        _disableFilterWarned = true;
                        Warn("[SongRequest] 检测到整库被 disable 过滤清空(" + raw
                            + " 首全部被判为禁用), 已自动关闭 disable 过滤 —— 这几首会照常显示");
                    }
                    sb = new StringBuilder(1 << 21);
                    sb.Append('[');
                    n = AppendAll(sb, snap, false);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[SongRequest] 导出曲目表失败: " + e.Message);
            }
            sb.Append(']');
            _jsonCount = n;
            // 记下这次用的原始条目数: Tick 靠它判断曲目表有没有变(不能跟 _jsonCount 比, 那个已滤掉禁用曲目)
            _builtSnapCount = _snapRaw;
            // 曲目数变了(热导入) -> 曲绘缓存也作废; 只是重进选曲界面就别清, 不然网页要把所有封面重编码一遍
            if (n != prevCount)
            {
                Jackets.ClearCache();
            }
            _types = null;
            ModLog.Info("[SongRequest] 曲目表已导出: " + n + " 首"
                + (_disableFilterOff ? " (disable 过滤已关闭)" : ""));
            return sb.ToString();
        }

        /// <summary>按有没有开 disable 过滤渲染 JSON 数组体, 返回写进去的条数</summary>
        private static int AppendAll(StringBuilder sb, List<KeyValuePair<int, Manager.MaiStudio.MusicData>> snap,
            bool useDisableFilter)
        {
            int n = 0;
            bool first = true;
            for (int i = 0; i < snap.Count; i++)
            {
                Manager.MaiStudio.MusicData md = snap[i].Value;
                if (md == null)
                {
                    continue;
                }
                if (useDisableFilter && IsDisabled(md))
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
            return n;
        }

        private static FieldInfo _fDisable;
        private static bool _disableFieldProbed;
        private static bool _disableFilterOff;
        private static bool _disableFilterWarned;

        /// <summary>
        /// 这首曲子算不算"游戏里禁用"。
        /// 老代码只看 MusicData.IsDisable() —— 在别的版本/别的私服数据上它可能把**整库**判成禁用
        /// (那台机器上曲库就是被这一条清成 0 首的)。现在: IsDisable() 为真 **且** 直接读到的
        /// disable 字段也为真 才跳过; 字段读不到(或取值失败)就退回只看 IsDisable()。
        /// </summary>
        private static bool IsDisabled(Manager.MaiStudio.MusicData md)
        {
            if (md == null)
            {
                return true;
            }
            bool byMethod;
            try
            {
                byMethod = md.IsDisable();
            }
            catch
            {
                return false;      // 连判定都炸了 -> 当没禁用(宁可多显示, 不能少给)
            }
            if (!byMethod)
            {
                return false;
            }
            bool? raw = RawDisable(md);
            return !raw.HasValue || raw.Value;
        }

        /// <summary>直接读 disable 字段(不同版本可能是字段 / 自动属性 / 只读属性), 读不到返回 null</summary>
        private static bool? RawDisable(Manager.MaiStudio.MusicData md)
        {
            try
            {
                if (!_disableFieldProbed)
                {
                    _disableFieldProbed = true;
                    System.Type t = md.GetType();
                    _fDisable = t.GetField("disable",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_fDisable == null)
                    {
                        _fDisable = t.GetField("<disable>k__BackingField",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    if (_fDisable == null)
                    {
                        PropertyInfo pv = t.GetProperty("disable",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (pv != null && pv.GetIndexParameters().Length == 0 && pv.CanRead)
                        {
                            _fDisableProp = pv;
                        }
                    }
                    ModLog.Info("[SongRequest] disable 判定: "
                        + (_fDisable != null ? ("字段 " + _fDisable.Name)
                            : (_fDisableProp != null ? "属性 disable" : "读不到, 只看 IsDisable()")));
                }
                object v = _fDisable != null ? _fDisable.GetValue(md)
                    : (_fDisableProp != null ? _fDisableProp.GetValue(md, null) : null);
                if (v is bool)
                {
                    return (bool)v;
                }
                return v == null ? (bool?)null : Convert.ToBoolean(v);
            }
            catch
            {
                return null;
            }
        }

        private static PropertyInfo _fDisableProp;

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
