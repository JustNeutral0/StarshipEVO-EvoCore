using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Lua scripts on ships (MoonSharp, a Lua interpreter in plain C#, shipped in UserLibs). Two kinds of host:
    //  - a Lua Computer block (LuaComputer.cs) runs scripts\<its Tag label>.lua, with what's linked into it as
    //    `inputs` and what it's linked to as `outputs`, and on_input(i, value) called when an input changes;
    //  - a text screen: [lua: name] runs UserData\EvoCore\scripts\name.lua; text starting with --lua is the script.
    // Files reload whenever they change (write them in any editor); inline scripts when the text is edited.
    // The script's body runs once; if it defines tick(dt), that runs every `rate` seconds (a global the script can
    // set; 0.1 by default). Each call may run InstructionLimit Lua steps; a script that goes over is stopped with an
    // error instead of freezing the game. Errors and print() output show in the Scripts tab.
    // Sandboxed (MoonSharp's soft sandbox): no files, no OS, no loading code. What a script sees:
    //  - ship: name, speed (m/s), mass, fuel, power, heat, hull, shield (levels 0-1, plus raw amounts), stalled,
    //    warping, cruising, planet, altitude, target (nil, or name/distance/speed), block(label) and blocks(label)
    //    (logic blocks found by their Tag label: name, kind, type, state, on, set(v), toggle(), read(name), values();
    //    see Blocks.cs and Readouts.cs); screen: text (what the screen shows; setting it changes only the picture, not
    //    the saved text; screen hosts only); time (seconds since start);
    //  - computers: inputs and outputs (count, get(i), on(i), name(i), block(i); outputs also set(i, value) and all(value),
    //    value a number or true/false; i a port name or a number) and ports (named link slots, two-way channels: Ports.cs);
    //  - pilot: flight input added to yours while you fly the ship (Pilot.cs);
    //  - display / displays: monitors the computer is linked to, drawn on by the script (Monitors.cs);
    //  - anim: poses and clips for the ship's rotators, hinges, pistons and rails (Animation.cs);
    //  - everything in lib\*.lua (loaded first; the stock ui.lua has ready-made gauges, meters, reticles...);
    //  - whatever mods add with Expose (e.g. Cockpit Displays' displays). Members are lower case.
    public static partial class Lua
    {
        // What a mod's API object is made for: one script, on one screen, on one ship.
        public class Context
        {
            public string Name;
            public ParentEntity Ship;
            public Command Host;               // the screen or computer running the script
            public CommandInputText Screen;    // null for a computer
            // Raised when the script stops (screen gone, script changed or failed), so APIs can undo what it set.
            public event Action Stopped;
            internal void Stop() { try { Stopped?.Invoke(); } catch (Exception e) { Core.Log.Error($"Lua {Name}: stop handler failed: {e}"); } }
        }

        // Mods add a global to every script: make is called once per script with its context and returns a plain C#
        // object, whose public members the script can use (lower case names read best in Lua).
        public static void Expose(string global, Func<Context, object> make) => modules[global] = make;

        public static int InstructionLimit = 200000;
        public static string ScriptsDir => Path.Combine(Path.Combine(Paths.UserData, "EvoCore"), "scripts");
        // Shared Lua (lib\*.lua, like the ui presets) that loads into every script before the script itself.
        public static string LibDir => Path.Combine(Path.Combine(Paths.UserData, "EvoCore"), "lib");
        static long libVersion;
        static string[] libFiles = new string[0];

        class Running
        {
            public Context Context;
            public string File, Source;
            public DateTime FileTime;
            public Script Script;
            public float Next;
            public string Status = "starting", Error;
            public readonly List<string> Output = new List<string>();
            public readonly List<KeyValuePair<string, string>> Watched = new List<KeyValuePair<string, string>>();
            public double Ms;
            public readonly Dictionary<string, float> LastInputs = new Dictionary<string, float>();
            public bool InputsPrimed;
            public int NodeVersion;   // Graphs.Version when it started: a graph restarts when a node changes
            public long LibVersion;   // libVersion when it started: every script restarts when a lib file changes
            public double StepsPerSecond;   // Lua steps it ran over the last second (counted in StepChunk lots)
            public long WindowSteps;
            public float WindowStart;
        }

        // Steps are counted by letting a call pause every StepChunk steps (MoonSharp has no step counter); a call that
        // runs past its limit (InstructionLimit, more for a mainframe) is stopped.
        const int StepChunk = 1000;

        // Lua steps a computer ran over the last second (0 when it isn't running): the load a mainframe measures.
        internal static double StepsPerSecond(Command host) => running.TryGetValue(host, out var r) && r.Script != null ? r.StepsPerSecond : 0.0;

        static readonly Dictionary<string, Func<Context, object>> modules = new Dictionary<string, Func<Context, object>>();
        static readonly Dictionary<Command, Running> running = new Dictionary<Command, Running>();
        static readonly Regex fileTag = new Regex(@"\[\s*lua\s*:?\s*([\w\-. ]+?)\s*\]", RegexOptions.IgnoreCase);
        static float nextScan;
        static bool ready;

        internal static void Init()
        {
            try
            {
                UserData.RegistrationPolicy = MoonSharp.Interpreter.Interop.InteropRegistrationPolicy.Automatic;
                ExposePilot();
                ExposePorts();
                ExposeControls();
                ExposePlayer();
                Mainframes.Expose();
                Buses.Expose();
                Apps.Expose();
                Media.Expose();
                ExposeUpgrades();
                BoardStore.Init();
                Directory.CreateDirectory(ScriptsDir);
                Graphs.AdoptOld();   // before the stock files, so a renamed stock template isn't written twice
                WriteStockLibs();
                var example = Path.Combine(ScriptsDir, "example.lua");
                if (!System.IO.File.Exists(example)) System.IO.File.WriteAllText(example, Example);
                var computer = Path.Combine(ScriptsDir, "computer.lua");
                if (!System.IO.File.Exists(computer)) System.IO.File.WriteAllText(computer, ComputerExample);
                var status = Path.Combine(ScriptsDir, "status.lua");
                if (!System.IO.File.Exists(status)) System.IO.File.WriteAllText(status, StatusExample);
                var hold = Path.Combine(ScriptsDir, "hold.lua");
                if (!System.IO.File.Exists(hold)) System.IO.File.WriteAllText(hold, HoldExample);
                var warnings = Path.Combine(ScriptsDir, "warnings.lua");
                if (!System.IO.File.Exists(warnings)) System.IO.File.WriteAllText(warnings, WarningsExample);
                Graphs.WriteExampleNodes();
                ready = true;
            }
            catch (Exception e) { Core.Log.Error("Lua unavailable (is MoonSharp.Interpreter.dll in UserLibs?): " + e); }
        }

        const string ComputerExample =
@"-- The Lua Computer's default script. Label a computer with the multitool's Tag mode to give it a script of its own.
-- Link buttons or switches into it and lights or doors out of it: output i follows input i.
function on_input(i, value)
  print('input ' .. i .. ' (' .. inputs.name(i) .. ') = ' .. value)
  outputs.set(i, value)
end
";

        const string StatusExample =
@"-- Put [lua: status] on a text screen: a ship status readout.
rate = 0.25
local function pct(x) return string.format('%3d%%', math.floor(x * 100 + 0.5)) end
function tick(dt)
  local lines = {
    ship.name,
    string.format('Speed  %.0f m/s', ship.speed),
    'Fuel   ' .. pct(ship.fuel),
    'Power  ' .. pct(ship.power) .. (ship.stalled and '  STALLED' or ''),
    'Heat   ' .. pct(ship.heat),
    'Hull   ' .. pct(ship.hull),
    'Shield ' .. (ship.shield_on and pct(ship.shield) or 'off'),
  }
  if ship.planet ~= '' then
    table.insert(lines, string.format('%s  %.1f km', ship.planet, ship.altitude / 1000))
  end
  local t = ship.target
  if t then table.insert(lines, string.format('Target %s  %.0f m', t.name, t.distance)) end
  screen.text = table.concat(lines, '\n')
end
";

        const string HoldExample =
@"-- For a Lua Computer labelled 'hold', with a switch linked into it. While the switch is on and you're flying,
-- the ship pushes against any drift up/down and sideways (along its own axes), so it hangs in place when you let
-- go of the keys. Your own keys still work on top.
rate = 0.05
local strength = 0.3      -- how hard it pushes back per m/s of drift
function tick(dt)
  if inputs.count > 0 and not inputs.on(1) then pilot.release() return end
  local right, up, forward = ship.velocity()
  pilot.move(-right * strength, -up * strength, 0)
end
";

        const string WarningsExample =
@"-- For a Lua Computer labelled 'warnings': link lights (or alarms) out of it.
-- Output 1: fuel under 20%. Output 2: power under 20%. Output 3: hull under 50%. Output 4: shield down.
rate = 0.5
function tick(dt)
  outputs.set(1, ship.fuel < 0.2)
  outputs.set(2, ship.power < 0.2)
  outputs.set(3, ship.hull < 0.5)
  outputs.set(4, not ship.shield_on)
end
";

        const string Example =
@"-- Put [lua: example] on a text screen to run this. Saving this file reloads it.
print('hello from ' .. ship.name)
rate = 0.5
function tick(dt)
  screen.text = string.format('%s\n%.1f m/s', ship.name, ship.speed)
end
";

        internal static void Update()
        {
            if (!ready) return;
            Graphs.ScanNodes();
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 1f;
                Graphs.AdoptOld();
                try { Scan(); }
                catch (Exception e) { Core.Log.Error("Lua scan failed: " + e); }
            }
            ScanControls();
            try { ScanTouch(); } catch (Exception e) { Core.Log.Error("Touch: " + e); }
            LogicRefit.Update();
            foreach (var r in running.Values.ToArray())
            {
                if (r.Script == null) continue;
                DispatchControls(r);
                if (r.Script == null) continue;
                if (r.Context.Host is LuaComputer computer)
                {
                    CheckInputs(r, computer);
                    if (r.Script != null) Deliver(r, computer);
                }
                if (r.Script == null || Time.unscaledTime < r.Next) continue;
                var tick = r.Script.Globals.Get("tick");
                var rate = r.Script.Globals.Get("rate");
                var interval = rate.Type == DataType.Number ? Mathf.Max(0.02f, (float)rate.Number) : 0.1f;
                // A mainframe over its capacity ticks that much slower; one without power doesn't tick at all.
                if (r.Context.Host is Mainframe mainframe)
                {
                    if (mainframe.Unpowered) { r.Next = Time.unscaledTime + 0.5f; continue; }
                    interval *= Mathf.Max(1f, mainframe.Load);
                }
                // Clock chips on its board (Parts.cs) make it tick faster.
                if (r.Context.Host is LuaComputer clocked) interval = Mathf.Max(0.02f, interval / Parts.Speed(clocked));
                // The first tick gets one interval, not the time since the game started.
                var dt = r.Next <= 0f ? interval : Time.unscaledTime - (r.Next - interval);
                r.Next = Time.unscaledTime + interval;
                if (tick.Type != DataType.Function) continue;
                Call(r, tick, DynValue.NewNumber(Mathf.Max(0f, dt)));
            }
        }

        // Start, reload and stop scripts to match the screens and computers in the world.
        static void Scan()
        {
            var seen = new HashSet<Command>();
            try
            {
                libFiles = Directory.Exists(LibDir) ? Directory.GetFiles(LibDir, "*.lua").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray() : new string[0];
                long v = libFiles.Length;
                foreach (var f in libFiles) v = v * 31 + System.IO.File.GetLastWriteTimeUtc(f).Ticks + f.GetHashCode();
                libVersion = v;
            }
            catch (Exception e) { Core.Log.Error("Lua lib scan failed: " + e.Message); }
            foreach (var command in CommandManager.commands.Datas)
            {
                if (!(command is Command host) || host.brickCoord.entity == null) continue;
                string file = null, source = null;
                if (host is LuaComputer computer)
                {
                    // A terminal runs nothing itself: its mainframe draws on its monitors (Mainframe.cs). Expansion
                    // Slots and PC Workstations (Expansion.cs) never run a program.
                    if (Mainframes.TerminalOf(computer) != null || computer is ExpansionSlot || computer is Workstation || computer is SalvageProcessor) continue;
                    if (computer.HasProgram)
                    {
                        // Its own program (ComputerPrograms.cs), through its working file.
                        ClaimId(computer);
                        try { file = Mirror(computer); }
                        catch (Exception e) { Core.Log.Error("Lua: computer program file: " + e.Message); continue; }
                    }
                    else
                    {
                        // A node graph (Graphs.cs) wins over a script of the same name.
                        var graph = Path.Combine(ScriptsDir, computer.ScriptName + ".circuit");
                        file = System.IO.File.Exists(graph) ? graph : Path.Combine(ScriptsDir, computer.ScriptName + ".lua");
                    }
                }
                else if (host is CommandInputText screen)
                {
                    var text = screen.data.text ?? "";
                    var m = fileTag.Match(text);
                    if (text.TrimStart().StartsWith("--lua")) source = text;
                    else if (m.Success) file = Path.Combine(ScriptsDir, m.Groups[1].Value.Trim() + ".lua");
                    else continue;
                }
                else continue;
                seen.Add(host);
                var time = file != null && System.IO.File.Exists(file) ? System.IO.File.GetLastWriteTimeUtc(file) : default;
                running.TryGetValue(host, out var r);
                var stale = r != null && file != null && file.EndsWith(".circuit", StringComparison.OrdinalIgnoreCase) && r.NodeVersion != Graphs.Version || r != null && r.LibVersion != libVersion;
                if (r != null && r.File == file && r.Source == source && r.FileTime == time && !stale) continue;
                if (r != null) Stop(host, r);
                Start(host, file, source, time);
            }
            foreach (var gone in running.Keys.Where(s => !seen.Contains(s)).ToList()) Stop(gone, running[gone]);
        }

        static void Start(Command host, string file, string source, DateTime time)
        {
            var name = file != null ? Path.GetFileNameWithoutExtension(file) : "inline";
            var r = new Running
            {
                Context = new Context { Name = name, Ship = host.brickCoord.entity.Parent, Host = host, Screen = host as CommandInputText },
                File = file, Source = source, FileTime = time, NodeVersion = Graphs.Version, LibVersion = libVersion,
            };
            running[host] = r;
            if (file != null && !System.IO.File.Exists(file)) { r.Status = "no such file: " + file; return; }
            var code = file != null ? System.IO.File.ReadAllText(file) : source;
            if (file != null && file.EndsWith(".circuit", StringComparison.OrdinalIgnoreCase))
            {
                try { code = Graphs.Compile(code, name); }
                catch (Graphs.GraphException e) { r.Error = e.Message; r.Status = "circuit error: " + e.Message; return; }
            }
            var script = new Script(CoreModules.Preset_SoftSandbox);
            script.Options.DebugPrint = s => Print(r, s);
            script.Globals["ship"] = new ShipApi(r.Context);
            script.Globals["screen"] = new ScreenApi(r.Context);
            script.Globals["time"] = (Func<double>)(() => Time.time);
            if (host is LuaComputer c)
            {
                script.Globals["inputs"] = new LinksApi(c, false);
                script.Globals["outputs"] = new LinksApi(c, true);
                script.Globals["watch"] = (Action<string, DynValue>)((label, value) => Watch(r, label, value));
            }
            foreach (var pair in modules)
            {
                try { script.Globals[pair.Key] = pair.Value(r.Context); }
                catch (Exception e) { Core.Log.Error($"Lua: making {pair.Key} for {name} failed: {e}"); }
            }
            r.Script = script;
            r.Status = "running";
            foreach (var lib in libFiles)
            {
                var libName = "lib/" + Path.GetFileName(lib);
                try { Call(r, script.LoadString(System.IO.File.ReadAllText(lib), null, libName)); }
                catch (InterpreterException e) { Fail(r, libName + ": " + (e.DecoratedMessage ?? e.Message)); }
                catch (Exception e) { Fail(r, libName + ": " + e.Message); }
                if (r.Script == null) return;
            }
            DynValue chunk;
            try { chunk = script.LoadString(code, null, name); }
            catch (InterpreterException e) { Fail(r, e.DecoratedMessage ?? e.Message); return; }
            Call(r, chunk);
        }

        // One call into the script as a coroutine that MoonSharp suspends after InstructionLimit steps: a script that
        // gets there is stopped.
        static void Call(Running r, DynValue function, params DynValue[] args)
        {
            var watch = Stopwatch.StartNew();
            var limit = r.Context.Host is Mainframe m ? m.StepLimit : InstructionLimit;
            long chunks = 0;
            try
            {
                var co = r.Script.CreateCoroutine(function).Coroutine;
                co.AutoYieldCounter = StepChunk;
                co.Resume(args);
                while (co.State == CoroutineState.ForceSuspended && ++chunks * StepChunk < limit) co.Resume();
                if (co.State == CoroutineState.ForceSuspended)
                    Fail(r, $"ran more than {limit} steps in one go (an endless loop?), so it was stopped");
            }
            catch (InterpreterException e) { Fail(r, e.DecoratedMessage ?? e.Message); }
            catch (Exception e) { Fail(r, e.Message); }
            var ms = watch.Elapsed.TotalMilliseconds;
            r.WindowSteps += chunks * StepChunk + StepChunk / 2;   // full chunks, and the last one part used: count it half
            if (Time.unscaledTime - r.WindowStart >= 1f)
            {
                var span = Mathf.Max(1f, Time.unscaledTime - r.WindowStart);
                r.StepsPerSecond = r.WindowStart <= 0f ? r.WindowSteps : r.WindowSteps / span;
                r.WindowSteps = 0;
                r.WindowStart = Time.unscaledTime;
            }
            r.Ms = r.Ms * 0.9 + ms * 0.1;
            Perf.Record("lua " + r.Context.Name, ms);
        }

        static void Fail(Running r, string error)
        {
            r.Error = error;
            r.Status = "stopped: " + error;
            r.Script = null;
            Core.Log.Warning($"Lua {r.Context.Name} on {r.Context.Ship?.EntityName}: {error}");
            r.Context.Stop();
        }

        static void Stop(Command host, Running r)
        {
            running.Remove(host);
            r.Script = null;
            r.Context.Stop();
            // Put a screen's own text back, in case the script changed what it showed.
            if (host is CommandInputText screen && screen.textMeshPro != null) screen.textMeshPro.SetText(screen.data.text);
        }

        // Calls on_input(i, value) for each input whose state changed since the last frame, and when links are added
        // or removed, for every input.
        // on_input(i, value) for each input that changed: by port name once input ports are declared, else by link
        // number. The first look only notes the values, as a script starts with the inputs as they are.
        static void CheckInputs(Running r, LuaComputer computer)
        {
            var handler = r.Script.Globals.Get("on_input");
            foreach (var (id, key, value) in InputValues(computer).ToList())
            {
                if (r.Script == null) return;
                var known = r.LastInputs.TryGetValue(key, out var last);
                r.LastInputs[key] = value;
                if (!r.InputsPrimed || known && last == value) continue;
                if (handler.Type == DataType.Function) Call(r, handler, id, DynValue.NewNumber(value));
            }
            r.InputsPrimed = true;
        }

        // For the computer's hover text.
        public static string ComputerStatus(LuaComputer computer)
        {
            if (!ready) return "Lua unavailable";
            if (!running.TryGetValue(computer, out var r)) return $"Lua: {computer.ScriptName}.lua (starting)";
            var last = r.Output.Count > 0 ? "\n" + r.Output[r.Output.Count - 1] : "";
            var file = r.File != null ? Path.GetFileName(r.File) : r.Context.Name;
            return $"Lua: {file}, {r.Status}\n{computer.Inputs.Count} in, {computer.Outputs.Count} out{last}";
        }

        // watch(name, value): a named value shown in the game's info panel (top right) while the computer is picked
        // with the link tool. Calling it again with the same name replaces the value; nil takes the line away.
        const int MaxWatched = 16, MaxWatchedText = 60;

        static void Watch(Running r, string label, DynValue value)
        {
            if (string.IsNullOrEmpty(label)) return;
            label = Clip(label);
            var at = r.Watched.FindIndex(w => w.Key == label);
            if (value == null || value.IsNil()) { if (at >= 0) r.Watched.RemoveAt(at); return; }
            var text = value.Type == DataType.Number ? value.Number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : value.Type == DataType.Boolean ? (value.Boolean ? "on" : "off") : Clip(value.ToPrintString());
            var line = new KeyValuePair<string, string>(label, text);
            if (at >= 0) r.Watched[at] = line;
            else if (r.Watched.Count < MaxWatched) r.Watched.Add(line);
        }

        // Rich text tags would run on into the rest of the panel.
        static string Clip(string s)
        {
            s = s.Replace("<", "").Replace(">", "").Replace("\n", " ").Replace("\r", "");
            return s.Length > MaxWatchedText ? s.Substring(0, MaxWatchedText) + "..." : s;
        }

        // The info panel's lines for a computer: what its script watches, or null for none.
        public static string WatchedText(LuaComputer computer)
        {
            if (!ready || !running.TryGetValue(computer, out var r) || r.Watched.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (var w in r.Watched) sb.Append('\n').Append(w.Key).Append(": ").Append(w.Value);
            return sb.ToString();
        }

        static void Print(Running r, string s)
        {
            r.Output.Add(s);
            if (r.Output.Count > 20) r.Output.RemoveAt(0);
        }

        // ---- What every script gets ----

        // The script's ship, read only. Levels are 0 to 1; amounts are in the game's own units (e for power).
        public class ShipApi
        {
            readonly Context c;
            public ShipApi(Context context) { c = context; }

            EntitySystems Systems => c.Ship?.Systems;
            EntitySystemResources.EntitySystemData Data => Systems != null ? Systems.Data : default;
            static double Ratio(float part, float whole) => whole > 0f ? Mathf.Clamp01(part / whole) : 0.0;

            public string name => c.Ship != null ? c.Ship.EntityName : "";
            public double speed => c.Ship != null && c.Ship.Rigidbody != null ? c.Ship.Rigidbody.LinearVelocity.magnitude : 0.0;
            public double mass => c.Ship != null ? c.Ship.Mass : 0.0;
            // Speed along the ship's right, up and forward axes (three numbers): local right, up, fwd = ship.velocity()
            public DynValue velocity() => LocalVelocity(c.Ship);

            // Fuel: the reactor, engine and mixed tanks together (what the game's fuel gauge shows).
            public double fuel => Ratio(Data.FuelStored, Data.FuelCapacity);
            public double fuel_stored => Data.FuelStored;
            public double fuel_capacity => Data.FuelCapacity;

            // Power: the stored charge, what's coming in, what's being used, and whether the ship has run dry.
            public double power => Ratio(Data.powerStored, Data.powerCapacity);
            public double power_stored => Data.powerStored;
            public double power_capacity => Data.powerCapacity;
            public double power_in => Data.powerOutput;
            public double power_use => Systems != null ? Systems.Power.FlowOut : 0.0;
            public bool stalled => Systems != null && Systems.IsPowerDepleted;

            public double heat => Systems != null ? Ratio(Systems.Heat.stock, Systems.Heat.MaxStock) : 0.0;

            // Hull: integrity as the game counts it (0 means the ship is lost), and the raw points.
            public double hull => Systems != null ? Mathf.Clamp01(Systems.HullIntegrity) : 0.0;
            public double hull_points => Systems != null ? Systems.HullPoint : 0;
            public double hull_max => Systems != null ? Systems.MaxHullPoint : 0;

            public double shield => c.Ship?.Shield != null ? Ratio(c.Ship.Shield.ShieldPoints.stock, c.Ship.Shield.ShieldPoints.MaxStock) : 0.0;
            public bool shield_on => c.Ship?.Shield != null && c.Ship.Shield.IsRaycastShieldOnline;

            public bool warping => Data.IsWarping;
            public bool cruising => Data.IsInLightcruise;

            // The nearest planet, moon or star, and the height above its surface radius (sea level, not the ground
            // under the ship), in metres.
            public string planet => Nearest(out var body, out _) ? body.name : "";
            public double altitude => Nearest(out var body, out var distance) ? distance - body.radius : 0.0;

            bool Nearest(out World.SolarBody body, out float distance)
            {
                body = null;
                distance = float.PositiveInfinity;
                var system = World.PlanetarySystem.currentSystem;
                if (system == null || c.Ship?.SkyTransform == null) return false;
                int id = 0;
                system.GetClosestBody(c.Ship.SkyTransform.SystemPosition, ref id, ref distance);
                if (id < 0 || id >= World.PlanetarySystem.SolarBodies.Count || float.IsInfinity(distance)) return false;
                body = World.PlanetarySystem.SolarBodies[id];
                return body != null;
            }

            // Logic blocks by label (Blocks.cs): block(name) is the first match or nil, blocks(name) all of them in
            // label order (every labelled block with no name).
            public BlockApi block(string name) => FindBlocks(c.Ship, name).FirstOrDefault();
            // A real Lua table (1-based, works with # and ipairs); MoonSharp fills in the execution context itself.
            public Table blocks(ScriptExecutionContext context, string name = null)
            {
                var table = new Table(context.GetScript());
                foreach (var found in FindBlocks(c.Ship, name)) table.Append(UserData.Create(found));
                return table;
            }

            // The ship's selected target: target.name, .distance (metres), .speed; nil with nothing selected.
            public TargetApi target
            {
                get
                {
                    var other = c.Ship?.TargetingSystem?.TargetEntity;
                    return other != null && other != c.Ship ? new TargetApi(c.Ship, other) : null;
                }
            }
        }

        public class TargetApi
        {
            readonly ParentEntity ship, other;
            public TargetApi(ParentEntity from, ParentEntity to) { ship = from; other = to; }
            public string name => other.EntityName;
            public double distance => (other.ParentSystemPosition - ship.ParentSystemPosition).ToVector3().magnitude;
            public double speed => other.Rigidbody != null ? other.Rigidbody.LinearVelocity.magnitude : 0.0;
        }

        public class ScreenApi
        {
            readonly Context c;
            public ScreenApi(Context context) { c = context; }
            public string text
            {
                get => c.Screen?.textMeshPro != null ? c.Screen.textMeshPro.text : "";
                set { if (c.Screen?.textMeshPro != null) c.Screen.textMeshPro.SetText(value ?? ""); }
            }
        }

        // A computer's inputs or outputs, numbered from 1 in the order they were linked.
        public class LinksApi
        {
            readonly LuaComputer computer;
            readonly bool outgoing;
            public LinksApi(LuaComputer host, bool outputs) { computer = host; outgoing = outputs; }

            List<ICommand> All => outgoing ? computer.Outputs : computer.Inputs;

            // i: a port name, or a number (the declared ports' order once ports are declared, else the links' order).
            public int count
            {
                get
                {
                    var d = DeclaredOf(computer);
                    var names = d == null ? null : outgoing ? d.Outputs : d.Inputs;
                    return names != null && names.Count > 0 ? names.Count : All.Count;
                }
            }

            public double get(DynValue i)
            {
                var target = Resolve(computer, outgoing, i, out var link);
                return outgoing ? target?.State ?? 0f : ValueOf(target, link, computer);
            }

            public bool on(DynValue i) => get(i) > 0.5;
            public string name(DynValue i) => Resolve(computer, outgoing, i, out _)?.OverTextIndirect ?? "";
            // The linked block itself, as ship.block() gives it (name, type, read, values, set...), or nil.
            public BlockApi block(DynValue i)
            {
                var target = Resolve(computer, outgoing, i, out _);
                return target != null ? new BlockApi(new Found { Snapshot = target }) : null;
            }

            // Outputs only: send a value (a number from 0 to 1, or true/false for 1/0) down one link or port, or down
            // all links. Numbers outside 0-1 are clamped.
            public void set(DynValue i, object value)
            {
                var target = Resolve(computer, outgoing, i, out _);
                if (!outgoing || target == null) return;
                var state = ToState(value);
                NoteSent(computer, target, state);
                LuaComputer.Send(target, state);
            }

            public void all(object value)
            {
                if (!outgoing) return;
                var state = ToState(value);
                foreach (var target in All)
                {
                    NoteSent(computer, target, state);
                    LuaComputer.Send(target, state);
                }
            }

            // Clamped here as well as in Send, so the value the editor shows as sent is the one that arrived.
            internal static float ToState(object value) => LuaComputer.Signal(
                value is bool b ? (b ? 1f : 0f) : value is double d ? (float)d : value is float f ? f : value is int n ? n : 0f);
        }

        // ---- Status, for the Scripts tab and DevBridge (get EvoCore.Lua.Report()) ----

        public static string Report()
        {
            if (!ready) return "Lua is unavailable; see the log.";
            if (running.Count == 0) return $"No scripts running. Scripts folder: {ScriptsDir}";
            return string.Join("\n", running.Values.Select(r =>
                $"{r.Context.Name} on {r.Context.Ship?.EntityName}: {r.Status} ({r.Ms:0.00} ms)" +
                (r.Output.Count > 0 ? "\n  " + string.Join("\n  ", r.Output.Skip(Math.Max(0, r.Output.Count - 5)).ToArray()) : "")).ToArray());
        }

        // For the graph editor: what's running a file (one line each), and a reload check on the next frame.
        internal static List<string> StatusOf(string file) => running
            .Where(p => p.Value.File != null && string.Equals(Path.GetFullPath(p.Value.File), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{(p.Key is LuaComputer ? "computer" : "screen")} on {p.Value.Context.Ship?.EntityName}: {p.Value.Status} ({p.Value.Ms:0.00} ms)" +
                         (p.Value.Output.Count > 0 ? " | " + p.Value.Output[p.Value.Output.Count - 1] : ""))
            .ToList();

        internal static void RescanSoon() => nextScan = 0f;

        // The stock lib files, scripts, graph nodes and apps (embedded from _ModDev\EvoCore\lib, scripts, nodes and apps) are
        // written when missing, never over edits.
        static void WriteStockLibs()
        {
            WriteStock("EvoCore.lib.", LibDir);
            WriteStock("EvoCore.scripts.", ScriptsDir);
            WriteStock("EvoCore.nodes.", Graphs.NodesDir);
            WriteStock("EvoCore.templates.", Graphs.TemplatesDir, ".circuit");
            WriteStock("EvoCore.apps.", Apps.Dir);
            WriteStock("EvoCore.apps.", Apps.Dir, ".circuit");
        }

        static void WriteStock(string prefix, string dir, string extension = ".lua")
        {
            Directory.CreateDirectory(dir);
            var asm = typeof(Lua).Assembly;
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix) && n.EndsWith(extension)))
            {
                var path = Path.Combine(dir, res.Substring(prefix.Length));
                if (System.IO.File.Exists(path)) continue;
                using (var stream = asm.GetManifestResourceStream(res))
                using (var reader = new StreamReader(stream)) System.IO.File.WriteAllText(path, reader.ReadToEnd());
            }
        }

        // Whether a host's script is loaded and not stopped by an error.
        internal static bool Runs(Command host) => running.TryGetValue(host, out var r) && r.Script != null;

        // A running graph's wire value ("node.port" in its V table), or null (LogicRefit's self-test reads these).
        internal static double? GraphValue(Command host, string wire)
        {
            if (!running.TryGetValue(host, out var r) || r.Script == null) return null;
            var v = r.Script.Globals.Get("V");
            if (v.Type != DataType.Table) return null;
            var x = v.Table.Get(wire);
            return x.Type == DataType.Number ? x.Number : (double?)null;
        }

        // For the graph canvas's live view: every wire's value ("node.port" -> value) in the first running copy of a
        // graph, read from the global V its compiled script keeps; null when nothing runs it. `from` says which copy.
        internal static Dictionary<string, double> GraphValues(string file, out string from)
        {
            from = null;
            foreach (var r in running.Values)
            {
                if (r.Script == null || r.File == null || !string.Equals(Path.GetFullPath(r.File), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) continue;
                var v = r.Script.Globals.Get("V");
                if (v.Type != DataType.Table) continue;
                var values = new Dictionary<string, double>();
                foreach (var pair in v.Table.Pairs)
                    if (pair.Key.Type == DataType.String && pair.Value.Type == DataType.Number) values[pair.Key.String] = pair.Value.Number;
                from = $"{(r.Context.Host is LuaComputer ? "computer" : "screen")} on {r.Context.Ship?.EntityName}";
                return values;
            }
            return null;
        }

        public static void ReloadAll()
        {
            foreach (var pair in running.ToList()) Stop(pair.Key, pair.Value);
            nextScan = 0f;
        }

        static bool graphMode;
        internal static void ShowGraphs() => graphMode = true;

        [ModSettingsTab("Scripts")]
        static void DrawTab()
        {
            GUILayout.Label("Lua Computer block (Logic tab): its Tag-mode label names its script, UserData\\EvoCore\\scripts\\<label>.lua; links into it are inputs, links out are outputs.");
            GUILayout.Label("Text screens: put [lua: name] on one to run name.lua on its ship, or start its text with --lua.");
            GUILayout.Label("Folder: " + ScriptsDir);
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(!graphMode, "Scripts", GUI.skin.button, GUILayout.Width(100))) graphMode = false;
            if (GUILayout.Toggle(graphMode, "Circuits", GUI.skin.button, GUILayout.Width(100))) graphMode = true;
            GUILayout.EndHorizontal();
            if (graphMode) Graphs.Canvas.DrawLauncher();
            else DrawScriptEditor();
            GUILayout.Space(8);
            if (GUILayout.Button("Reload all scripts", GUILayout.Width(180))) ReloadAll();
            GUILayout.Label(Report());
        }
    }
}
