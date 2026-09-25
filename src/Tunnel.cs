using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;

namespace SongRequestMod
{
    /// <summary>
    /// 远程分享: 用 Cloudflare 免费临时隧道(cloudflared "quick tunnel")把本机点歌台映射成一个公网 https 链接,
    /// 贴给别人就能远程点歌。不需要注册账号、不需要改路由器 / 防火墙。
    ///   - cloudflared.exe 放在 Mods\SongRequestMod\ 下; 没有就自动下载(官方 GitHub Release)
    ///   - 每次开启都会换一个新链接 + 新密钥, 关掉后旧链接立刻失效
    ///   - 远程访问必须带密钥(?k=), 首次打开后写进 cookie, 之后页面内请求自动带上
    /// </summary>
    internal static class Tunnel
    {
        private const string DownloadUrl =
            "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

        private static readonly object _lock = new object();
        private static Process _proc;
        private static string _state = "off";     // off / downloading / starting / running / error
        private static string _msg = "";
        private static string _baseUrl;           // https://xxx.trycloudflare.com
        private static string _key;               // 本次分享的访问密钥

        internal static string State { get { lock (_lock) { CheckExited(); return _state; } } }
        internal static string Key { get { lock (_lock) { return _state == "running" ? _key : null; } } }

        /// <summary>给网页的状态 JSON(只给本机/局域网看, 里面有带密钥的链接)</summary>
        internal static string Json()
        {
            lock (_lock)
            {
                CheckExited();
                string share = _state == "running" && _baseUrl != null ? _baseUrl + "/?k=" + _key : "";
                return "{\"ok\":true,\"state\":\"" + _state + "\",\"url\":\"" + SongTable.Escape(share)
                    + "\",\"msg\":\"" + SongTable.Escape(_msg) + "\"}";
            }
        }

        /// <summary>开启分享(后台线程做下载/启动, 立即返回)</summary>
        internal static void Start()
        {
            lock (_lock)
            {
                CheckExited();
                if (_state == "downloading" || _state == "starting" || _state == "running")
                {
                    return;
                }
                _state = "starting";
                _msg = "正在启动隧道…";
                _baseUrl = null;
                _key = NewKey();
                _lastLine = "";
            }
            Thread th = new Thread(Run);
            th.IsBackground = true;
            th.Start();
        }

        internal static void Stop()
        {
            Process p;
            lock (_lock)
            {
                p = _proc;
                _proc = null;
                _state = "off";
                _msg = "";
                _baseUrl = null;
                _key = null;
            }
            Kill(p);
            DeletePidFile();
        }

        private static void Run()
        {
            try
            {
                KillOrphan();
                string exe = FindExe();
                if (exe == null)
                {
                    SetState("downloading", "首次使用, 正在下载 cloudflared(约 50MB)…");
                    exe = Download();
                    if (exe == null)
                    {
                        SetState("error", "下载 cloudflared 失败。请手动下载 " + DownloadUrl
                            + " 并改名为 cloudflared.exe 放到 " + ExeCandidates()[0]);
                        return;
                    }
                    lock (_lock)
                    {
                        if (_state != "downloading") return;   // 下载途中用户点了关闭
                    }
                    SetState("starting", "正在启动隧道…");
                }

                // 先用默认协议(QUIC/UDP); 有的网络挡 UDP, 连不上就换 HTTP2(TCP) 再试一次
                string[] protocols = { "", " --protocol http2" };
                for (int a = 0; a < protocols.Length; a++)
                {
                    if (a > 0)
                    {
                        SetState("starting", "默认线路连不上, 改用 TCP 线路重试…");
                    }
                    bool? ok = TryStart(exe, protocols[a]);
                    if (ok != false)
                    {
                        return;   // true: 成功; null: 用户中途关闭
                    }
                }
                SetState("error", "连不上 Cloudflare, 请检查网络(需要能访问外网 7844 端口)后重试。" + _lastLine);
            }
            catch (Exception e)
            {
                SetState("error", "启动隧道失败: " + e.Message);
            }
        }

        /// <summary>启动一次 cloudflared 并等到隧道真正连上(true); 连不上(false); 用户中途关闭(null)</summary>
        private static bool? TryStart(string exe, string extraArgs)
        {
            int port = Config.Port;
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            // --http-host-header: 把 Host 改回 127.0.0.1, 否则 HttpListener 按前缀匹配会直接拒掉
            psi.Arguments = "tunnel --no-autoupdate --url http://127.0.0.1:" + port
                + " --http-host-header 127.0.0.1:" + port + extraArgs;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            Process p = Process.Start(psi);
            lock (_lock)
            {
                if (_state != "starting")
                {
                    Kill(p);   // 启动途中用户点了关闭
                    return null;
                }
                _proc = p;
                _pendingUrl = null;
            }
            WritePidFile(p);
            StartReader(p, p.StandardError);
            StartReader(p, p.StandardOutput);

            // 链接一般几秒就打出来, 但要等 "Registered tunnel connection" 才真的能访问; 最多等 25 秒
            for (int i = 0; i < 250; i++)
            {
                Thread.Sleep(100);
                lock (_lock)
                {
                    if (_proc != p) return null;
                    if (_baseUrl != null) return true;
                    if (p.HasExited) break;
                }
            }
            lock (_lock)
            {
                if (_proc != p) return null;
                if (_baseUrl != null) return true;
                _proc = null;
            }
            Kill(p);
            DeletePidFile();
            return false;
        }

