using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using UnityEngine;

namespace EvoCore
{
    // Custom nodes. Two ways to add one:
    //  - a Lua file in UserData\EvoCore\nodes (live: save it and every graph using it reloads):
    //      return {
    //        help = "on above high, off below low, else keeps its last value",
    //        category = "Custom",                 -- the node menu's group (optional)
    //        ins = { "x" },                        -- input names (lower case)
    //        outs = { "out" },                     -- output names (optional: one called "out")
    //        settings = { low = 0.3, high = 0.7 }, -- setting names and defaults (optional)
    //        run = function(i, s, state)           -- i.x the inputs, s.low the settings, state this node's memory
    //          if i.x > s.high then state.on = true elseif i.x < s.low then state.on = false end
    //          return { out = state.on }          -- or just a value when there's one output; true/false count as 1/0
    //        end,
    //      }
    //    The file's name is the node's name. It's read in the sandbox at load to learn its ports; its code goes into
    //    each graph that uses it, so run() has the same APIs a script has (ship, outputs...).
    //  - from a C# mod: Graphs.Register(...), with Code writing the Lua (needs a restart, like any C# change).
    public static partial class Graphs
    {
        public static string NodesDir => Path.Combine(Path.Combine(Paths.UserData, "EvoCore"), "nodes");
        // Starting points for new graphs (GraphEditor's New row): any .graph here, {name} replaced by the new name.
        public static string TemplatesDir => Path.Combine(Path.Combine(Paths.UserData, "EvoCore"), "templates");

        // Circuit files end in .circuit. Up to EvoCore 0.14 they were called graphs and ended in .graph: any of those
        // lying in the EvoCore folders (an old install, a file from a friend) is renamed where it is, unless a
        // .circuit of that name is already there. Inside, the two are the same text.
        internal static void AdoptOld()
        {
            foreach (var dir in new[] { Lua.ScriptsDir, NodesDir, TemplatesDir, Apps.Dir, Lua.ComputersDir })
            {
                string[] old;
                try { old = Directory.GetFiles(dir, "*.graph"); }
                catch { continue; }
                foreach (var path in old)
                {
                    var to = Path.ChangeExtension(path, ".circuit");
                    if (File.Exists(to)) continue;
                    try
                    {
                        File.Move(path, to);
                        Core.Log.Msg($"Circuits: renamed {Path.GetFileName(path)} to {Path.GetFileName(to)}");
                    }
                    catch (Exception e) { Core.Log.Warning($"Circuits: couldn't rename {Path.GetFileName(path)}: {e.Message}"); }
                }
            }
        }

        // Goes up whenever a node is added, changed or removed; the runner restarts graphs started on an older one.
        public static int Version { get; private set; }

        // Why a node file didn't load, by node name.
        public static readonly Dictionary<string, string> NodeErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> nodeTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex validNode = new Regex(@"^[\w\- ]+$");
        static float nextNodeScan;

        public static NodeType Register(string name, string category, string help, string[] ins, string[] outs,
            Dictionary<string, string> settings, Func<Emit, string> code)
        {
            var t = new NodeType
            {
                Name = name, Category = category ?? "", Help = help ?? "", Ins = ins ?? new string[0], Outs = outs ?? new[] { "out" },
                Settings = settings ?? new Dictionary<string, string>(), Code = code,
            };
            if (Types.TryGetValue(name, out var old) && old.Source == null && old.File == null)
                Core.Log.Warning($"Circuits: node \"{name}\" registered again; the new one wins");
            Types[name] = t;
            Version++;
            return t;
        }

        internal static void ScanNodesSoon() => nextNodeScan = 0;

