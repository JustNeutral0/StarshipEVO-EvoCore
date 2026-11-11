using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Manager;
using MoonSharp.Interpreter;
using UnityEngine;

namespace EvoCore
{
    // A Lua Computer's own program: once you open a computer (the activate key), its program is stored in the block
    // itself (LuaComputer.Program, saved with the ship as the block's custom data), so every computer has its own and
    // it travels with the ship. Until then a computer runs the shared scripts\<label> file as before.
    // The editor and the runner work on files, so each stored program is mirrored to computers\<label> <id>.graph or
    // .lua: the block's copy is written there when it changes (a load, another player's edit), and a save in the editor
    // (or any other editor) is copied back into the block. Files in scripts\ are a library: "Copy to scripts" exports
    // the computer's program, "Put on computer" loads a library file into it.
    public static partial class Lua
    {
        public static string ComputersDir => Path.Combine(Path.Combine(Paths.UserData, "EvoCore"), "computers");

        // The computer the editor was last opened from: the export/import buttons act on it.
        internal static LuaComputer Attached;

        // Per computer: the program text last written to or read from its mirror, and the mirror's time then.
        static readonly Dictionary<LuaComputer, (string Text, DateTime Time, string Path)> mirrors = new Dictionary<LuaComputer, (string, DateTime, string)>();

        internal static string MirrorPath(LuaComputer c)
        {
            var name = new string(c.ScriptName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            return Path.Combine(ComputersDir, $"{name} {c.ProgramId}{(c.ProgramKind == "graph" ? ".circuit" : ".lua")}");
        }

        internal static bool IsMirror(string path) =>
            path != null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(ComputersDir), StringComparison.OrdinalIgnoreCase);

        // The computer's mirror file, up to date: an edit saved to the file goes into the block first, then the block's
        // copy is written out if the file doesn't hold it.
        internal static string Mirror(LuaComputer c)
        {
            Directory.CreateDirectory(ComputersDir);
            var path = MirrorPath(c);
            if (mirrors.TryGetValue(c, out var last) && SamePath(last.Path, path) && File.Exists(path) && File.GetLastWriteTimeUtc(path) != last.Time)
            {
                var edited = File.ReadAllText(path).Replace("\r\n", "\n");
                if (edited != c.Program) c.SetProgram(c.ProgramKind, edited);
                mirrors[c] = (edited, File.GetLastWriteTimeUtc(path), path);
            }
            if (!mirrors.TryGetValue(c, out last) || last.Text != c.Program || !SamePath(last.Path, path) || !File.Exists(path))
            {
                var onDisk = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
                if (onDisk != c.Program) File.WriteAllText(path, c.Program);
                mirrors[c] = (c.Program, File.GetLastWriteTimeUtc(path), path);
            }
            return path;
        }

        // Blueprint copies carry the same program id; the second computer found with an id gets a new one.
        static readonly Dictionary<string, LuaComputer> ids = new Dictionary<string, LuaComputer>();

        static void ClaimId(LuaComputer c)
        {
            if (ids.TryGetValue(c.ProgramId, out var other) && other != c && other.brickCoord.entity != null) c.NewProgramId();
            ids[c.ProgramId] = c;
        }

        // A new computer's program: output 1 follows input 1, output 2 input 2, with a hint.
        const string StarterGraph =
@"# This computer's program. Output 1 follows input 1 and output 2 follows input 2.
rate 0.1
comment at=0,-70 size=520,50 text=""Link switches into the computer and lights out of it. Right-click empty space to add a node; Ctrl+S saves.""
node 1 input index=1 at=0,0
node 2 output index=1 at=240,0
node 3 input index=2 at=0,100
node 4 output index=2 at=240,100
wire 1.out 2.in
wire 3.out 4.in
";

        // The activate key on a computer: stores the program it runs now in it (first time), opens the editor on it.
        internal static void OpenComputer(LuaComputer c)
        {
            try
            {
                if (!c.HasProgram)
                {
                    // Graphs first: a computer gets its label's graph, else its label's Lua script (but not the stock
                    // computer.lua, which nobody picked), else the starter graph.
                    var graph = Path.Combine(ScriptsDir, c.ScriptName + ".circuit");
                    var lua = Path.Combine(ScriptsDir, c.ScriptName + ".lua");
                    if (File.Exists(graph)) c.SetProgram("graph", File.ReadAllText(graph).Replace("\r\n", "\n"));
                    else if (c.ScriptName != "computer" && File.Exists(lua)) c.SetProgram("lua", File.ReadAllText(lua).Replace("\r\n", "\n"));
                    else c.SetProgram("graph", StarterGraph.Replace("\r", ""));
                }
                ClaimId(c);
                Attached = c;
                OpenPath(Mirror(c));
                nextScan = 0f;
            }
            catch (Exception e) { Core.Log.Error("Opening the computer failed: " + e); }
        }

