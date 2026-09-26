using System;
using System.Reflection;
using MelonLoader;
using HarmonyLib;
using UnityEngine;

[assembly: MelonInfo(typeof(SongRequestMod.Mod), "SongRequest", "1.0.7", "")]
[assembly: MelonGame("sega-interactive", "Sinmai")]
[assembly: AssemblyVersion("1.0.7.0")]
[assembly: AssemblyFileVersion("1.0.7.0")]

namespace SongRequestMod
{
    /// <summary>
    /// 点歌台 mod:
    ///   - 读游戏自己的曲目表(DataManager.GetMusics) -> 全部曲目 + 5 个难度 + 等级
    ///   - 本机网页(http://127.0.0.1:8790/)搜索/筛选/点歌
    ///   - 点难度 -> 主线程驱动 MusicSelectProcess 跳到该曲目 + 该难度
    ///   - 网页右侧常驻"当前游玩"面板(曲名/难度/Combo/分数/判定) —— 顶掉单独装 ComboWeb 的需求
    /// 全部内存内操作, 不改任何游戏文件。
    /// </summary>
    public class Mod : MelonMod
    {
        internal static Mod Instance;

        public override void OnInitializeMelon()
        {
            Instance = this;
            MainThread.Init();
            Web.InitGameDir();
            Config.Load();
            try
            {
                HarmonyInstance.PatchAll(typeof(Patch_SelectOnStart));
                HarmonyInstance.PatchAll(typeof(Patch_SelectOnRelease));
                if (Config.CrashGuardEnabled)
                {
                    CrashGuard.Install();   // 兜住游戏自身的 RestoreGhost/CategoryTab* 空引用闪退(默认关)
                }
                ModLog.Info("Harmony 已挂: MusicSelectProcess.OnStart / OnRelease");
            }
            catch (Exception e)
            {
                MelonLogger.Error("挂 Harmony 失败: " + e.Message);
            }
            if (!Config.WebEnable)
            {
                ModLog.Always("网页已关闭(配置 网页=true 打开)");
            }
        }

        /// <summary>网页服务放到"晚初始化": 别的 mod 都初始化完了再起, 免得日志顺序靠前、也免得抢资源</summary>
        public override void OnLateInitializeMelon()
        {
            if (Config.Enable && Config.WebEnable)
            {
                Web.Start(Config.Port);
                _urlLoggedAt = Time.realtimeSinceStartup;
                // 别名库启动就解析一次: /api/status 的"别名 N 条 / M 首"从此跟曲库有没有读出来无关
                try
                {
                    Aliases.Preload();
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("别名库预载失败: " + e.Message);
                }
            }
        }

