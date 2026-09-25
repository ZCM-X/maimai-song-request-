using MelonLoader;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SongRequestMod
{
    /// <summary>
    /// 曲绘封面: 网页要图 -> HTTP 线程排队 -> 主线程(Tick/Jackets.Pump)从游戏资源里取贴图并编码 PNG。
    /// 所有 Unity API 都在主线程调用(HTTP 线程碰 Unity 会偶发崩)。
    /// 贴图是 AssetBundle 里来的, 通常不可读 -> 走 RenderTexture 回读, 顺便缩到 512px 以内省带宽。
    /// </summary>
    internal static class Jackets
    {
        private const int MaxPx = 512;

        private sealed class Job
        {
            public int Id;
            public bool Small;
            public byte[] Png;
            public bool Done;
            public readonly object Lock = new object();
        }

        private static readonly Dictionary<string, byte[]> _cache = new Dictionary<string, byte[]>();
        private static readonly List<Job> _queue = new List<Job>();
        private static readonly object _lock = new object();
        /// <summary>正在排队/处理中的封面(同一张图合并请求, 避免 400 行各排一次队)</summary>
        private static readonly Dictionary<string, Job> _inflight = new Dictionary<string, Job>();
        private static int _failed;

        internal static byte[] Get(int id, bool small, int timeoutMs)
        {
            if (!Config.JacketService || id <= 0)
            {
                return null;
            }
            string key = id + (small ? "_s" : "_f");
            lock (_lock)
            {
                byte[] hit;
                if (_cache.TryGetValue(key, out hit))
                {
                    return hit;
                }
                if (_queue.Count > 80)
                {
                    return null; // 页面一次要太多图, 后面的直接放弃, 别把游戏拖死
                }
            }
            Job job = new Job();
            job.Id = id;
            job.Small = small;
            lock (_lock)
            {
                Job pendingJob;
                if (_inflight.TryGetValue(key, out pendingJob))
                {
                    job = pendingJob;          // 同一张图已经在排队 -> 合并, 不再重复入队
                }
                else
                {
                    _inflight[key] = job;
                    _queue.Add(job);
                }
            }
            lock (job.Lock)
            {
                if (!job.Done)
                {
                    System.Threading.Monitor.Wait(job.Lock, timeoutMs);
                }
            }
            return job.Png;
        }

        /// <summary>主线程每帧处理几张(限流, 别卡游戏)</summary>
        internal static void Pump(int max)
        {
            for (int i = 0; i < max; i++)
            {
                Job job = null;
                lock (_lock)
                {
                    if (_queue.Count > 0)
                    {
                        job = _queue[0];
                        _queue.RemoveAt(0);
                    }
                }
                if (job == null)
                {
                    return;
                }
                byte[] png = null;
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
                if (png != null)
                {
                    lock (_lock)
                    {
                        if (_cache.Count > 400)
                        {
                            _cache.Clear();
                        }
                        _cache[job.Id + (job.Small ? "_s" : "_f")] = png;
                    }
                }
                lock (_lock)
                {
                    _inflight.Remove(job.Id + (job.Small ? "_s" : "_f"));
                }
                lock (job.Lock)
                {
                    job.Png = png;
                    job.Done = true;
                    System.Threading.Monitor.PulseAll(job.Lock);
                }
            }
        }

        internal static void ClearCache()
        {
            lock (_lock)
            {
                _cache.Clear();
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
            Texture2D readable = ToReadable(tex);
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

        /// <summary>把任意贴图弄成可编码的 Texture2D(必要时 GPU 回读 + 缩到 MaxPx)</summary>
        private static Texture2D ToReadable(Texture2D src)
        {
            int w = Mathf.Min(src.width, MaxPx);
            int h = Mathf.Min(src.height, MaxPx);
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
