using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Manager;
using MAI2.Util;
using MAI2System;
using Process;
using UnityEngine;

namespace SongRequestMod
{
    /// <summary>
    /// 抓 MusicSelectProcess 实例。
    /// 跳曲用的是游戏自己的公开 API:
    ///   CurrentCategorySelect / CurrentMusicSelect / ScoreType / CurrentDifficulty / DifficultySelectIndex
    ///   ChangeBGM() / CombineMusicDataList / MonitorArray[i].SetScrollMusicCard|SetScrollGenreCard|OutGenreTab
    /// 换画面: 子序列数组切到 SubSequence.Difficulty(=3, 难度选择画面)。
    /// 注: 游戏里 `Process.SubSequence` 既是枚举名又是命名空间名(SequenceBase 在那个命名空间里),
    /// 静态引用必撞, 所以子序列那几个 private 数组一律反射拿、反射调, 不给编译器机会。
    /// </summary>
    [HarmonyPatch(typeof(MusicSelectProcess), "OnStart")]
    internal static class Patch_SelectOnStart
    {
        private static void Postfix(MusicSelectProcess __instance)
        {
            SelectDriver.Capture(__instance);
        }
    }

    [HarmonyPatch(typeof(MusicSelectProcess), "OnRelease")]
    internal static class Patch_SelectOnRelease
    {
        private static void Postfix()
        {
            SelectDriver.Release();
        }
    }

    internal static class SelectDriver
    {
        /// <summary>子序列下标(MusicSelectProcess.SubSequence): Music=0, Genre=1, SortSetting=2,
        /// Difficulty=3, UtageDifficulty=4, Menu=5, Option=6, Character=7</summary>
        private const int SubSeqMusic = 0;
        private const int SubSeqDifficulty = 3;
        private const int SubSeqUtageDifficulty = 4;
        private static readonly string[] SubSeqNames =
            { "选曲列表", "分类选择", "排序设置", "难度选择", "宴会场难度选择", "菜单", "选项", "角色选择" };
        /// <summary>MusicSelectProcess._mainSequence 里的 Update(=3): 只有这时才能点歌, 进场/退场动画中不能动</summary>
        private const int MainSeqUpdate = 3;
        /// <summary>跟游戏自己确认选曲时一样, 切画面期间锁输入这么久</summary>
        private const float InputLockMs = 1200f;
        /// <summary>游戏还在锁输入(切画面动画中)时, 点歌请求最多等这么久再执行</summary>
        private const int DeferMaxMs = 3000;

        /// <summary>被后面的点歌取代的请求, 结果以这个前缀开头(Web 转成 superseded, 网页不当错误)</summary>
        internal const string SupersededPrefix = "SUPERSEDED:";

        private static MusicSelectProcess _process;
        private static Array _subSeqArray;    // SequenceBase[player][]
        private static Array _curSeq;         // SubSequence[player]
        private static Array _prevSeq;        // SubSequence[player]
        private static FieldInfo _fSubSeqArray;
        private static FieldInfo _fCurSeq;
        private static FieldInfo _fPrevSeq;
        private static FieldInfo _fMainSeq;
        private static MethodInfo _mSyncNext;
        /// <summary>主线程每帧更新的"在不在选曲界面"(给网页线程读, 网页线程不能自己去查游戏对象)</summary>
        private static volatile bool _inSelectCached;

        /// <summary>网页线程塞进来、主线程执行的请求</summary>
        private sealed class Request
        {
            public int MusicId;
            public int Difficulty;   // -1 = 自动(最高可用)
            public bool Random;
            public int RandomDiff = -1;
            public string Result;
            public bool Done;
            public int QueuedAt = Environment.TickCount;
            public readonly object Lock = new object();
        }

        /// <summary>正在等游戏解锁输入的那一个请求(只留最新的)</summary>
        private static Request _deferred;
        /// <summary>切到难度画面的协程还没跑完(协程万一没跑, 3 秒后自动作废, 免得之后的点歌一直在等)</summary>
        private static bool _switchPending;
        private static int _switchPendingAt;

        private static readonly List<Request> _queue = new List<Request>();
        private static readonly object _queueLock = new object();

