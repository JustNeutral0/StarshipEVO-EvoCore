using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EvoCore
{
    // Where frame time goes. Every loaded mod's OnUpdate, OnLateUpdate, OnFixedUpdate and OnGUI is timed (the UCS
    // loader's too), plus the scheduler's slices, save handlers and background jobs. Time a mod spends in its own
    // Harmony patches on game methods isn't counted: that shows up as game time.
    public static class Perf
    {
        public class Counter
        {
            public string Name;
            public bool Background;                 // a worker thread's time: costs no frame time
            internal double frameMs, windowMs, windowPeak;
            internal int windowCalls;
            public double AvgMs, PeakMs;            // per frame over the last second (per call for background work)
            public int Calls;                       // in the last second
        }

        static readonly Dictionary<string, Counter> counters = new Dictionary<string, Counter>();
        static readonly Dictionary<MethodBase, Counter> byMethod = new Dictionary<MethodBase, Counter>();
        static readonly object gate = new object();
        static int windowFrames;
        static float windowStart, windowWorstFrame;

        public static float Fps { get; private set; }
        public static float WorstFrameMs { get; private set; }
        public static double ModMsPerFrame { get; private set; }
        public static IEnumerable<Counter> Counters { get { lock (gate) return counters.Values.ToList(); } }

        public static void Record(string name, double ms, bool background = false)
        {
            lock (gate)
            {
                if (!counters.TryGetValue(name, out var c)) counters[name] = c = new Counter { Name = name, Background = background };
                c.frameMs += ms;
                c.windowCalls++;
            }
        }

        // Wraps every mod's per-frame callbacks. Called once all mods are loaded.
        public static void HookMods(HarmonyLib.Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(Perf).GetMethod(nameof(Before), BindingFlags.NonPublic | BindingFlags.Static));
            var postfix = new HarmonyMethod(typeof(Perf).GetMethod(nameof(After), BindingFlags.NonPublic | BindingFlags.Static));
            // Mods on the layer are timed by their own Tick/LateTick/FixedTick/Gui overrides, the rest by the loader's names.
            var labels = new[] { "Update", "LateUpdate", "FixedUpdate", "GUI" };
            foreach (var mod in Mods.Loaded)
            {
                var names = mod.Instance is EvoMod ? EvoMod.FrameMethods : Mods.LegacyFrameMethods;
                for (int i = 0; i < names.Length; i++)
                {
                    var method = mod.Instance.GetType().GetMethod(names[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (method == null) continue;
                    try
                    {
                        byMethod[method] = new Counter { Name = $"{mod.Name} {labels[i]}" };
                        harmony.Patch(method, prefix, postfix);
                    }
                    catch (Exception e) { Core.Log.Warning($"Can't time {mod.Name}.{names[i]}: {e.Message}"); }
                }
            }
            lock (gate) foreach (var c in byMethod.Values) counters[c.Name] = c;
        }

        static void Before(out long __state) => __state = Stopwatch.GetTimestamp();

        static void After(long __state, MethodBase __originalMethod)
        {
            if (!byMethod.TryGetValue(__originalMethod, out var c)) return;
            var ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            lock (gate) { c.frameMs += ms; c.windowCalls++; }
        }

        // Once a frame, first thing (EvoCore updates before the other mods): closes the last frame's sums.
        internal static void EndFrame()
        {
            windowFrames++;
            windowWorstFrame = Mathf.Max(windowWorstFrame, Time.unscaledDeltaTime);
            lock (gate)
            {
                foreach (var c in counters.Values)
                {
                    c.windowMs += c.frameMs;
                    if (c.frameMs > c.windowPeak) c.windowPeak = c.frameMs;
                    c.frameMs = 0;
                }
                if (Time.unscaledTime - windowStart < 1f) return;
                var seconds = Time.unscaledTime - windowStart;
                Fps = windowFrames / Mathf.Max(0.001f, seconds);
                WorstFrameMs = windowWorstFrame * 1000f;
                double mods = 0;
                foreach (var c in counters.Values)
                {
                    c.AvgMs = c.Background ? (c.windowCalls == 0 ? 0 : c.windowMs / c.windowCalls) : c.windowMs / windowFrames;
                    c.PeakMs = c.windowPeak;
                    c.Calls = c.windowCalls;
                    if (!c.Background) mods += c.AvgMs;
                    c.windowMs = c.windowPeak = 0;
                    c.windowCalls = 0;
                }
                ModMsPerFrame = mods;
                windowStart = Time.unscaledTime;
                windowFrames = 0;
                windowWorstFrame = 0;
            }
        }

        public static string Summary(int top) =>
            $"{Fps:0} fps, worst frame {WorstFrameMs:0} ms, mods {ModMsPerFrame:0.00} ms/frame" +
            string.Concat(Counters.Where(c => !c.Background && c.Calls > 0).OrderByDescending(c => c.AvgMs).Take(top)
                .Select(c => $"\n  {c.Name}: {c.AvgMs:0.00} ms (peak {c.PeakMs:0.0})").ToArray()) +
            string.Concat(Counters.Where(c => c.Background && c.Calls > 0).OrderByDescending(c => c.AvgMs).Take(top)
                .Select(c => $"\n  {c.Name}: {c.AvgMs:0.0} ms per run, {c.Calls} run(s), off the main thread").ToArray());

        static GUIStyle style;

        internal static void DrawOverlay()
        {
            if (style == null) style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 12, richText = false };
            var text = Summary(Core.OverlayLines);
            var size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(Screen.width - size.x - 12, 40, size.x + 4, size.y + 2), text, style);
        }
    }
}
