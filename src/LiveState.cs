using MelonLoader;
using System;
using System.Text;
using Manager;
using MAI2.Util;
using UnityEngine;

namespace SongRequestMod
{
    /// <summary>
    /// 网页右侧「当前游玩」面板的数据源(等价于单独装一个 ComboWeb)。
    /// 游玩中读游戏自己的成绩对象: GamePlayManager.GetGameScore(玩家) -> GameScoreList,
    ///   SessionInfo.musicId / SessionInfo.difficulty 是当前曲目与难度,
    ///   Combo / MaxCombo / CriticalNum…MissNum / GetAchivement() / Life 就是判定与分数。
    /// 选曲界面读 MusicSelectProcess 的光标位置。
    /// </summary>
    internal static class LiveState
    {
        private static volatile string _json = "{\"state\":\"idle\"}";

        /// <summary>当前状态(给 /api/status 用)</summary>
        internal static volatile string State = "idle";

        internal static string Json()
        {
            return _json;
        }

        internal static void Refresh()
        {
            try
            {
                _json = Build();
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 状态快照失败: " + e.Message);
            }
        }

        private static bool _monitorFound;
        private static float _monitorCheckAt = -1f;

        /// <summary>
        /// 场景里有没有游玩画面的 GameMonitor。FindObjectOfType 要扫全场景, 很贵 ——
        /// 以前每 50ms(一秒 20 次)扫一遍, 一直在吃主线程; 现在一秒最多扫一次, 中间用上次的结果。
        /// </summary>
        private static bool FindGameMonitor()
        {
            float now = Time.unscaledTime;
            if (_monitorCheckAt >= 0f && now - _monitorCheckAt < 1f)
            {
                return _monitorFound;
            }
            _monitorCheckAt = now;
            try
            {
                _monitorFound = UnityEngine.Object.FindObjectOfType<Monitor.GameMonitor>() != null;
            }
            catch
            {
                _monitorFound = false;
            }
            return _monitorFound;
        }

        /// <summary>游戏自己的"是否在游玩中"标志(ComboWeb 也是用这个), 场景里没有 GameMonitor 时兜一下</summary>
        private static bool IsInGame()
        {
            try
            {
                if (GameManager.IsInGame)
                {
                    return true;
                }
            }
            catch
            {
            }
            return FindGameMonitor();
        }

