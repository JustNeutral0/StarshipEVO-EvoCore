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
                Core.Log.Warning($"Graphs: node \"{name}\" registered again; the new one wins");
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
                    Core.Log.Warning($"Graphs: node {name}.lua: {error}");
                }
            }
            foreach (var gone in nodeTimes.Keys.Where(n => !present.Contains(n)).ToList())
            {
                nodeTimes.Remove(gone);
                NodeErrors.Remove(gone);
                if (Types.TryGetValue(gone, out var t) && t.File != null) Types.Remove(gone);
                changed = true;
            }
            if (changed) Version++;
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
            var clash = ins.Intersect(outs).FirstOrDefault() ?? ins.Intersect(settings.Keys).FirstOrDefault();
            if (clash != null) { error = $"\"{clash}\" is used twice; inputs, outputs and settings need different names"; return null; }

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
                Ins = ins, Outs = outs, Settings = settings, Source = source, File = path,
            };
            t.Code = e => CallCode(t, e);
            return t;
        }

        static string[] Names(DynValue v, out string error)
        {
            error = null;
            if (v.IsNil()) return new string[0];
            if (v.Type != DataType.Table) { error = "write a list of names, like { \"a\", \"b\" }"; return null; }
            var names = new List<string>();
            foreach (var item in v.Table.Values)
            {
                var n = item.CastToString();
                if (string.IsNullOrEmpty(n) || !Regex.IsMatch(n, @"^[A-Za-z_]\w*$")) { error = $"\"{n}\" isn't a usable name (letters, numbers, _)"; return null; }
                names.Add(n.ToLowerInvariant());
            }
            if (names.Distinct().Count() != names.Count) { error = "a name is listed twice"; return null; }
            return names.ToArray();
        }

        // The Lua that runs one custom node: run(inputs, settings, memory), its results onto the output wires.
        static string CallCode(NodeType t, Emit e)
        {
            var ins = string.Join(", ", t.Ins.Select(p => $"[{LuaString(p)}] = {e.In(p)}").ToArray());
            var settings = string.Join(", ", t.Settings.Keys.Select(k => $"[{LuaString(k)}] = {SettingValue(e.Setting(k))}").ToArray());
            var call = $"N[{LuaString(t.Name)}].run({{{ins}}}, {{{settings}}}, {e.State})";
            if (t.Outs.Length == 0) return call;
            if (t.Outs.Length == 1)
                return $"do local r = {call}; if type(r) == \"table\" then r = r[{LuaString(t.Outs[0])}] end; {e.Out(t.Outs[0])} = num(r) end";
            return $"do local r = {call} or {{}}; " + string.Join(" ", t.Outs.Select(o => $"{e.Out(o)} = num(r[{LuaString(o)}]);").ToArray()) + " end";
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
            catch (Exception e) { Core.Log.Warning("Graphs: couldn't write the example node: " + e.Message); }
        }

        const string ExampleNode =
@"-- hysteresis.lua: an example custom node. Change it and save: graphs using it reload.
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
$@"-- {name}.lua: a custom node. Save it and graphs using it reload.
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
                else if (!Dirty && type != null) GUILayout.Label("<color=#a1ffe0>Loaded. Graphs using it run this version.</color>", Rich());
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
                    message = "Saved " + DateTime.Now.ToString("HH:mm:ss") + ". Graphs using it reload in a second.";
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