        // Opens a graph or Lua file on the Computer screen's matching tab.
        internal static void OpenPath(string path)
        {
            if (path.EndsWith(".circuit", StringComparison.OrdinalIgnoreCase))
            {
                Graphs.Canvas.Open(path);
                Graphs.Canvas.ShowGraph();
                Graphs.Canvas.Show();
            }
            else
            {
                Editor.Open(path);
                Graphs.OpenScriptTab();
            }
        }

        // The attached computer's program, if it's still there.
        static LuaComputer AttachedNow => Attached != null && Attached.brickCoord.entity != null ? Attached : null;

        // Buttons for the editors' header rows, given the open file: on the computer's own program, export it; on a
        // library file, load it into the computer.
        internal static void ComputerButtons(string openFile)
        {
            var c = AttachedNow;
            if (c == null || openFile == null || Graphs.IsChipFile(openFile)) return;   // a chip goes on boards, not computers
            if (c.HasProgram && SamePath(openFile, MirrorPath(c)))
            {
                GUILayout.Label("on this computer", GUILayout.Width(120));
                if (GUILayout.Button("Copy to scripts", GUILayout.Width(130))) programMessage = Export(c);
                if (programMessage != "") GUILayout.Label(programMessage, GUILayout.Width(280));
            }
            else if (!IsMirror(openFile) && GUILayout.Button("Put on computer", GUILayout.Width(140))) PutOnComputer(openFile);
        }

        // Makes a scripts\ file the attached computer's program and opens the computer's copy. Returns what happened.
        internal static string PutOnComputer(string openFile)
        {
            var c = AttachedNow;
            if (c == null) return "Open a Lua Computer first: aim at it and press the use key.";
            var kind = openFile.EndsWith(".circuit", StringComparison.OrdinalIgnoreCase) ? "graph" : "lua";
            c.SetProgram(kind, File.ReadAllText(openFile).Replace("\r\n", "\n"));
            OpenPath(Mirror(c));
            nextScan = 0f;
            return programMessage = $"{Path.GetFileName(openFile)} is now this computer's program.";
        }

        // The computer that was opened (aimed at and used), if it's still there.
        internal static LuaComputer AttachedComputer => AttachedNow;

        // The computer running a graph or script file: the opened computer when the file is its own program, else any
        // computer running it. Null when nothing runs it.
        internal static LuaComputer RunnerOf(string file)
        {
            if (file == null) return null;
            var a = AttachedNow;
            if (a != null && a.HasProgram && SamePath(file, MirrorPath(a))) return a;
            foreach (var pair in running)
                if (pair.Key is LuaComputer c && pair.Value.File != null && SamePath(pair.Value.File, file)) return c;
            return null;
        }

        // What a computer's link (by number, or a port name) goes to, or null when nothing is there.
        internal static string LinkName(LuaComputer c, bool outgoing, string which)
        {
            if (c == null || string.IsNullOrEmpty(which)) return null;
            var v = double.TryParse(which, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? DynValue.NewNumber(n) : DynValue.NewString(which);
            try
            {
                var target = Resolve(c, outgoing, v, out _);
                return target != null ? Describe(target) : null;
            }
            catch { return null; }
        }

        // Delete for the Graph, Script and Nodes editors: asks first, then moves the file into a "deleted" folder beside
        // it (named with the time), so a slip can be undone by moving it back. Not offered for a computer's own program.
        // Returns where it went once it's moved, else null.
        static string confirmDelete;

        internal static string DeleteButton(string file)
        {
            if (file == null || IsMirror(file) || !File.Exists(file)) return null;
            if (confirmDelete != file)
            {
                if (GUILayout.Button("Delete", GUILayout.Width(80))) confirmDelete = file;
                return null;
            }
            GUILayout.Label("Delete it?", GUILayout.Width(70));
            var yes = GUILayout.Button("Yes", GUILayout.Width(50));
            if (GUILayout.Button("No", GUILayout.Width(50))) confirmDelete = null;
            if (!yes) return null;
            confirmDelete = null;
            try
            {
                var dir = Path.Combine(Path.GetDirectoryName(file), "deleted");
                Directory.CreateDirectory(dir);
                var to = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)} {DateTime.Now:yyyy-MM-dd HHmmss}{Path.GetExtension(file)}");
                File.Move(file, to);
                nextScan = 0f;
                return to;
            }
            catch (Exception e) { Core.Log.Warning("Delete failed: " + e.Message); return null; }
        }