        private static string Build()
        {
            bool playing = IsInGame();
            bool inSelect = SelectDriver.InSelect;

            StringBuilder sb = new StringBuilder(400);
            sb.Append('{');

            if (playing)
            {
                Manager.GameScoreList gs = null;
                Manager.GamePlayManager gpm = null;
                try
                {
                    gpm = Singleton<GamePlayManager>.Instance;
                    if (gpm != null)
                    {
                        gs = gpm.GetGameScore(0);
                    }
                }
                catch
                {
                }
                if (gs != null && gs.IsEnable)
                {
                    int id = gs.SessionInfo.musicId;
                    int diff = gs.SessionInfo.difficulty;
                    string name = SongTable.NameOf(id);
                    var md = SongTable.GetMusic(id);

                    // Combo 直接用 GamePlayManager 的(ComboWeb 同源), 拿不到再退回成绩对象
                    uint combo = 0;
                    uint maxCombo = 0;
                    try
                    {
                        if (gpm != null)
                        {
                            combo = gpm.GetCombo(0);
                            maxCombo = gpm.GetMaxCombo(0);
                        }
                    }
                    catch
                    {
                    }
                    if (combo == 0 && maxCombo == 0)
                    {
                        combo = gs.Combo;
                        maxCombo = gs.MaxCombo;
                    }

                    State = "playing";
                    sb.Append("\"state\":\"playing\"");
                    sb.Append(",\"id\":").Append(id);
                    sb.Append(",\"name\":");
                    Q(sb, name);
                    sb.Append(",\"artist\":");
                    Q(sb, md != null && md.artistName != null ? md.artistName.str : "");
                    sb.Append(",\"difficulty\":\"").Append(SongTable.DifficultyName(diff)).Append('"');
                    sb.Append(",\"type\":\"").Append(diff >= 0 ? SongTable.TypeLabel(id) : "").Append('"');
                    sb.Append(",\"diffType\":").Append(diff);
                    sb.Append(",\"levelStr\":");
                    Q(sb, LevelOf(id, diff));
                    sb.Append(",\"combo\":").Append(combo);
                    sb.Append(",\"maxCombo\":").Append(maxCombo);
                    sb.Append(",\"score\":").Append(Score(gs));
                    sb.Append(",\"life\":").Append(gs.Life);
                    sb.Append(",\"startLife\":").Append(gs.StartLife);
                    sb.Append(",\"notes\":").Append(gs.TheoryCombo);
                    sb.Append(",\"trackNo\":").Append(gs.TrackNo);
                    sb.Append(",\"player\":");
                    Q(sb, gs.UserName);
                    sb.Append(",\"judge\":{");
                    sb.Append("\"criticalPerfect\":").Append(gs.CriticalNum);
                    sb.Append(",\"trueCritical\":").Append(gs.TrueCriticalNum);
                    sb.Append(",\"perfect\":").Append(gs.PerfectNum);
                    sb.Append(",\"great\":").Append(gs.GreatNum);
                    sb.Append(",\"good\":").Append(gs.GoodNum);
                    sb.Append(",\"miss\":").Append(gs.MissNum);
                    sb.Append('}');
                    sb.Append('}');
                    return sb.ToString();
                }
                // 在游玩场景但成绩对象还没就绪(TRACK 起幕 / 曲间加载 / 结算) ->
                // 用游戏自己的"已选曲目"兜底, 免得面板在这个界面空白
                State = "playing";
                int selId = -1, selDiff = -1;
                try
                {
                    if (Manager.GameManager.SelectMusicID != null && Manager.GameManager.SelectMusicID.Length > 0)
                    {
                        selId = Manager.GameManager.SelectMusicID[0];
                    }
                    if (Manager.GameManager.SelectDifficultyID != null && Manager.GameManager.SelectDifficultyID.Length > 0)
                    {
                        selDiff = Manager.GameManager.SelectDifficultyID[0];
                    }
                }
                catch
                {
                }
                if (selId <= 0)
                {
                    try { selId = SelectDriver.CursorMusicId; selDiff = SelectDriver.CursorDifficulty; } catch { }
                }
                sb.Append("\"state\":\"playing\"");

                if (selId > 0)
                {
                    sb.Append(",\"id\":").Append(selId);
                    sb.Append(",\"name\":");
                    Q(sb, SongTable.NameOf(selId));
                    var mdP = SongTable.GetMusic(selId);
                    sb.Append(",\"artist\":");
                    Q(sb, mdP != null && mdP.artistName != null ? mdP.artistName.str : "");
                    sb.Append(",\"difficulty\":\"").Append(SongTable.DifficultyName(selDiff)).Append('"');
                    sb.Append(",\"diffType\":").Append(selDiff);
                    sb.Append(",\"type\":\"").Append(SongTable.TypeLabel(selId)).Append('"');
                    sb.Append(",\"levelStr\":");
                    Q(sb, LevelOf(selId, selDiff));
                }
                sb.Append('}');
                return sb.ToString();
            }

            if (inSelect)
            {
                int cursor = SelectDriver.CursorMusicId;
                int cdiff = SelectDriver.CursorDifficulty;
                State = "select";
                sb.Append("\"state\":\"select\"");
                if (cursor > 0)
                {
                    sb.Append(",\"id\":").Append(cursor);
                    sb.Append(",\"name\":");
                    Q(sb, SongTable.NameOf(cursor));
                    var md = SongTable.GetMusic(cursor);
                    sb.Append(",\"artist\":");
                    Q(sb, md != null && md.artistName != null ? md.artistName.str : "");
                    sb.Append(",\"difficulty\":\"").Append(SongTable.DifficultyName(cdiff)).Append('"');
                    sb.Append(",\"diffType\":").Append(cdiff);
                    sb.Append(",\"type\":\"").Append(SongTable.TypeLabel(cursor)).Append('"');
                }
                sb.Append('}');
                return sb.ToString();
            }

            // 选曲 -> 游玩之间还有一段过场(TRACK 起幕/曲间加载): 既不在选曲界面, 也还没进游玩场景,
            // 但游戏已经记下"选了哪一首" -> 这时面板不该变休眠, 按当前曲目显示
            int preId = -1, preDiff = -1;
            try
            {
                if (Manager.GameManager.SelectMusicID != null && Manager.GameManager.SelectMusicID.Length > 0)
                {
                    preId = Manager.GameManager.SelectMusicID[0];
                }
                if (Manager.GameManager.SelectDifficultyID != null && Manager.GameManager.SelectDifficultyID.Length > 0)
                {
                    preDiff = Manager.GameManager.SelectDifficultyID[0];
                }
            }
            catch
            {
            }
            if (preId > 0)
            {
                State = "playing";
                sb.Append("\"state\":\"playing\"");
                sb.Append(",\"id\":").Append(preId);
                sb.Append(",\"name\":");
                Q(sb, SongTable.NameOf(preId));
                var mdPre = SongTable.GetMusic(preId);
                sb.Append(",\"artist\":");
                Q(sb, mdPre != null && mdPre.artistName != null ? mdPre.artistName.str : "");
                sb.Append(",\"difficulty\":\"").Append(SongTable.DifficultyName(preDiff)).Append('"');
                sb.Append(",\"diffType\":").Append(preDiff);
                sb.Append(",\"type\":\"").Append(SongTable.TypeLabel(preId)).Append('"');
                sb.Append(",\"levelStr\":");
                Q(sb, LevelOf(preId, preDiff));
                sb.Append('}');
                return sb.ToString();
            }

            State = "idle";
            sb.Append("\"state\":\"idle\"");
            sb.Append('}');
            return sb.ToString();
        }

        private static string LevelOf(int id, int diff)
        {
            return SongTable.LevelOf(id, diff);
        }

        private static string Score(Manager.GameScoreList gs)
        {
            try
            {
                decimal a = gs.GetAchivement();
                return ((double)a).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return "0";
            }
        }

        private static void Q(StringBuilder sb, string s)
        {
            sb.Append('"').Append(SongTable.Escape(s)).Append('"');
        }
    }
}