        // Every second (from Lua.Update): loads new and changed node files, drops removed ones.
        internal static void ScanNodes()
        {
            if (Time.unscaledTime < nextNodeScan) return;
            nextNodeScan = Time.unscaledTime + 1f;
            string[] files;
            try
            {
                Directory.CreateDirectory(NodesDir);
                files = Directory.GetFiles(NodesDir, "*.lua");
            }
            catch { return; }
            var changed = false;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                present.Add(name);
                DateTime time;
                try { time = File.GetLastWriteTimeUtc(path); } catch { continue; }
                if (nodeTimes.TryGetValue(name, out var known) && known == time) continue;
                nodeTimes[name] = time;
                changed = true;
                var type = LoadNode(path, out var error);
                if (type != null) { Types[name] = type; NodeErrors.Remove(name); }
                else
                {
                    NodeErrors[name] = error;
                    if (Types.TryGetValue(name, out var stale) && stale.File != null) Types.Remove(name);
                    Core.Log.Warning($"Circuits: node {name}.lua: {error}");
                }
            }
            foreach (var gone in nodeTimes.Keys.Where(n => !present.Contains(n)).ToList())
            {
                nodeTimes.Remove(gone);
                NodeErrors.Remove(gone);
                if (Types.TryGetValue(gone, out var t) && t.File != null) Types.Remove(gone);
                changed = true;
            }
            if (ScanChips(changed)) changed = true;
            if (changed) Version++;
        }

        // ---- Chips: a .graph in the nodes folder is a node made of nodes ----
        // Its `chip in` nodes are its input pins and its `chip out` nodes its output pins (top to bottom on its
        // board); everything between runs inside it, each chip on a board with its own memory (Graphs.ChipModule).
        // A first line "# help: ..." says what it does. Chips can hold Lua-written nodes and other chips, not lua
        // nodes, and not themselves.