        // What the buttons last did, shown beside them.
        internal static string programMessage = "";

        // ---- For DevBridge ----

        // "Make it a graph" and "Back to Lua" on the computer with that Tag label (and, of several, the ship's id).
        public static string WrapComputer(string label, string programId = null)
        {
            var c = ByLabel(label, programId);
            if (c == null || !c.HasProgram || c.ProgramKind != "lua") return "no Lua computer labelled " + label;
            var lua = c.Program ?? "";
            Export(c);
            c.SetProgram("graph", Wrap(c, lua));
            wrapped[c] = lua;
            Mirror(c);
            nextScan = 0f;
            return $"wrapped {label} {c.ProgramId}: {c.Inputs.Count} in, {c.Outputs.Count} out";
        }

        public static string UnwrapComputer(string label, string programId = null)
        {
            var c = ByLabel(label, programId);
            if (c == null || !wrapped.TryGetValue(c, out var before)) return "nothing to undo for " + label;
            c.SetProgram("lua", before);
            wrapped.Remove(c);
            Mirror(c);
            nextScan = 0f;
            return "back to Lua: " + label;
        }

        // Puts a scripts\ file on the computer with that label and id, as "Put on computer" does.
        public static string PutScript(string label, string programId, string file)
        {
            var c = ByLabel(label, programId);
            var path = Path.Combine(ScriptsDir, file);
            if (c == null || !File.Exists(path)) return "no such computer or file";
            c.SetProgram(file.EndsWith(".circuit", StringComparison.OrdinalIgnoreCase) ? "graph" : "lua", File.ReadAllText(path).Replace("\r\n", "\n"));
            Mirror(c);
            nextScan = 0f;
            return $"{file} is now on {label} {c.ProgramId}";
        }

        // Display port test: puts a program declaring one display port, "main", on a computer, puts its first linked
        // display in that port, and DisplayPortUndo puts the old program back. The program prints what it sees.
        static readonly Dictionary<LuaComputer, string> beforeDisplayTest = new Dictionary<LuaComputer, string>();

        const string DisplayTest =
@"ports.declare{ displays = {'main'} }
function tick(dt)
  local d = displays.get('main')
  if d.present then d.clear(); d.text(10, 10, 'PORT main', 'green', 28) end
  print(string.format('main=%s count=%d display1=%s', tostring(d.present), displays.count, tostring(display.present)))
end
";

        public static string DisplayPortTest(string label, string programId, bool assign)
        {
            var c = ByLabel(label, programId);
            if (c == null) return "no such computer";
            if (!beforeDisplayTest.ContainsKey(c)) beforeDisplayTest[c] = c.Program;
            if (c.Program != DisplayTest) { c.SetProgram("lua", DisplayTest); Mirror(c); nextScan = 0f; }
            var first = c.Outputs.FirstOrDefault(Monitors.IsDisplay);
            if (assign && first != null) AssignDisplay(c, "main", first);
            return $"test program on; first display: {(first != null ? Describe(first) : "none")}{(assign ? ", put in port main" : "")}";
        }

        public static string DisplayPortUndo(string label, string programId)
        {
            var c = ByLabel(label, programId);
            if (c == null || !beforeDisplayTest.TryGetValue(c, out var before)) return "nothing to undo";
            Clear(c, "disp", "main");
            c.SetProgram("lua", before);
            beforeDisplayTest.Remove(c);
            Mirror(c);
            nextScan = 0f;
            return "program restored";
        }

        static LuaComputer ByLabel(string label, string programId) =>
            VoxelEngine.CommandManager.commands.Datas.OfType<LuaComputer>()
                .FirstOrDefault(c => c.brickCoord.entity != null && c.ScriptName == label && (programId == null || c.ProgramId == programId));

        // Opens the Lua Computer nearest the player as its activate key would (storing its program the first time).
        public static string OpenNearestComputer()
        {
            var eye = Dependency.RenderPipeline.playerCamera.transform.position;
            var c = VoxelEngine.CommandManager.commands.Datas.OfType<LuaComputer>().Where(x => x.brickCoord.entity != null)
                .OrderBy(x => (x.brickCoord.WorldPosition - eye).sqrMagnitude).FirstOrDefault();
            if (c == null) return "no Lua Computer";
            OpenComputer(c);
            return ProgramReport();
        }

