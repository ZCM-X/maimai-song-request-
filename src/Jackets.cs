using MelonLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace SongRequestMod
{
    /// <summary>
    /// 曲绘封面: 网页要图 -> 排队 -> 主线程(Tick/Jackets.Pump)从游戏资源里取贴图并编码 PNG -> 回调里回给网页。
    /// 所有 Unity API 都在主线程调用(网页线程碰 Unity 会偶发崩)。
    /// 贴图是 AssetBundle 里来的, 通常不可读 -> 走 RenderTexture 回读; 列表小图缩到 160px, 大图 512px。
    /// 网页线程不在这里等结果(以前每个请求占着一个网页工作线程最多 6 秒, 4 个线程全在等封面时点歌只能排队)。
    /// </summary>
    internal static class Jackets
    {
        private const int MaxPx = 512;
        private const int SmallPx = 160;
        /// <summary>主线程每帧最多花在编码封面上的时间</summary>
        private const double FrameBudgetMs = 4.0;
        /// <summary>游玩中每隔几帧才处理一张, 别让打歌掉帧</summary>
        private const int PlayingFrameInterval = 10;
        /// <summary>排队超过这么久的请求直接放弃(浏览器那边早就不等了)</summary>
        private const int StaleMs = 15000;
        private const int MaxQueue = 240;
        private const long CacheMaxBytes = 48L * 1024 * 1024;

        private sealed class Job
        {
            public int Id;
            public bool Small;
            public string Key;
            public int EnqueuedAt;
            public readonly List<Action<byte[]>> Callbacks = new List<Action<byte[]>>();
        }

        private static readonly Dictionary<string, byte[]> _cache = new Dictionary<string, byte[]>();
        /// <summary>缓存的插入顺序, 超过 CacheMaxBytes 时从最旧的开始扔</summary>
        private static readonly Queue<string> _cacheOrder = new Queue<string>();
        private static long _cacheBytes;
        private static readonly Queue<Job> _queue = new Queue<Job>();
        private static readonly object _lock = new object();
        /// <summary>正在排队/处理中的封面(同一张图合并请求, 避免同一张图排好几次队)</summary>
        private static readonly Dictionary<string, Job> _inflight = new Dictionary<string, Job>();
        private static int _failed;
        private static int _frame;

        /// <summary>要一张封面。done 可能在当前线程(有缓存时)或线程池线程上被调用, png 为 null 表示没有图</summary>
        internal static void Request(int id, bool small, Action<byte[]> done)
        {
            if (!Config.JacketService || id <= 0)
            {
                done(null);
                return;
            }
            string key = id + (small ? "_s" : "_f");
            byte[] hit = null;
            bool refuse = false;
            lock (_lock)
            {
                if (!_cache.TryGetValue(key, out hit))
                {
                    Job job;
                    if (_inflight.TryGetValue(key, out job))
                    {
                        job.Callbacks.Add(done);   // 同一张图已经在排队 -> 合并
                        return;
                    }
                    if (_queue.Count >= MaxQueue)
                    {
                        refuse = true;             // 页面一次要太多图, 后面的先不给, 别把游戏拖死
                    }
                    else
                    {
                        job = new Job();
                        job.Id = id;
                        job.Small = small;
                        job.Key = key;
                        job.EnqueuedAt = Environment.TickCount;
                        job.Callbacks.Add(done);
                        _inflight[key] = job;
                        _queue.Enqueue(job);
                        return;
                    }
                }
            }
            done(refuse ? null : hit);
        }

        /// <summary>主线程每帧调用: 在时间预算内处理排队的封面</summary>
        internal static void Pump()
        {
            _frame++;
            bool playing = LiveState.State == "playing";
            if (playing && _frame % PlayingFrameInterval != 0)
            {
                return;
            }
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                Job job = null;
                lock (_lock)
                {
                    if (_queue.Count > 0)
                    {
                        job = _queue.Dequeue();
                    }
                }
                if (job == null)
                {
                    return;
                }
                byte[] png = null;
                if (unchecked(Environment.TickCount - job.EnqueuedAt) < StaleMs)
                {
                    try
                    {
                        png = Resolve(job.Id, job.Small);
                    }
                    catch (Exception e)
                    {
                        if (_failed++ < 5)
                        {
                            ModLog.Info("[SongRequest] 取曲绘失败 id=" + job.Id + ": " + e.Message);
                        }
                    }
                }
                Finish(job, png);
                // 游玩中一次只做一张; 平时做到这一帧的预算用完为止
                if (playing || sw.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                {
                    return;
                }
            }
        }

        private static void Finish(Job job, byte[] png)
        {
            Action<byte[]>[] callbacks;
            lock (_lock)
            {
                if (png != null && !_cache.ContainsKey(job.Key))
                {
                    _cache[job.Key] = png;
                    _cacheOrder.Enqueue(job.Key);
                    _cacheBytes += png.Length;
                    while (_cacheBytes > CacheMaxBytes && _cacheOrder.Count > 0)
                    {
                        string old = _cacheOrder.Dequeue();
                        byte[] ob;
                        if (_cache.TryGetValue(old, out ob))
                        {
                            _cacheBytes -= ob.Length;
                            _cache.Remove(old);
                        }
                    }
                }
                _inflight.Remove(job.Key);
                callbacks = job.Callbacks.ToArray();
            }
            // 回调里要写网络, 不能在主线程做; 也不放线程池(见 Background)
            foreach (Action<byte[]> cb in callbacks)
            {
                Action<byte[]> c = cb;
                Background.Run(delegate { c(png); });
            }
        }

        internal static void ClearCache()
        {
            lock (_lock)
            {
                _cache.Clear();
                _cacheOrder.Clear();
                _cacheBytes = 0;
            }
        }

        private static byte[] Resolve(int id, bool small)
        {
            AssetManager am = AssetManager.Instance();
            if (am == null)
            {
                ModLog.Info("[SongRequest] 曲绘: AssetManager 还没起来");
                return null;
            }
            // 优先用游戏数据里的真实资源名(jacketFile / thumbnailName):
            // AssetManager.GetJacketTexture2D(int) 内部会调 GetMusic(id), 那个被别的 mod 过滤过,
            // 对本地全量表里的曲子会拿到 null -> 占位图。所以直接传资源名。
            string name = SongTable.JacketAssetName(id, small);
            int jid = id % 10000;   // 曲绘文件名用的是非 DX 的 id
            Texture2D tex = null;
            try
            {
                tex = small ? am.GetJacketThumbTexture2D(name) : am.GetJacketTexture2D(name);
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 曲绘(" + name + ")取图异常: " + e.Message);
            }
            if (tex == null)
            {
                try
                {
                    tex = small ? am.GetJacketThumbTexture2D(jid) : am.GetJacketTexture2D(jid);
                }
                catch
                {
                }
            }
            if (tex == null && !small)
            {
                // 大图没有就退小图, 网页照样有图
                try
                {
                    tex = am.GetJacketThumbTexture2D(SongTable.JacketAssetName(id, true));
                }
                catch
                {
                }
            }
            if (tex == null)
            {
                ModLog.Info("[SongRequest] 曲绘没找到: id=" + id + " small=" + small + " name=" + name);
                return null;
            }
            Texture2D readable = ToReadable(tex, small ? SmallPx : MaxPx);
            if (readable == null)
            {
                return null;
            }
            byte[] png;
            try
            {
                png = ImageConversion.EncodeToPNG(readable);
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 曲绘编码失败 " + name + ": " + e.Message);
                png = null;
            }
            if (readable != tex)
            {
                UnityEngine.Object.Destroy(readable);
            }
            if (png == null)
            {
                ModLog.Info("[SongRequest] 曲绘编码返回空: " + name);
            }
            return png;
        }

        /// <summary>把任意贴图弄成可编码的 Texture2D(GPU 回读 + 等比缩到 maxPx 以内)</summary>
        private static Texture2D ToReadable(Texture2D src, int maxPx)
        {
            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0)
            {
                return null;
            }
            if (w > maxPx || h > maxPx)
            {
                float k = Mathf.Min((float)maxPx / w, (float)maxPx / h);
                w = Mathf.Max(1, Mathf.RoundToInt(w * k));
                h = Mathf.Max(1, Mathf.RoundToInt(h * k));
            }
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
