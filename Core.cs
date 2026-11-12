using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;

// The loader lines (see Loader.cs). A BepInEx build (-p:Loader=BepInEx) uses the [BepInPlugin] on Core instead.
#if !BEPINEX
[assembly: MelonLoader.MelonInfo(typeof(EvoCore.Core), "EvoCore", "0.15.2", "Claude")]
[assembly: MelonLoader.MelonGame("Moonfire Entertainment", "Starship EVO")]
[assembly: MelonLoader.MelonPriority(-1000)] // before every mod that uses it, so its frame bookkeeping runs first
#endif

namespace EvoCore
{
    // Matched by name by the ModSettings mod, so this mod needs no reference to it.
    [AttributeUsage(AttributeTargets.Method)]
    class ModSettingsTabAttribute : Attribute
    {
        public ModSettingsTabAttribute(string title) { }
    }

    // What the other mods stand on: per-save data (Saves), the id registry (Ids), Lua scripts on ships (Lua), homes for simulation work with a
    // frame budget and worker threads (Sim), and frame timing (Perf). See README.md for how a mod uses each.
#if BEPINEX
    [BepInEx.BepInPlugin("evo.core", "EvoCore", "0.15.2")]
#endif
    public class Core : EvoMod
    {
        public static new ModLog Log;

        static Settings settings;
        static Setting<float> frameBudget;
        static Setting<bool> overlay;
        static Setting<int> overlayLines;
        static Setting<bool> versionWarning;
        static Setting<bool> graphAutoRun;
        static Setting<bool> colourHeaders;

        public static float FrameBudgetMs => frameBudget?.Value ?? 2f;
        public static int OverlayLines => overlayLines?.Value ?? 6;
        public static bool VersionWarning => versionWarning?.Value ?? true;
        public static bool ColourHeaders => colourHeaders?.Value ?? true;
        public static bool GraphAutoRun
        {
            get => graphAutoRun?.Value ?? false;
            set { if (graphAutoRun != null) graphAutoRun.Value = value; }
        }

        static int mainThread;

        // Before any mod's Harmony patches: see Limits.
        protected override void EarlyInit()
        {
            Log = base.Log;
            Limits.Grow();
        }

        protected override void Init()
        {
            Log = base.Log;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            settings = Settings.For("EvoCore", "EvoCore");
            frameBudget = settings.Add("FrameBudgetMs", 2f, "Milliseconds per frame shared by all sliced simulation work");
            overlay = settings.Add("ShowOverlay", false, "Show frame timing per mod in the top right corner");
            overlayLines = settings.Add("OverlayLines", 6, "How many of the costliest mods the overlay lists");
            versionWarning = settings.Add("VersionWarning", true, "Show a notice when the game isn't the build these mods were made for");
            colourHeaders = settings.Add("ColourHeaders", true, "Circuit editor: fill each node's title bar with its group's colour (off: a thin stripe under the name)");
            graphAutoRun = settings.Add("GraphAutoRun", false, "Circuit editor: save and rerun the circuit a moment after each change (a rerun starts its timers and counters again)");
            Lua.Init();
            Monitors.Init();
            Anim.Init();
            Help.Init();
        }

        protected override void AllLoaded() => Perf.HookMods(Harmony);

        protected override void Tick()
        {
            Perf.EndFrame();
            GameVersion.Tick();
            Sim.Update();
            Lua.Update();
            Mainframes.Tick();
            Anim.Update();
        }

        protected override void LateTick()
        {
            Graphs.Canvas.LateTick();
            if (LinkPicker.IsOpen && !GameLook.Ready) GameLook.Prepare();
            Monitors.LateTick();
        }

        protected override void Gui()
        {
            if (overlay.Value && Event.current.type == EventType.Repaint) Perf.DrawOverlay();
            Graphs.Canvas.OnGui();
            LinkPicker.OnGui();
            GameVersion.Gui();
        }