        // Writes every ship with a stored program to the save now (the game saves ships every so often, not on quit).
        public static string SaveComputerShips()
        {
            var ships = VoxelEngine.CommandManager.commands.Datas.OfType<LuaComputer>()
                .Where(c => c.HasProgram && c.brickCoord.entity?.Parent != null).Select(c => c.brickCoord.entity.Parent).Distinct().ToList();
            foreach (var ship in ships) { ship.SetDirty(); Manager.Save.EntityInstanceSystem_mp.SaveEntity(ship); }
            return "saved " + string.Join(", ", ships.Select(s => s.EntityName).ToArray());
        }

        // Opens the nearest computer on the Anim tab.
        public static string OpenNearestAnim()
        {
            var r = OpenNearestComputer();
            Graphs.Canvas.ShowAnim();
            return r;
        }

        public static string ProgramReport() => string.Join("\n", VoxelEngine.CommandManager.commands.Datas.OfType<LuaComputer>()
            .Where(c => c.brickCoord.entity != null)
            .Select(c => $"{c.ScriptName} on {c.brickCoord.entity.Parent?.EntityName}: " +
                         (c.HasProgram ? $"own {c.ProgramKind} program {c.ProgramId}, {c.Program.Length} chars, file {Path.GetFileName(MirrorPath(c))}" : "runs scripts\\" + c.ScriptName))
            .ToArray());

        // "This computer" at the top of the Graph and Scripts lists, saying which kind of program it holds (it opens on
        // that kind's tab). A computer holds one program, a graph or Lua. Lua can't be turned into graph nodes, so on the
        // Graph tab a Lua computer offers to wrap it instead: the whole program goes into one lua node, wired to an input
        // node per link in and an output node per link out, so it keeps running while graph logic grows around it (and
        // a copy goes to scripts). Until the editor closes, "Back to Lua" puts the program back as it was.
        internal static void ComputerListEntry(string openFile, bool graphTab)
        {
            var c = AttachedNow;
            if (c == null || !c.HasProgram) return;
            var path = MirrorPath(c);
            var isGraph = c.ProgramKind == "graph";
            if (GUILayout.Button((SamePath(path, openFile) ? "> " : "") + "This computer" + (isGraph ? " (circuit)" : " (Lua)"), GUI.skin.label))
                OpenPath(Mirror(c));
            // The ship's logic tools (work in progress), folded away so they don't crowd the file list; an undoable refit
            // keeps them open so its undo stays in sight.
            var ship = c.brickCoord.entity.Parent;
            if (graphTab && !LogicRefit.CanUndo(ship))
                showShipLogic = GUILayout.Toggle(showShipLogic, (showShipLogic ? "v " : "> ") + "Ship logic (WIP)", GUI.skin.label);
            if (!graphTab || (!showShipLogic && !LogicRefit.CanUndo(ship))) confirmRefit = false;
            // The ship's logic gates and timers as a graph (LogicConverter): read only, the ship stays as it is.
            else if (GUILayout.Button("Convert ship's logic", GUILayout.Width(150)))
            {
                var report = LogicConverter.ConvertAndSave(ship, out var made);
                if (made != null) OpenPath(made);
                Graphs.Canvas.Say(report.Replace("\r", "").Replace("\n", "  "));
            }
            // Step 2: the converted logic onto this computer (LogicRefit), asked first; and its undo.
            if (graphTab && LogicRefit.CanUndo(ship))
            {
                if (GUILayout.Button("Undo refit", GUILayout.Width(150)))
                    Graphs.Canvas.Say(LogicRefit.Undo(ship).Replace("\r", "").Replace("\n", "  "));
            }
            else if (graphTab && showShipLogic)
            {
                if (!confirmRefit)
                {
                    if (GUILayout.Button("Refit onto this computer", GUILayout.Width(150))) confirmRefit = true;
                }
                else
                {
                    GUILayout.Label("Run the ship's gates and timers as a circuit here? It tests first: if every output matches, the old ones are removed (no undo); if not, they stay and Undo works.", GUILayout.Width(160));
                    GUILayout.BeginHorizontal(GUILayout.Width(160));
                    if (GUILayout.Button("Yes", GUILayout.Width(70)))
                    {
                        confirmRefit = false;
                        var done = LogicRefit.Refit(c);
                        OpenPath(Mirror(c));
                        Graphs.Canvas.Say(done.Replace("\r", "").Replace("\n", "  "));
                    }
                    if (GUILayout.Button("No", GUILayout.Width(70))) confirmRefit = false;
                    GUILayout.EndHorizontal();
                }
            }
            if (graphTab && isGraph && wrapped.TryGetValue(c, out var before))
            {
                if (GUILayout.Button("Back to Lua", GUILayout.Width(150)))
                {
                    c.SetProgram("lua", before);
                    wrapped.Remove(c);
                    OpenPath(Mirror(c));
                    nextScan = 0f;
                    programMessage = "Back to its Lua, as it was.";
                }
                return;
            }
            if (!graphTab || isGraph) { confirmGraph = false; return; }
            if (!confirmGraph)
            {
                if (GUILayout.Button("Make it a circuit", GUILayout.Width(150))) confirmGraph = true;
                return;
            }
            GUILayout.Label("Put its Lua into a lua node inside a new circuit, wired to its links? It keeps running as it is.", GUILayout.Width(160));
            GUILayout.BeginHorizontal(GUILayout.Width(160));
            if (GUILayout.Button("Yes", GUILayout.Width(70)))
            {
                confirmGraph = false;
                var lua = c.Program ?? "";
                var copied = Export(c);
                c.SetProgram("graph", Wrap(c, lua));
                wrapped[c] = lua;
                OpenPath(Mirror(c));
                nextScan = 0f;
                programMessage = "Now a circuit: its Lua is in the lua node (click it to see the code). " + copied;
            }
            if (GUILayout.Button("No", GUILayout.Width(70))) confirmGraph = false;
            GUILayout.EndHorizontal();
        }