        public override void OnUpdate()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                if (!_updateErrorLogged)
                {
                    _updateErrorLogged = true;
                    MelonLogger.Error("OnUpdate 异常(后续不再重复报): " + e.Message);
                }
            }
        }

        private static bool _updateErrorLogged;
        private static volatile bool _ready;

        private static float _urlLoggedAt = -1f;
        private static int _urlRelog;
        private float _liveTimer;

        /// <summary>
        /// 游戏数据(DataManager)加载完之前什么都不做(移植自原作者 v1.0.3):
        /// 加载途中就去读曲目表 / 游玩状态, 会跟游戏自己和别的 mod 的初始化撞上, 登录时闪退。
        /// </summary>
        /// <summary>游戏数据已就绪(网页线程也用它挡住加载期间的请求)</summary>
        internal static bool Ready
        {
            get { return _ready; }
        }

        private static bool GameReady()
        {
            if (_ready)
            {
                return true;
            }
            try
            {
                var dm = MAI2.Util.Singleton<Manager.DataManager>.Instance;
                if (dm == null || !dm.IsLoaded())
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
            _ready = true;
            ModLog.Always("游戏数据就绪, 开始工作");
            return true;
        }

        private void Tick()
        {
            if (!GameReady())
            {
                return;
            }
            // 1) 网页线程交过来的活(刷新曲目表 / 自检), 以及给网页线程看的"在不在选曲界面"
            MainThread.Pump();
            SelectDriver.UpdateCache();

            // 2) 网页点歌请求: 统一在主线程执行(HTTP 线程直接动 Unity/游戏对象会偶发崩)
            SelectDriver.RunPending();

            // 3) 封面 PNG 编码: 也必须主线程(每帧有时间预算, 游玩中更少, 别卡游戏)
            Jackets.Pump();

            // 4) 当前游玩状态快照(给网页轮询)
            _liveTimer += Time.unscaledDeltaTime;
            if (_liveTimer >= 0.05f)
            {
                _liveTimer = 0f;
                LiveState.Refresh();
                Web.PushNowPlaying(LiveState.Json());   // SSE: 有新变化就推给浏览器
            }

            // 5) 曲目表: 游戏表变了(热导入新歌)就重新导出
            // 别的 mod 日志会把我们那行顶到上面去, 所以在 15s / 90s 各补打一次(还是同一行内容)
            SongTable.Tick(Time.unscaledDeltaTime);
        }
    }

    /// <summary>日志: 默认只留「点歌台地址 + 报错」, 详细日志=true 才输出过程</summary>
    internal static class ModLog
    {
        internal static void Info(string msg)
        {
            if (Config.VerboseLog)
            {
                MelonLogger.Msg(msg);
            }
        }

        internal static void Always(string msg)
        {
            MelonLogger.Msg(msg);
        }
    }

    internal static class Config
    {
        public static bool Enable = true;
        public static bool WebEnable = true;
        public static int Port = 8790;
        /// <summary>是否同时监听局域网 IP(手机同网访问); 关掉就只有本机能连</summary>
        public static bool LanAccess = true;
        public static bool VerboseLog = false;
        /// <summary>点歌后是否自动切到难度选择画面(关掉就只移动光标, 不换画面)</summary>
        public static bool JumpToDifficultyScreen = true;
        /// <summary>网页曲绘封面服务(需要把游戏里的曲绘编码成 PNG, 会有一点开销)</summary>
        public static bool JacketService = true;
        /// <summary>崩溃保护: 给游戏自身的 RestoreGhost/CategoryTab* 挂 finalizer 兜住闪退。
        /// 默认关 —— 它会吞掉游戏异常, 出问题时游戏会带病继续跑(实测会导致选曲列表变空)。</summary>
        public static bool CrashGuardEnabled = false;

        private static string PathFile
        {
            get
            {
                string dir;
                try
                {
                    dir = System.IO.Path.GetDirectoryName(Application.dataPath);
                }
                catch
                {
                    dir = Environment.CurrentDirectory;
                }
                if (string.IsNullOrEmpty(dir))
                {
                    dir = Environment.CurrentDirectory;
                }
                return System.IO.Path.Combine(dir, "SongRequestMod.toml");
            }
        }

        public static void Load()
        {
            try
            {
                if (!System.IO.File.Exists(PathFile))
                {
                    Save();
                    MelonLogger.Msg("已生成配置: " + PathFile);
                    return;
                }
                foreach (string raw in System.IO.File.ReadAllLines(PathFile, System.Text.Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("["))
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim().Trim('"');
                    bool b;
                    int n;
                    if (key == "启用" || key.Equals("Enable", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(val, out b)) Enable = b;
                    }
                    else if (key == "网页" || key.Equals("WebEnable", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(val, out b)) WebEnable = b;
                    }
                    else if (key == "网页端口" || key.Equals("Port", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out n) && n > 1024 && n < 65535) Port = n;
                    }
                    else if (key == "局域网访问" || key.Equals("LanAccess", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(val, out b)) LanAccess = b;
                    }
                    else if (key == "详细日志" || key.Equals("VerboseLog", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(val, out b)) VerboseLog = b;
                    }
                    else if (key == "跳转后进难度画面" || key.Equals("JumpToDifficultyScreen", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(val, out b)) JumpToDifficultyScreen = b;
                    }
                    else if (key == "封面服务" || key.Equals("JacketService", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(val, out b)) JacketService = b;
                    }
                    else if (key == "崩溃保护" || key.Equals("CrashGuard", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(val, out b)) CrashGuardEnabled = b;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("读配置失败: " + e.Message);
            }
            EnsureAllKeys();
        }

        /// <summary>老配置文件缺了新版本加的键 -> 自动补上并重写(保留用户已改的值)</summary>
        private static void EnsureAllKeys()
        {
            try
            {
                string[] need = { "启用", "网页", "网页端口", "局域网访问", "跳转后进难度画面", "封面服务", "崩溃保护", "详细日志" };
                if (!System.IO.File.Exists(PathFile)) { Save(); return; }
                string txt = System.IO.File.ReadAllText(PathFile);
                for (int i = 0; i < need.Length; i++)
                {
                    if (txt.IndexOf(need[i] + "=", StringComparison.Ordinal) < 0)
                    {
                        MelonLogger.Msg("配置缺 " + need[i] + " -> 自动补全并重写 " + PathFile);
                        Save();
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("补全配置失败: " + e.Message);
            }
        }

        private static string B(bool v)
        {
            return v ? "true" : "false";
        }

        /// <summary>写配置: 用当前值(补全缺项时不能把用户改过的值冲回默认)</summary>
        public static void Save()
        {
            try
            {
                System.IO.File.WriteAllText(PathFile,
                    "## ===== SongRequestMod 点歌台 =====\r\n"
                    + "## 浏览器打开 http://127.0.0.1:" + Port + "/ 搜索点歌; 端口可在下面「网页端口」改\r\n"
                    + "\r\n"
                    + "## 总开关\r\n"
                    + "启用=" + B(Enable) + "\r\n"
                    + "\r\n"
                    + "## 网页\r\n"
                    + "网页=" + B(WebEnable) + "\r\n"
                    + "网页端口=" + Port + "\r\n"
                    + "## 局域网访问: 手机/平板同网也能打开(会绑本机局域网 IP)\r\n"
                    + "## 关掉就只有本机能连。首次用手机连如果连不上, 多半是 Windows 防火墙挡了入站:\r\n"
                    + "##   管理员 CMD 执行一次: netsh advfirewall firewall add rule name=\"SongRequestMod\" dir=in action=allow protocol=TCP localport=" + Port + "\r\n"
                    + "局域网访问=" + B(LanAccess) + "\r\n"
                    + "\r\n"
                    + "## 点歌后是否自动切到「难度选择」画面(关掉只移动光标不换画面)\r\n"
                    + "跳转后进难度画面=" + B(JumpToDifficultyScreen) + "\r\n"
                    + "\r\n"
                    + "## 网页显示曲绘封面(把游戏内曲绘编码成 PNG, 首次访问某首会有一点开销)\r\n"
                    + "封面服务=" + B(JacketService) + "\r\n"
                    + "\r\n"
                    + "## ===== 崩溃保护 =====\r\n"
                    + "## true: 给游戏自身的 RestoreGhost/CategoryTab* 打 finalizer 兜住闪退; \r\n"
                    + "##       注意它会吞掉游戏异常, 如果出现\"歌全没了\"等异常表现请改回 false\r\n"
                    + "崩溃保护=" + B(CrashGuardEnabled) + "\r\n"
                    + "\r\n"
                    + "## ===== 日志 =====\r\n"
                    + "## false(默认): 只留点歌台地址和报错; true: 过程日志全开\r\n"
                    + "详细日志=" + B(VerboseLog) + "\r\n",
                    new System.Text.UTF8Encoding(true));
            }
            catch (Exception e)
            {
                MelonLogger.Error("写配置失败: " + e.Message);
            }
        }
    }
}
