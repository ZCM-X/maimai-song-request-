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
    /// 本机点歌台网页(只监听 127.0.0.1)。
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



        internal static void Start(int port)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                // 局域网: 让同网手机也能打开。绑到具体网卡 IP(不需要管理员权限,
                // 用 "+"/"*" 通配才需要 netsh 预先加 URL ACL)。
                List<string> lans = Config.LanAccess ? LanIps() : new List<string>();
                foreach (string ip in lans)
                {
                    try
                    {
                        _listener.Prefixes.Add("http://" + ip + ":" + port + "/");
                    }
                    catch (Exception e2)
                    {
                        MelonLogger.Warning("[SongRequest] 绑 " + ip + " 失败: " + e2.Message);
                    }
                }
                _listener.Start();
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
            }
            catch (Exception e)
            {
                MelonLogger.Error("[SongRequest] 启动点歌台失败(端口 " + port + " 可能被占用): " + e.Message);
            }
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

            if (path == "/api/songs")
            {
                // ?refresh=1 强制重读游戏曲目表; 平时走缓存(曲目数/类型变了会自动重建)
                ReplyJson(ctx, SongTable.Json(Query(ctx.Request.Url.Query, "refresh") == "1"));
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
                    + ",\"state\":\"" + LiveState.State
                    + "\",\"inSelect\":" + (SelectDriver.InSelect ? "true" : "false") + "}");
                return;
            }
            if (path == "/api/npstream")
            {
                // SSE 推送: 连接挂着不关, 主线程每帧把最新状态写进来(比轮询延迟低一个数量级)
                try
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
                    ctx.Response.Headers["Cache-Control"] = "no-cache";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";
                    ctx.Response.SendChunked = true;
                    lock (_sseLock)
                    {
                        if (_sse.Count >= 8) { ctx.Response.Close(); return; }
                        _sse.Add(ctx);
                    }
                    byte[] hello = Encoding.UTF8.GetBytes(": connected\n\n");
                    ctx.Response.OutputStream.Write(hello, 0, hello.Length);
                    ctx.Response.OutputStream.Flush();
                    ModLog.Info("[SongRequest] SSE 客户端接入, 当前 " + _sse.Count + " 个");
                }
                catch (Exception e)
                {
                    ModLog.Info("[SongRequest] SSE 建立失败: " + e.Message);
                }
                return;   // 注意: 不 Close, 连接保持
            }
            if (path == "/api/selfcheck")
            {
                ReplyJson(ctx, SelectDriver.Diagnostics());
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
                string msg = SelectDriver.Enqueue(id, diff);
                bool ok = msg != null && msg.StartsWith("已跳转");
                ReplyJson(ctx, "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":" + Q(msg) + "}");
                return;
            }
            if (path == "/api/random" && method == "POST")
            {
                var form = ReadForm(ctx);
                string msg = SelectDriver.EnqueueRandom(Int(form, "diff", -1));
                bool ok = msg != null && msg.StartsWith("已跳转");
                ReplyJson(ctx, "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":" + Q(msg) + "}");
                return;
            }
            if (path == "/jacket")
            {
                string q = ctx.Request.Url.Query;
                int id = Int(Query(q, "id"), -1);
                bool small = Query(q, "s") == "1";
                byte[] png = Jackets.Get(id, small, 6000);
                if (png == null)
                {
                    ctx.Response.StatusCode = 404;
                    ReplyBytes(ctx, new byte[0], "image/png");
                    return;
                }
                ReplyBytes(ctx, png, "image/png");
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
            ReplyJson(ctx, "{\"ok\":false,\"msg\":\"no route: " + path + "\"}", 404);
        }

        // ── 页面文件 ───────────────────────────────────────────────
        internal static string GameDir
        {
            get
            {
                try
                {
                    string d = Path.GetDirectoryName(Application.dataPath);
                    if (!string.IsNullOrEmpty(d))
                    {
                        return d;
                    }
                }
                catch
                {
                }
                return Environment.CurrentDirectory;
            }
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
                    if (_pages.TryGetValue(name, out c) && c.Text != null
                        && t == c.Stamp && string.Equals(c.Path, p, StringComparison.OrdinalIgnoreCase))
                    {
                        return c.Text;
                    }
                    string txt = File.ReadAllText(p, Encoding.UTF8);
                    PageCache nc = new PageCache();
                    nc.Text = txt;
                    nc.Stamp = t;
                    nc.Path = p;
                    _pages[name] = nc;
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
            if (_embedded.TryGetValue(logicalName, out cached))
            {
                return cached;
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
                        _embedded[logicalName] = txt;
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

        private static readonly List<HttpListenerContext> _sse = new List<HttpListenerContext>();
        private static readonly object _sseLock = new object();

        /// <summary>主线程调用: 把最新状态 JSON 推给所有 SSE 客户端(内容没变就不推)</summary>
        internal static void PushNowPlaying(string json)
        {
            if (json == null) return;
            lock (_sseLock)
            {
                if (_sse.Count == 0 || json == _sseLast) { _sseLast = json; return; }
                _sseLast = json;
                if (_sse.Count == 0) return;
            }
            byte[] buf = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
            List<HttpListenerContext> dead = null;
            lock (_sseLock)
            {
                for (int i = 0; i < _sse.Count; i++)
                {
                    try
                    {
                        _sse[i].Response.OutputStream.Write(buf, 0, buf.Length);
                        _sse[i].Response.OutputStream.Flush();
                    }
                    catch
                    {
                        if (dead == null) dead = new List<HttpListenerContext>();
                        dead.Add(_sse[i]);
                    }
                }
                if (dead != null)
                {
                    foreach (HttpListenerContext d in dead)
                    {
                        _sse.Remove(d);
                        try { d.Response.Close(); } catch { }
                    }
                }
            }
            if (dead != null) ModLog.Info("[SongRequest] SSE 断开清理, 剩 " + _sse.Count + " 个");
        }

        private static string _sseLast;

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