        static bool confirmGraph, confirmRefit, showShipLogic;
        static readonly Dictionary<LuaComputer, string> wrapped = new Dictionary<LuaComputer, string>();

        // The graph that runs a computer's Lua unchanged: input nodes, one lua node holding the code, output nodes.
        static string Wrap(LuaComputer c, string lua)
        {
            int ins = Mathf.Clamp(c.Inputs.Count, 1, Graphs.MaxLuaPorts), outs = Mathf.Clamp(c.Outputs.Count, 1, Graphs.MaxLuaPorts);
            var g = new Graphs.Graph();
            var script = new Graphs.Node { Id = 1, Type = Graphs.Types["lua"], X = 280, Y = 40, Placed = true, Script = lua.Replace("\r", "") };
            script.Settings["inputs"] = ins.ToString();
            script.Settings["outputs"] = outs.ToString();
            g.Nodes[1] = script;
            var id = 2;
            for (int i = 1; i <= ins; i++, id++)
            {
                var input = new Graphs.Node { Id = id, Type = Graphs.Types["input"], X = 0, Y = 40 + 80 * (i - 1), Placed = true };
                input.Settings["index"] = i.ToString();
                g.Nodes[id] = input;
                g.Wires.Add((id, "out", 1, "in" + i, 0));
            }
            for (int i = 1; i <= outs; i++, id++)
            {
                var output = new Graphs.Node { Id = id, Type = Graphs.Types["output"], X = 560, Y = 40 + 80 * (i - 1), Placed = true };
                output.Settings["index"] = i.ToString();
                g.Nodes[id] = output;
                g.Wires.Add((1, "out" + i, id, "in", 0));
            }
            return Graphs.Write(g, c.ScriptName);
        }

        // A small "+ New" button at the top of a file list; pressed, it becomes a name field with Make and x. Returns
        // the name once Make (or Enter) is pressed.
        static readonly Dictionary<string, string> naming = new Dictionary<string, string>();
        static string focusNew;

        internal static string NewRow(string list)
        {
            if (!naming.TryGetValue(list, out var name))
            {
                if (GUILayout.Button("+ New", GUILayout.Width(70))) { naming[list] = ""; focusNew = list; }
                return null;
            }
            var control = "EvoCoreNew" + list;
            var e = Event.current;
            var enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == control;
            var escape = e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && GUI.GetNameOfFocusedControl() == control;
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(control);
            name = naming[list] = GUILayout.TextField(name);
            if (focusNew == list) { GUI.FocusControl(control); focusNew = null; }
            var make = GUILayout.Button("Make", GUILayout.Width(50)) || enter;
            var cancel = GUILayout.Button("x", GUILayout.Width(24)) || escape;
            GUILayout.EndHorizontal();
            if (enter || escape) e.Use();
            if (cancel) { naming.Remove(list); return null; }
            if (!make || name.Trim() == "") return null;
            naming.Remove(list);
            return name.Trim();
        }

        internal static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static string Export(LuaComputer c)
        {
            var ext = c.ProgramKind == "graph" ? ".circuit" : ".lua";
            var path = Path.Combine(ScriptsDir, c.ScriptName + ext);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(ScriptsDir, $"{c.ScriptName} {n}{ext}");
            File.WriteAllText(path, c.Program);
            return "Copied to scripts as " + Path.GetFileName(path) + ".";
        }
    }
}
