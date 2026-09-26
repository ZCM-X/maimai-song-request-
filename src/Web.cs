using System.Collections.Generic;
using MelonLoader;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace SongRequestMod
{
    /// <summary>
    /// 本机点歌台网页(监听 127.0.0.1 + 可选局域网 IP)。
    ///   /                 点歌台页面(磁盘上的 page.html, 改完刷新页面就生效, 不用重编 mod)
    ///   /api/songs        游戏全部曲目 + 难度 JSON
    ///   /api/nowplaying   当前游玩状态(曲名/难度/Combo/分数/判定)
    ///   /api/status       轻量状态
    ///   POST /api/play    id=&diff=  点歌
    ///   POST /api/random  diff=      随机点歌
    ///   /jacket?id=&s=    曲绘 PNG
    /// </summary>
    internal static class Web
    {
        private static HttpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;
        private static List<string> _lanUrls;



        /// <summary>启动点歌台; 失败(端口被占等)返回 false</summary>
        internal static bool Start(int port)
        {
            try
            {
                // 局域网: 让同网手机也能打开。绑到具体网卡 IP(不需要管理员权限,
                // 用 "+"/"*" 通配才需要 netsh 预先加 URL ACL)。
                List<string> lans = Config.LanAccess ? LanIps() : new List<string>();
                try
                {
                    _listener = Listen(port, lans);
                }
                catch (Exception e1)
                {
                    if (lans.Count == 0)
                    {
                        throw;
                    }
                    // 某个局域网 IP 绑不上(网卡刚换 IP / 被别的程序占着)会让整个 Start 失败, 连本机都打不开 ->
                    // 退一步只开本机, 至少电脑上能用
                    MelonLogger.Warning("[SongRequest] 局域网地址绑定失败, 只开本机访问: " + e1.Message);
                    lans = new List<string>();
                    _listener = Listen(port, lans);
                }
                _lanUrls = lans;
                _running = true;
                // 4 个工作线程: 之前单线程时, 页面一次要几百张封面会把 /api/play 堵在后面
                for (int w = 0; w < 4; w++)
                {
                    Thread th = new Thread(Loop);
                    th.IsBackground = true;
                    th.Start();
                }
                string lanTxt = "";
                if (_lanUrls != null && _lanUrls.Count > 0)
                {
                    for (int i = 0; i < _lanUrls.Count; i++)
                    {
                        lanTxt += (i == 0 ? "    手机(同网): http://" : " 或 http://") + _lanUrls[i] + ":" + port + "/";
                    }
                }
                ModLog.Always("[SongRequest] v" + typeof(Web).Assembly.GetName().Version
                    + "  本机: http://127.0.0.1:" + port + "/" + lanTxt);
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[SongRequest] 启动点歌台失败(端口 " + port + " 可能被占用, 常见原因是上次游戏没关干净,"
                    + " 在任务管理器结束残留的 Sinmai 后重开; 或改 SongRequestMod.toml 的「网页端口」): " + e.Message);
                return false;
            }
        }

        private static HttpListener Listen(int port, List<string> lans)
        {
            HttpListener l = new HttpListener();
            l.Prefixes.Add("http://127.0.0.1:" + port + "/");
            foreach (string ip in lans)
            {
                l.Prefixes.Add("http://" + ip + ":" + port + "/");
            }
            try
            {
                l.Start();
            }
            catch
            {
                try
                {
                    l.Close();
                }
                catch
                {
                }
                throw;
            }
            return l;
        }

        internal static void Stop()
        {
            try
            {
                _running = false;
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                    _listener = null;
                }
            }
            catch
            {
            }
        }

        private static void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (!_running)
                    {
                        return;
                    }
                    Thread.Sleep(50);
                    continue;
                }
                try
                {
                    Handle(ctx);
                }
                catch (Exception e)
                {
                    try
                    {
                        ReplyJson(ctx, "{\"ok\":false,\"msg\":" + Q("服务端异常: " + e.Message) + "}", 500);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            string method = ctx.Request.HttpMethod;

            // 游戏数据还没加载完: 碰曲目表 / 曲绘 / 点歌的接口一律先不处理(同 Mod.GameReady 的原因)
            if (!Mod.Ready && (path == "/api/songs" || path == "/jacket" || path == "/api/play"
                || path == "/api/random" || path == "/api/selftest"))
            {
                ReplyJson(ctx, "{\"ok\":false,\"msg\":\"游戏数据加载中, 请稍候再试\"}", 503);
                return;
            }
            if (path == "/api/songs")
            {
                // 平时直接给主线程建好的缓存; ?refresh=1 = 让主线程重探一次曲目表(最多等 5 秒, 超时给旧的)
                string cached = SongTable.CachedJson;
                ReplyJson(ctx, Query(ctx.Request.Url.Query, "refresh") == "1"
                    ? MainThread.Run(SongTable.JsonFresh, 5000, cached) : cached);
                return;
            }
            if (path == "/api/nowplaying")
            {
                ReplyJson(ctx, LiveState.Json());
                return;
            }
            if (path == "/api/status")
            {
                ReplyJson(ctx, "{\"ok\":true,\"version\":\"" + typeof(Web).Assembly.GetName().Version
                    + "\",\"songs\":" + SongTable.Count + ",\"rev\":" + SongTable.Rev
                    + ",\"aliases\":" + Aliases.Count + ",\"aliasSongs\":" + Aliases.SongCount
                    + ",\"jacketGen\":" + Jackets.Gen
                    + ",\"state\":\"" + LiveState.State
                    + "\",\"inSelect\":" + (SelectDriver.InSelectCached ? "true" : "false") + "}");
                return;
            }
            if (path == "/api/npstream")
            {
                // SSE 推送: 连接挂着不关, 每个客户端一条写线程(见 AddSse), 主线程只放最新状态不做网络 IO
                AddSse(ctx);
                return;   // 注意: 不 Close, 连接保持
            }
            if (path == "/api/perf")
            {
                // 排查"主线程卡了一下"用: 各项耗时(次数/上次/最大/平均, 毫秒). ?reset=1 清零再读
                if (Query(ctx.Request.Url.Query, "reset") == "1")
                {
                    Perf.Reset();
                }
                ReplyJson(ctx, "{\"ok\":true,\"perf\":" + Perf.Json() + "}");
                return;
            }
            if (path == "/api/selfcheck")
            {
                ReplyJson(ctx, MainThread.Run(SelectDriver.Diagnostics, 3000,
                    "{\"ok\":false,\"msg\":\"主线程忙或游戏数据还没就绪, 请稍后再试\"}"));
                return;
            }
            if (path == "/api/selftest")
            {
                // 布防: 等下次进选曲界面自动点一首(用来验证跳转链路, 不影响正常点歌)
                int tid = Int(Query(ctx.Request.Url.Query, "id"), 15001);
                int tdiff = Int(Query(ctx.Request.Url.Query, "diff"), 3);
                SelectDriver.ArmAutoTest(tid, tdiff);
                ReplyJson(ctx, "{\"ok\":true,\"msg\":" + Q("已布防: 进选曲界面自动点 " + tid) + "}");
                return;
            }
            if (path == "/api/play" && method == "POST")
            {
                var form = ReadForm(ctx);
                int id = Int(form, "id", -1);
                int diff = Int(form, "diff", -1);
                if (id <= 0)
                {
                    ReplyJson(ctx, "{\"ok\":false,\"msg\":\"缺少 id\"}");
                    return;
                }
                // 谱面类型: type=dx|std (也认 dx=1/0); 不传 = 自动(按点的 id 判断, 优先 DX)。
                // 一首歌的 DX 与标准谱在游戏里是同一张卡、靠 ScoreType 切换, 不传就只能猜,
                // 同时有 DX+标准 的歌会停在标准谱 —— 就是"点不了 DX 谱"。
                int scoreKind = -1;
                string ty = "";
                if (form.TryGetValue("type", out ty) && ty != null)
                {
                    ty = ty.Trim().ToLowerInvariant();
                }
                else
                {
                    ty = "";
                }
                if (ty == "dx" || ty == "deluxe" || ty == "1")
                {
                    scoreKind = 1;
                }
                else if (ty == "std" || ty == "standard" || ty == "0")
                {
                    scoreKind = 0;
                }
                else if (form.ContainsKey("dx"))
                {
                    scoreKind = Int(form, "dx", 1) != 0 ? 1 : 0;
                }
                ReplyPlay(ctx, SelectDriver.Enqueue(id, diff, scoreKind));
                return;
            }
            if (path == "/api/random" && method == "POST")
            {
                var form = ReadForm(ctx);
                ReplyPlay(ctx, SelectDriver.EnqueueRandom(Int(form, "diff", -1)));
                return;
            }
            if (path == "/jacket")
            {
                string q = ctx.Request.Url.Query;
                int id = Int(Query(q, "id"), -1);
                bool small = Query(q, "s") == "1";
                // 异步: 不占着网页工作线程等主线程编码(以前 4 个线程全在等封面时, 点歌请求只能排队)
                Jackets.Request(id, small, delegate (byte[] png)
                {
                    try
                    {
                        ReplyBytes(ctx, png, "image/png");
                    }
                    catch
                    {
                    }
                });
                return;
            }
            if (path == "/favicon.ico")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }
            // 页面本体(改磁盘上的 page.html 后刷新浏览器即可, 不用重启游戏)
            if (path == "/" || path == "/index.html" || path == "/overlay")
            {
                string file = path == "/overlay" ? "overlay.html" : "page.html";
                string html = ReadPageFile(file);
                if (html != null)
                {
                    ReplyText(ctx, html, "text/html; charset=utf-8");
                    return;
                }
            }
            ReplyJson(ctx, "{\"ok\":false,\"msg\":" + Q("no route: " + path) + "}", 404);
        }

        /// <summary>点歌结果: "已跳转…" 算成功; 被后面的点歌取代的标 superseded(网页不当错误提示)</summary>
        private static void ReplyPlay(HttpListenerContext ctx, string msg)
        {
            if (msg == null)
            {
                msg = "点歌超时: 游戏主线程没有响应";
            }
            bool superseded = msg.StartsWith(SelectDriver.SupersededPrefix, StringComparison.Ordinal);
            if (superseded)
            {
                msg = msg.Substring(SelectDriver.SupersededPrefix.Length);
            }
            bool ok = msg.StartsWith("已跳转", StringComparison.Ordinal);
            ReplyJson(ctx, "{\"ok\":" + (ok ? "true" : "false") + (superseded ? ",\"superseded\":true" : "")
                + ",\"msg\":" + Q(msg) + "}");
        }

        // ── 页面文件 ───────────────────────────────────────────────
        private static string _gameDir;

        /// <summary>主线程(OnInitializeMelon)里调一次: Application.dataPath 是 Unity API, 网页/后台线程不能碰</summary>
        internal static void InitGameDir()
        {
            try
            {
                string d = Path.GetDirectoryName(Application.dataPath);
                if (!string.IsNullOrEmpty(d))
                {
                    _gameDir = d;
                }
            }
            catch
            {
            }
        }

        internal static string GameDir
        {
            get { return _gameDir ?? Environment.CurrentDirectory; }
        }

        private sealed class PageCache
        {
            public string Text;
            public DateTime Stamp;
            public string Path;
        }

        private static readonly System.Collections.Generic.Dictionary<string, PageCache> _pages =
            new System.Collections.Generic.Dictionary<string, PageCache>();

        /// <summary>
        /// 读页面文件: 磁盘改了(mtime 变)就重读, 没改就回缓存 —— 所以改完 page.html 刷浏览器即生效。
        /// (之前这里条件写错, 第二请求之后会掉进兜底页, 已修)
        /// </summary>
        private static string ReadPageFile(string name)
        {
            try
            {
                string[] cands = new string[]
                {
                    Path.Combine(GameDir, "Mods", "SongRequestMod", name),
                    Path.Combine(GameDir, "Mods", name),
                    Path.Combine(GameDir, "SongRequestMod." + name),
                    Path.Combine(GameDir, name)
                };
                foreach (string p in cands)
                {
                    if (!File.Exists(p))
                    {
                        continue;
                    }
                    DateTime t = File.GetLastWriteTimeUtc(p);
                    PageCache c;
                    bool hit;
                    lock (_pages)
                    {
                        hit = _pages.TryGetValue(name, out c);
                    }
                    if (hit && c.Text != null
                        && t == c.Stamp && string.Equals(c.Path, p, StringComparison.OrdinalIgnoreCase))
                    {
                        return c.Text;
                    }
                    string txt = File.ReadAllText(p, Encoding.UTF8);
                    PageCache nc = new PageCache();
                    nc.Text = txt;
                    nc.Stamp = t;
                    nc.Path = p;
                    lock (_pages)
                    {
                        _pages[name] = nc;
                    }
                    ModLog.Info("[SongRequest] 页面已加载: " + p + " (" + txt.Length + " 字符)");
                    return txt;
                }
                ModLog.Info("[SongRequest] 页面文件没找到: " + name + " (找过 "
                    + Path.Combine(GameDir, "Mods", "SongRequestMod", name) + " 等 4 个位置)");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 读页面失败: " + e.Message);
            }
            // 磁盘上没找到 -> 用内嵌在 dll 里的那份(这样只丢一个 dll 也能用)
            string embedded = ReadEmbedded("SongRequestMod." + name);
            if (embedded != null)
            {
                ModLog.Info("[SongRequest] 页面用内嵌版本: " + name + " (" + embedded.Length + " 字符)");
                return embedded;
            }
            return Fallback(name);
        }

        private static string Fallback(string name)
        {
            if (name == "overlay.html")
            {
                return null;
            }
            return "<!doctype html><meta charset=\"utf-8\"><title>SongRequestMod</title>"
                + "<body style=\"background:#0e1120;color:#e6e9f2;font:14px/1.6 system-ui,sans-serif;padding:24px\">"
                + "<h2>点歌台页面文件缺失</h2>"
                + "<p>把 <code>page.html</code> 放到 <code>" + GameDir + "\\Mods\\SongRequestMod\\page.html</code> 即可。</p>"
                + "<p>接口仍然可用: <a style=\"color:#ff5ca0\" href=\"/api/songs\">/api/songs</a> · "
                + "<a style=\"color:#ff5ca0\" href=\"/api/nowplaying\">/api/nowplaying</a></p></body>";
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> _embedded =
            new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>读内嵌资源(磁盘上没有时用)</summary>
        internal static string ReadEmbedded(string logicalName)
        {
            string cached;
            lock (_embedded)
            {
                if (_embedded.TryGetValue(logicalName, out cached))
                {
                    return cached;
                }
            }
            try
            {
                var asm = typeof(Web).Assembly;
                using (var s = asm.GetManifestResourceStream(logicalName))
                {
                    if (s == null)
                    {
                        return null;
                    }
                    using (var r = new StreamReader(s, Encoding.UTF8))
                    {
                        string txt = r.ReadToEnd();
                        lock (_embedded)
                        {
                            _embedded[logicalName] = txt;
                        }
                        return txt;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 读内嵌资源失败 " + logicalName + ": " + e.Message);
                return null;
            }
        }

        /// <summary>把点歌台地址再打一遍(日志被别的 mod 刷屏时用)</summary>
        internal static void LogUrls()
        {
            int port = Config.Port;
            string lanTxt = "";
            if (_lanUrls != null && _lanUrls.Count > 0)
            {
                for (int i = 0; i < _lanUrls.Count; i++)
                {
                    lanTxt += (i == 0 ? "    手机(同网): http://" : " 或 http://") + _lanUrls[i] + ":" + port + "/";
                }
            }
            ModLog.Always("[SongRequest] 点歌台: http://127.0.0.1:" + port + "/" + lanTxt);
        }

        /// <summary>
        /// 一个 SSE 连接。以前是主线程直接往所有连接里 Write —— 只要有一个客户端不收数据
        /// (手机锁屏 / 切后台 / 网络卡住 / 隧道对端流控), TCP 发送缓冲写满后 Write 就会一直阻塞,
        /// 游戏主线程跟着卡死("没有回应")。现在每个连接一条写线程, 主线程只把最新状态放进 Pending
        /// (旧的没发出去就直接被新的覆盖), 写卡住超过 10 秒的连接直接掐掉。
        /// </summary>
        private sealed class SseClient
        {
            public HttpListenerContext Ctx;
            public string Pending;
            public bool Dead;
            public int WriteStartedAt;   // 0 = 没在写
            public readonly object Lock = new object();
        }

        private const int SseMaxClients = 16;
        private const int SseStuckMs = 10000;
        private const int SsePingMs = 15000;
        private static readonly List<SseClient> _sse = new List<SseClient>();
        private static readonly object _sseLock = new object();

        private static void AddSse(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                ctx.Response.SendChunked = true;
                SseClient c = new SseClient();
                c.Ctx = ctx;
                c.Pending = _sseLast;   // 一连上就给当前状态, 不用等下一次变化
                lock (_sseLock)
                {
                    if (_sse.Count >= SseMaxClients)
                    {
                        ctx.Response.Close();   // 满了: 网页会退回轮询
                        return;
                    }
                    _sse.Add(c);
                }
                Thread th = new Thread(() => SseWriter(c));
                th.IsBackground = true;
                th.Start();
                ModLog.Info("[SongRequest] SSE 客户端接入, 当前 " + _sse.Count + " 个");
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] SSE 建立失败: " + e.Message);
            }
        }

        private static void SseWriter(SseClient c)
        {
            try
            {
                Stream os = c.Ctx.Response.OutputStream;
                byte[] hello = Encoding.UTF8.GetBytes(": connected\n\n");
                SseWrite(c, os, hello);
                while (true)
                {
                    string msg;
                    lock (c.Lock)
                    {
                        if (!c.Dead && c.Pending == null)
                        {
                            System.Threading.Monitor.Wait(c.Lock, SsePingMs);
                        }
                        if (c.Dead)
                        {
                            break;
                        }
                        msg = c.Pending;
                        c.Pending = null;
                    }
                    // 没有新状态就发一行注释当心跳: 连接断了能尽快发现, 也免得代理/隧道把空闲连接掐掉
                    SseWrite(c, os, Encoding.UTF8.GetBytes(msg != null ? "data: " + msg + "\n\n" : ": ping\n\n"));
                }
            }
            catch
            {
            }
            lock (_sseLock)
            {
                _sse.Remove(c);
            }
            try
            {
                c.Ctx.Response.Abort();
            }
            catch
            {
            }
            ModLog.Info("[SongRequest] SSE 断开清理, 剩 " + _sse.Count + " 个");
        }

        /// <summary>
        /// 带超时的写: 对端不收数据时同步 Write 会一直阻塞, 而且 Abort 连接也不一定能把它打断,
        /// 所以用异步写 + 等 SseStuckMs, 超时就放弃这个连接(写线程随之退出, 不会一直挂着)。
        /// </summary>
        private static void SseWrite(SseClient c, Stream os, byte[] buf)
        {
            c.WriteStartedAt = Environment.TickCount | 1;   // |1: 保证不是 0
            try
            {
                IAsyncResult ar = os.BeginWrite(buf, 0, buf.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(SseStuckMs))
                {
                    throw new IOException("SSE 客户端 " + SseStuckMs + "ms 没收数据");
                }
                os.EndWrite(ar);
                os.Flush();
            }
            finally
            {
                c.WriteStartedAt = 0;
            }
        }

        /// <summary>主线程调用: 把最新状态交给各 SSE 写线程(内容没变就不推)。这里不做任何网络 IO</summary>
        internal static void PushNowPlaying(string json)
        {
            if (json == null || json == _sseLast)
            {
                return;
            }
            _sseLast = json;
            SseClient[] clients;
            lock (_sseLock)
            {
                if (_sse.Count == 0)
                {
                    return;
                }
                clients = _sse.ToArray();
            }
            int now = Environment.TickCount;
            foreach (SseClient c in clients)
            {
                int started = c.WriteStartedAt;
                if (started != 0 && unchecked(now - started) > SseStuckMs && !c.Dead)
                {
                    // 写卡住太久(兜底; 正常情况下 SseWrite 自己的超时会先生效): 移出名单, 后台线程掐掉连接
                    c.Dead = true;
                    lock (_sseLock)
                    {
                        _sse.Remove(c);
                    }
                    HttpListenerContext ctx = c.Ctx;
                    Background.Run(delegate { ctx.Response.Abort(); });
                    continue;
                }
                lock (c.Lock)
                {
                    c.Pending = json;
                    System.Threading.Monitor.Pulse(c.Lock);
                }
            }
        }

        private static volatile string _sseLast;

        /// <summary>本机所有可用的局域网 IPv4(给手机访问用)</summary>
        private static List<string> LanIps()
        {
            List<string> ips = new List<string>();
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in
                    System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        continue;
                    }
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }
                    // 过滤虚拟网卡: 否则日志里会列一堆 VMware/ZeroTier/Radmin 地址
                    string niName = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    string[] vHints = { "vmware", "virtualbox", "hyper-v", "vethernet", "zerotier",
                                        "radmin", "tap-", "openvpn", "wireguard", "wsl", "docker",
                                        "bluetooth", "virtual", "vpn", "hamachi", "npcap" };
                    bool vSkip = false;
                    for (int h = 0; h < vHints.Length; h++) { if (niName.Contains(vHints[h])) { vSkip = true; break; } }
                    if (vSkip) { continue; }
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in
                        ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            continue;
                        }
                        string s = ua.Address.ToString();
                        if (s.StartsWith("169.254.") || s.StartsWith("127."))
                        {
                            continue;   // 自动私有地址/回环不要
                        }
                        if (!ips.Contains(s))
                        {
                            ips.Add(s);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 取本机 IP 失败: " + e.Message);
            }
            if (ips.Count == 0)
            {
                // 兜底: 用户只有虚拟网卡(例如走 Radmin/ZeroTier 联机)时, 至少给一个可用地址
                foreach (System.Net.NetworkInformation.NetworkInterface ni2 in
                    System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni2.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni2.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua2 in ni2.GetIPProperties().UnicastAddresses)
                    {
                        if (ua2.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string s2 = ua2.Address.ToString();
                        if (s2.StartsWith("169.254.") || s2.StartsWith("127.") || ips.Contains(s2)) continue;
                        ips.Add(s2);
                    }
                }
            }
            return ips;
        }

        // ── HTTP 小工具 ───────────────────────────────────────────
        private static string ReadBody(HttpListenerContext ctx)
        {
            try
            {
                if (!ctx.Request.HasEntityBody)
                {
                    return "";
                }
                using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch
            {
                return "";
            }
        }

        private static System.Collections.Generic.Dictionary<string, string> ReadForm(HttpListenerContext ctx)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var kv in Parse(ReadBody(ctx)))
            {
                d[kv.Key] = kv.Value;
            }
            if (d.Count == 0)
            {
                foreach (var kv in Parse(ctx.Request.Url.Query))
                {
                    d[kv.Key] = kv.Value;
                }
            }
            return d;
        }

        private static System.Collections.Generic.Dictionary<string, string> Parse(string s)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            if (string.IsNullOrEmpty(s))
            {
                return d;
            }
            foreach (string part in s.Split('&'))
            {
                if (part.Length == 0)
                {
                    continue;
                }
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? "" : part.Substring(eq + 1);
                d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace("+", " "));
            }
            return d;
        }

        private static string Query(string q, string key)
        {
            var d = Parse(q == null ? "" : q.TrimStart('?'));
            string v;
            return d.TryGetValue(key, out v) ? v : "";
        }

        private static int Int(System.Collections.Generic.Dictionary<string, string> d, string key, int def)
        {
            string v;
            int n;
            if (d.TryGetValue(key, out v) && int.TryParse(v, out n))
            {
                return n;
            }
            return def;
        }

        private static int Int(string v, int def)
        {
            int n;
            return int.TryParse(v, out n) ? n : def;
        }

        internal static void ReplyJson(HttpListenerContext ctx, string json, int code)
        {
            ReplyText(ctx, json, "application/json; charset=utf-8", code);
        }

        internal static void ReplyJson(HttpListenerContext ctx, string json)
        {
            ReplyText(ctx, json, "application/json; charset=utf-8", 200);
        }

        internal static void ReplyText(HttpListenerContext ctx, string text, string type)
        {
            ReplyText(ctx, text, type, 200);
        }

        internal static void ReplyText(HttpListenerContext ctx, string text, string type, int code)
        {
            byte[] buf = Encoding.UTF8.GetBytes(text ?? "");
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            if (buf.Length > 0)
            {
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            }
            ctx.Response.Close();
        }

        internal static void ReplyBytes(HttpListenerContext ctx, byte[] data, string type)
        {
            ctx.Response.StatusCode = data != null && data.Length > 0 ? 200 : 404;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = data == null ? 0 : data.Length;
            if (data != null && data.Length > 0)
            {
                ctx.Response.Headers["Cache-Control"] = "max-age=3600";
                ctx.Response.OutputStream.Write(data, 0, data.Length);
            }
            ctx.Response.Close();
        }

        private static string Q(string s)
        {
            return "\"" + SongTable.Escape(s) + "\"";
        }
    }
}