        static readonly Dictionary<string, DateTime> chipTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        internal static bool IsChipFile(string path) =>
            path != null && path.EndsWith(".circuit", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)).TrimEnd('\\', '/'), Path.GetFullPath(NodesDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        // Loads every chip again when a chip file or any node changed: in passes, so a chip using another chip
        // loads after it. True when anything changed.
        static bool ScanChips(bool nodesChanged)
        {
            string[] files;
            try { files = Directory.GetFiles(NodesDir, "*.circuit"); }
            catch { return false; }
            var changed = nodesChanged;
            var present = new HashSet<string>(files.Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                DateTime time;
                try { time = File.GetLastWriteTimeUtc(path); } catch { continue; }
                var name = Path.GetFileNameWithoutExtension(path);
                if (!chipTimes.TryGetValue(name, out var known) || known != time) { chipTimes[name] = time; changed = true; }
            }
            foreach (var gone in chipTimes.Keys.Where(n => !present.Contains(n)).ToList())
            {
                chipTimes.Remove(gone);
                NodeErrors.Remove(gone);
                if (Types.TryGetValue(gone, out var t) && t.File != null && IsChipFile(t.File)) Types.Remove(gone);
                changed = true;
            }
            if (!changed) return false;
            // Out with the old chip types, then load until a pass loads nothing more.
            foreach (var old in Types.Where(p => p.Value.File != null && IsChipFile(p.Value.File)).Select(p => p.Key).ToList()) Types.Remove(old);
            var waiting = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (waiting.Count > 0)
            {
                var loaded = false;
                foreach (var path in waiting.ToList())
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var type = LoadChip(path, out var error);
                    if (type == null) { errors[name] = error; continue; }
                    Types[name] = type;
                    errors.Remove(name);
                    NodeErrors.Remove(name);
                    waiting.Remove(path);
                    loaded = true;
                }
                if (!loaded) break;
            }
            foreach (var e in errors)
            {
                NodeErrors[e.Key] = e.Value;
                Core.Log.Warning($"Circuits: chip {e.Key}.circuit: {e.Value}");
            }
            return true;
        }

        // Reads a chip file into a node type, or says why it can't.
        internal static NodeType LoadChip(string path, out string error)
        {
            error = null;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!validNode.IsMatch(name)) { error = "use letters, numbers, spaces, - or _ in the file name"; return null; }
            if (Types.TryGetValue(name, out var existing) && !(existing.File != null && IsChipFile(existing.File)))
            {
                error = existing.File == null ? $"a built-in node is already called \"{name}\"; rename the file" : $"{name}.lua is a node with the same name; rename one";
                return null;
            }
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e) { error = e.Message; return null; }
            try
            {
                var g = Parse(text);
                var uses = g.Nodes.Values.Select(n => n.Type).Where(t => t.Source != null).Distinct().ToList();
                bool Inside(NodeType t, HashSet<NodeType> seen) =>
                    string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) || t.Uses != null && seen.Add(t) && t.Uses.Any(u => Inside(u, seen));
                if (uses.Any(u => Inside(u, new HashSet<NodeType>()))) { error = "it holds itself (maybe through another chip)"; return null; }
                var pinsIn = g.Nodes.Values.Where(n => n.Type.Name == "chip in").OrderBy(n => n.Y).ThenBy(n => n.Id).ToList();
                var pinsOut = g.Nodes.Values.Where(n => n.Type.Name == "chip out").OrderBy(n => n.Y).ThenBy(n => n.Id).ToList();
                var ins = pinsIn.Select(n => n.Outs[0]).Distinct().ToArray();
                var outs = pinsOut.Select(n => n.Ins[0]).Distinct().ToArray();
                var clash = ins.Intersect(outs).FirstOrDefault();
                if (clash != null) { error = $"\"{clash}\" is both an input pin and an output pin; rename one"; return null; }
                // Its settings: each chip setting node's name, with its value as the default.
                var knobs = g.Nodes.Values.Where(n => n.Type.Name == "chip setting").OrderBy(n => n.Y).ThenBy(n => n.Id).ToList();
                var settings = new Dictionary<string, string>();
                foreach (var k in knobs) settings[k.Outs[0]] = SettingOr(k, "value");
                var knobClash = settings.Keys.Intersect(ins.Concat(outs)).FirstOrDefault();
                if (knobClash != null) { error = $"\"{knobClash}\" is both a pin and a setting; rename one"; return null; }
                var kinds = new Dictionary<string, string>();
                foreach (var n in pinsIn) kinds[n.Outs[0]] = PortKind(n, n.Outs[0]);
                foreach (var n in pinsOut) kinds[n.Ins[0]] = PortKind(n, n.Ins[0]);
                var help = text.Replace("\r", "").Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("# help:", StringComparison.OrdinalIgnoreCase));
                var t = new NodeType
                {
                    Name = name, Category = "Chips", File = path, Uses = uses, Kinds = kinds,
                    Help = help != null ? help.Substring(7).Trim() : $"a chip of {g.Nodes.Count} nodes",
                    Ins = ins, Outs = outs, Settings = settings, Source = ChipModule(g, name), ChipPorts = DeclaredPorts(g),
                };
                foreach (var k in knobs) t.Docs[k.Outs[0]] = $"a setting of this chip (it starts at {SettingOr(k, "value")})";
                foreach (var n in pinsIn) t.Docs[n.Outs[0]] = "an input pin of this chip";
                foreach (var n in pinsOut) t.Docs[n.Ins[0]] = "an output pin of this chip";
                t.Code = e => CallCode(t, e);
                return t;
            }
            catch (GraphException e) { error = e.Message; return null; }
        }

        // Reads a node file in the sandbox and makes its node type, or says why it can't.
        internal static NodeType LoadNode(string path, out string error)
        {
            error = null;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!validNode.IsMatch(name)) { error = "use letters, numbers, spaces, - or _ in the file name"; return null; }
            if (Types.TryGetValue(name, out var existing) && existing.File == null)
            {
                error = $"a built-in node is already called \"{name}\"; rename the file";
                return null;
            }
            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception e) { error = e.Message; return null; }
            DynValue result;
            try
            {
                var script = new Script(CoreModules.Preset_SoftSandbox);
                var co = script.CreateCoroutine(script.LoadString(source, null, name)).Coroutine;
                co.AutoYieldCounter = 100000;
                result = co.Resume();
                if (co.State == CoroutineState.ForceSuspended) { error = "it runs too long while loading (an endless loop outside run?)"; return null; }
            }
            catch (InterpreterException e) { error = e.DecoratedMessage ?? e.Message; return null; }
            catch (Exception e) { error = e.Message; return null; }
            if (result.Type != DataType.Table) { error = "it should return a table: return { ins = { ... }, outs = { ... }, run = function(i, s, state) ... end }"; return null; }
            var table = result.Table;
            if (table.Get("run").Type != DataType.Function) { error = "it needs run = function(i, s, state) ... end"; return null; }

            var ins = Names(table.Get("ins"), out var insError);
            if (insError != null) { error = "ins: " + insError; return null; }
            var outsValue = table.Get("outs");
            string outsError = null;
            var outs = outsValue.IsNil() ? new[] { "out" } : Names(outsValue, out outsError);
            if (outs == null) { error = "outs: " + outsError; return null; }
            var settings = new Dictionary<string, string>();
            var settingsValue = table.Get("settings");
            if (settingsValue.Type == DataType.Table)
                foreach (var pair in settingsValue.Table.Pairs)
                {
                    if (pair.Key.Type != DataType.String) { error = "settings: write them as name = default"; return null; }
                    var key = pair.Key.String.ToLowerInvariant();
                    settings[key] = pair.Value.Type == DataType.Number ? pair.Value.Number.ToString("R", CultureInfo.InvariantCulture)
                        : pair.Value.Type == DataType.Boolean ? (pair.Value.Boolean ? "1" : "0") : pair.Value.CastToString() ?? "";
                }
            var fixedIns = ins.Where(p => !p.StartsWith("$")).ToArray();
            var fixedOuts = outs.Where(p => !p.StartsWith("$")).ToArray();
            var clash = fixedIns.Intersect(fixedOuts).FirstOrDefault() ?? fixedIns.Intersect(settings.Keys).FirstOrDefault();
            if (clash != null) { error = $"\"{clash}\" is used twice; inputs, outputs and settings need different names"; return null; }
            var unknown = ins.Concat(outs).Where(p => p.StartsWith("$") && !settings.ContainsKey(p.Substring(1))).FirstOrDefault();
            if (unknown != null) { error = $"{unknown} names the setting \"{unknown.Substring(1)}\", which isn't in settings"; return null; }

            Dictionary<string, string> declares = null;
            var declaresValue = table.Get("declares");
            if (declaresValue.Type == DataType.Table)
            {
                declares = new Dictionary<string, string>();
                foreach (var pair in declaresValue.Table.Pairs)
                {
                    var kind = pair.Key.CastToString();
                    var setting = pair.Value.CastToString()?.ToLowerInvariant();
                    if (!new[] { "inputs", "outputs", "channels", "displays" }.Contains(kind)) { error = "declares: use inputs, outputs, channels or displays"; return null; }
                    if (setting == null || !settings.ContainsKey(setting)) { error = $"declares: {kind} = \"{setting}\" should name one of its settings"; return null; }
                    declares[kind] = setting;
                }
            }

            // choices = { mode = { "hold", "toggle", "pulse" }, low = { 0, 1 } }: picked from a list ({0, 1}: on/off).
            Dictionary<string, string[]> choices = null;
            var choicesValue = table.Get("choices");
            if (choicesValue.Type == DataType.Table)
            {
                choices = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in choicesValue.Table.Pairs)
                {
                    var key = pair.Key.CastToString()?.ToLowerInvariant();
                    if (key == null || !settings.ContainsKey(key)) { error = $"choices: \"{key}\" isn't one of its settings"; return null; }
                    if (pair.Value.Type != DataType.Table) { error = $"choices: {key} should be a list, like {{ \"a\", \"b\" }}"; return null; }
                    choices[key] = pair.Value.Table.Values.Select(v => v.Type == DataType.Number ? v.Number.ToString("R", CultureInfo.InvariantCulture) : v.CastToString() ?? "").ToArray();
                }
            }

            // kinds = { value = "level", hide = "onoff" }: what a port carries (number, onoff or level; number if unlisted).
            Dictionary<string, string> kinds = null;
            var kindsValue = table.Get("kinds");
            if (kindsValue.Type == DataType.Table)
            {
                kinds = new Dictionary<string, string>();
                foreach (var pair in kindsValue.Table.Pairs)
                {
                    var port = pair.Key.CastToString()?.ToLowerInvariant();
                    var kind = pair.Value.CastToString()?.ToLowerInvariant();
                    if (port == null || !FloatKinds.Contains(kind)) { error = $"kinds: {port} = \"{kind}\"; use number, onoff or level"; return null; }
                    kinds[port] = kind;
                }
            }

            // optional = { "hide" }: inputs most graphs leave alone; the canvas keeps them out of the way until used.
            // Unsaid, an input called hide is one (node files from before this could be said).
            HashSet<string> optional = null;
            var optionalValue = table.Get("optional");
            if (optionalValue.Type == DataType.Table)
            {
                var named = Names(optionalValue, out var optionalError);
                if (optionalError != null) { error = "optional: " + optionalError; return null; }
                var stray = named.FirstOrDefault(p => !fixedIns.Contains(p));
                if (stray != null) { error = $"optional: \"{stray}\" isn't one of its inputs"; return null; }
                if (named.Length > 0) optional = new HashSet<string>(named);
            }
            else if (fixedIns.Contains("hide")) optional = new HashSet<string> { "hide" };

            var docs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var docTable = table.Get("docs");
            if (docTable.Type == DataType.Table)
                foreach (var pair in docTable.Table.Pairs)
                    if (pair.Key.Type == DataType.String) docs[pair.Key.String] = pair.Value.CastToString() ?? "";
            var t = new NodeType
            {
                Name = name, Docs = docs,
                Help = table.Get("help").CastToString() ?? "a Lua-written node",
                Category = table.Get("category").CastToString() ?? "Custom",
                Ins = fixedIns, Outs = fixedOuts, Settings = settings, Source = source, File = path, Declares = declares, Choices = choices, Kinds = kinds, Optional = optional,
            };
            if (ins.Length != fixedIns.Length) t.InsOf = n => Expand(t, n, ins, "in");
            if (outs.Length != fixedOuts.Length) t.OutsOf = n => Expand(t, n, outs, "out");
            t.Code = e => CallCode(t, e);
            return t;
        }

        // A port list with "$setting" entries: those become the names listed in that setting of the node (commas or
        // spaces between), in order, skipping names that aren't usable, repeat, or clash with a setting or a fixed
        // port; at most MaxLuaPorts in all. Cached by the settings' text, as the canvas asks every frame.
        static string[] Expand(NodeType t, Node n, string[] list, string side)
        {
            var key = side + "|" + string.Join("|", list.Where(p => p.StartsWith("$"))
                .Select(p => n.Settings.TryGetValue(p.Substring(1), out var v) ? v : t.Settings[p.Substring(1)]).ToArray());
            lock (expanded)
            {
                if (expanded.TryGetValue(t, out var cache) && cache.TryGetValue(key, out var hit)) return hit;
                var taken = new HashSet<string>(t.Settings.Keys.Concat(t.Ins).Concat(t.Outs));
                var ports = new List<string>();
                foreach (var p in list)
                {
                    if (!p.StartsWith("$")) { ports.Add(p); continue; }
                    var raw = n.Settings.TryGetValue(p.Substring(1), out var v) ? v : t.Settings[p.Substring(1)];
                    foreach (var word in Regex.Split(raw ?? "", @"[,\s]+"))
                    {
                        var name = word.ToLowerInvariant();
                        if (name == "" || name == "none" || !Regex.IsMatch(name, @"^[a-z_]\w*$") || !taken.Add(name)) continue;
                        ports.Add(name);
                    }
                }
                var result = ports.Take(MaxLuaPorts).ToArray();
                if (cache == null) expanded[t] = cache = new Dictionary<string, string[]>();
                if (cache.Count > 200) cache.Clear();
                cache[key] = result;
                return result;
            }
        }
        static readonly Dictionary<NodeType, Dictionary<string, string[]>> expanded = new Dictionary<NodeType, Dictionary<string, string[]>>();

        static string[] Names(DynValue v, out string error)
        {
            error = null;
            if (v.IsNil()) return new string[0];
            if (v.Type != DataType.Table) { error = "write a list of names, like { \"a\", \"b\" }"; return null; }
            var names = new List<string>();
            foreach (var item in v.Table.Values)
            {
                var n = item.CastToString();
                // "$fields": the names listed in the node's `fields` setting (Expand).
                if (string.IsNullOrEmpty(n) || !Regex.IsMatch(n, @"^\$?[A-Za-z_]\w*$")) { error = $"\"{n}\" isn't a usable name (letters, numbers, _; or $setting)"; return null; }
                names.Add(n.ToLowerInvariant());
            }
            if (names.Distinct().Count() != names.Count) { error = "a name is listed twice"; return null; }
            return names.ToArray();
        }

        // The Lua that runs one custom node: run(inputs, settings, memory), its results onto the output wires.
        static string CallCode(NodeType t, Emit e)
        {
            // The node's own ports: a "$setting" port list depends on this node's settings.
            var nodeIns = e.Node.Ins;
            var nodeOuts = e.Node.Outs;
            var ins = string.Join(", ", nodeIns.Select(p => $"[{LuaString(p)}] = {e.In(p)}").ToArray());
            var settings = string.Join(", ", t.Settings.Keys.Select(k => $"[{LuaString(k)}] = {SettingValue(e.Setting(k))}").ToArray());
            var call = $"N[{LuaString(t.Name)}].run({{{ins}}}, {{{settings}}}, {e.State})";
            if (nodeOuts.Length == 0) return call;
            if (nodeOuts.Length == 1 && t.OutsOf == null)
                return $"do local r = {call}; if type(r) == \"table\" then r = r[{LuaString(nodeOuts[0])}] end; {e.Out(nodeOuts[0])} = num(r) end";
            return $"do local r = {call}; if type(r) ~= \"table\" then r = {{}} end; " + string.Join(" ", nodeOuts.Select(o => $"{e.Out(o)} = num(r[{LuaString(o)}]);").ToArray()) + " end";
        }

        // A setting as Lua: a number if it reads as one, else text.
        static string SettingValue(string raw) =>
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Lit(d) : LuaString(raw);

        internal static void WriteExampleNodes()
        {
            try
            {
                Directory.CreateDirectory(NodesDir);
                var path = Path.Combine(NodesDir, "hysteresis.lua");
                if (!File.Exists(path)) File.WriteAllText(path, ExampleNode);
            }
            catch (Exception e) { Core.Log.Warning("Circuits: couldn't write the example node: " + e.Message); }
        }

        const string ExampleNode =
