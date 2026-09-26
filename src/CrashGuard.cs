using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SongRequestMod
{
    /// <summary>
    /// 游戏自身崩溃口子的兜底(纯内存补丁, 游戏文件一个不动)。
    ///
    /// 1.79/1.70 的 PlInformationProcess.RestoreGhost 里有这段:
    ///     var boss = GetUdemaeBoss(npcParamBoss);      // 客户端表里没有这个 id 时返回 null
    ///     int id  = boss.music.id;                     // ← 直接解引用 -> NullReferenceException -> 整个进程闪退
    ///   而 A000\udemaeBoss 里 id=20/21/22(真皆伝)引用的 music 12024 在这个客户端里缺失,
    ///   配合私服给的 rating/段位就容易踩到。这里给 GetUdemaeBoss 补一个 postfix:
    ///   拿不到就退到最近的存在的 BOSS, 让游戏继续跑。
    ///
    /// 2. 再给 RestoreGhost 挂一个 finalizer 兜底: 万一还有别的 null(比如
    ///    uid.RatingList.Udemae 为空), 把异常吞掉并记一次日志, 而不是让进程崩。
    /// </summary>
    internal static class CrashGuard
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _loggedBoss;
        private static bool _loggedGhost;
        private static bool _loggedAny;
        private static bool _loggedGenre;
        private static readonly HashSet<string> _swallowed = new HashSet<string>();

        [ThreadStatic]
        private static bool _inBossGuard;

        [ThreadStatic]
        private static bool _inGenreGuard;

        internal static void Install()
        {
            try
            {
                _harmony = new HarmonyLib.Harmony("zcmx.songrequest.crashguard");
                int n = 0;

                MethodInfo boss = AccessTools.Method(typeof(Manager.DataManager), "GetUdemaeBoss",
                    new Type[] { typeof(int) });
                if (boss != null)
                {
                    _harmony.Patch(boss, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CrashGuard), "GetUdemaeBoss_Postfix")));
                    n++;
                }

                MethodInfo ghost = AccessTools.Method(typeof(Process.PlInformationProcess), "RestoreGhost",
                    new Type[] { typeof(int) });
                if (ghost != null)
                {
                    _harmony.Patch(ghost, finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(CrashGuard), "RestoreGhost_Finalizer")));
                    n++;
                }

                // 自制谱 Music.xml 里的 genreName.id 如果填了客户端不存在的分类(比如转换器默认的 100),
                // DataManager.GetMusicGenre 会返回 null, 然后 MusicSelectProcess.CategoryTabGenre 里
                // 一句 .name 就是 NRE -> 选曲界面直接闪退。这里给它兜一个合法分类。
                MethodInfo genre = AccessTools.Method(typeof(Manager.DataManager), "GetMusicGenre",
                    new Type[] { typeof(int) });
                if (genre != null)
                {
                    _harmony.Patch(genre, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CrashGuard), "GetMusicGenre_Postfix")));
                    n++;
                }

                // 再给选曲界面的分类构建挂 finalizer 兜底
                n += AddFinalizer(typeof(Process.MusicSelectProcess), "CategoryTabGenre");
                n += AddFinalizer(typeof(Process.MusicSelectProcess), "CategoryTabSort");

                if (n > 0)
                {
                    ModLog.Info("崩溃保护已装(" + n + " 处)");
                }
                else
                {
                    ModLog.WarnOnce("崩溃保护没找到目标方法, 跳过");
                }
            }
            catch (Exception e)
            {
                ModLog.WarnOnce("装崩溃保护失败: " + e.Message);
            }
        }

        /// <summary>段位 BOSS id 缺失时退到最近的存在项, 别把 null 交给游戏</summary>
        private static void GetUdemaeBoss_Postfix(int id, ref Manager.MaiStudio.UdemaeBossData __result)
        {
            if (__result != null || _inBossGuard)
            {
                return;
            }
            _inBossGuard = true;
            try
            {
                var dm = MAI2.Util.Singleton<Manager.DataManager>.Instance;
                if (dm == null)
                {
                    return;
                }
                // 往下找最近的(常见: id 比表里最大还大)
                for (int k = 1; k <= 64 && __result == null; k++)
                {
                    if (id - k < 0)
                    {
                        break;
                    }
                    __result = dm.GetUdemaeBoss(id - k);
                }
                // 再往上找(常见: id 比表里最小还小)
                for (int k = 1; k <= 64 && __result == null; k++)
                {
                    __result = dm.GetUdemaeBoss(id + k);
                }
                if (__result != null)
                {
                    if (!_loggedBoss)
                    {
                        _loggedBoss = true;
                        ModLog.WarnOnce("段位 BOSS id=" + id
                            + " 在客户端表里不存在, 已退到最近的 BOSS(游戏数据缺曲子/私服段位越界, 不是 mod 的锅)");
                    }
                }
            }
            catch (Exception e)
            {
                if (!_loggedAny)
                {
                    _loggedAny = true;
                    ModLog.WarnOnce("兜段位 BOSS 时出错: " + e.Message);
                }
            }
            finally
            {
                _inBossGuard = false;
            }
        }

        /// <summary>兜底: RestoreGhost 里再出任何异常都吞掉, 不升级成闪退</summary>
        private static Exception RestoreGhost_Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }
            if (!_loggedGhost)
            {
                _loggedGhost = true;
                ModLog.WarnOnce("吞掉游戏 RestoreGhost 的异常(防止闪退): "
                    + __exception.GetType().Name + ": " + __exception.Message);
            }
            return null;
        }

        /// <summary>
        /// 分类 id 在客户端里不存在时(自制谱写错 genreName 就是这种情况), 兜一个合法分类,
        /// 不然 MusicSelectProcess.CategoryTabGenre 拿 null 直接 NRE 闪退。
        /// </summary>
        private static void GetMusicGenre_Postfix(int id, ref Manager.MaiStudio.MusicGenreData __result)
        {
            if (__result != null || _inGenreGuard)
            {
                return;
            }
            _inGenreGuard = true;
            try
            {
                var dm = MAI2.Util.Singleton<Manager.DataManager>.Instance;
                if (dm == null)
                {
                    return;
                }
                var all = dm.GetMusicGenres();
                if (all == null || all.Count == 0)
                {
                    return;
                }
                int used = -1;
                // 优先 104(ゲーム&バラエティ) -> 101(poppop) -> 其它 1xx -> 最后一个
                if (all.ContainsKey(104))
                {
                    __result = all[104];
                    used = 104;
                }
                if (__result == null)
                {
                    foreach (var kv in all)
                    {
                        if (kv.Key >= 101 && kv.Key < 200)
                        {
                            __result = kv.Value;
                            used = kv.Key;
                            break;
                        }
                    }
                }
                if (__result == null)
                {
                    foreach (var kv in all)
                    {
                        __result = kv.Value;
                        used = kv.Key;
                    }
                }
                if (__result != null && !_loggedGenre)
                {
                    _loggedGenre = true;
                    ModLog.WarnOnce("有曲子用了客户端不存在的分类 id=" + id
                        + ", 已顶成 " + used + "(检查自制谱 Music.xml 里的 genreName, 合法值是 101~107)");
                }
            }
            catch
            {
            }
            finally
            {
                _inGenreGuard = false;
            }
        }

        private static int AddFinalizer(Type t, string name)
        {
            try
            {
                MethodInfo mi = AccessTools.Method(t, name);
                if (mi == null)
                {
                    return 0;
                }
                _harmony.Patch(mi, finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(CrashGuard), "Swallow_Finalizer")));
                return 1;
            }
            catch (Exception e)
            {
                ModLog.WarnOnce("给 " + t.Name + "." + name + " 挂保险失败: " + e.Message);
                return 0;
            }
        }

        private static Exception Swallow_Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null)
            {
                return null;
            }
            string key = (__originalMethod == null) ? "?" : __originalMethod.Name;
            if (_swallowed.Add(key))
            {
                ModLog.WarnOnce("吞掉 " + key + " 的异常(防止闪退): "
                    + __exception.GetType().Name + ": " + __exception.Message);
            }
            return null;
        }
    }
}