        private static string _pendingUrl;   // cloudflared 已分配、但还没连上边缘节点的链接

        private static readonly Regex UrlRe = new Regex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
        private static string _lastLine = "";

        private static void StartReader(Process p, StreamReader reader)
        {
            Thread th = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf(" ERR ", StringComparison.Ordinal) >= 0)
                        {
                            _lastLine = " 最后错误: " + line.Trim();
                        }
                        ModLog.Info("[SongRequest] cloudflared: " + line);
                        Match m = UrlRe.Match(line);
                        // api.trycloudflare.com 是它申请隧道用的接口地址, 不是分配给我们的链接
                        if (m.Success && !m.Value.StartsWith("https://api.", StringComparison.OrdinalIgnoreCase))
                        {
                            lock (_lock)
                            {
                                if (_proc == p && _pendingUrl == null) _pendingUrl = m.Value.ToLowerInvariant();
                            }
                            continue;
                        }
                        if (line.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        string share = null;
                        lock (_lock)
                        {
                            if (_proc == p && _pendingUrl != null && _baseUrl == null)
                            {
                                _baseUrl = _pendingUrl;
                                _state = "running";
                                _msg = "";
                                share = _baseUrl + "/?k=" + _key;
                            }
                        }
                        if (share != null)
                        {
                            ModLog.Always("[SongRequest] 远程分享已开启: " + share);
                        }
                    }
                }
                catch
                {
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        /// <summary>隧道进程意外退出 -> 状态回到 error(调用方持锁)</summary>
        private static void CheckExited()
        {
            if (_proc == null || _state != "running")
            {
                return;
            }
            try
            {
                if (_proc.HasExited)
                {
                    _state = "error";
                    _msg = "隧道已断开(cloudflared 退出), 请重新开启分享";
                    _baseUrl = null;
                    _key = null;
                    _proc = null;
                }
            }
            catch
            {
            }
        }

        private static void SetState(string state, string msg)
        {
            lock (_lock)
            {
                if (_state == "off") return;   // 用户已关闭, 不要把状态又改回来
                _state = state;
                _msg = msg;
            }
            if (state == "error") MelonLogger.Warning("[SongRequest] 远程分享: " + msg);
        }

        /// <summary>访问密钥: 16 字节随机数, URL 安全的 base64</summary>
        private static string NewKey()
        {
            byte[] b = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(b);
            }
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>常量时间比较, 免得密钥被逐字符试出来</summary>
        internal static bool KeyMatches(string given)
        {
            string key = Key;
            if (key == null || given == null || given.Length != key.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < key.Length; i++)
            {
                diff |= key[i] ^ given[i];
            }
            return diff == 0;
        }

        // ── cloudflared.exe 查找 / 下载 ─────────────────────────────
        private static string[] ExeCandidates()
        {
            return new string[]
            {
                Path.Combine(Web.GameDir, "Mods", "SongRequestMod", "cloudflared.exe"),
                Path.Combine(Web.GameDir, "Mods", "cloudflared.exe"),
                Path.Combine(Web.GameDir, "cloudflared.exe")
            };
        }

        private static string FindExe()
        {
            foreach (string p in ExeCandidates())
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static string Download()
        {
            string dst = ExeCandidates()[0];
            string tmp = dst + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
            }
            // 1) 游戏自带 Mono 的 TLS 未必够新, 先试一次
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
                using (WebClient wc = new WebClient())
                {
                    wc.DownloadFile(DownloadUrl, tmp);
                }
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] WebClient 下载失败, 改用 curl: " + e.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            // 2) 不行就用 Windows 10+ 自带的 curl.exe
            if (!File.Exists(tmp))
            {
                try
                {
                    string curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = File.Exists(curl) ? curl : "curl.exe";
                    psi.Arguments = "-L -f -s -o \"" + tmp + "\" \"" + DownloadUrl + "\"";
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (Process p = Process.Start(psi))
                    {
                        if (!p.WaitForExit(300000))
                        {
                            Kill(p);
                        }
                    }
                }
                catch (Exception e)
                {
                    ModLog.Info("[SongRequest] curl 下载失败: " + e.Message);
                }
            }
            try
            {
                // 正常的 exe 有几十 MB, 太小说明拿到的是错误页
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 1000000)
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                    ModLog.Always("[SongRequest] cloudflared 已下载: " + dst);
                    return dst;
                }
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 保存 cloudflared 失败: " + e.Message);
            }
            return null;
        }

        // ── 进程收尾: 游戏崩了没来得及关的隧道, 下次启动时按 pid 文件清掉 ──
        private static string PidFile
        {
            get { return Path.Combine(Web.GameDir, "Mods", "SongRequestMod", "cloudflared.pid"); }
        }

        private static void WritePidFile(Process p)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PidFile));
                File.WriteAllText(PidFile, p.Id.ToString());
            }
            catch
            {
            }
        }

        private static void DeletePidFile()
        {
            try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { }
        }

        private static void KillOrphan()
        {
            try
            {
                if (!File.Exists(PidFile)) return;
                int pid;
                if (int.TryParse(File.ReadAllText(PidFile).Trim(), out pid))
                {
                    Process old = Process.GetProcessById(pid);
                    if (old.ProcessName.IndexOf("cloudflared", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Kill(old);
                    }
                }
            }
            catch
            {
            }
            DeletePidFile();
        }

        private static void Kill(Process p)
        {
            if (p == null) return;
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch
            {
            }
        }
    }
}