@"-- hysteresis.lua: an example custom node. Change it and save: circuits using it reload.
-- Turns on when x goes above high, off when it drops below low, and otherwise keeps what it was,
-- so a value hovering around one threshold doesn't flicker (a fuel warning, a door near a sensor's edge).
return {
  help = ""on above high, off below low, else keeps its last value"",
  category = ""Custom"",
  ins = { ""x"" },
  outs = { ""out"" },
  settings = { low = 0.3, high = 0.7 },
  -- what each input, output and setting means (the Help tab shows these)
  docs = {
    x = ""the value to watch"",
    out = ""1 while on, 0 while off"",
    low = ""turns off when x drops below this"",
    high = ""turns on when x goes above this"",
  },
  -- i: the inputs (i.x), s: the settings (s.low, s.high), state: this node's own memory between ticks
  run = function(i, s, state)
    if i.x > s.high then state.on = true
    elseif i.x < s.low then state.on = false end
    return state.on
  end,
}
";

        internal static string NewNode(string name) =>
$@"-- {name}.lua: a custom node. Save it and circuits using it reload.
return {{
  help = ""what this node does"",
  category = ""Custom"",
  ins = {{ ""a"", ""b"" }},
  outs = {{ ""out"" }},
  settings = {{ scale = 1 }},
  -- what each input, output and setting means (the Help tab shows these)
  docs = {{
    a = ""the first number"",
    b = ""the second number"",
    out = ""(a + b) times scale"",
    scale = ""what the sum is multiplied by"",
  }},
  -- i: the inputs (i.a, i.b), s: the settings (s.scale), state: this node's own memory between ticks
  run = function(i, s, state)
    return {{ out = (i.a + i.b) * s.scale }}
  end,
}}
";

        // ---- The Nodes tab of the Computer screen ----

        internal static class NodeEditor
        {
            static string file, text = "", saved = "", newName = "", message = "", syntax = "";
            static DateTime fileTime;
            static Vector2 listScroll, codeScroll;
            static int cursorLine = 1;
            static string checkedText;
            static float checkAt;
            const string Control = "EvoCoreNodeEditor";

            static bool Dirty => file != null && text != saved;
            static string Name => Path.GetFileNameWithoutExtension(file);

            internal static void Draw(float height)
            {
                HandleKeys();
                GUILayout.BeginHorizontal();
                DrawList(height);
                GUILayout.BeginVertical();
                if (file == null) GUILayout.Label("Pick a node on the left, or make a new one. Nodes live in " + NodesDir + ".");
                else DrawEditor(height);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }

            static void DrawList(float height)
            {
                GUILayout.BeginVertical(GUILayout.Width(190));
                var made = Lua.NewRow("node");
                if (made != null) Create(made);
                listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(Mathf.Max(200, height)));
                foreach (var path in Files())
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var broken = NodeErrors.ContainsKey(name);
                    var label = (Same(path, file) ? "> " : "") + name + (broken ? "  (error)" : "");
                    if (GUILayout.Button(label, GUI.skin.label)) Open(path);
                }
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
            }

            static void DrawEditor(float height)
            {
                CheckDisk();
                GUILayout.BeginHorizontal();
                GUILayout.Label(Name + ".lua" + (Dirty ? "  (unsaved)" : ""), GUILayout.ExpandWidth(true));
                GUILayout.Label("Line " + cursorLine, GUILayout.Width(80));
                GUI.enabled = Dirty;
                if (GUILayout.Button("Save", GUILayout.Width(80))) Save();
                if (GUILayout.Button("Revert", GUILayout.Width(80))) { text = saved; message = "Reverted."; }
                GUI.enabled = true;
                var gone = Lua.DeleteButton(file);
                GUILayout.EndHorizontal();
                if (gone != null)
                {
                    message = $"Deleted {Path.GetFileName(file)}: moved to {Path.GetFileName(Path.GetDirectoryName(gone))}\\{Path.GetFileName(gone)}.";
                    file = null;
                    text = saved = "";
                    ScanNodesSoon();
                    return;
                }

                LiveCheck();
                NodeErrors.TryGetValue(Name, out var loadError);
                var shown = syntax != "" ? syntax : (Dirty ? null : loadError);
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical();
                text = Lua.CodeView.Draw(text, ref codeScroll, height, Control, Lua.CodeView.ErrorLine(shown), out cursorLine);
                GUILayout.EndVertical();
                GUILayout.Space(14);
                GUILayout.BeginVertical(GUILayout.Width(250));
                GUILayout.Label("On the canvas:");
                Types.TryGetValue(Name, out var type);
                if (type != null && type.File != null && !Dirty)
                {
                    var spot = GUILayoutUtility.GetRect(250, 250, 40, 40 + Canvas.PreviewHeight(type));
                    Canvas.Preview(type, spot);
                    GUILayout.Label(type.Help, Wrap());
                    GUILayout.Label($"Menu group: {type.Category}");
                }
                else if (Dirty) GUILayout.Label("Save to see it.", Wrap());
                else GUILayout.Label("Not loaded yet.", Wrap());
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(shown)) GUILayout.Label("<color=#ff8a80>" + (syntax != "" ? "Syntax: " : "Won't load: ") + shown + "</color>", Rich());
                else if (!Dirty && type != null) GUILayout.Label("<color=#a1ffe0>Loaded. Circuits using it run this version.</color>", Rich());
                if (message != "") GUILayout.Label(message);
            }

            static void LiveCheck()
            {
                if (text == checkedText) return;
                if (checkAt <= 0) { checkAt = Time.realtimeSinceStartup + 0.5f; return; }
                if (Time.realtimeSinceStartup < checkAt) return;
                checkedText = text;
                checkAt = 0;
                try { new Script(CoreModules.None).LoadString(text, null, Name); syntax = ""; }
                catch (SyntaxErrorException e) { syntax = e.DecoratedMessage ?? e.Message; }
                catch (Exception e) { syntax = e.Message; }
            }

            static void HandleKeys()
            {
                var e = Event.current;
                if (e.type != EventType.KeyDown || file == null || GUI.GetNameOfFocusedControl() != Control) return;
                if (e.keyCode == KeyCode.Tab || e.character == '\t')
                {
                    var editor = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
                    if (editor != null && e.keyCode == KeyCode.Tab) { editor.ReplaceSelection("  "); text = editor.text; }
                    e.Use();
                }
                else if (e.keyCode == KeyCode.S && (e.control || e.command)) { if (Dirty) Save(); e.Use(); }
            }

            static IEnumerable<string> Files()
            {
                try { return Directory.GetFiles(NodesDir, "*.lua").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { return Enumerable.Empty<string>(); }
            }

            static void Open(string path)
            {
                if (Same(path, file)) return;
                if (Dirty) { message = "Save or revert " + Name + ".lua first."; return; }
                try
                {
                    text = saved = File.ReadAllText(path).Replace("\r\n", "\n");
                    file = path;
                    fileTime = File.GetLastWriteTimeUtc(path);
                    message = syntax = "";
                    codeScroll = Vector2.zero;
                }
                catch (Exception e) { message = "Couldn't open it: " + e.Message; }
            }

            static void Create(string name)
            {
                if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (name == "" || !validNode.IsMatch(name)) { message = "Use letters, numbers, spaces, - or _ in the name."; return; }
                if (Types.TryGetValue(name, out var t) && t.File == null) { message = $"A built-in node is already called \"{name}\"."; return; }
                if (Dirty) { message = "Save or revert " + Name + ".lua first."; return; }
                var path = Path.Combine(NodesDir, name + ".lua");
                try
                {
                    Directory.CreateDirectory(NodesDir);
                    if (!File.Exists(path)) File.WriteAllText(path, NewNode(name));
                    newName = "";
                    file = null;
                    Open(path);
                    ScanNodesSoon();
                    message = $"Made {name}.lua: right-click the canvas and find it under Custom.";
                }
                catch (Exception e) { message = "Couldn't create it: " + e.Message; }
            }

            static void Save()
            {
                try
                {
                    File.WriteAllText(file, text.Replace("\n", Environment.NewLine));
                    saved = text;
                    fileTime = File.GetLastWriteTimeUtc(file);
                    message = "Saved " + DateTime.Now.ToString("HH:mm:ss") + ". Circuits using it reload in a second.";
                    ScanNodesSoon();
                }
                catch (Exception e) { message = "Couldn't save: " + e.Message; }
            }

            static void CheckDisk()
            {
                try
                {
                    if (!File.Exists(file)) { message = "The file is gone from disk; Save writes it again."; return; }
                    var time = File.GetLastWriteTimeUtc(file);
                    if (time == fileTime) return;
                    fileTime = time;
                    var disk = File.ReadAllText(file).Replace("\r\n", "\n");
                    if (Dirty) { saved = disk; message = "Changed on disk too: Save keeps your version, Revert takes the disk's."; return; }
                    text = saved = disk;
                    message = "Reloaded: changed on disk.";
                }
                catch { }
            }

            static bool Same(string a, string b) =>
                a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

            static GUIStyle rich, wrap;
            static GUIStyle Rich() => rich ?? (rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
            static GUIStyle Wrap() => wrap ?? (wrap = new GUIStyle(GUI.skin.label) { wordWrap = true });
        }
    }
}
