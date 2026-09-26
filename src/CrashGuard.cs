using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MAI2.Util;
using Manager;
using Manager.MaiStudio;
using MelonLoader;

namespace SongRequestMod
{
    /// <summary>
    /// 崩溃保护(移植自原作者 v1.0.3): 游戏数据缺曲子 / 私服段位越界时, 游戏自己的几处代码会 NullReference 闪退,
    /// 例如登录时 PlInformationProcess.RestoreGhost 找不到段位 BOSS 的曲子。
    ///   - DataManager.GetUdemaeBoss 拿不到 -> 退到最近的 BOSS
    ///   - DataManager.GetMusicGenre 拿不到 -> 顶成一个存在的分类(自制谱 genreName 写错时)
    ///   - RestoreGhost / 选曲分类页签的异常直接吞掉, 不让游戏闪退
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
                MethodInfo boss = AccessTools.Method(typeof(DataManager), "GetUdemaeBoss", new System.Type[] { typeof(int) });
                if (boss != null)
                {
                    _harmony.Patch(boss, null, new HarmonyMethod(AccessTools.Method(typeof(CrashGuard), "GetUdemaeBoss_Postfix")));
                    n++;
                }
                MethodInfo ghost = AccessTools.Method(typeof(Process.PlInformationProcess), "RestoreGhost", new System.Type[] { typeof(int) });
                if (ghost != null)
                {
                    _harmony.Patch(ghost, finalizer: new HarmonyMethod(AccessTools.Method(typeof(CrashGuard), "RestoreGhost_Finalizer")));
                    n++;
                }
                MethodInfo genre = AccessTools.Method(typeof(DataManager), "GetMusicGenre", new System.Type[] { typeof(int) });
                if (genre != null)
                {
                    _harmony.Patch(genre, null, new HarmonyMethod(AccessTools.Method(typeof(CrashGuard), "GetMusicGenre_Postfix")));
                    n++;
                }
                n += AddFinalizer(typeof(Process.MusicSelectProcess), "CategoryTabGenre");
                n += AddFinalizer(typeof(Process.MusicSelectProcess), "CategoryTabSort");
                if (n > 0)
                {
                    ModLog.Info("[SongRequest] 崩溃保护已装(" + n + " 处)");
                }
                else
                {
                    MelonLogger.Warning("[SongRequest] 崩溃保护没找到目标方法, 跳过");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 装崩溃保护失败: " + e.Message);
            }
        }

        private static void GetUdemaeBoss_Postfix(int id, ref UdemaeBossData __result)
        {
            if (__result != null || _inBossGuard)
            {
                return;
            }
            _inBossGuard = true;
            try
            {
                DataManager dm = Singleton<DataManager>.Instance;
                if (dm == null)
                {
                    return;
                }
                for (int i = 1; i <= 64 && __result == null && id - i >= 0; i++)
                {
                    __result = dm.GetUdemaeBoss(id - i);
                }
                for (int i = 1; i <= 64 && __result == null; i++)
                {
                    __result = dm.GetUdemaeBoss(id + i);
                }
                if (__result != null && !_loggedBoss)
                {
                    _loggedBoss = true;
                    MelonLogger.Warning("[SongRequest] 段位 BOSS id=" + id + " 在客户端表里不存在, 已退到最近的 BOSS(游戏数据缺曲子/私服段位越界)");
                }
            }
            catch (Exception e)
            {
                if (!_loggedAny)
                {
                    _loggedAny = true;
                    MelonLogger.Warning("[SongRequest] 兜段位 BOSS 时出错: " + e.Message);
                }
            }
            finally
            {
                _inBossGuard = false;
            }
        }

        private static Exception RestoreGhost_Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }
            if (!_loggedGhost)
            {
                _loggedGhost = true;
                MelonLogger.Warning("[SongRequest] 吞掉游戏 RestoreGhost 的异常(防止闪退): "
                    + __exception.GetType().Name + ": " + __exception.Message);
            }
            return null;
        }

        private static void GetMusicGenre_Postfix(int id, ref MusicGenreData __result)
        {
            if (__result != null || _inGenreGuard)
            {
                return;
            }
            _inGenreGuard = true;
            try
            {
                DataManager dm = Singleton<DataManager>.Instance;
                if (dm == null)
                {
                    return;
                }
                var genres = dm.GetMusicGenres();
                if (genres == null || genres.Count == 0)
                {
                    return;
                }
                int used = -1;
                if (genres.ContainsKey(104))
                {
                    __result = genres[104];
                    used = 104;
                }
                if (__result == null)
                {
                    foreach (KeyValuePair<int, MusicGenreData> kv in genres)
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
                    foreach (KeyValuePair<int, MusicGenreData> kv in genres)
                    {
                        __result = kv.Value;
                        used = kv.Key;
                    }
                }
                if (__result != null && !_loggedGenre)
                {
                    _loggedGenre = true;
                    MelonLogger.Warning("[SongRequest] 有曲子用了客户端不存在的分类 id=" + id + ", 已顶成 " + used
                        + "(检查自制谱 Music.xml 里的 genreName, 合法值是 101~107)");
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

        private static int AddFinalizer(System.Type t, string name)
        {
            try
            {
                MethodInfo m = AccessTools.Method(t, name);
                if (m == null)
                {
                    return 0;
                }
                _harmony.Patch(m, finalizer: new HarmonyMethod(AccessTools.Method(typeof(CrashGuard), "Swallow_Finalizer")));
                return 1;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 给 " + t.Name + "." + name + " 挂保险失败: " + e.Message);
                return 0;
            }
        }

        private static Exception Swallow_Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null)
            {
                return null;
            }
            string name = __originalMethod == null ? "?" : __originalMethod.Name;
            if (_swallowed.Add(name))
            {
                MelonLogger.Warning("[SongRequest] 吞掉 " + name + " 的异常(防止闪退): "
                    + __exception.GetType().Name + ": " + __exception.Message);
            }
            return null;
        }
    }
}