        // ---- Status, for the Core tab and the DevBridge smoke test (get EvoCore.Core.Report()) ----

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Mods: " + string.Join(", ", Mods.Loaded.Select(m => $"{m.Name} {m.Version}").ToArray()));
            sb.AppendLine(GameVersion.Status());
            sb.AppendLine(Saves.Active ? $"Save: \"{Saves.Character}\", data in {Saves.Root}": "Save: none loaded");
            if (Saves.LastEvent != "") sb.AppendLine("  " + Saves.LastEvent);
            if (Saves.Failures > 0) sb.AppendLine($"  {Saves.Failures} save handler(s) have failed; see the log");
            sb.AppendLine(Limits.Status());
            sb.AppendLine(Looks.Report());
            foreach (var t in Ids.Check())
            {
                sb.AppendLine($"{t.Space} table: {t.Used} of {t.Size} used ({t.Stock} game, {t.Claimed} registered, {t.Strangers.Count} other), {t.Free} free");
                foreach (var p in t.Problems) sb.AppendLine("  PROBLEM " + p);
                if (t.Strangers.Count > 0) sb.AppendLine("  not registered: " + string.Join(", ", t.Strangers.ToArray()));
            }
            foreach (var p in Ids.Problems) sb.AppendLine("  registry: " + p);
            sb.AppendLine($"Sim: {Sim.StepsLastFrame} slice step(s) last frame in {Sim.LastSliceMs:0.00} of {FrameBudgetMs:0.#} ms, {Sim.BackgroundRunning} job(s) running");
            sb.Append(Perf.Summary(OverlayLines));
            return sb.ToString();
        }

        public static string SelfTestResult = "not run";

        // Exercises each piece: a background job's round trip, sliced work under the budget and, with a save loaded,
        // a write and read of save data. The answer lands in SelfTestResult within a second or so.
        public static string SelfTest()
        {
            SelfTestResult = "running";
            var failures = new List<string>();
            if (Saves.Active)
            {
                var stamp = DateTime.Now.Ticks.ToString();
                Saves.WriteLines("EvoCore", "selftest.txt", new[] { stamp });
                var back = Saves.ReadLines("EvoCore", "selftest.txt");
                if (back == null || back.Length != 1 || back[0] != stamp) failures.Add("save data didn't read back");
            }
            var problems = Ids.Check().Sum(t => t.Problems.Count);
            if (problems > 0) failures.Add($"{problems} id problem(s)");

            Sim.Stop("selftest");
            Sim.Stop("selftest check");
            int sliced = 0;
            Sim.Sliced("selftest", () => ++sliced < 1000);
            var started = Time.frameCount;
            Sim.Background("selftest", () =>
            {
                if (Thread.CurrentThread.ManagedThreadId == mainThread) throw new Exception("ran on the main thread");
                long sum = 0;
                for (int i = 1; i <= 1000000; i++) sum += i;
                return sum;
            }, sum =>
            {
                if (sum != 500000500000L) failures.Add("job gave the wrong sum");
                if (Thread.CurrentThread.ManagedThreadId != mainThread) failures.Add("job result came back off the main thread");
                Sim.Every("selftest check", 0.5f, () =>
                {
                    if (SelfTestResult != "running") return;
                    if (sliced < 1000) failures.Add($"sliced work stalled at {sliced}");
                    Sim.Stop("selftest");
                    Sim.Stop("selftest check");
                    SelfTestResult = (failures.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", failures.ToArray())) +
                                     $" ({Time.frameCount - started} frames)";
                    Log.Msg("Self-test: " + SelfTestResult);
                });
            });
            return "started";
        }

        static Vector2 scroll;

        [ModSettingsTab("Core")]
        static void DrawTab()
        {
            overlay.Value = GUILayout.Toggle(overlay.Value, " Show frame timing overlay");
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Simulation budget: {FrameBudgetMs:0.0} ms per frame", GUILayout.Width(260));
            frameBudget.Value = Mathf.Round(GUILayout.HorizontalSlider(frameBudget.Value, 0.5f, 8f, GUILayout.Width(200)) * 2f) / 2f;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Run self-test", GUILayout.Width(140))) SelfTest();
            GUILayout.Label("Self-test: " + SelfTestResult);
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label(Report());
            GUILayout.Label("<b>Id registry</b>");
            foreach (var c in Ids.Claims.OrderBy(c => c.Space).ThenBy(c => c.Id))
                GUILayout.Label($"  {c.Space} {c.Id}  {c.Key}" + (c.ThisSession ? "" : "  (not claimed this run)"));
            GUILayout.EndScrollView();
        }
    }
}