        // ── 自动验证(给网页/我自己查链路用, 不参与正常点歌) ──
        private static int _autoTestId;
        private static int _autoTestDiff = -1;
        internal static string LastJumpResult = "";
        internal static string LastJumpAt = "";

        /// <summary>等下次进入选曲界面时自动跳一首, 结果记在 LastJumpResult</summary>
        internal static void ArmAutoTest(int musicId, int difficulty)
        {
            _autoTestId = musicId;
            _autoTestDiff = difficulty;
            LastJumpResult = "(已布防: 进选曲界面就点 " + musicId + " / "
                + SongTable.DifficultyName(difficulty) + ")";
            LastJumpAt = DateTime.Now.ToString("HH:mm:ss");
        }

        /// <summary>自检用: 各环节状态</summary>
        internal static string Diagnostics()
        {
            var sb = new System.Text.StringBuilder();
            bool inSelect = InSelect;   // 会顺手触发兜底查找
            sb.Append("{\"inSelect\":").Append(inSelect ? "true" : "false");
            sb.Append(",\"process\":").Append(_process != null ? "true" : "false");
            sb.Append(",\"subSeqCaptured\":").Append(_subSeqArray != null ? "true" : "false");
            int cats = -1;
            int items = 0;
            try
            {
                if (_process != null && _process.CombineMusicDataList != null)
                {
                    cats = _process.CombineMusicDataList.Count;
                    for (int i = 0; i < cats; i++)
                    {
                        items += _process.CombineMusicDataList[i].Count;
                    }
                }
            }
            catch
            {
            }
            sb.Append(",\"categories\":").Append(cats);
            sb.Append(",\"items\":").Append(items);
            sb.Append(",\"cursorId\":").Append(CursorMusicId);
            sb.Append(",\"cursorDiff\":").Append(CursorDifficulty);
            sb.Append(",\"armed\":").Append(_autoTestId > 0 ? _autoTestId.ToString() : "0");
            sb.Append(",\"lastJump\":\"").Append(SongTable.Escape(LastJumpResult)).Append('"');
            sb.Append(",\"lastJumpAt\":\"").Append(SongTable.Escape(LastJumpAt)).Append('"');
            sb.Append(",\"songTable\":").Append(SongTable.DiagJson());
            sb.Append('}');
            return sb.ToString();
        }

        internal static void Capture(MusicSelectProcess process)
        {
            _process = process;
            _switchPending = false;
            ResetPlayableCache();
            try
            {
                if (_fSubSeqArray == null)
                {
                    _fSubSeqArray = AccessTools.Field(typeof(MusicSelectProcess), "_subSequenceArray");
                    _fCurSeq = AccessTools.Field(typeof(MusicSelectProcess), "_currentPlayerSubSequence");
                    _fPrevSeq = AccessTools.Field(typeof(MusicSelectProcess), "_beforePlayerSubSequence");
                    _fMainSeq = AccessTools.Field(typeof(MusicSelectProcess), "_mainSequence");
                    // 游戏自己切子画面用的 SyncNext(SubSequence): Reset 当前 -> 记 before -> 切过去 -> OnStartSequence, 所有玩家一起
                    _mSyncNext = AccessTools.Method(typeof(MusicSelectProcess), "SyncNext");
                }
                _subSeqArray = _fSubSeqArray == null ? null : (Array)_fSubSeqArray.GetValue(process);
                _curSeq = _fCurSeq == null ? null : (Array)_fCurSeq.GetValue(process);
                _prevSeq = _fPrevSeq == null ? null : (Array)_fPrevSeq.GetValue(process);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 取子序列数组失败(不影响点歌, 只是不会自动跳难度画面): " + e.Message);
                _subSeqArray = null;
            }
            ModLog.Info("[SongRequest] MusicSelectProcess 已捕获, 曲目分类 "
                + (process.CombineMusicDataList == null ? -1 : process.CombineMusicDataList.Count) + " 组, 子序列="
                + (_subSeqArray != null));
        }

        internal static void Release()
        {
            _process = null;
            _subSeqArray = null;
            _curSeq = null;
            _prevSeq = null;
            _switchPending = false;
            _inSelectCached = false;
            ModLog.Info("[SongRequest] MusicSelectProcess 已释放");
        }

