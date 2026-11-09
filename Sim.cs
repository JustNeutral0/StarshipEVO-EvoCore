using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace EvoCore
{
    // Shared homes for simulation work, so a growing universe can't eat the frame:
    //  - Every: a tick on a timer, timed in the overlay.
    //  - Sliced: work done in small steps, as many as fit in a shared per-frame budget (Core > FrameBudgetMs), taking
    //    turns between mods. A step does a bit and says whether more is waiting this frame.
    //  - Background: pure work on a worker thread, whose result is handed back on the main thread. The work must not
    //    touch Unity objects or game state the main thread changes; copy what it needs in first.
    public static class Sim
    {
        class Ticker { public string Name; public float Interval, Next; public Action Tick; }
        class Slicer { public string Name; public Func<bool> Step; public bool Waiting = true; }

        static readonly List<Ticker> tickers = new List<Ticker>();
        static readonly List<Slicer> slicers = new List<Slicer>();
        static readonly Queue<Action> finished = new Queue<Action>();
        static int next, running;

        public static int BackgroundRunning => running;
        public static double LastSliceMs { get; private set; }
        public static int StepsLastFrame { get; private set; }

        public static void Every(string name, float seconds, Action tick) =>
            tickers.Add(new Ticker { Name = name, Interval = seconds, Next = Time.unscaledTime + seconds, Tick = tick });

        public static void Sliced(string name, Func<bool> step) => slicers.Add(new Slicer { Name = name, Step = step });

        public static void Stop(string name)
        {
            tickers.RemoveAll(t => t.Name == name);
            slicers.RemoveAll(s => s.Name == name);
        }

        public static void Background<T>(string name, Func<T> work, Action<T> done)
        {
            Interlocked.Increment(ref running);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var watch = Stopwatch.StartNew();
                T result = default;
                Exception error = null;
                try { result = work(); }
                catch (Exception e) { error = e; }
                Perf.Record("job " + name, watch.Elapsed.TotalMilliseconds, background: true);
                lock (finished)
                    finished.Enqueue(() =>
                    {
                        Interlocked.Decrement(ref running);
                        if (error != null) { Core.Log.Error($"Background job {name} failed: {error}"); return; }
                        var applyWatch = Stopwatch.StartNew();
                        try { done(result); }
                        catch (Exception e) { Core.Log.Error($"Background job {name} result failed: {e}"); }
                        Perf.Record("job " + name + " (apply)", applyWatch.Elapsed.TotalMilliseconds);
                    });
            });
        }

        internal static void Update()
        {
            while (true)
            {
                Action apply;
                lock (finished) { if (finished.Count == 0) break; apply = finished.Dequeue(); }
                apply();
            }

            foreach (var t in tickers.ToArray())
            {
                if (Time.unscaledTime < t.Next) continue;
                t.Next = Time.unscaledTime + t.Interval;
                var watch = Stopwatch.StartNew();
                try { t.Tick(); }
                catch (Exception e) { Core.Log.Error($"{t.Name} tick failed: {e}"); }
                Perf.Record("tick " + t.Name, watch.Elapsed.TotalMilliseconds);
            }

            // Round robin, starting after whoever went first last frame, until the budget is gone or nobody has work.
            var budget = Core.FrameBudgetMs;
            var frame = Stopwatch.StartNew();
            int steps = 0, idle = 0;
            foreach (var s in slicers) s.Waiting = true;
            while (slicers.Count > 0 && idle < slicers.Count && frame.Elapsed.TotalMilliseconds < budget)
            {
                var s = slicers[next = (next + 1) % slicers.Count];
                if (!s.Waiting) { idle++; continue; }
                var watch = Stopwatch.StartNew();
                try { s.Waiting = s.Step(); }
                catch (Exception e) { s.Waiting = false; Core.Log.Error($"{s.Name} step failed: {e}"); }
                Perf.Record("slice " + s.Name, watch.Elapsed.TotalMilliseconds);
                steps++;
                idle = s.Waiting ? 0 : idle + 1;
            }
            LastSliceMs = frame.Elapsed.TotalMilliseconds;
            StepsLastFrame = steps;
        }
    }
}
