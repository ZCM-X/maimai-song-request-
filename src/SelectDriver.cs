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
        /// <summary>难度选择子序列的下标(Music=0, Genre=1, SortSetting=2, Difficulty=3)</summary>
        private const int SubSeqDifficulty = 3;

        private static MusicSelectProcess _process;
        private static Array _subSeqArray;    // SequenceBase[player][]
        private static Array _curSeq;         // SubSequence[player]
        private static Array _prevSeq;        // SubSequence[player]
        private static FieldInfo _fSubSeqArray;
        private static FieldInfo _fCurSeq;
        private static FieldInfo _fPrevSeq;

        /// <summary>网页线程塞进来、主线程执行的请求</summary>
        private sealed class Request
        {
            public int MusicId;
            public int Difficulty;   // -1 = 自动(最高可用)
            public bool Random;
            public int RandomDiff = -1;
            public string Result;
            public bool Done;
            public readonly object Lock = new object();
        }

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
            sb.Append('}');
            return sb.ToString();
        }

        internal static void Capture(MusicSelectProcess process)
        {
            _process = process;
            try
            {
                if (_fSubSeqArray == null)
                {
                    _fSubSeqArray = AccessTools.Field(typeof(MusicSelectProcess), "_subSequenceArray");
                    _fCurSeq = AccessTools.Field(typeof(MusicSelectProcess), "_currentPlayerSubSequence");
                    _fPrevSeq = AccessTools.Field(typeof(MusicSelectProcess), "_beforePlayerSubSequence");
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
            ModLog.Info("[SongRequest] MusicSelectProcess 已释放");
        }

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

        private static float _findCooldown;
        private static object _lastMonitor;

        /// <summary>
        /// 兜底: 万一时序上没接到 OnStart(或以后游戏改了方法名), 就从场景里活的
        /// MusicSelectMonitor 反查它的 MusicSelectProcess 字段把实例捞回来。
        /// </summary>
        private static void TryFindProcess()
        {
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
                int scoreType = (int)_process.ScoreType;
                for (int cat = 0; cat < list.Count; cat++)
                {
                    var page = list[cat];
                    for (int i = 0; i < page.Count; i++)
                    {
                        var c = page[i];
                        if (c == null || c.msDetailData == null || c.musicSelectData == null)
                        {
                            continue;
                        }
                        int id = c.msDetailData.musicId;
                        if (id <= 0 || map.ContainsKey(id))
                        {
                            continue;
                        }
                        if (scoreType < 0 || scoreType >= c.musicSelectData.Count)
                        {
                            continue;
                        }
                        object msd = c.musicSelectData[scoreType];
                        if (msd == null)
                        {
                            continue;
                        }
                        if (_fExistsScore == null)
                        {
                            _fExistsScore = msd.GetType().GetField("isExistsScore",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        }
                        if (_fExistsScore == null)
                        {
                            continue;
                        }
                        bool[] arr = _fExistsScore.GetValue(msd) as bool[];
                        if (arr != null)
                        {
                            map[id] = arr;
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

        private static FieldInfo _fExistsScore;
        private static Dictionary<int, bool[]> _playable;
        private static int _playableRev = -1;

        /// <summary>该曲该难度游戏是否认为可玩(null=还不知道, 那就按 XML 的来)</summary>
        internal static bool? GamePlayable(int musicId, int diff)
        {
            try
            {
                if (_playable == null || _playableRev != SongTable.Rev)
                {
                    _playable = PlayableMap();
                    _playableRev = SongTable.Rev;
                }
                if (_playable == null || _playable.Count == 0)
                {
                    return null;
                }
                bool[] arr;
                if (!_playable.TryGetValue(musicId, out arr) || arr == null)
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
                if (_queue.Count > 8)
                {
                    return "点歌请求太快了, 缓一下";
                }
                _queue.Add(r);
            }
            if (!r.Done)
            {
                // 最多等 4 秒(主线程每帧都会来取)
                WaitResult(r, 4000);
            }
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
            WaitResult(r, 4000);
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
            if (batch == null)
            {
                return;
            }
            foreach (Request r in batch)
            {
                string msg;
                try
                {
                    if (r.Random)
                    {
                        msg = JumpRandom(r.RandomDiff);
                    }
                    else
                    {
                        msg = Jump(r.MusicId, r.Difficulty);
                    }
                }
                catch (Exception e)
                {
                    msg = "点歌失败: " + e.Message;
                    MelonLogger.Warning("[SongRequest] 点歌异常: " + e);
                }
                lock (r.Lock)
                {
                    r.Result = msg;
                    r.Done = true;
                    System.Threading.Monitor.PulseAll(r.Lock);
                }
            }
        }

        // ── 核心: 跳到某曲某难度 ──────────────────────────────────────
        internal static string Jump(int musicId, int difficulty)
        {
            if (!InSelect)
            {
                return "当前不在选曲界面(先回到选曲画面再点歌)";
            }
            var list = _process.CombineMusicDataList;
            for (int cat = 0; cat < list.Count; cat++)
            {
                MusicSelectProcess.CombineMusicSelectData hit = null;
                for (int i = 0; i < list[cat].Count; i++)
                {
                    if (list[cat][i].msDetailData.musicId == musicId)
                    {
                        hit = list[cat][i];
                        break;
                    }
                }
                if (hit == null && musicId >= 10000)
                {
                    int alt = musicId % 10000;
                    for (int i = 0; i < list[cat].Count; i++)
                    {
                        if (list[cat][i].msDetailData.musicId == alt)
                        {
                            hit = list[cat][i];
                            break;
                        }
                    }
                }
                if (hit == null)
                {
                    continue;
                }
                var swJump = System.Diagnostics.Stopwatch.StartNew();
                int idx = list[cat].IndexOf(hit);
                int realId = hit.msDetailData.musicId;

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

                for (int p = 0; p < _process.MonitorArray.Length; p++)
                {
                    Monitor.MusicSelectMonitor mon = _process.MonitorArray[p];
                    if (mon == null || !IsEntry(p))
                    {
                        continue;
                    }
                    mon.SetScrollMusicCard(false);
                    mon.SetScrollGenreCard(false);
                    mon.OutGenreTab();
                    if (Config.JumpToDifficultyScreen && _subSeqArray != null)
                    {
                        mon.StartCoroutine(NextFrame(p));
                    }
                }

                swJump.Stop();
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
            return "找不到这首歌: " + musicId + " —— 它可能刚热导入还没进选曲列表(回一次标题再进选曲界面即可), 或者被游戏过滤/未开放";
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
        private static IEnumerator NextFrame(int playerIndex)
        {
            yield return null;
            try
            {
                if (_subSeqArray == null || _curSeq == null || _prevSeq == null)
                {
                    yield break;
                }
                Array inner = (Array)_subSeqArray.GetValue(playerIndex);
                if (inner == null)
                {
                    yield break;
                }
                int cur = Convert.ToInt32(_curSeq.GetValue(playerIndex));
                object curSeqObj = inner.GetValue(cur);
                Invoke(curSeqObj, "Reset");
                _prevSeq.SetValue(_curSeq.GetValue(playerIndex), playerIndex);
                _curSeq.SetValue(Enum.ToObject(_curSeq.GetType().GetElementType(), SubSeqDifficulty), playerIndex);
                object diffSeqObj = inner.GetValue(SubSeqDifficulty);
                Invoke(diffSeqObj, "OnStartSequence");
                ModLog.Info("[SongRequest] 已切到难度选择画面 (player " + playerIndex + ")");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 切难度画面失败: " + e.Message);
            }
        }

        private static void Invoke(object target, string method)
        {
            if (target == null)
            {
                return;
            }
            var m = target.GetType().GetMethod(method,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null)
            {
                MelonLogger.Warning("[SongRequest] 找不到方法 " + target.GetType().Name + "." + method);
                return;
            }
            m.Invoke(target, null);
        }
    }
}