        /// <summary>只能在主线程用(会碰游戏对象); 网页线程用 InSelectCached</summary>
        internal static bool InSelect
        {
            get
            {
                if (_process == null || _process.CombineMusicDataList == null)
                {
                    TryFindProcess();
                }
                return _process != null && _process.CombineMusicDataList != null;
            }
        }

        /// <summary>网页线程可读: 主线程最近一次看到的"在不在选曲界面"</summary>
        internal static bool InSelectCached
        {
            get { return _inSelectCached; }
        }

        /// <summary>主线程每帧调用, 刷新给网页线程看的缓存</summary>
        internal static void UpdateCache()
        {
            _inSelectCached = InSelect;
        }

        private static float _findCooldown;
        private static object _lastMonitor;

        /// <summary>
        /// 选曲列表快照(给 SongTable 当数据源①用): 游戏自己建好的可点歌曲表,
        /// 每条里有 msDetailData.musicId 和 musicSelectData[STD/DX].MusicData(真 MusicData 对象)。
        /// 没进过选曲界面(或已释放)时返回 null —— 调用方要能优雅跳过。
        /// </summary>
        internal static List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> SelectList
        {
            get
            {
                try
                {
                    return InSelect ? _process.CombineMusicDataList : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 兜底: 万一时序上没接到 OnStart(或以后游戏改了方法名), 就从场景里活的
        /// MusicSelectMonitor 反查它的 MusicSelectProcess 字段把实例捞回来。
        /// </summary>
        private static void TryFindProcess()
        {
            if (!MainThread.IsMain)
            {
                return;   // FindObjectOfType / Time 都是 Unity API, 只能主线程调
            }
            try
            {
                if (Time.unscaledTime < _findCooldown)
                {
                    return;
                }
                _findCooldown = Time.unscaledTime + 1f;
                Monitor.MusicSelectMonitor mon =
                    UnityEngine.Object.FindObjectOfType<Monitor.MusicSelectMonitor>();
                if (mon == null || mon == _lastMonitor)
                {
                    return;
                }
                _lastMonitor = mon;
                object proc = null;
                FieldInfo[] fields = typeof(Monitor.MusicSelectMonitor).GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (FieldInfo f in fields)
                {
                    if (typeof(MusicSelectProcess).IsAssignableFrom(f.FieldType))
                    {
                        proc = f.GetValue(mon);
                        if (proc != null)
                        {
                            ModLog.Info("[SongRequest] 兜底找到选曲进程, 字段 " + f.Name);
                            break;
                        }
                    }
                }
                if (proc is MusicSelectProcess)
                {
                    Capture((MusicSelectProcess)proc);
                }
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 兜底找进程失败: " + e.Message);
            }
        }

        /// <summary>当前光标所在曲目 id(None 返回 -1)</summary>
        internal static int CursorMusicId
        {
            get
            {
                try
                {
                    if (!InSelect)
                    {
                        return -1;
                    }
                    var list = _process.CombineMusicDataList;
                    int c = _process.CurrentCategorySelect;
                    int m = _process.CurrentMusicSelect;
                    if (c < 0 || c >= list.Count)
                    {
                        return -1;
                    }
                    if (m < 0 || m >= list[c].Count)
                    {
                        return -1;
                    }
                    return list[c][m].msDetailData.musicId;
                }
                catch
                {
                    return -1;
                }
            }
        }

        internal static int CursorDifficulty
        {
            get
            {
                try
                {
                    return InSelect ? _process.GetCurrentDifficulty(0) : -1;
                }
                catch
                {
                    return -1;
                }
            }
        }

        // ── 网页 -> 主线程 ────────────────────────────────────────────

        /// <summary>
        /// STD / DX 类型表(直接读游戏自己的选曲数据结构, 最准):
        ///   CombineMusicSelectData.existStandardScore / existDeluxeScore
        /// 返回 曲目 id -> 0=只有标准 1=只有 DX 2=两种都有。没进过选曲界面时返回空表。
        /// </summary>
        internal static Dictionary<int, int> ScoreTypeMap()
        {
            Dictionary<int, int> map = new Dictionary<int, int>();
            try
            {
                if (!InSelect)
                {
                    return map;
                }
                var list = _process.CombineMusicDataList;
                for (int cat = 0; cat < list.Count; cat++)
                {
                    var page = list[cat];
                    for (int i = 0; i < page.Count; i++)
                    {
                        var c = page[i];
                        if (c == null || c.msDetailData == null)
                        {
                            continue;
                        }
                        int id = c.msDetailData.musicId;
                        if (id <= 0)
                        {
                            continue;
                        }
                        int v = 0;
                        if (c.existStandardScore)
                        {
                            v |= 1;
                        }
                        if (c.existDeluxeScore)
                        {
                            v |= 2;
                        }
                        map[id] = v == 0 ? (id >= 10000 ? 2 : 1) : v;
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 读 STD/DX 类型失败: " + e.Message);
            }
            return map;
        }

        /// <summary>
        /// 每首曲子的"这档谱到底能不能玩" —— 直读游戏选曲数据里的
        /// CombineMusicSelectData.musicSelectData[ScoreType].isExistsScore[5]。
        /// 这才是游戏自己判断用的值(XML 里的 isEnable 只是声明, 谱面文件缺失/版本没同步时
        /// 游戏仍然认为不存在 -> 会把你点的 Re:MASTER 悄悄退成 MASTER)。
        /// 返回 曲目 id -> bool[5]; 没进过选曲界面时返回空表。
        /// </summary>
        internal static Dictionary<int, bool[]> PlayableMap()
        {
            Dictionary<int, bool[]> map = new Dictionary<int, bool[]>();
            try
            {
                if (!InSelect)
                {
                    return map;
                }
                var list = _process.CombineMusicDataList;
                for (int cat = 0; cat < list.Count; cat++)
                {
                    var page = list[cat];
                    if (page == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < page.Count; i++)
                    {
                        var c = page[i];
                        if (c == null || c.msDetailData == null || c.musicSelectData == null)
                        {
                            continue;
                        }
                        int detailId = c.msDetailData.musicId;
                        // STD / DX 两个槽都要读: 只读当前 ScoreType 那一个槽的话, 另一边的曲子拿不到真值
                        for (int s = 0; s < c.musicSelectData.Count; s++)
                        {
                            var msd = c.musicSelectData[s];
                            if (msd == null)
                            {
                                continue;
                            }
                            bool[] arr = ExistsScore(msd);
                            if (arr == null)
                            {
                                continue;
                            }
                            int id = detailId;
                            try
                            {
                                if (msd.MusicData != null && msd.MusicData.GetID() > 0)
                                {
                                    id = msd.MusicData.GetID();
                                }
                            }
                            catch
                            {
                            }
                            if (id > 0)
                            {
                                map[id] = arr;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 读可玩谱面表失败: " + e.Message);
            }
            return map;
        }

        /// <summary>
        /// 取 MusicSelectData.isExistsScore —— 这档谱到底存不存在(游戏自己的真值)。
        /// 注意: 这个字段在 1.70 是 List&lt;bool&gt;, 老代码写的是 `as bool[]` → 永远是 null,
        /// 于是"游戏真值"这条路一直静默失效、白读一遍。这里 bool[] / List&lt;bool&gt; / IList 都认。
        /// </summary>
        private static bool[] ExistsScore(MusicSelectProcess.MusicSelectData msd)
        {
            try
            {
                if (_fExistsScore == null)
                {
                    _fExistsScore = msd.GetType().GetField("isExistsScore",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_fExistsScore == null)
                {
                    return null;
                }
                object raw = _fExistsScore.GetValue(msd);
                var arr = raw as bool[];
                if (arr != null)
                {
                    return arr;
                }
                var list = raw as List<bool>;
                if (list != null)
                {
                    return list.ToArray();
                }
                var any = raw as IList;
                if (any != null && any.Count > 0)
                {
                    bool[] r = new bool[any.Count];
                    for (int i = 0; i < any.Count; i++)
                    {
                        r[i] = Convert.ToBoolean(any[i]);
                    }
                    return r;
                }
            }
            catch
            {
            }
            return null;
        }

        private static FieldInfo _fExistsScore;
        private static Dictionary<int, bool[]> _playable;

        /// <summary>选曲列表变了(进选曲界面 / 重建曲目表)时丢掉"能不能玩"缓存, 下次用到再按当前列表重算</summary>
        internal static void ResetPlayableCache()
        {
            _playable = null;
        }

        /// <summary>该曲该难度游戏是否认为可玩(null=还不知道, 那就按 XML 的来)</summary>
        internal static bool? GamePlayable(int musicId, int diff)
        {
            try
            {
                var playable = _playable;
                if (playable == null)
                {
                    playable = PlayableMap();
                    // 没在选曲界面时拿到的是空表, 不缓存, 等进了选曲界面再算
                    _playable = playable.Count > 0 ? playable : null;
                }
                if (playable.Count == 0)
                {
                    return null;
                }
                bool[] arr;
                if (!playable.TryGetValue(musicId, out arr) || arr == null)
                {
                    return null;
                }
                if (diff < 0 || diff >= arr.Length)
                {
                    return false;
                }
                return arr[diff];
            }
            catch
            {
                return null;
            }
        }

        internal static string Enqueue(int musicId, int difficulty)
        {
            Request r = new Request();
            r.MusicId = musicId;
            r.Difficulty = difficulty;
            lock (_queueLock)
            {
                _queue.Add(r);
            }
            // 最多等 5 秒(主线程每帧都会来取; 游戏在切画面时最多再等 3 秒)
            WaitResult(r, 5000);
            return r.Result;
        }

        internal static string EnqueueRandom(int difficulty)
        {
            Request r = new Request();
            r.Random = true;
            r.RandomDiff = difficulty;
            lock (_queueLock)
            {
                _queue.Add(r);
            }
            WaitResult(r, 5000);
            return r.Result;
        }

        private static void WaitResult(Request r, int ms)
        {
            lock (r.Lock)
            {
                if (!r.Done)
                {
                    System.Threading.Monitor.Wait(r.Lock, ms);
                }
            }
        }

        internal static void RunPending()
        {
            // 自动验证: 进了选曲界面就自己点一首(网页 /api/selftest 触发, 用来确认跳转链路)
            if (_autoTestId > 0 && InSelect)
            {
                int id = _autoTestId;
                int diff = _autoTestDiff;
                _autoTestId = 0;
                try
                {
                    LastJumpResult = Jump(id, diff);
                }
                catch (Exception e)
                {
                    LastJumpResult = "自动验证异常: " + e.Message;
                }
                LastJumpAt = DateTime.Now.ToString("HH:mm:ss");
                ModLog.Info("[SongRequest] 自动点歌验证: " + LastJumpResult);
            }

            Request[] batch = null;
            lock (_queueLock)
            {
                if (_queue.Count > 0)
                {
                    batch = _queue.ToArray();
                    _queue.Clear();
                }
            }
            // 连点只执行最新的一个: 以前同一帧里把排队的请求全执行一遍(每个都 ChangeBGM + 切画面),
            // 连点几下就把选曲界面搅乱。前面的直接回"被取代"。
            if (batch != null)
            {
                Request newest = batch[batch.Length - 1];
                for (int i = 0; i < batch.Length - 1; i++)
                {
                    Complete(batch[i], SupersededPrefix + "已被后面的点歌取代");
                }
                if (_deferred != null && _deferred != newest)
                {
                    Complete(_deferred, SupersededPrefix + "已被后面的点歌取代");
                }
                _deferred = newest;
            }
            Request r = _deferred;
            if (r == null)
            {
                return;
            }
            if (_switchPending && unchecked(Environment.TickCount - _switchPendingAt) > DeferMaxMs)
            {
                _switchPending = false;
            }
            // 游戏正在切画面(锁着输入)或上一次跳转还没切完: 先等, 别在动画中间改状态
            if (_switchPending || AnyInputLocked())
            {
                if (unchecked(Environment.TickCount - r.QueuedAt) < DeferMaxMs)
                {
                    return;
                }
                _deferred = null;
                Complete(r, "游戏正在切换画面, 请稍后再点");
                return;
            }
            _deferred = null;
            string msg;
            try
            {
                msg = r.Random ? JumpRandom(r.RandomDiff) : Jump(r.MusicId, r.Difficulty);
            }
            catch (Exception e)
            {
                msg = "点歌失败: " + e.Message;
                MelonLogger.Warning("[SongRequest] 点歌异常: " + e);
            }
            Complete(r, msg);
        }

        private static void Complete(Request r, string msg)
        {
            lock (r.Lock)
            {
                r.Result = msg;
                r.Done = true;
                System.Threading.Monitor.PulseAll(r.Lock);
            }
        }

        /// <summary>有登录的玩家里, 有没有谁正被游戏锁着输入(切画面动画中)</summary>
        private static bool AnyInputLocked()
        {
            try
            {
                if (_process == null)
                {
                    return false;
                }
                for (int p = 0; p < _process.MonitorArray.Length; p++)
                {
                    if (IsEntry(p) && _process.IsInputLocking(p))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>现在能不能点歌; 不能就返回原因</summary>
        private static string BlockReason()
        {
            try
            {
                if (_fMainSeq != null && Convert.ToInt32(_fMainSeq.GetValue(_process)) != MainSeqUpdate)
                {
                    return "选曲界面还在进场/退场, 请稍后再点";
                }
                if (_curSeq != null)
                {
                    for (int p = 0; p < _curSeq.Length && p < _process.MonitorArray.Length; p++)
                    {
                        if (!IsEntry(p))
                        {
                            continue;
                        }
                        int cur = Convert.ToInt32(_curSeq.GetValue(p));
                        if (cur != SubSeqMusic && cur != SubSeqDifficulty && cur != SubSeqUtageDifficulty)
                        {
                            string where = cur >= 0 && cur < SubSeqNames.Length ? SubSeqNames[cur] : ("画面 " + cur);
                            return "游戏现在在「" + where + "」, 请先回到选曲列表再点歌";
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 读选曲状态失败: " + e.Message);
            }
            return null;
        }

        /// <summary>有登录的玩家里, 有没有谁已经在(宴会场)难度画面</summary>
        private static bool AnyInDifficulty()
        {
            try
            {
                if (_curSeq == null)
                {
                    return false;
                }
                for (int p = 0; p < _curSeq.Length && p < _process.MonitorArray.Length; p++)
                {
                    int cur = Convert.ToInt32(_curSeq.GetValue(p));
                    if (IsEntry(p) && (cur == SubSeqDifficulty || cur == SubSeqUtageDifficulty))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>调游戏自己的 SyncNext(子画面)</summary>
        private static bool SyncNext(int subSeq)
        {
            if (_mSyncNext == null || _curSeq == null)
            {
                return false;
            }
            System.Type enumType = _curSeq.GetType().GetElementType();
            _mSyncNext.Invoke(_process, new object[] { Enum.ToObject(enumType, subSeq) });
            return true;
        }

        // ── 核心: 跳到某曲某难度 ──────────────────────────────────────
        internal static string Jump(int musicId, int difficulty)
        {
            if (!InSelect)
            {
                return "当前不在选曲界面(先回到选曲画面再点歌)";
            }
            string blocked = BlockReason();
            if (blocked != null)
            {
                return blocked;
            }
            var list = _process.CombineMusicDataList;
            int oldCat = _process.CurrentCategorySelect;
            int oldIdx = _process.CurrentMusicSelect;
            bool onlySpecial = false;
            for (int cat = 0; cat < list.Count; cat++)
            {
                int idx = FindInCategory(list[cat], musicId);
                if (idx < 0)
                {
                    continue;
                }
                // 同一首歌会出现在好几个分类里; 对战(ghost)/挑战/段位/联机/推荐 这类特殊资料夹会触发特殊玩法,
                // 先临时选中看看是不是特殊资料夹, 是就换下一个分类(宴会场的歌只在宴会场资料夹里, 允许)
                _process.CurrentCategorySelect = cat;
                _process.CurrentMusicSelect = idx;
                if (IsSpecialHere())
                {
                    onlySpecial = true;
                    continue;
                }
                var hit = list[cat][idx];
                int realId = hit.msDetailData.musicId;

                // 已经在难度画面: 先按游戏自己"返回"的做法退回选曲列表, 再换曲子重新进难度画面。
                // 以前直接在难度画面上再切一次难度画面, before 会被记成难度画面自己, 之后按返回就回不去了。
                if (AnyInDifficulty())
                {
                    for (int p = 0; p < _process.DifficultySelectIndex.Length; p++)
                    {
                        _process.DifficultySelectIndex[p] = -1;
                    }
                    SyncNext(SubSeqMusic);
                }

                // 再设一次: 上面从难度画面退回选曲列表时, 游戏的 OnStartSequence 可能动过光标
                _process.CurrentCategorySelect = cat;
                _process.CurrentMusicSelect = idx;
                _process.ScoreType = (ConstParameter.ScoreKind)(realId >= 10000 ? 1 : 0);
                _process.ChangeBGM();

                // 先让游戏按新曲目重算一遍监视器数据 —— CalcMonitorDifficulty 内部会按曲子默认值
                // 覆写 DifficultySelectIndex, 所以必须放在"设难度"之前, 否则难度会被顶掉。
                try
                {
                    _process.CalcMonitorDifficulty(true);
                }
                catch (Exception e)
                {
                    ModLog.Info("[SongRequest] CalcMonitorDifficulty 失败(不影响跳转): " + e.Message);
                }

                // 再写难度(UI 每帧读 GetCurrentDifficulty -> 会把难度条刷到我们指定的那个)
                int applied = ApplyDifficulty(realId, difficulty);

                if (Config.JumpToDifficultyScreen && _mSyncNext != null)
                {
                    // 跟游戏自己确认选曲时一样: 收起曲目卡/分类页签、换按钮图、锁输入, 下一帧切到难度画面
                    Monitor.MusicSelectMonitor first = null;
                    for (int p = 0; p < _process.MonitorArray.Length; p++)
                    {
                        Monitor.MusicSelectMonitor mon = _process.MonitorArray[p];
                        if (mon == null || !IsEntry(p))
                        {
                            continue;
                        }
                        if (first == null)
                        {
                            first = mon;
                        }
                        try
                        {
                            mon.ChangeButtonImage(InputManager.ButtonSetting.Button04, 14);
                        }
                        catch
                        {
                        }
                        mon.SetScrollMusicCard(false);
                        mon.OutGenreTab();
                        _process.SetInputLockInfo(p, InputLockMs);
                    }
                    if (first != null)
                    {
                        _switchPending = true;
                        _switchPendingAt = Environment.TickCount;
                        first.StartCoroutine(NextFrame());
                    }
                }

                string name = SongTable.NameOf(realId);
                string dname = SongTable.DifficultyName(applied);
                ModLog.Info("[SongRequest] 点歌: " + realId + " " + name + " / " + dname
                    + " (分类 " + cat + " 第 " + idx + " 首)");
                if (_lastClampedFrom >= 0)
                {
                    return "已跳转: " + name + " [" + dname + "] (该曲 "
                        + SongTable.DifficultyName(_lastClampedFrom) + " 这个版本不能玩, 游戏自己退到 "
                        + dname + ")";
                }
                return "已跳转: " + name + " [" + dname + "]";
            }
            // 没点成: 光标放回原处
            _process.CurrentCategorySelect = oldCat;
            _process.CurrentMusicSelect = oldIdx;
            if (onlySpecial)
            {
                return "这首歌只在特殊分类(对战/挑战/段位/联机等)里, 为免触发特殊玩法不自动跳转, 请在游戏里手动选";
            }
            return "找不到这首歌: " + musicId + " —— 它可能刚热导入还没进选曲列表(回一次标题再进选曲界面即可), 或者被游戏过滤/未开放";
        }

        /// <summary>在一个分类里找这首歌(DX 找不到就试同曲的标准 id), 跳过空格子和"随机"格; 找不到返回 -1</summary>
        private static int FindInCategory(ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData> page, int musicId)
        {
            if (page == null)
            {
                return -1;
            }
            int alt = musicId >= 10000 ? musicId % 10000 : -1;
            int altIdx = -1;
            for (int i = 0; i < page.Count; i++)
            {
                var c = page[i];
                if (c == null || c.msDetailData == null || c.isRandomScore)
                {
                    continue;
                }
                int id = c.msDetailData.musicId;
                if (id == musicId)
                {
                    return i;
                }
                if (id == alt && altIdx < 0)
                {
                    altIdx = i;
                }
            }
            return altIdx;
        }

        /// <summary>当前选中的格子在不在特殊资料夹(只读分类名, 不改游戏状态)</summary>
        private static bool IsSpecialHere()
        {
            try
            {
                if (_process.IsUtageMusicFolder())
                {
                    return false;
                }
                return _process.IsGhostFolder(0) || _process.IsConnectionFolder() || _process.IsChallengeFolder()
                    || _process.IsTournamentFolder() || _process.IsExtraFolder(0);
            }
            catch
            {
                return false;
            }
        }

        internal static string JumpRandom(int difficulty)
        {
            if (!InSelect)
            {
                return "当前不在选曲界面(先回到选曲画面再点歌)";
            }
            try
            {
                List<int> pool = SongTable.RandomPool(difficulty);
                if (pool.Count == 0)
                {
                    return "没有可随机的曲目";
                }
                int id = pool[UnityEngine.Random.Range(0, pool.Count)];
                return Jump(id, difficulty);
            }
            catch (Exception e)
            {
                return "随机失败: " + e.Message;
            }
        }

        /// <summary>
        /// 写难度: 该曲没有这个难度就退到最高可用难度。
        /// CurrentDifficulty 是 UI 当前显示的难度, DifficultySelectIndex 是难度条上的位置, 两个都写。
        /// </summary>
        private static int ApplyDifficulty(int musicId, int difficulty)
        {
            // 能不能选以游戏自己的判断为准(选曲数据 isExistsScore), 拿不到才看 XML 的 isEnable。
            // 不可选就往下退(Re:MASTER -> MASTER -> EXPERT…), 退到哪档会写进返回消息,
            // 免得再出现"点了 14+ 结果跳到 14"却不知道为什么。
            int want = difficulty;
            int d = -1;
            if (want >= 0 && want <= 4 && Playable(musicId, want))
            {
                d = want;
            }
            if (d < 0)
            {
                for (int i = (want >= 0 && want <= 4) ? want : 4; i >= 0; i--)
                {
                    if (Playable(musicId, i))
                    {
                        d = i;
                        break;
                    }
                }
            }
            if (d < 0)
            {
                d = SongTable.HighestEnabled(musicId);
            }
            if (d < 0)
            {
                d = 3;
            }
            _lastClampedFrom = (want >= 0 && want <= 4 && d != want) ? want : -1;
            for (int p = 0; p < _process.MonitorArray.Length; p++)
            {
                if (_process.CurrentDifficulty != null && p < _process.CurrentDifficulty.Length)
                {
                    _process.CurrentDifficulty[p] = (MusicDifficultyID)d;
                }
                if (_process.DifficultySelectIndex != null && p < _process.DifficultySelectIndex.Length)
                {
                    _process.DifficultySelectIndex[p] = d;
                }
            }
            GameManager.SelectDifficultyID[0] = d;
            if (GameManager.SelectDifficultyID.Length > 1)
            {
                GameManager.SelectDifficultyID[1] = d;
            }
            return d;
        }

        private static int _lastClampedFrom = -1;

        /// <summary>这档谱能不能玩: 游戏说不能就是不能, 游戏还不知道才看 XML 的声明</summary>
        private static bool Playable(int musicId, int diff)
        {
            bool? g = GamePlayable(musicId, diff);
            return g.HasValue ? g.Value : SongTable.DifficultyEnabled(musicId, diff);
        }

        private static bool IsEntry(int playerIndex)
        {
            try
            {
                var ud = Singleton<UserDataManager>.Instance.GetUserData((long)playerIndex);
                return ud != null && ud.IsEntry;
            }
            catch
            {
                return playerIndex == 0;
            }
        }

        /// <summary>下一帧再切画面: 跟游戏自己换子序列的做法一致, 免得跟当帧的 Update 打架</summary>
        private static IEnumerator NextFrame()
        {
            yield return null;
            try
            {
                if (_process != null && _process.CombineMusicDataList != null && BlockReason() == null)
                {
                    bool utage = false;
                    try
                    {
                        utage = _process.IsUtageMusicFolder();
                    }
                    catch
                    {
                    }
                    SyncNext(utage ? SubSeqUtageDifficulty : SubSeqDifficulty);
                    ModLog.Info("[SongRequest] 已切到" + (utage ? "宴会场" : "") + "难度选择画面");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 切难度画面失败: " + e.Message);
            }
            finally
            {
                _switchPending = false;
            }
        }
    }
}
