using System;
using System.Collections.Generic;
using System.Threading;

namespace SongRequestMod
{
    /// <summary>
    /// 网页线程 -> 游戏主线程 的派工器。
    /// 游戏对象 / Unity API / 我们自己的曲目表只允许在主线程读写; 网页线程要用时把活交给这里,
    /// 由主线程在 Mod.Tick 里执行, 网页线程最多等 timeoutMs 毫秒拿结果(超时就用 fallback)。
    /// </summary>
    internal static class MainThread
    {
        private static int _mainId = -1;

        private sealed class Job
        {
            public Func<object> Work;
            public object Result;
            public bool Done;
        }

        private static readonly Queue<Job> _queue = new Queue<Job>();
        private static readonly object _lock = new object();

        /// <summary>在主线程(OnInitializeMelon)里调一次, 记下主线程 id</summary>
        internal static void Init()
        {
            _mainId = Thread.CurrentThread.ManagedThreadId;
        }

        internal static bool IsMain
        {
            get { return Thread.CurrentThread.ManagedThreadId == _mainId; }
        }

        /// <summary>在主线程执行 work 并等结果; 本身就在主线程时直接执行</summary>
        internal static T Run<T>(Func<T> work, int timeoutMs, T fallback)
        {
            if (IsMain)
            {
                return work();
            }
            Job job = new Job();
            job.Work = delegate { return work(); };
            lock (_lock)
            {
                if (_queue.Count > 32)
                {
                    return fallback;   // 主线程忙不过来(或还没开始 Tick), 别无限排队
                }
                _queue.Enqueue(job);
            }
            lock (job)
            {
                if (!job.Done)
                {
                    System.Threading.Monitor.Wait(job, timeoutMs);
                }
                return job.Done && job.Result is T ? (T)job.Result : fallback;
            }
        }

        /// <summary>主线程每帧调用: 执行排队的活(每帧最多 4 个, 别把一帧拖长)</summary>
        internal static void Pump()
        {
            for (int i = 0; i < 4; i++)
            {
                Job job;
                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }
                    job = _queue.Dequeue();
                }
                object result = null;
                try
                {
                    result = job.Work();
                }
                catch (Exception e)
                {
                    ModLog.Info("[SongRequest] 主线程任务异常: " + e.Message);
                }
                lock (job)
                {
                    job.Result = result;
                    job.Done = true;
                    System.Threading.Monitor.PulseAll(job);
                }
            }
        }
    }
}

namespace SongRequestMod
{
    /// <summary>
    /// 几条专用的后台线程, 给"把结果写回网页"这种可能被慢客户端卡住的活用。
    /// 不用 ThreadPool: HttpListener 自己收连接也靠线程池, 线程池被几个卡住的写操作占满时,
    /// 新请求会连不进来, 整个点歌台看起来像死了一样。
    /// </summary>
    internal static class Background
    {
        private const int Workers = 4;
        private static readonly System.Collections.Generic.Queue<System.Action> _queue =
            new System.Collections.Generic.Queue<System.Action>();
        private static readonly object _lock = new object();
        private static bool _started;

        internal static void Run(System.Action work)
        {
            lock (_lock)
            {
                if (!_started)
                {
                    _started = true;
                    for (int i = 0; i < Workers; i++)
                    {
                        System.Threading.Thread th = new System.Threading.Thread(Loop);
                        th.IsBackground = true;
                        th.Start();
                    }
                }
                _queue.Enqueue(work);
                System.Threading.Monitor.Pulse(_lock);
            }
        }

        private static void Loop()
        {
            while (true)
            {
                System.Action work;
                lock (_lock)
                {
                    while (_queue.Count == 0)
                    {
                        System.Threading.Monitor.Wait(_lock);
                    }
                    work = _queue.Dequeue();
                }
                try
                {
                    work();
                }
                catch
                {
                }
            }
        }
    }
}
