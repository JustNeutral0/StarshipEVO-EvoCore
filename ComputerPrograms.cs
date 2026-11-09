using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Manager;
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
            return Path.Combine(ComputersDir, $"{name} {c.ProgramId}{(c.ProgramKind == "graph" ? ".graph" : ".lua")}");
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

        // The activate key on a computer: stores the program it runs now in it (first time), opens the editor on it.
        internal static void OpenComputer(LuaComputer c)
        {
            try
            {
                if (!c.HasProgram)
                {
                    var graph = Path.Combine(ScriptsDir, c.ScriptName + ".graph");
                    var lua = Path.Combine(ScriptsDir, c.ScriptName + ".lua");
                    var fallback = Path.Combine(ScriptsDir, "computer.lua");
                    if (File.Exists(graph)) c.SetProgram("graph", File.ReadAllText(graph).Replace("\r\n", "\n"));
                    else if (File.Exists(lua)) c.SetProgram("lua", File.ReadAllText(lua).Replace("\r\n", "\n"));
                    else if (File.Exists(fallback)) c.SetProgram("lua", File.ReadAllText(fallback).Replace("\r\n", "\n"));
                    else c.SetProgram("lua", "-- This computer's program.\nfunction on_input(i, value)\n  outputs.set(i, value)\nend\n");
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
            if (path.EndsWith(".graph", StringComparison.OrdinalIgnoreCase))
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
            if (c == null || openFile == null) return;
            if (c.HasProgram && SamePath(openFile, MirrorPath(c)))
            {
                GUILayout.Label("on this computer", GUILayout.Width(120));
                if (GUILayout.Button("Copy to scripts", GUILayout.Width(130))) programMessage = Export(c);
                if (programMessage != "") GUILayout.Label(programMessage, GUILayout.Width(280));
            }
            else if (!IsMirror(openFile) && GUILayout.Button("Put on computer", GUILayout.Width(140)))
            {
                var kind = openFile.EndsWith(".graph", StringComparison.OrdinalIgnoreCase) ? "graph" : "lua";
                c.SetProgram(kind, File.ReadAllText(openFile).Replace("\r\n", "\n"));
                OpenPath(Mirror(c));
                nextScan = 0f;
                programMessage = $"{Path.GetFileName(openFile)} is now this computer's program.";
            }
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
            if (GUILayout.Button((SamePath(path, openFile) ? "> " : "") + "This computer" + (isGraph ? " (graph)" : " (Lua)"), GUI.skin.label))
                OpenPath(Mirror(c));
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
                if (GUILayout.Button("Make it a graph", GUILayout.Width(150))) confirmGraph = true;
                return;
            }
            GUILayout.Label("Put its Lua into a lua node inside a new graph, wired to its links? It keeps running as it is.", GUILayout.Width(160));
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
                programMessage = "Now a graph: its Lua is in the lua node (click it to see the code). " + copied;
            }
            if (GUILayout.Button("No", GUILayout.Width(70))) confirmGraph = false;
            GUILayout.EndHorizontal();
        }

        static bool confirmGraph;
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
            var ext = c.ProgramKind == "graph" ? ".graph" : ".lua";
            var path = Path.Combine(ScriptsDir, c.ScriptName + ext);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(ScriptsDir, $"{c.ScriptName} {n}{ext}");
            File.WriteAllText(path, c.Program);
            return "Copied to scripts as " + Path.GetFileName(path) + ".";
        }
    }
}
