using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EvoCore
{
    // The graph editor: a screen of its own, filling the game's window. It opens from using a Lua Computer that runs a
    // graph, from the Graphs list in the Scripts tab (Mod Settings, F11), or `get EvoCore.Graphs.OpenCanvas("name")`;
    // Esc closes it. While it's open it holds the game's in-menu flag, as Mod Settings does, so the mouse and keys
    // don't fly the ship. Pick a .graph on the left; nodes are boxes with inputs on the left edge and outputs on the
    // right. Saving writes the .graph text (Graphs.Write, with each node's place as at=x,y), and every computer
    // running it reloads within a second, as with any other save. Unsaved edits stay while the editor is closed.
    //
    //   drag from an output (orange) to an input (blue)   wire them; an input takes one wire, a new one replaces it
    //   drag a wired input away                           unplug it (drop it on another input to move it)
    //   drag a node by its title                          move it; click a node to select it, Delete removes it
    //   right-click empty space                           the node menu (type to search)
    //   right or middle drag / mouse wheel                pan / zoom
    //   Ctrl+S                                            save
    // Unwired inputs and settings are typed into the node itself. Everything is drawn with IMGUI by hand: Unity's own
    // node tools only exist inside the Unity editor.
    public static partial class Graphs
    {
        // Opens the graph editor on scripts\<name>.graph.
        public static string OpenCanvas(string name)
        {
            var path = Path.Combine(Lua.ScriptsDir, name + ".graph");
            if (!File.Exists(path)) return "no such graph: " + path;
            Canvas.Open(path);
            Canvas.ShowGraph();
            Canvas.Show();
            return "opened " + name + ".graph";
        }

        // Opens the Computer screen on its Script tab (the script itself is picked by Lua.OpenInEditor).
        public static void OpenScriptTab()
        {
            Canvas.ShowScript();
            Canvas.Show();
        }

        public static bool EditorOpen => Canvas.IsOpen;

        internal static class Canvas
        {
            // ---- The screen ----

            static bool open, blockingGame;
            static readonly System.Reflection.PropertyInfo inMenu = HarmonyLib.AccessTools.Property(typeof(PlayerState), nameof(PlayerState.InMenu));
            static readonly System.Reflection.MethodInfo closeSettings = HarmonyLib.AccessTools.Method("ModSettings.Mod:Close");
            internal static bool IsOpen => open;

            internal static void Show()
            {
                if (open) return;
                try { closeSettings?.Invoke(null, null); } catch { }   // one window at a time
                open = true;
                // The game's own in-menu flag stops the camera and movement and frees the mouse, as its menus do.
                blockingGame = !PlayerState.InMenu && inMenu?.GetSetMethod(true) != null;
                if (blockingGame) inMenu.SetValue(null, true, null);
            }

            internal static void Hide()
            {
                if (!open) return;
                open = false;
                menuOpen = false;
                drag = Drag.None;
                GUIUtility.keyboardControl = 0;
                if (blockingGame && PlayerState.InMenu) inMenu.SetValue(null, false, null);
                blockingGame = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            // Escape, after the game's Update (which ignores it while we hold the in-menu flag): closes the node menu
            // if it's open, else the editor. The game's look is prepared here too: copying its sprites needs the GPU,
            // which OnGUI shouldn't touch.
            internal static void LateTick()
            {
                if (open && !GameLook.Ready) GameLook.Prepare();
                if (!open || !Input.GetKeyDown(KeyCode.Escape)) return;
                if (menuOpen) menuOpen = false;
                else Hide();
            }

            // ---- The Computer screen: the game's window look around three tabs ----

            enum Tab { Graph, Script, Nodes, Anim, Links, Help }
            static Tab tab;
            internal static void ShowScript() => tab = Tab.Script;
            internal static void ShowGraph() => tab = Tab.Graph;
            internal static void ShowAnim() => tab = Tab.Anim;
            internal static void ShowLinks() => tab = Tab.Links;

            static Rect screen;
            const float TitleH = 46;
            static GUIStyle titleText, titleTag, readout;

            internal static void OnGui()
            {
                if (!open || !GameLook.Ready) return;
                var old = GUI.skin;
                GUI.skin = GameLook.Skin();
                try { DrawScreen(); }
                finally { GUI.skin = old; }
            }

            static void DrawScreen()
            {
                var w = Mathf.Min(Screen.width - 60, 1800f);
                var h = Screen.height - 60f;
                screen = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
                var e = Event.current;
                MakeFrameStyles();
                if (e.type == EventType.Repaint)
                {
                    Sliced(new Rect(screen.x, screen.y + TitleH - 6, screen.width, screen.height - TitleH + 6), GameLook.Body, GameLook.BodyBorder);
                    Sliced(new Rect(screen.x, screen.y, screen.width, TitleH), GameLook.TitleBar, GameLook.TitleBorder);
                }

                // The title in the game's font, then the computer's own tag in its hologram font.
                var title = new GUIContent("Lua Computer");
                var titleW = titleText.CalcSize(title).x;
                GUI.Label(new Rect(screen.x + 18, screen.y, titleW + 4, TitleH), title, titleText);
                var what = tab == Tab.Graph ? (file != null ? Name + ".graph" : "graphs") : tab == Tab.Script ? "scripts" : tab == Tab.Anim ? "animation" : tab == Tab.Help ? "help" : tab == Tab.Links ? "links" : "nodes";
                GUI.Label(new Rect(screen.x + 30 + titleW, screen.y + 1, 700, TitleH), "// " + what.ToUpperInvariant(), titleTag);

                // The close cross, drawn like the game's.
                var x = new Rect(screen.xMax - TitleH - 4, screen.y, TitleH, TitleH);
                if (e.type == EventType.Repaint)
                {
                    var c = x.center;
                    const float k = 9f;
                    var colour = x.Contains(e.mousePosition) ? new Color(0.72f, 0.18f, 0.12f) : new Color(0.16f, 0.16f, 0.17f);
                    Line(c + new Vector2(-k, -k), c + new Vector2(k, k), 5, colour);
                    Line(c + new Vector2(-k, k), c + new Vector2(k, -k), 5, colour);
                }
                if (GUI.Button(x, GUIContent.none, GUIStyle.none)) { Hide(); return; }

                GUILayout.BeginArea(new Rect(screen.x + 18, screen.y + TitleH + 12, screen.width - 36, screen.height - TitleH - 26));
                GUILayout.BeginHorizontal();
                foreach (Tab t in Enum.GetValues(typeof(Tab)))
                    if (GUILayout.Toggle(tab == t, t.ToString(), GameLook.TabStyle(), GUILayout.Width(120)) && tab != t)
                    {
                        tab = t;
                        menuOpen = false;
                        GUIUtility.keyboardControl = 0;
                    }
                GUILayout.FlexibleSpace();
                var state = Readout(out var lit);
                readout.normal.textColor = lit;
                GUILayout.Label(state, readout, GUILayout.Height(34));
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
                switch (tab)
                {
                    case Tab.Graph: Draw(); break;
                    case Tab.Script: Lua.DrawScriptEditor(screen.height - 420); break;
                    case Tab.Nodes: NodeEditor.Draw(screen.height - 400); break;
                    case Tab.Anim: AnimEditor.Draw(screen.height - 330); break;
                    case Tab.Help: Help.Draw(screen.height - 230); break;
                    case Tab.Links: LinksEditor.Draw(screen.height - 260); break;
                }
                GUILayout.EndArea();
            }

            // The status readout, in the hologram font: is the open graph running, failing, or waiting for a computer.
            static string Readout(out Color colour)
            {
                if (tab == Tab.Graph && file != null)
                {
                    if (compileStatus.Contains("Won't run")) { colour = new Color(1f, 0.42f, 0.35f); return "FAULT"; }
                    if (Lua.StatusOf(file).Count > 0) { colour = new Color(0.45f, 1f, 0.55f); return "ONLINE"; }
                    colour = new Color(0.7f, 0.72f, 0.75f);
                    return "STANDBY";
                }
                colour = new Color(0.7f, 0.72f, 0.75f);
                return "READY";
            }

            static void MakeFrameStyles()
            {
                MakeTextures();
                if (titleText != null) return;
                titleText = new GUIStyle(GUI.skin.label) { font = GameLook.TitleFont, fontSize = 21, alignment = TextAnchor.MiddleLeft };
                titleText.normal.textColor = new Color(0.17f, 0.17f, 0.18f);
                titleTag = new GUIStyle(GUI.skin.label) { font = GameLook.DisplayFont, fontSize = 13, alignment = TextAnchor.MiddleLeft };
                titleTag.normal.textColor = new Color(0.3f, 0.3f, 0.32f);
                readout = new GUIStyle(GUI.skin.label) { font = GameLook.DisplayFont, fontSize = 15, alignment = TextAnchor.MiddleRight, padding = new RectOffset(0, 8, 0, 0) };
            }

            // A texture drawn 9-slice: its border keeps its size, the middle stretches.
            static readonly Dictionary<Texture2D, GUIStyle> sliced = new Dictionary<Texture2D, GUIStyle>();
            static void Sliced(Rect r, Texture2D t, RectOffset border)
            {
                if (t == null) return;
                if (!sliced.TryGetValue(t, out var style))
                {
                    style = new GUIStyle { border = border ?? new RectOffset(0, 0, 0, 0) };
                    style.normal.background = t;
                    sliced[t] = style;
                }
                style.Draw(r, false, false, false, false);
            }

            // The Scripts tab's Graphs mode: the list, opening the editor on a pick.
            internal static void DrawLauncher()
            {
                GUILayout.Label("Graphs open in the graph editor, a screen of its own. Using a Lua Computer that runs a graph opens it too.");
                GUILayout.BeginHorizontal();
                DrawList(true);
                GUILayout.BeginVertical();
                GUI.enabled = file != null;
                if (GUILayout.Button(file != null ? $"Open the editor on {Name}.graph" : "Pick a graph", GUILayout.Width(260))) Show();
                GUI.enabled = true;
                if (message != "") GUILayout.Label(message);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }

            static int seenVersion;
            static string file;                 // full path of the open graph, or null
            static Graph graph;
            static string saved = "", message = "", newName = "", rateText = "";
            static DateTime fileTime;
            static Vector2 listScroll, luaScroll;
            static bool showLua;

            // The view: screen = canvas * zoom + pan, in the canvas area's own pixels.
            static Vector2 pan = new Vector2(20, 20);
            static float zoom = 1f;

            enum Drag { None, Node, Wire, Pan, Box, Comment, Resize }
            static Drag drag;
            static Vector2 dragStart, dragOffset;
            static bool dragMoved;
            static int dragNode, wireNode;
            static string wirePort;

            static bool menuOpen;
            static Vector2 menuAt;
            static string menuSearch = "";
            static Vector2 menuScroll;
            // The canvas's last real size. Every IMGUI event comes after a Layout pass in which a GUILayout rect is a 1x1
            // placeholder, so anything worked out then (the menu's place, once) is wrong for the click that follows.
            static Rect canvasView;
            static bool menuBarDrag;
            static float menuBarGrab;

            static int selected = -1, pendingDelete = -1;     // selected: the node whose help shows below the canvas
            static readonly HashSet<int> picked = new HashSet<int>();   // the selected nodes
            static Dictionary<int, Vector2> dragFrom = new Dictionary<int, Vector2>();   // moving nodes' places at the drag's start
            static Vector2 boxStart, lastMouse, commentFrom, commentSizeFrom;
            static Comment dragComment, pickedComment, editingComment, pendingDeleteComment;
            static bool deletePicked;
            const string CommentField = "EvoCoreGraphCommentTitle";
            // A menu entry that makes a comment box rather than a node.
            static readonly NodeType CommentEntry = new NodeType { Name = "comment box", Category = "Layout", Help = "a labelled box behind nodes, to group and explain them; drag its title to move it with what's inside, its corner to resize, double-click the title to rename" };
            static string compiledFrom, compileStatus = "", compiledLua = "";

            const float NodeW = 190, Head = 24, Row = 22, Port = 11;
            const string MenuSearch = "EvoCoreGraphMenuSearch";
            static readonly Regex validName = new Regex(@"^[\w\-. ]+$");
            static readonly Color inColour = new Color(0.35f, 0.7f, 1f), outColour = new Color(1f, 0.6f, 0.2f);

            static GUIStyle label, labelRight, head, field, code;
            static float stylesFor = -1;

            static string Name => Path.GetFileNameWithoutExtension(file);
            static bool Dirty => graph != null && Write(graph, Name) != saved;

            static void Draw()
            {
                HandleKeys();
                GUILayout.BeginHorizontal();
                DrawList(false);
                GUILayout.BeginVertical();
                if (file == null) GUILayout.Label("Pick a graph on the left, or make a new one. A Lua Computer labelled `name` runs name.graph (before name.lua).");
                else if (graph == null) GUILayout.Label(message);
                else DrawEditor();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                Snapshot();
            }

            // ---- The file list ----

            // The graphs in the scripts folder; from the Scripts tab (launch) a pick also opens the editor.
            static void DrawList(bool launch)
            {
                GUILayout.BeginVertical(GUILayout.Width(170));
                var made = Lua.NewRow("graph");
                if (made != null)
                {
                    Create(made);
                    if (launch && graph != null) Show();
                }
                listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(launch ? 260 : 360));
                Lua.ComputerListEntry(file, true);
                foreach (var path in Files())
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var runs = Lua.StatusOf(path).Count;
                    if (GUILayout.Button((Same(path, file) ? "> " : "") + name + (runs > 0 ? $"  ({runs} running)" : ""), GUI.skin.label))
                    {
                        Open(path);
                        if (launch && Same(path, file)) Show();
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
            }

            static IEnumerable<string> Files()
            {
                try { return Directory.GetFiles(Lua.ScriptsDir, "*.graph").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { return Enumerable.Empty<string>(); }
            }

            internal static void Open(string path)
            {
                if (Same(path, file)) return;
                if (Dirty) { message = "Save or revert " + Name + ".graph first."; return; }
                file = path;
                Load();
                pan = new Vector2(20, 20);
                zoom = 1f;
            }

            static void Load()
            {
                selected = -1;
                menuOpen = false;
                drag = Drag.None;
                try
                {
                    fileTime = File.GetLastWriteTimeUtc(file);
                    graph = Parse(File.ReadAllText(file));
                    PlaceUnplaced();
                    saved = Write(graph, Name);
                    current = saved;
                    undo.Clear();
                    redo.Clear();
                    picked.Clear();
                    pickedComment = editingComment = null;
                    rateText = graph.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    message = "";
                }
                catch (GraphException e) { graph = null; message = $"{Path.GetFileName(file)} can't open in the canvas: {e.Message}. Fix it in a text editor and pick it again."; }
                catch (Exception e) { graph = null; message = "Couldn't open it: " + e.Message; }
            }

            static void Create(string name)
            {
                if (name.EndsWith(".graph", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 6);
                if (name == "" || !validName.IsMatch(name)) { message = "Use letters, numbers, spaces, - _ or . in the name."; return; }
                var path = Path.Combine(Lua.ScriptsDir, name + ".graph");
                if (Dirty) { message = "Save or revert " + Name + ".graph first."; return; }
                if (!File.Exists(path))
                {
                    try
                    {
                        File.WriteAllText(path, $"# {name}.graph\nnode 1 input index=1 at=0,40\nnode 2 output index=1 at=260,40\nwire 1.out 2.in\n");
                    }
                    catch (Exception e) { message = "Couldn't create it: " + e.Message; return; }
                }
                newName = "";
                file = null;
                Open(path);
                message = $"Made {name}.graph: link in 1 goes straight to link out 1. Label a Lua Computer \"{name}\" to run it.";
            }

            static void Save()
            {
                try
                {
                    var text = Write(graph, Name);
                    File.WriteAllText(file, text.Replace("\n", Environment.NewLine));
                    saved = text;
                    fileTime = File.GetLastWriteTimeUtc(file);
                    message = "Saved " + DateTime.Now.ToString("HH:mm:ss") + ". Computers running it reload in a second.";
                    Lua.RescanSoon();
                }
                catch (Exception e) { message = "Couldn't save: " + e.Message; }
            }

            // Another editor saved the file: take its version unless there are edits here.
            static void CheckDisk()
            {
                try
                {
                    if (!File.Exists(file)) { message = "The file is gone from disk; Save writes it again."; return; }
                    // A node file changed: reload, so nodes show their new ports (unless there are edits here).
                    if (seenVersion != Version && !Dirty) { seenVersion = Version; Load(); return; }
                    var time = File.GetLastWriteTimeUtc(file);
                    if (time == fileTime) return;
                    if (Dirty) { fileTime = time; message = "Changed on disk too: Save keeps your version, Revert takes the disk's."; return; }
                    Load();
                    message = "Reloaded: changed on disk.";
                }
                catch { }
            }

            // Nodes from a hand-written file have no place yet: columns by how far they are from the graph's start.
            static void PlaceUnplaced()
            {
                var unplaced = graph.Nodes.Values.Where(n => !n.Placed).ToList();
                if (unplaced.Count == 0) return;
                var depth = graph.Nodes.Keys.ToDictionary(id => id, id => 0);
                for (int pass = 0; pass < graph.Nodes.Count; pass++)
                    foreach (var w in graph.Wires)
                        if (w.fromNode != w.toNode && depth.ContainsKey(w.fromNode) && depth.ContainsKey(w.toNode) && depth[w.toNode] < depth[w.fromNode] + 1 && depth[w.fromNode] < graph.Nodes.Count)
                            depth[w.toNode] = depth[w.fromNode] + 1;
                var rows = new Dictionary<int, float>();
                foreach (var node in unplaced)
                {
                    var column = depth[node.Id];
                    rows.TryGetValue(column, out var y);
                    node.X = column * (NodeW + 70);
                    node.Y = y;
                    node.Placed = true;
                    rows[column] = y + Height(node) + 20;
                }
            }

            // ---- The editor ----

            static void DrawEditor()
            {
                CheckDisk();
                GUILayout.BeginHorizontal();
                GUILayout.Label(Name + ".graph" + (Dirty ? "  (unsaved)" : ""), GUILayout.ExpandWidth(true));
                GUILayout.Label("rate", GUILayout.Width(30));
                var rate = GUILayout.TextField(rateText, GUILayout.Width(45));
                if (rate != rateText)
                {
                    rateText = rate;
                    if (double.TryParse(rate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0) graph.Rate = r;
                }
                if (GUILayout.Button("View: reset", GUILayout.Width(110))) { pan = new Vector2(20, 20); zoom = 1f; }
                showLua = GUILayout.Toggle(showLua, "Show Lua", GUI.skin.button, GUILayout.Width(100));
                Lua.ComputerButtons(file);
                GUI.enabled = Dirty;
                if (GUILayout.Button("Save", GUILayout.Width(80))) Save();
                if (GUILayout.Button("Revert", GUILayout.Width(80))) { Load(); message = "Reverted."; }
                GUI.enabled = true;
                var gone = Lua.DeleteButton(file);
                GUILayout.EndHorizontal();
                if (gone != null)
                {
                    message = $"Deleted {Path.GetFileName(file)}: moved to {Path.GetFileName(Path.GetDirectoryName(gone))}\\{Path.GetFileName(gone)}.";
                    file = null;
                    graph = null;
                    return;
                }

                // A selected lua node's code opens under the canvas.
                Node coding = null;
                if (picked.Count <= 1 && selected >= 0 && graph.Nodes.TryGetValue(selected, out var maybe) && maybe.Type.InsOf != null) coding = maybe;
                DropDeadWires();

                // As wide as the screen allows, and as tall as leaves room for the lines under it.
                var height = Mathf.Max(260, screen.height - 290 - (showLua ? 230 : 0) - (coding != null ? CodeH + 26 : 0));
                var area = GUILayoutUtility.GetRect(300, 10000, height, height, GUILayout.ExpandWidth(true));
                DrawCanvas(area);
                if (coding != null)
                {
                    GUILayout.Label($"node {coding.Id}'s Lua: inputs.get(1) is in1, outputs.set(1, v) sets out1; tick(dt) and on_input(i, value) run each graph tick. Its ports: inputs= and outputs= on the node.");
                    var code = Lua.CodeView.Draw(coding.Script ?? "", ref codeScroll, CodeH, "EvoCoreGraphLua", codeError, out _);
                    if (code != (coding.Script ?? "")) coding.Script = code;
                }

                UpdateCompile();
                GUILayout.Label(compileStatus, Rich());
                if (picked.Count > 1)
                    GUILayout.Label($"{picked.Count} nodes picked.  Drag a title: move them.  Ctrl+C copy, Ctrl+D duplicate, Ctrl+G comment box around them, Delete remove.");
                else if (selected >= 0 && graph.Nodes.TryGetValue(selected, out var sel))
                    GUILayout.Label($"node {sel.Id}, {sel.Type.Name}: {sel.Type.Help}.");
                else if (pickedComment != null)
                    GUILayout.Label("Comment box: drag its title to move it with what's inside, its corner to resize; double-click the title to rename; Delete removes it.");
                else
                    GUILayout.Label("Right-click: add.  Drag out (orange) to in (blue): wire.  Drag empty space: box select (Shift adds).  Right/middle drag: pan.  Wheel: zoom.  Ctrl+Z/Y undo/redo, Ctrl+C/V copy/paste, Ctrl+A all.");
                if (message != "") GUILayout.Label(message);
                var runs = Lua.StatusOf(file);
                if (runs.Count == 0) GUILayout.Label($"Not running. Label a Lua Computer \"{Name}\" with the Tag tool to run it.");
                else if (Dirty) GUILayout.Label("Live values pause while there are unsaved changes.");
                else if (live != null) GUILayout.Label($"<color=#a1ffe0>Live: wire values from the {liveFrom}.</color>", Rich());
                foreach (var line in runs) GUILayout.Label(line);
                if (showLua)
                {
                    luaScroll = GUILayout.BeginScrollView(luaScroll, GUILayout.Height(220));
                    GUILayout.Label(compiledLua, Code());
                    GUILayout.EndScrollView();
                }
            }

            const float CodeH = 230;
            static Vector2 codeScroll;
            static int codeError;

            // Wires into or out of ports a lua node no longer has (its inputs= or outputs= went down) are dropped.
            static void DropDeadWires()
            {
                graph.Wires.RemoveAll(w =>
                    graph.Nodes.TryGetValue(w.fromNode, out var from) && from.Type.OutsOf != null && !from.Outs.Contains(w.fromPort) ||
                    graph.Nodes.TryGetValue(w.toNode, out var to) && to.Type.InsOf != null && !to.Ins.Contains(w.toPort));
            }

            static void UpdateCompile()
            {
                var text = Write(graph, Name);
                if (text == compiledFrom) return;
                compiledFrom = text;
                try
                {
                    compiledLua = Compile(text, Name);
                    compileStatus = $"<color=#80ff80>Compiles: {graph.Nodes.Count} nodes, {graph.Wires.Count} wires.</color>";
                    codeError = 0;
                }
                catch (GraphException e)
                {
                    // A lua node's syntax error marks its line in the code panel.
                    codeError = e.Message.StartsWith("node ") ? Lua.CodeView.ErrorLine(e.Message) : 0;
                    compiledLua = e.Message;
                    compileStatus = "<color=#ff8080>Won't run: " + e.Message + "</color>";
                }
            }

            static void HandleKeys()
            {
                var e = Event.current;
                if (e.type != EventType.KeyDown || graph == null) return;
                var ctrl = e.control || e.command;
                if (e.keyCode == KeyCode.S && ctrl) { if (Dirty) Save(); e.Use(); return; }
                if (GUIUtility.keyboardControl != 0) return;   // typing in a field: the keys are the field's
                if (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace)
                {
                    if (picked.Count > 0) deletePicked = true;
                    else if (pickedComment != null) pendingDeleteComment = pickedComment;
                    e.Use();
                }
                else if (ctrl && e.keyCode == KeyCode.Z) { if (e.shift) Redo(); else Undo(); e.Use(); }
                else if (ctrl && e.keyCode == KeyCode.Y) { Redo(); e.Use(); }
                else if (ctrl && e.keyCode == KeyCode.A) { picked.Clear(); foreach (var id in graph.Nodes.Keys) picked.Add(id); e.Use(); }
                else if (ctrl && e.keyCode == KeyCode.C)
                {
                    if (picked.Count > 0) { GUIUtility.systemCopyBuffer = Fragment(); message = $"Copied {picked.Count} node(s) as .graph text."; }
                    e.Use();
                }
                else if (ctrl && e.keyCode == KeyCode.V) { Paste(GUIUtility.systemCopyBuffer, (lastMouse - pan) / zoom); e.Use(); }
                else if (ctrl && e.keyCode == KeyCode.D)
                {
                    if (picked.Count > 0) Paste(Fragment(), TopLeft(picked) + new Vector2(40, 40));
                    e.Use();
                }
                else if (ctrl && e.keyCode == KeyCode.G) { if (picked.Count > 0) WrapInComment(); e.Use(); }
            }

            // ---- Copy, paste, comments around a selection, undo ----

            static Vector2 TopLeft(IEnumerable<int> ids) => ids.Where(graph.Nodes.ContainsKey).Select(id => graph.Nodes[id])
                .Aggregate(new Vector2(float.MaxValue, float.MaxValue), (m, n) => Vector2.Min(m, new Vector2(n.X, n.Y)));

            // The picked nodes and the wires between them, as .graph text (for the system clipboard, so a piece of a
            // graph can be pasted into another graph, or into a note or message).
            static string Fragment()
            {
                var g = new Graph { Rate = graph.Rate };
                foreach (var id in picked) if (graph.Nodes.TryGetValue(id, out var n)) g.Nodes[id] = n;
                foreach (var w in graph.Wires) if (picked.Contains(w.fromNode) && picked.Contains(w.toNode)) g.Wires.Add(w);
                return Write(g, "copied nodes");
            }

            // Adds a piece of .graph text with new node numbers, its top-left corner at `at`; the pasted nodes end up picked.
            static void Paste(string text, Vector2 at)
            {
                Graph g;
                try { g = Parse(text ?? ""); }
                catch (GraphException) { message = "The clipboard doesn't hold graph nodes."; return; }
                if (g.Nodes.Count == 0) { message = "The clipboard doesn't hold graph nodes."; return; }
                var min = g.Nodes.Values.Aggregate(new Vector2(float.MaxValue, float.MaxValue), (m, n) => Vector2.Min(m, new Vector2(n.X, n.Y)));
                var next = graph.Nodes.Count > 0 ? graph.Nodes.Keys.Max() + 1 : 1;
                var ids = new Dictionary<int, int>();
                picked.Clear();
                foreach (var n in g.Nodes.Values)
                {
                    var copy = new Node { Id = next, Type = n.Type, X = Snap(n.X - min.x + at.x), Y = Snap(n.Y - min.y + at.y), Placed = true };
                    foreach (var setting in n.Settings) copy.Settings[setting.Key] = setting.Value;
                    copy.Script = n.Script;
                    graph.Nodes[next] = copy;
                    ids[n.Id] = next;
                    picked.Add(next);
                    selected = next;
                    next++;
                }
                foreach (var w in g.Wires)
                    if (ids.ContainsKey(w.fromNode) && ids.ContainsKey(w.toNode)) graph.Wires.Add((ids[w.fromNode], w.fromPort, ids[w.toNode], w.toPort, 0));
                message = $"Pasted {g.Nodes.Count} node(s).";
            }

            // What Ctrl+C and Ctrl+V do, callable from DevBridge (which can't press keys): get EvoCore.Graphs.Canvas.CopyPicked()
            internal static string CopyPicked() { GUIUtility.systemCopyBuffer = Fragment(); return GUIUtility.systemCopyBuffer; }
            internal static string PasteClipboard(float x, float y) { Paste(GUIUtility.systemCopyBuffer, new Vector2(x, y)); return message; }

            // A comment box around the picked nodes, its title ready to type.
            static void WrapInComment()
            {
                var boxes = picked.Where(graph.Nodes.ContainsKey).Select(id => graph.Nodes[id]).Select(n => new Rect(n.X, n.Y, NodeW, Height(n))).ToList();
                if (boxes.Count == 0) return;
                float x0 = boxes.Min(r => r.xMin) - 20, y0 = boxes.Min(r => r.yMin) - 46, x1 = boxes.Max(r => r.xMax) + 20, y1 = boxes.Max(r => r.yMax) + 20;
                var c = new Comment { X = Snap(x0), Y = Snap(y0), W = Snap(x1 - x0), H = Snap(y1 - y0) };
                graph.Comments.Add(c);
                pickedComment = editingComment = c;
                picked.Clear();
            }

            // Undo keeps whole-graph snapshots (graphs are small). After each event, any change since the last snapshot
            // is a step, except typing in a field within a second of the last step, which joins it. Nothing is taken
            // mid-drag, so a drag is one step.
            static readonly List<string> undo = new List<string>(), redo = new List<string>();
            static string current;
            static float lastStep;

            static void Snapshot()
            {
                if (graph == null || drag != Drag.None) return;
                var now = Write(graph, Name);
                if (current == null) { current = now; return; }
                if (now == current) return;
                var typing = GUIUtility.keyboardControl != 0;
                if (!(typing && Time.realtimeSinceStartup - lastStep < 1f && undo.Count > 0))
                {
                    undo.Add(current);
                    if (undo.Count > 200) undo.RemoveAt(0);
                }
                redo.Clear();
                current = now;
                lastStep = Time.realtimeSinceStartup;
            }

            static void Undo()
            {
                if (undo.Count == 0) { message = "Nothing to undo."; return; }
                var back = undo[undo.Count - 1];
                undo.RemoveAt(undo.Count - 1);
                redo.Add(current);
                Restore(back);
            }

            static void Redo()
            {
                if (redo.Count == 0) { message = "Nothing to redo."; return; }
                var forward = redo[redo.Count - 1];
                redo.RemoveAt(redo.Count - 1);
                undo.Add(current);
                Restore(forward);
            }

            static void Restore(string text)
            {
                try { graph = Parse(text); }
                catch (GraphException e) { message = "Couldn't undo: " + e.Message; return; }
                current = text;
                picked.Clear();
                selected = -1;
                pickedComment = editingComment = null;
                rateText = graph.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // ---- The canvas ----

            // ---- Live values: the running graph's wires, read once a frame ----

            static Dictionary<string, double> live;
            static string liveFrom;
            static int liveFrame = -1;

            static void ReadLive()
            {
                if (liveFrame == Time.frameCount) return;
                liveFrame = Time.frameCount;
                // With unsaved edits the node numbers may not match what runs, so the view pauses.
                live = file != null && !Dirty ? Lua.GraphValues(file, out liveFrom) : null;
            }

            static bool Live(int node, string port, out double value)
            {
                value = 0;
                return live != null && live.TryGetValue(node + "." + port, out value);
            }

            static string Show(double v)
            {
                if (Math.Abs(v - Math.Round(v)) < 1e-6 && Math.Abs(v) < 1e9) return Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return v.ToString(Math.Abs(v) < 1000 ? "0.###" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
            }

            static void DrawCanvas(Rect area)
            {
                MakeStyles();
                ReadLive();
                if (Event.current.type == EventType.Repaint)
                {
                    Fill(area, new Color(0.075f, 0.08f, 0.095f, 1f));
                    Border(area, new Color(1, 1, 1, 0.08f));
                }
                GUI.BeginGroup(area);
                var view = new Rect(0, 0, area.width, area.height);
                if (Event.current.type != EventType.Layout) canvasView = view;
                if (view.Contains(Event.current.mousePosition)) lastMouse = Event.current.mousePosition;   // where Ctrl+V pastes
                if (Event.current.type == EventType.Repaint) DrawGrid(view);

                var rects = graph.Nodes.Values.ToDictionary(n => n.Id, NodeRect);
                HandleMouse(view, rects);
                rects = graph.Nodes.Values.ToDictionary(n => n.Id, NodeRect);
                foreach (var c in graph.Comments.ToList()) DrawComment(c);

                if (Event.current.type == EventType.Repaint)
                {
                    foreach (var w in graph.Wires)
                        if (rects.ContainsKey(w.fromNode) && rects.ContainsKey(w.toNode))
                        {
                            // Live: on wires bright and thick, off wires dimmed.
                            var colour = WireColour(graph.Nodes[w.fromNode].Type);
                            var width = 1f;
                            if (Live(w.fromNode, w.fromPort, out var v))
                            {
                                if (v > 0.5) { colour = Color.Lerp(colour, Color.white, 0.45f); width = 1.5f; }
                                else colour = new Color(colour.r, colour.g, colour.b, 0.45f);
                            }
                            Curve(OutPort(w.fromNode, w.fromPort, rects), InPort(w.toNode, w.toPort, rects), colour, width);
                        }
                    if (drag == Drag.Wire && graph.Nodes.ContainsKey(wireNode))
                        Curve(OutPort(wireNode, wirePort, rects), Event.current.mousePosition, WireColour(graph.Nodes[wireNode].Type));
                }
                foreach (var node in graph.Nodes.Values.ToList()) DrawNode(node, rects[node.Id]);
                if (live != null && Event.current.type == EventType.Repaint)
                    foreach (var w in graph.Wires)
                        if (rects.ContainsKey(w.fromNode) && rects.ContainsKey(w.toNode) && Live(w.fromNode, w.fromPort, out var v))
                            Pill(CurvePoint(OutPort(w.fromNode, w.fromPort, rects), InPort(w.toNode, w.toPort, rects), 0.5f), Show(v), v > 0.5);
                if (drag == Drag.Box && Event.current.type == EventType.Repaint)
                {
                    var box = BoxRect(boxStart, Event.current.mousePosition);
                    Fill(box, new Color(1f, 0.78f, 0.3f, 0.08f));
                    Border(box, new Color(1f, 0.78f, 0.3f, 0.7f));
                }
                if (menuOpen) DrawMenu(view);
                GUI.EndGroup();

                if (deletePicked)
                {
                    foreach (var id in picked) graph.Nodes.Remove(id);
                    graph.Wires.RemoveAll(w => picked.Contains(w.fromNode) || picked.Contains(w.toNode));
                    picked.Clear();
                    selected = -1;
                    deletePicked = false;
                }
                if (pendingDeleteComment != null)
                {
                    graph.Comments.Remove(pendingDeleteComment);
                    if (pickedComment == pendingDeleteComment) pickedComment = null;
                    if (editingComment == pendingDeleteComment) editingComment = null;
                    pendingDeleteComment = null;
                }

                if (pendingDelete >= 0)
                {
                    graph.Nodes.Remove(pendingDelete);
                    graph.Wires.RemoveAll(w => w.fromNode == pendingDelete || w.toNode == pendingDelete);
                    if (selected == pendingDelete) selected = -1;
                    picked.Remove(pendingDelete);
                    pendingDelete = -1;
                }
            }

            // Fine lines every 20 units, stronger ones every 100.
            static void DrawGrid(Rect view)
            {
                var step = 20f * zoom;
                if (step < 6) step *= 5;
                var fine = new Color(1, 1, 1, 0.025f);
                var strong = new Color(1, 1, 1, 0.06f);
                var first = Mathf.Floor(-pan.x / step);
                for (var k = first; k * step + pan.x < view.width; k++)
                    Fill(new Rect(k * step + pan.x, 0, 1, view.height), Mathf.RoundToInt(k) % 5 == 0 ? strong : fine);
                first = Mathf.Floor(-pan.y / step);
                for (var k = first; k * step + pan.y < view.height; k++)
                    Fill(new Rect(0, k * step + pan.y, view.width, 1), Mathf.RoundToInt(k) % 5 == 0 ? strong : fine);
            }

            // A node's size in canvas units: a title, a row per input/output pair (inputs left, outputs right), then a
            // row per setting.
            static int PortRows(Node n) => Math.Max(n.Ins.Length, n.Outs.Length);
            static float Height(Node n) => Head + Row * (PortRows(n) + Settings(n.Type).Count()) + (Settings(n.Type).Any() ? 6 : 0) + 6;
            static IEnumerable<string> Settings(NodeType t) => t.Settings.Keys;

            static Rect NodeRect(Node n) => new Rect(n.X * zoom + pan.x, n.Y * zoom + pan.y, NodeW * zoom, Height(n) * zoom);

            static Vector2 InPort(int id, string port, Dictionary<int, Rect> rects)
            {
                var i = Array.IndexOf(graph.Nodes[id].Ins, port);
                return new Vector2(rects[id].x, rects[id].y + (Head + 3 + Row * i + Row / 2) * zoom);
            }

            static Vector2 OutPort(int id, string port, Dictionary<int, Rect> rects)
            {
                var i = Array.IndexOf(graph.Nodes[id].Outs, port);
                return new Vector2(rects[id].xMax, rects[id].y + (Head + 3 + Row * i + Row / 2) * zoom);
            }

            static bool Near(Vector2 a, Vector2 b) => (a - b).sqrMagnitude <= Mathf.Pow(Mathf.Max(8f, Port * zoom), 2);

            static bool HitOut(Vector2 m, Dictionary<int, Rect> rects, out int id, out string port)
            {
                foreach (var n in graph.Nodes.Values.Reverse())
                    foreach (var p in n.Outs)
                        if (Near(m, OutPort(n.Id, p, rects))) { id = n.Id; port = p; return true; }
                id = -1; port = null;
                return false;
            }

            static bool HitIn(Vector2 m, Dictionary<int, Rect> rects, out int id, out string port)
            {
                foreach (var n in graph.Nodes.Values.Reverse())
                    foreach (var p in n.Ins)
                        if (Near(m, InPort(n.Id, p, rects))) { id = n.Id; port = p; return true; }
                id = -1; port = null;
                return false;
            }

            static void HandleMouse(Rect view, Dictionary<int, Rect> rects)
            {
                var e = Event.current;
                var m = e.mousePosition;
                switch (e.type)
                {
                    case EventType.MouseDown:
                        if (!view.Contains(m)) return;
                        if (menuOpen)
                        {
                            if (MenuRect().Contains(m)) return;   // the menu's own controls take it
                            menuOpen = false;
                            e.Use();
                            return;
                        }
                        // A click away from the comment title being typed ends the typing.
                        if (editingComment != null && !CommentHead(editingComment).Contains(m)) { editingComment = null; GUIUtility.keyboardControl = 0; }
                        if (e.button == 0)
                        {
                            if (HitOut(m, rects, out var outNode, out var outPort))
                            {
                                drag = Drag.Wire; wireNode = outNode; wirePort = outPort;
                                e.Use();
                                return;
                            }
                            if (HitIn(m, rects, out var inNode, out var inPort))
                            {
                                var i = graph.Wires.FindIndex(w => w.toNode == inNode && w.toPort == inPort);
                                if (i >= 0)
                                {
                                    // Pick the wire up by its end: it stays on its output and follows the mouse.
                                    drag = Drag.Wire; wireNode = graph.Wires[i].fromNode; wirePort = graph.Wires[i].fromPort;
                                    graph.Wires.RemoveAt(i);
                                    e.Use();
                                    return;
                                }
                            }
                            var hit = graph.Nodes.Values.Reverse().FirstOrDefault(n => rects[n.Id].Contains(m));
                            if (hit != null)
                            {
                                // Shift adds or removes; a plain click on an unpicked node picks just it.
                                pickedComment = null;
                                if (e.shift) { if (!picked.Remove(hit.Id)) picked.Add(hit.Id); }
                                else if (!picked.Contains(hit.Id)) { picked.Clear(); picked.Add(hit.Id); }
                                selected = hit.Id;
                                var head = new Rect(rects[hit.Id].x, rects[hit.Id].y, rects[hit.Id].width - 22 * zoom, Head * zoom);
                                if (head.Contains(m) && picked.Contains(hit.Id))
                                {
                                    // Every picked node moves together.
                                    drag = Drag.Node; dragStart = m;
                                    dragFrom = picked.Where(graph.Nodes.ContainsKey).ToDictionary(id => id, id => new Vector2(graph.Nodes[id].X, graph.Nodes[id].Y));
                                    GUIUtility.keyboardControl = 0;
                                    e.Use();
                                }
                                return;   // a click in the body goes on to the node's fields
                            }
                            // Comments sit behind the nodes: the title moves one with what's inside it, a double-click
                            // there types its title, the corner resizes, the cross removes it.
                            for (int i = graph.Comments.Count - 1; i >= 0; i--)
                            {
                                var c = graph.Comments[i];
                                if (CommentClose(c).Contains(m)) { pendingDeleteComment = c; e.Use(); return; }
                                if (CommentHead(c).Contains(m))
                                {
                                    if (c == editingComment) return;   // the title field takes the click
                                    pickedComment = c;
                                    picked.Clear();
                                    selected = -1;
                                    if (e.clickCount == 2) { editingComment = c; e.Use(); return; }
                                    var inside = CommentRect(c);
                                    drag = Drag.Comment; dragComment = c; dragStart = m; commentFrom = new Vector2(c.X, c.Y);
                                    dragFrom = graph.Nodes.Values.Where(n => inside.Contains(rects[n.Id].center)).ToDictionary(n => n.Id, n => new Vector2(n.X, n.Y));
                                    GUIUtility.keyboardControl = 0;
                                    e.Use();
                                    return;
                                }
                                if (CommentCorner(c).Contains(m))
                                {
                                    pickedComment = c;
                                    drag = Drag.Resize; dragComment = c; dragStart = m; commentSizeFrom = new Vector2(c.W, c.H);
                                    e.Use();
                                    return;
                                }
                            }
                            // Empty canvas, or a comment's body: a box selection (Shift keeps what's picked).
                            if (!e.shift) { picked.Clear(); selected = -1; }
                            pickedComment = null;
                            drag = Drag.Box; boxStart = m;
                            GUIUtility.keyboardControl = 0;
                            e.Use();
                        }
                        else if (e.button == 1 || e.button == 2)
                        {
                            drag = Drag.Pan; dragStart = m; dragMoved = false;
                            e.Use();
                        }
                        break;

                    case EventType.MouseDrag:
                        var moved = (m - dragStart) / zoom;
                        if (drag == Drag.Node || drag == Drag.Comment)
                        {
                            foreach (var from in dragFrom)
                                if (graph.Nodes.TryGetValue(from.Key, out var n)) { n.X = Snap(from.Value.x + moved.x); n.Y = Snap(from.Value.y + moved.y); }
                            if (drag == Drag.Comment) { dragComment.X = Snap(commentFrom.x + moved.x); dragComment.Y = Snap(commentFrom.y + moved.y); }
                            e.Use();
                        }
                        else if (drag == Drag.Resize)
                        {
                            dragComment.W = Mathf.Max(120, Snap(commentSizeFrom.x + moved.x));
                            dragComment.H = Mathf.Max(60, Snap(commentSizeFrom.y + moved.y));
                            e.Use();
                        }
                        else if (drag == Drag.Pan)
                        {
                            pan += e.delta;
                            if ((m - dragStart).sqrMagnitude > 16) dragMoved = true;
                            e.Use();
                        }
                        else if (drag == Drag.Wire || drag == Drag.Box) e.Use();
                        break;

                    case EventType.MouseUp:
                        if (drag == Drag.Wire)
                        {
                            if (HitIn(m, rects, out var toNode, out var toPort))
                            {
                                graph.Wires.RemoveAll(w => w.toNode == toNode && w.toPort == toPort);
                                graph.Wires.Add((wireNode, wirePort, toNode, toPort, 0));
                                graph.Nodes[toNode].Settings.Remove(toPort);   // a wired input has no typed value
                            }
                            drag = Drag.None;
                            e.Use();
                        }
                        else if (drag == Drag.Pan)
                        {
                            if (!dragMoved && e.button == 1) OpenMenu(m);
                            drag = Drag.None;
                            e.Use();
                        }
                        else if (drag == Drag.Box)
                        {
                            var box = BoxRect(boxStart, m);
                            if (box.width > 3 || box.height > 3)
                                foreach (var n in graph.Nodes.Values) if (box.Overlaps(rects[n.Id])) picked.Add(n.Id);
                            if (picked.Count == 1) selected = picked.First();
                            drag = Drag.None;
                            e.Use();
                        }
                        else if (drag != Drag.None) { drag = Drag.None; e.Use(); }
                        break;

                    case EventType.ScrollWheel:
                        if (!view.Contains(m)) return;
                        if (menuOpen && MenuRect().Contains(m))
                        {
                            // The menu's list scrolls itself: Unity's scroll view didn't get the wheel in here. Three rows a
                            // notch, by direction only: Unity 6 reports far smaller wheel steps than older Unity did.
                            if (e.delta.y != 0f) menuScroll.y = Mathf.Clamp(menuScroll.y + Mathf.Sign(e.delta.y) * MenuRow * 3, 0, menuMax);
                            e.Use();
                            return;
                        }
                        var point = (m - pan) / zoom;
                        zoom = Mathf.Clamp(zoom * (e.delta.y > 0 ? 0.9f : 1.1f), 0.4f, 2f);
                        pan = m - point * zoom;
                        e.Use();
                        break;
                }
            }

            static float Snap(float v) => Mathf.Round(v / 10) * 10;
            static Rect BoxRect(Vector2 a, Vector2 b) => Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

            // ---- Comment boxes ----

            static Rect CommentRect(Comment c) => new Rect(c.X * zoom + pan.x, c.Y * zoom + pan.y, c.W * zoom, c.H * zoom);
            static Rect CommentHead(Comment c) { var r = CommentRect(c); return new Rect(r.x, r.y, r.width - 26 * zoom, 28 * zoom); }
            static Rect CommentClose(Comment c) { var r = CommentRect(c); return new Rect(r.xMax - 26 * zoom, r.y, 26 * zoom, 28 * zoom); }
            static Rect CommentCorner(Comment c) { var r = CommentRect(c); return new Rect(r.xMax - 18 * zoom, r.yMax - 18 * zoom, 18 * zoom, 18 * zoom); }

            static void DrawComment(Comment c)
            {
                var r = CommentRect(c);
                var head = new Rect(r.x, r.y, r.width, 28 * zoom);
                var e = Event.current;
                if (e.type == EventType.Repaint)
                {
                    Rounded(r, new Color(0.45f, 0.55f, 0.75f, 0.09f));
                    Rounded(head, new Color(0.45f, 0.55f, 0.75f, 0.26f));
                    if (c == pickedComment) Border(r, new Color(1f, 0.78f, 0.3f, 0.85f));
                    var k = r.max;   // the resize grip: three short diagonals in the corner
                    for (int i = 1; i <= 3; i++) Line(k - new Vector2(4 * i * zoom + 3, 3), k - new Vector2(3, 4 * i * zoom + 3), 1.5f, new Color(1, 1, 1, 0.35f));
                }
                if (c == editingComment)
                {
                    GUI.SetNextControlName(CommentField);
                    c.Text = GUI.TextField(new Rect(head.x + 6 * zoom, head.y + 4 * zoom, head.width - 38 * zoom, head.height - 8 * zoom), c.Text, field);
                    if (GUI.GetNameOfFocusedControl() != CommentField) GUI.FocusControl(CommentField);
                    if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                    {
                        editingComment = null;
                        GUIUtility.keyboardControl = 0;
                        e.Use();
                    }
                }
                else GUI.Label(new Rect(head.x + 10 * zoom, head.y, head.width - 42 * zoom, head.height), c.Text, commentTitle);
                GUI.Label(CommentClose(c), "\u00d7", close);   // the click is taken in HandleMouse
            }

            static void DrawNode(Node n, Rect r)
            {
                var wired = new HashSet<string>(graph.Wires.Where(w => w.toNode == n.Id).Select(w => w.toPort));
                var outWired = new HashSet<string>(graph.Wires.Where(w => w.fromNode == n.Id).Select(w => w.fromPort));
                var colour = Header(n.Type);
                var headH = Head * zoom;
                if (Event.current.type == EventType.Repaint)
                {
                    Rounded(new Rect(r.x + 3 * zoom, r.y + 5 * zoom, r.width, r.height), new Color(0, 0, 0, 0.45f));   // shadow
                    if (picked.Contains(n.Id)) Rounded(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(1f, 0.78f, 0.3f));
                    Rounded(r, new Color(0.155f, 0.165f, 0.19f, 1f));
                    Rounded(new Rect(r.x, r.y, r.width, headH), colour);
                    Fill(new Rect(r.x, r.y + headH - Mathf.Min(6, headH / 2), r.width, Mathf.Min(6, headH / 2)), colour);   // square bottom
                }
                GUI.Label(new Rect(r.x + 8 * zoom, r.y, r.width - 50 * zoom, headH), n.Type.Name, head);
                GUI.Label(new Rect(r.xMax - 52 * zoom, r.y, 28 * zoom, headH), "#" + n.Id, idStyle);
                if (GUI.Button(new Rect(r.xMax - 21 * zoom, r.y + 3 * zoom, 17 * zoom, headH - 6 * zoom), "×", close)) pendingDelete = n.Id;

                var rows = PortRows(n);
                for (int i = 0; i < rows; i++)
                {
                    var y = r.y + (Head + 3 + Row * i) * zoom;
                    var rowH = Row * zoom;
                    var hasOut = i < n.Outs.Length;
                    if (i < n.Ins.Length)
                    {
                        var p = n.Ins[i];
                        PortDot(new Vector2(r.x, y + rowH / 2), inColour, wired.Contains(p));
                        GUI.Label(new Rect(r.x + 10 * zoom, y, 44 * zoom, rowH), p, label);
                        if (!wired.Contains(p))
                        {
                            // An unwired input's value, typed in; empty means the node's default (shown greyed).
                            n.Settings.TryGetValue(p, out var v);
                            var width = hasOut ? r.width * 0.5f - 52 * zoom : r.width - 64 * zoom;
                            var box = new Rect(r.x + 52 * zoom, y + 3 * zoom, width, rowH - 6 * zoom);
                            var typed = GUI.TextField(box, v ?? "", field);
                            if (typed != (v ?? "")) { if (typed == "") n.Settings.Remove(p); else n.Settings[p] = typed; }
                            if (string.IsNullOrEmpty(typed) && Event.current.type == EventType.Repaint)
                                GUI.Label(new Rect(box.x + 4 * zoom, box.y, box.width, box.height), Default(n.Type, p), hint);
                        }
                    }
                    if (hasOut)
                    {
                        var p = n.Outs[i];
                        GUI.Label(new Rect(r.x + r.width * 0.5f, y, r.width * 0.5f - 12 * zoom, rowH), p, labelRight);
                        if (Live(n.Id, p, out var value) && !outWired.Contains(p))
                        {
                            // An output with no wire shows its value beside its name.
                            var nameW = labelRight.CalcSize(new GUIContent(p)).x;
                            GUI.Label(new Rect(r.x + 10 * zoom, y, r.width - 22 * zoom - nameW - 6 * zoom, rowH), Show(value), liveText);
                        }
                        PortDot(new Vector2(r.xMax, y + rowH / 2), outColour, outWired.Contains(p));
                    }
                }
                var settings = Settings(n.Type).ToList();
                if (settings.Count == 0) return;
                var top = r.y + (Head + 3 + Row * rows + 3) * zoom;
                if (Event.current.type == EventType.Repaint) Fill(new Rect(r.x + 8 * zoom, top - 2 * zoom, r.width - 16 * zoom, 1), new Color(1, 1, 1, 0.07f));
                for (int i = 0; i < settings.Count; i++)
                {
                    var s = settings[i];
                    var y = top + Row * i * zoom;
                    GUI.Label(new Rect(r.x + 10 * zoom, y, 56 * zoom, Row * zoom), s, dim);
                    var value = n.Settings.TryGetValue(s, out var v) ? v : n.Type.Settings[s];
                    var typed = GUI.TextField(new Rect(r.x + 68 * zoom, y + 3 * zoom, r.width - 80 * zoom, (Row - 6) * zoom), value, field);
                    if (typed != value) n.Settings[s] = typed;
                }
            }

            // A node drawn on its own, as the canvas would show it: the Nodes tab's preview. Uses a throwaway graph so
            // its fields and close cross touch nothing real.
            internal static float PreviewHeight(NodeType t) => Height(new Node { Type = t });

            internal static void Preview(NodeType t, Rect at)
            {
                float keptZoom = zoom;
                var keptGraph = graph;
                int keptDelete = pendingDelete, keptSelected = selected;
                zoom = 1f;
                MakeStyles();
                try
                {
                    graph = new Graph();
                    var n = new Node { Id = 0, Type = t, Placed = true };
                    graph.Nodes[0] = n;
                    selected = -1;
                    DrawNode(n, new Rect(at.x + 12, at.y + 10, NodeW, Height(n)));
                }
                finally
                {
                    graph = keptGraph;
                    zoom = keptZoom;
                    pendingDelete = keptDelete;
                    selected = keptSelected;
                }
            }

            // What an unwired input reads as when nothing is typed in.
            static string Default(NodeType t, string port) =>
                t.InDefaults.TryGetValue(port, out var d) ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";

            // Title colours by what a node does; wires take their output's colour, lighter.
            static Color Header(NodeType t)
            {
                switch (t.Category)
                {
                    case "Links": return new Color(0.2f, 0.36f, 0.6f);
                    case "Logic": return new Color(0.22f, 0.46f, 0.3f);
                    case "Maths": return new Color(0.36f, 0.32f, 0.58f);
                    case "Time and memory": return new Color(0.62f, 0.4f, 0.16f);
                    case "Ship": return new Color(0.55f, 0.24f, 0.48f);
                    case "Custom": return new Color(0.13f, 0.45f, 0.47f);
                    default: return new Color(0.32f, 0.33f, 0.37f);
                }
            }

            static Color WireColour(NodeType t) => Color.Lerp(Header(t), Color.white, 0.35f);

            // Hollow when unwired, filled when wired.
            static void PortDot(Vector2 at, Color colour, bool wired)
            {
                if (Event.current.type != EventType.Repaint) return;
                var s = Port * zoom;
                var r = new Rect(at.x - s / 2, at.y - s / 2, s, s);
                Tinted(r, circle, new Color(0.08f, 0.08f, 0.1f, 1f));
                Tinted(r, wired ? circle : ring, colour);
            }

            // ---- The node menu ----

            static float menuMax;

            static void OpenMenu(Vector2 at)
            {
                menuOpen = true;
                menuAt = at;
                menuSearch = "";
                menuScroll = Vector2.zero;
                GUI.FocusControl(MenuSearch);
            }

            // A searchable list grouped by category; the wheel scrolls it (HandleMouse), the hovered node's help shows
            // at the bottom, Enter adds the first match and Escape closes it.
            const float MenuRow = 22;

            const float MenuW = 250;

            // Where the menu is: at the click, kept inside the canvas, as tall as the canvas allows up to 560.
            static Rect MenuRect()
            {
                var h = Mathf.Clamp(canvasView.height - 8, 200, 560);
                var x = canvasView.width > MenuW + 8 ? Mathf.Clamp(menuAt.x, 4, canvasView.width - MenuW - 4) : menuAt.x;
                var y = canvasView.height > h + 8 ? Mathf.Clamp(menuAt.y, 4, canvasView.height - h - 4) : menuAt.y;
                return new Rect(x, y, MenuW, h);
            }

            static void DrawMenu(Rect view)
            {
                const float W = MenuW, RowH = MenuRow, HelpH = 46, Bar = 12;
                var menuRect = MenuRect();
                var H = menuRect.height;
                var e = Event.current;
                // Page Up/Down and the arrow keys scroll it too (before the search field, which would take the arrows).
                if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.PageDown || e.keyCode == KeyCode.PageUp || e.keyCode == KeyCode.DownArrow || e.keyCode == KeyCode.UpArrow))
                {
                    var page = e.keyCode == KeyCode.PageDown || e.keyCode == KeyCode.PageUp;
                    var step = page ? H - 36 - HelpH - 4 - RowH : RowH;
                    var down = e.keyCode == KeyCode.PageDown || e.keyCode == KeyCode.DownArrow;
                    menuScroll.y = Mathf.Clamp(menuScroll.y + (down ? step : -step), 0, menuMax);
                    e.Use();
                }
                if (e.type == EventType.Repaint)
                {
                    Rounded(new Rect(menuRect.x + 3, menuRect.y + 5, W, H), new Color(0, 0, 0, 0.5f));
                    Rounded(new Rect(menuRect.x - 1, menuRect.y - 1, W + 2, H + 2), new Color(1, 1, 1, 0.12f));
                    Rounded(menuRect, new Color(0.12f, 0.13f, 0.155f, 1f));
                }
                GUI.SetNextControlName(MenuSearch);
                var search = new Rect(menuRect.x + 8, menuRect.y + 8, W - 16, 22);
                menuSearch = GUI.TextField(search, menuSearch, menuField);
                if (menuSearch == "" && e.type == EventType.Repaint) GUI.Label(new Rect(search.x + 6, search.y, search.width, search.height), "search nodes...", hintMenu);
                if (GUI.GetNameOfFocusedControl() != MenuSearch && GUIUtility.keyboardControl == 0) GUI.FocusControl(MenuSearch);

                // Headers and nodes, in the order the nodes were defined.
                var entries = new List<(string header, NodeType type)>();
                foreach (var group in Types.Values.Distinct().GroupBy(t => t.Category))
                {
                    var matches = group.Where(t => menuSearch == "" || t.Name.IndexOf(menuSearch, StringComparison.OrdinalIgnoreCase) >= 0 || t.Help.IndexOf(menuSearch, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (matches.Count == 0) continue;
                    entries.Add((group.Key == "" ? "Other" : group.Key, null));
                    foreach (var t in matches) entries.Add((null, t));
                }
                if (menuSearch == "" || CommentEntry.Name.IndexOf(menuSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    entries.Add(("Layout", null));
                    entries.Add((null, CommentEntry));
                }
                var list = new Rect(menuRect.x + 6, menuRect.y + 36, W - 12, H - 36 - HelpH - 4);
                menuMax = Mathf.Max(0, entries.Count * RowH - list.height);
                menuScroll.y = Mathf.Clamp(menuScroll.y, 0, menuMax);

                // The scroll bar: drag the handle, or click the track above or below it for a page.
                var track = new Rect(list.xMax - Bar, list.y, Bar, list.height);
                var thumbH = Mathf.Max(28, list.height * list.height / (list.height + menuMax));
                var thumb = new Rect(track.x, track.y + (track.height - thumbH) * (menuMax > 0 ? menuScroll.y / menuMax : 0), Bar, thumbH);
                if (menuMax > 0)
                {
                    if (e.type == EventType.MouseDown && e.button == 0 && track.Contains(e.mousePosition))
                    {
                        if (thumb.Contains(e.mousePosition)) { menuBarDrag = true; menuBarGrab = e.mousePosition.y - thumb.y; }
                        else menuScroll.y = Mathf.Clamp(menuScroll.y + (e.mousePosition.y > thumb.y ? 1 : -1) * (list.height - RowH), 0, menuMax);
                        e.Use();
                    }
                    else if (e.type == EventType.MouseDrag && menuBarDrag)
                    {
                        var room = Mathf.Max(1f, track.height - thumbH);
                        menuScroll.y = Mathf.Clamp((e.mousePosition.y - menuBarGrab - track.y) / room * menuMax, 0, menuMax);
                        e.Use();
                    }
                }
                if (e.type == EventType.MouseUp && menuBarDrag) { menuBarDrag = false; e.Use(); }
                NodeType hovered = null;
                GUI.BeginGroup(list);
                for (int i = 0; i < entries.Count; i++)
                {
                    var row = new Rect(0, i * RowH - menuScroll.y, list.width - (menuMax > 0 ? Bar + 4 : 0), RowH);
                    if (row.yMax < 0 || row.y > list.height) continue;
                    var entry = entries[i];
                    if (entry.type == null) { GUI.Label(new Rect(row.x + 4, row.y + 4, row.width, row.height - 4), entry.header.ToUpperInvariant(), menuHeader); continue; }
                    var over = row.Contains(e.mousePosition);
                    if (over) hovered = entry.type;
                    if (e.type == EventType.Repaint)
                    {
                        if (over) Rounded(new Rect(row.x + 2, row.y + 1, row.width - 4, row.height - 2), new Color(1, 1, 1, 0.08f));
                        Tinted(new Rect(row.x + 10, row.y + 8, 6, 6), circle, Header(entry.type));
                    }
                    if (GUI.Button(new Rect(row.x + 22, row.y, row.width - 22, row.height), entry.type.Name, menuItem)) Add(entry.type);
                }
                GUI.EndGroup();
                if (menuMax > 0 && e.type == EventType.Repaint)
                {
                    Rounded(track, new Color(1, 1, 1, 0.05f));
                    var lit = menuBarDrag || thumb.Contains(e.mousePosition);
                    Rounded(new Rect(thumb.x + 2, thumb.y + 1, thumb.width - 4, thumb.height - 2), new Color(1, 1, 1, lit ? 0.42f : 0.24f));
                }

                var help = hovered ?? entries.Select(x => x.type).FirstOrDefault(t => t != null);
                var helpRect = new Rect(menuRect.x + 10, menuRect.yMax - HelpH - 2, W - 20, HelpH);
                if (e.type == EventType.Repaint) Fill(new Rect(helpRect.x, helpRect.y - 3, helpRect.width, 1), new Color(1, 1, 1, 0.07f));
                GUI.Label(helpRect, help != null ? help.Help : "Nothing matches.", menuHelp);

                if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    var first = entries.Select(x => x.type).FirstOrDefault(t => t != null);
                    if (first != null) Add(first);
                    e.Use();
                }
                else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { menuOpen = false; e.Use(); }

            }

            static void Add(NodeType type)
            {
                if (type == CommentEntry)
                {
                    var spot = (menuAt - pan) / zoom;
                    var c = new Comment { X = Snap(spot.x), Y = Snap(spot.y) };
                    graph.Comments.Add(c);
                    pickedComment = editingComment = c;
                    menuOpen = false;
                    return;
                }
                var id = graph.Nodes.Count > 0 ? graph.Nodes.Keys.Max() + 1 : 1;
                var at = (menuAt - pan) / zoom;
                var node = new Node { Id = id, Type = type, X = Mathf.Round(at.x / 10) * 10, Y = Mathf.Round(at.y / 10) * 10, Placed = true };
                foreach (var s in type.Settings.Where(s => s.Value == "")) node.Settings[s.Key] = "";   // required: typed in the node
                if (type.InsOf != null) node.Script = LuaNodeExample;
                graph.Nodes[id] = node;
                selected = id;
                picked.Clear();
                picked.Add(id);
                menuOpen = false;
                GUIUtility.keyboardControl = 0;
            }

            // ---- Drawing helpers ----

            static Texture2D round, circle, ring, soft, fieldBack, fieldFocus;
            static GUIStyle roundStyle;

            // The few textures everything is drawn from, made once in code: a rounded square (drawn 9-slice, so the
            // corners keep their size), a circle and a ring for ports, a strip with soft edges for wires, and the text
            // fields' backgrounds.
            static void MakeTextures()
            {
                if (round != null) return;
                round = Shape(24, (x, y) => RoundedCoverage(x, y, 24, 7));
                circle = Shape(32, (x, y) => Mathf.Clamp01(15.5f - Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(16, 16)) + 0.5f));
                ring = Shape(32, (x, y) =>
                {
                    var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(16, 16));
                    return Mathf.Clamp01(15.5f - d + 0.5f) * Mathf.Clamp01(d - 10f + 0.5f);
                });
                soft = new Texture2D(1, 8, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var a = new[] { 0f, 0.45f, 1f, 1f, 1f, 1f, 0.45f, 0f };
                for (int i = 0; i < 8; i++) soft.SetPixel(0, i, new Color(1, 1, 1, a[i]));
                soft.Apply();
                fieldBack = Solid(new Color(0.085f, 0.09f, 0.11f, 1f));
                fieldFocus = Solid(new Color(0.11f, 0.13f, 0.18f, 1f));
                roundStyle = new GUIStyle { border = new RectOffset(8, 8, 8, 8) };
                roundStyle.normal.background = round;
            }

            static float RoundedCoverage(int x, int y, int size, float radius)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var c = new Vector2(Mathf.Clamp(p.x, radius, size - radius), Mathf.Clamp(p.y, radius, size - radius));
                return Mathf.Clamp01(radius - Vector2.Distance(p, c) + 0.5f);
            }

            static Texture2D Shape(int size, Func<int, int, float> alpha)
            {
                var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++) t.SetPixel(x, y, new Color(1, 1, 1, alpha(x, y)));
                t.Apply();
                return t;
            }

            static Texture2D Solid(Color c)
            {
                var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                t.SetPixel(0, 0, c);
                t.Apply();
                return t;
            }

            internal static void Fill(Rect r, Color c) => Tinted(r, Texture2D.whiteTexture, c);

            internal static void Tinted(Rect r, Texture t, Color c)
            {
                var old = GUI.color;
                GUI.color = c;
                GUI.DrawTexture(r, t);
                GUI.color = old;
            }

            // A rounded rectangle; too small for its corners, a plain one.
            static void Rounded(Rect r, Color c)
            {
                MakeTextures();
                if (r.width < 16 || r.height < 16) { Fill(r, c); return; }
                var old = GUI.color;
                GUI.color = c;
                roundStyle.Draw(r, false, false, false, false);
                GUI.color = old;
            }

            static void Border(Rect r, Color c)
            {
                Fill(new Rect(r.x, r.y, r.width, 1), c);
                Fill(new Rect(r.x, r.yMax - 1, r.width, 1), c);
                Fill(new Rect(r.x, r.y, 1, r.height), c);
                Fill(new Rect(r.xMax - 1, r.y, 1, r.height), c);
            }

            // A filled dot (the canvas's port circle), for other screens' drawings (the Anim tab's skeleton).
            internal static void Dot(Vector2 at, float radius, Color c)
            {
                MakeTextures();
                Tinted(new Rect(at.x - radius, at.y - radius, radius * 2, radius * 2), circle, c);
            }

            internal static void Ring(Vector2 at, float radius, Color c)
            {
                MakeTextures();
                Tinted(new Rect(at.x - radius, at.y - radius, radius * 2, radius * 2), ring, c);
            }

            // A straight piece of wire: the soft strip, rotated about its start.
            internal static void Line(Vector2 a, Vector2 b, float width, Color c)
            {
                var d = b - a;
                var length = d.magnitude;
                if (length < 0.01f) return;
                var matrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
                Tinted(new Rect(a.x, a.y - width / 2, length + 0.5f, width), soft, c);
                GUI.matrix = matrix;
            }

            // A wire: a curve leaving the output to the right and entering the input from the left.
            static Vector2 CurvePoint(Vector2 a, Vector2 b, float t)
            {
                var bend = Mathf.Max(40f * zoom, Mathf.Abs(b.x - a.x) * 0.5f);
                Vector2 p1 = a + new Vector2(bend, 0), p2 = b - new Vector2(bend, 0);
                var u = 1 - t;
                return u * u * u * a + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * b;
            }

            // A wire's live value: a small dark tag on the wire, mint when the value is on.
            static void Pill(Vector2 at, string text, bool on)
            {
                var size = pill.CalcSize(new GUIContent(text));
                var r = new Rect(at.x - size.x / 2 - 5 * zoom, at.y - size.y / 2 - 1, size.x + 10 * zoom, size.y + 2);
                Rounded(r, new Color(0.06f, 0.07f, 0.09f, 0.92f));
                pill.normal.textColor = on ? new Color(0.63f, 1f, 0.88f) : new Color(0.75f, 0.78f, 0.83f);
                GUI.Label(r, text, pill);
            }

            static void Curve(Vector2 a, Vector2 b, Color c, float thickness = 1f)
            {
                MakeTextures();
                var bend = Mathf.Max(40f * zoom, Mathf.Abs(b.x - a.x) * 0.5f);
                Vector2 p1 = a + new Vector2(bend, 0), p2 = b - new Vector2(bend, 0), last = a;
                var pieces = Mathf.Clamp(Mathf.RoundToInt((b - a).magnitude / 12f), 8, 40);
                for (int i = 1; i <= pieces; i++)
                {
                    float t = i / (float)pieces, u = 1 - t;
                    var p = u * u * u * a + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * b;
                    Line(last, p, Mathf.Max(2.5f, 4f * zoom) * thickness, c);
                    last = p;
                }
            }

            static GUIStyle idStyle, close, dim, hint, menuField, hintMenu, menuHeader, menuItem, menuHelp, pill, liveText, commentTitle;

            static void MakeStyles()
            {
                MakeTextures();
                if (Mathf.Approximately(stylesFor, zoom) && label != null) return;
                stylesFor = zoom;
                int size = Mathf.Max(6, Mathf.RoundToInt(11 * zoom)), small = Mathf.Max(5, Mathf.RoundToInt(9 * zoom));
                Color text = new Color(0.88f, 0.9f, 0.94f), muted = new Color(0.72f, 0.75f, 0.8f);
                label = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0), wordWrap = false, clipping = TextClipping.Clip };
                label.normal.textColor = text;
                labelRight = new GUIStyle(label) { alignment = TextAnchor.MiddleRight };
                dim = new GUIStyle(label);
                dim.normal.textColor = muted;
                head = new GUIStyle(label) { fontStyle = FontStyle.Bold, fontSize = Mathf.Max(6, Mathf.RoundToInt(12 * zoom)) };
                head.normal.textColor = Color.white;
                idStyle = new GUIStyle(label) { fontSize = small, alignment = TextAnchor.MiddleRight };
                idStyle.normal.textColor = new Color(1, 1, 1, 0.55f);
                close = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.Max(6, Mathf.RoundToInt(13 * zoom)) };
                close.normal.textColor = new Color(1, 1, 1, 0.6f);
                close.hover.textColor = new Color(1f, 0.55f, 0.5f);
                field = new GUIStyle(GUI.skin.textField) { fontSize = size, padding = new RectOffset(4, 4, 0, 0), alignment = TextAnchor.MiddleLeft, border = new RectOffset(0, 0, 0, 0) };
                field.normal.background = field.hover.background = fieldBack;
                field.focused.background = field.onFocused.background = fieldFocus;
                field.normal.textColor = field.hover.textColor = field.focused.textColor = text;
                pill = new GUIStyle(label) { font = GameLook.CodeFont, fontSize = Mathf.Max(6, Mathf.RoundToInt(10 * zoom)), alignment = TextAnchor.MiddleCenter };
                liveText = new GUIStyle(labelRight) { font = GameLook.CodeFont, fontSize = Mathf.Max(6, Mathf.RoundToInt(10 * zoom)) };
                liveText.normal.textColor = new Color(0.63f, 1f, 0.88f);
                commentTitle = new GUIStyle(label) { fontStyle = FontStyle.Bold, fontSize = Mathf.Max(6, Mathf.RoundToInt(13 * zoom)) };
                commentTitle.normal.textColor = new Color(1, 1, 1, 0.85f);
                hint = new GUIStyle(label) { fontSize = size };
                hint.normal.textColor = new Color(1, 1, 1, 0.25f);

                menuField = new GUIStyle(field) { fontSize = 12, padding = new RectOffset(6, 6, 0, 0) };
                hintMenu = new GUIStyle(hint) { fontSize = 12 };
                menuHeader = new GUIStyle(GUI.skin.label) { fontSize = 9, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft, padding = new RectOffset(0, 0, 0, 0) };
                menuHeader.normal.textColor = muted;
                menuItem = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0) };
                menuItem.normal.textColor = menuItem.hover.textColor = text;
                menuHelp = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true, alignment = TextAnchor.UpperLeft };
                menuHelp.normal.textColor = muted;
            }

            static GUIStyle Code()
            {
                if (code != null) return code;
                code = new GUIStyle(GUI.skin.label) { wordWrap = false, richText = false };
                try
                {
                    var mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 13);
                    if (mono != null) code.font = mono;
                }
                catch { }
                return code;
            }

            static GUIStyle rich;
            static GUIStyle Rich() => rich ?? (rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

            static bool Same(string a, string b) =>
                a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
