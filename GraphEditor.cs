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
    //   drag from an output (right edge) to an input (left edge)   wire them; an input takes one wire, a new one replaces it
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
            var path = Path.Combine(Lua.ScriptsDir, name + ".circuit");
            if (!File.Exists(path)) return "no such circuit: " + path;
            Canvas.Open(path);
            Canvas.ShowGraph();
            Canvas.Show();
            return "opened " + name + ".circuit";
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
                askThen = null;
                drag = Drag.None;
                GUIUtility.keyboardControl = 0;
                if (blockingGame && PlayerState.InMenu) inMenu.SetValue(null, false, null);
                blockingGame = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            // Escape, after the game's Update (which ignores it while we hold the in-menu flag): closes the node menu
            // if it's open, else the editor (which asks first when the graph has unsaved changes). The game's look is prepared here too: copying its sprites needs the GPU,
            // which OnGUI shouldn't touch.
            internal static void LateTick()
            {
                if (open && !GameLook.Ready) GameLook.Prepare();
                if (!open || !Input.GetKeyDown(KeyCode.Escape)) return;
                if (askThen != null) askThen = null;
                else if (choiceNode != null) choiceNode = null;
                else if (menuOpen) menuOpen = false;
                else Close();
            }

            // ---- Leaving a graph with unsaved changes: ask first ----

            static string askDoing;   // what was about to happen: "close the editor", "open x.graph"
            static bool askKeeps;     // closing keeps the changes in the editor; opening another graph drops them
            static Action askThen;    // what to go on with once answered; null when nothing is being asked
            static GUIStyle askTitle, askText;

            // True when it's fine to go ahead now. With unsaved changes it asks instead, and runs `then` on the answer.
            static bool Leave(string doing, bool keeps, Action then)
            {
                if (!Dirty) return true;
                askDoing = doing;
                askKeeps = keeps;
                askThen = then;
                menuOpen = false;
                choiceNode = null;
                drag = Drag.None;
                GUIUtility.keyboardControl = 0;
                return false;
            }

            static void Close()
            {
                if (Leave("close the editor", true, Hide)) Hide();
            }

            enum Answer { Save, Dont, Back }

            static void Answered(Answer answer)
            {
                var then = askThen;
                askThen = null;
                if (answer == Answer.Back) return;
                if (answer == Answer.Save) { Save(); if (Dirty) return; }   // couldn't save: the message line says why
                else if (!askKeeps) Load();   // back to the saved file, so nothing is left to ask about
                then();
            }

            static Rect AskRect(out Rect save, out Rect dont, out Rect back)
            {
                const float w = 620, h = 190, bw = 180, bh = 34, gap = 15;
                var r = new Rect(Mathf.Round(screen.center.x - w / 2), Mathf.Round(screen.center.y - h / 2), w, h);
                var x = r.center.x - (3 * bw + 2 * gap) / 2;
                var y = r.yMax - bh - 18;
                save = new Rect(x, y, bw, bh);
                dont = new Rect(x + bw + gap, y, bw, bh);
                back = new Rect(x + 2 * (bw + gap), y, bw, bh);
                return r;
            }

            // The question's clicks and keys, before the screen under it sees them. True when the event was its.
            static bool AskInput()
            {
                if (askThen == null) return false;
                var e = Event.current;
                if (e.type == EventType.Layout || e.type == EventType.Repaint) return false;
                AskRect(out var save, out var dont, out var back);
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    if (save.Contains(e.mousePosition)) Answered(Answer.Save);
                    else if (dont.Contains(e.mousePosition)) Answered(Answer.Dont);
                    else if (back.Contains(e.mousePosition)) Answered(Answer.Back);
                }
                else if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) Answered(Answer.Save);
                e.Use();
                return true;
            }

            // Drawn last, over the dimmed screen. Styles draw it directly: no controls, so nothing under it shifts.
            static void AskDraw()
            {
                if (askThen == null || Event.current.type != EventType.Repaint) return;
                if (askTitle == null)
                {
                    askTitle = new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.UpperCenter, richText = false };
                    askText = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.UpperCenter, wordWrap = true, richText = false };
                }
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.DrawTexture(screen, Texture2D.whiteTexture);
                GUI.color = old;
                var r = AskRect(out var save, out var dont, out var back);
                Sliced(r, GameLook.Body, GameLook.BodyBorder);
                var mouse = Event.current.mousePosition;
                askTitle.Draw(new Rect(r.x + 20, r.y + 18, r.width - 40, 26), new GUIContent(Name + ".circuit has unsaved changes"), false, false, false, false);
                var body = "Computers keep running the saved version until you save. " +
                    (askKeeps ? "If you close without saving, the changes stay here in the editor, but are lost when you quit the game."
                              : "To " + askDoing + ", save them or throw them away.") +
                    "\nEnter saves, Escape goes back.";
                askText.Draw(new Rect(r.x + 24, r.y + 52, r.width - 48, 70), new GUIContent(body), false, false, false, false);
                var button = GUI.skin.button;
                button.Draw(save, new GUIContent("Save"), save.Contains(mouse), false, false, false);
                button.Draw(dont, new GUIContent(askKeeps ? "Close without saving" : "Throw away changes"), dont.Contains(mouse), false, false, false);
                button.Draw(back, new GUIContent("Go back"), back.Contains(mouse), false, false, false);
            }

            // ---- The Computer screen: the game's window look around three tabs ----

            enum Tab { Graph, Script, Nodes, Anim, Links, Hardware, Help }
            static Tab tab;
            internal static void ShowScript() => tab = Tab.Script;
            internal static void ShowGraph() => tab = Tab.Graph;
            internal static void ShowAnim() => tab = Tab.Anim;
            internal static void ShowLinks() => tab = Tab.Links;
            internal static void ShowHardware() => tab = Tab.Hardware;
            internal static void Say(string text) => message = text;   // a line under the canvas

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
                if (AskInput()) return;
                if (e.type == EventType.Repaint)
                {
                    Sliced(new Rect(screen.x, screen.y + TitleH - 6, screen.width, screen.height - TitleH + 6), GameLook.Body, GameLook.BodyBorder);
                    Sliced(new Rect(screen.x, screen.y, screen.width, TitleH), GameLook.TitleBar, GameLook.TitleBorder);
                }

                // The title in the game's font, then the computer's own tag in its hologram font.
                var title = new GUIContent("Lua Computer");
                var titleW = titleText.CalcSize(title).x;
                GUI.Label(new Rect(screen.x + 18, screen.y, titleW + 4, TitleH), title, titleText);
                var what = tab == Tab.Graph ? (file != null ? Name + ".circuit" : "circuits") : tab == Tab.Script ? "scripts" : tab == Tab.Anim ? "animation" : tab == Tab.Help ? "help" : tab == Tab.Links ? "links" : tab == Tab.Hardware ? "hardware" : "nodes";
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
                if (GUI.Button(x, GUIContent.none, GUIStyle.none)) { Close(); return; }

                GUILayout.BeginArea(new Rect(screen.x + 18, screen.y + TitleH + 12, screen.width - 36, screen.height - TitleH - 26));
                GUILayout.BeginHorizontal();
                foreach (Tab t in Enum.GetValues(typeof(Tab)))
                    if (GUILayout.Toggle(tab == t, t == Tab.Graph ? "Circuit" : t.ToString(), GameLook.TabStyle(), GUILayout.Width(120)) && tab != t)
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
                    case Tab.Hardware: HardwareEditor.Draw(screen.height - 260); break;
                }
                GUILayout.EndArea();
                AskDraw();
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
                GUILayout.Label("Circuits open in the circuit editor, a screen of its own. Using a Lua Computer that runs a circuit opens it too.");
                GUILayout.BeginHorizontal();
                DrawList(true);
                GUILayout.BeginVertical();
                GUI.enabled = file != null;
                if (GUILayout.Button(file != null ? $"Open the editor on {Name}.circuit" : "Pick a circuit", GUILayout.Width(260))) Show();
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
            const float HomeZoom = 1.15f;   // the view a graph opens at: text big enough to read
            static float zoom = HomeZoom;

            enum Drag { None, Node, Wire, Pan, Box, Comment, Resize, Place }
            static Drag drag;
            static Vector2 dragStart, dragOffset;
            static bool dragMoved;
            static int dragNode, wireNode;
            static string wirePort;

            static bool menuOpen;
            static (int node, string port)? pendingWire;   // a wire dropped on empty space, waiting for the node it opens the menu for
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

            const float NodeW = 200, Head = 24, Row = 22, Port = 11;
            const string MenuSearch = "EvoCoreGraphMenuSearch";
            static readonly Regex validName = new Regex(@"^[\w\-. ]+$");

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
                if (file == null) GUILayout.Label("Pick a circuit on the left, or make a new one. A Lua Computer labelled `name` runs name.circuit (before name.lua).");
                else if (graph == null) GUILayout.Label(message);
                else DrawEditor();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                Snapshot();
                AutoRun();
            }

            // ---- Auto-run: saves, so computers reload, a moment after the last change. Off unless switched on,
            // because a reload starts the graph again: its timers, counters and latches go back to the start. ----

            const float AutoRunDelay = 1f;
            const string AutoRunDoc = "Auto-run: saves a moment after each change, as long as the circuit compiles, so computers run it straight away. Each save starts the circuit again: timers, counters and latches go back to the start.";
            static string autoTried;   // the text a save failed on: not tried again until it changes
            static bool overAutoRun;

            static bool AutoRunHeld => compileStatus.Contains("Won't") || graph.Nodes.Count == 0;

            static void AutoRun()
            {
                if (!Core.GraphAutoRun || graph == null || askThen != null || drag != Drag.None || Event.current.type != EventType.Repaint) return;
                if (Time.realtimeSinceStartup - lastStep < AutoRunDelay) return;
                var text = Write(graph, Name);
                if (text == saved || text != compiledFrom || text == autoTried || AutoRunHeld) return;
                Save();
                if (Dirty) autoTried = text;
                else message = "Auto-run s" + message.Substring(1);
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
                DrawTemplatePicker();
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
                var chips = ChipFiles();
                if (chips.Count > 0)
                {
                    GUILayout.Space(8);
                    GUILayout.Label("Chips");
                    foreach (var path in chips)
                        if (GUILayout.Button((Same(path, file) ? "> " : "") + Path.GetFileNameWithoutExtension(path), GUI.skin.label))
                        {
                            Open(path);
                            if (launch && Same(path, file)) Show();
                        }
                }
                // Lua scripts too, so a computer can be given one from here: a pick opens it on the Scripts tab, where
                // "Put on computer" is.
                if (!launch)
                {
                    GUILayout.Space(8);
                    GUILayout.Label("Lua scripts");
                    foreach (var path in Lua.LuaFiles())
                    {
                        var runs = Lua.StatusOf(path).Count;
                        if (GUILayout.Button(Path.GetFileNameWithoutExtension(path) + ".lua" + (runs > 0 ? $"  ({runs} running)" : ""), GUI.skin.label))
                            Lua.OpenPath(path);
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
            }

            static List<string> ChipFiles()
            {
                try { return Directory.GetFiles(NodesDir, "*.circuit").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { return new List<string>(); }
            }

            static IEnumerable<string> Files()
            {
                try { return Directory.GetFiles(Lua.ScriptsDir, "*.circuit").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { return Enumerable.Empty<string>(); }
            }

            internal static void Open(string path)
            {
                if (Same(path, file)) return;
                if (!Leave("open " + Path.GetFileName(path), false, () => Open(path))) return;
                file = path;
                Load();
                pan = new Vector2(20, 20);
                zoom = HomeZoom;
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
                if (name.EndsWith(".circuit", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 8);
                if (name == "" || !validName.IsMatch(name)) { message = "Use letters, numbers, spaces, - _ or . in the name."; return; }
                var path = Path.Combine(Lua.ScriptsDir, name + ".circuit");
                if (!Leave((File.Exists(path) ? "open " : "make ") + name + ".circuit", false, () => Create(name))) return;
                var existed = File.Exists(path);
                if (!existed)
                {
                    try
                    {
                        var start = template != null && File.Exists(template) ? File.ReadAllText(template).Replace("{name}", name)
                            : $"# {name}.circuit\nrate 0.1\n";
                        File.WriteAllText(path, start);
                    }
                    catch (Exception e) { message = "Couldn't create it: " + e.Message; return; }
                }
                newName = "";
                file = null;
                Open(path);
                if (!existed && TemplateName(template) == "blank") showGuide = true;   // a blank start: walk them through it
                message = existed ? $"{name}.circuit already exists: opened it." :
                    $"Made {name}.circuit from \"{TemplateName(template)}\". " + (Lua.AttachedComputer != null ? "Put it on the computer with the button above the canvas." : "Open a Lua Computer to put it on one.");
            }

            // ---- Chips: the picked nodes packed into one node, saved in the nodes folder for any graph ----

            static string chipName = "";
            static int unpack = -1;

            // A chip back into its nodes on this board, wired as the chip was: what fed an input pin feeds what the pin
            // fed, what an output pin fed is fed from inside, and each chip setting becomes a constant with this
            // chip's value. The chip file stays, for other boards.
            static void Unpack(int id)
            {
                if (!graph.Nodes.TryGetValue(id, out var chipNode) || chipNode.Type.File == null) return;
                Graph inside;
                try { inside = Parse(File.ReadAllText(chipNode.Type.File)); }
                catch (Exception e) { message = "Couldn't read the chip: " + e.Message; return; }
                // A hand-written chip has no places yet: lay it out on its own board first.
                var board = graph;
                graph = inside;
                PlaceUnplaced();
                graph = board;
                var next = graph.Nodes.Keys.Max() + 1;
                var ids = new Dictionary<int, int>();
                var corner = inside.Nodes.Values.Where(n => !n.Type.Name.StartsWith("chip ")).DefaultIfEmpty().Aggregate(
                    new Vector2(float.MaxValue, float.MaxValue), (m, n) => n == null ? m : Vector2.Min(m, new Vector2(n.X, n.Y)));
                if (corner.x == float.MaxValue) corner = Vector2.zero;
                picked.Clear();
                foreach (var n in inside.Nodes.Values)
                {
                    if (n.Type.Name == "chip in" || n.Type.Name == "chip out") continue;
                    Node copy;
                    if (n.Type.Name == "chip setting")
                    {
                        // This chip's own value for the setting, as a constant.
                        copy = new Node { Id = next, Type = Types["constant"], X = Snap(n.X - corner.x + chipNode.X), Y = Snap(n.Y - corner.y + chipNode.Y), Placed = true };
                        copy.Settings["value"] = SettingOf(chipNode, n.Outs[0]);
                    }
                    else
                    {
                        copy = new Node { Id = next, Type = n.Type, X = Snap(n.X - corner.x + chipNode.X), Y = Snap(n.Y - corner.y + chipNode.Y), Placed = true, Script = n.Script };
                        foreach (var setting in n.Settings) copy.Settings[setting.Key] = setting.Value;
                    }
                    graph.Nodes[next] = copy;
                    ids[n.Id] = next;
                    picked.Add(next);
                    next++;
                }
                string Out(Node n, string port) => n.Type.Name == "chip setting" ? "out" : port;
                foreach (var w in inside.Wires)
                {
                    if (!inside.Nodes.TryGetValue(w.fromNode, out var from) || !inside.Nodes.TryGetValue(w.toNode, out var to)) continue;
                    if (from.Type.Name == "chip in")
                    {
                        // What fed this pin on the board now feeds the node inside directly.
                        foreach (var outer in graph.Wires.Where(x => x.toNode == id && x.toPort == from.Outs[0]).ToList())
                            if (ids.ContainsKey(w.toNode)) graph.Wires.Add((outer.fromNode, outer.fromPort, ids[w.toNode], w.toPort, 0));
                    }
                    else if (to.Type.Name == "chip out")
                    {
                        foreach (var outer in graph.Wires.Where(x => x.fromNode == id && x.fromPort == to.Ins[0]).ToList())
                            if (ids.ContainsKey(w.fromNode)) graph.Wires.Add((ids[w.fromNode], Out(from, w.fromPort), outer.toNode, outer.toPort, 0));
                    }
                    else if (ids.ContainsKey(w.fromNode) && ids.ContainsKey(w.toNode))
                        graph.Wires.Add((ids[w.fromNode], Out(from, w.fromPort), ids[w.toNode], w.toPort, 0));
                }
                graph.Wires.RemoveAll(x => x.fromNode == id || x.toNode == id);
                graph.Nodes.Remove(id);
                selected = -1;
                message = $"Unpacked {chipNode.Type.Name} into {ids.Count} nodes (picked). The chip itself is still in the nodes folder.";
            }

            static void DrawMakeChip()
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(picked.Count == 1 ? "Pack this node into a chip, named" : $"Pack these {picked.Count} nodes into a chip, named", GUILayout.ExpandWidth(false));
                chipName = GUILayout.TextField(chipName, GUILayout.Width(180));
                if (GUILayout.Button("Make chip", GUILayout.Width(110))) MakeChip();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            // The picked nodes go into nodes\<name>.graph with a chip in pin for each thing wired into them from
            // outside and a chip out pin for each output wired out; here they become one node of that chip, wired the
            // same way.
            static void MakeChip()
            {
                var name = chipName.Trim();
                if (name == "" || !validName.IsMatch(name) || name.Contains(".")) { message = "Give the chip a name: letters, numbers, spaces, - or _."; return; }
                if (Types.TryGetValue(name, out var existing)) { message = $"There's already a node or chip called \"{name}\"."; return; }
                var path = Path.Combine(NodesDir, name + ".circuit");
                if (File.Exists(path)) { message = $"There's already a chip file called {name}.circuit."; return; }
                var sel = picked.Where(graph.Nodes.ContainsKey).ToList();
                if (sel.Count == 0) return;
                if (sel.Any(id => graph.Nodes[id].Type.Name == "lua")) { message = "A chip can't hold a lua node yet."; return; }
                var inSel = new HashSet<int>(sel);
                var chip = new Graph { Rate = graph.Rate };
                foreach (var id in sel) chip.Nodes[id] = graph.Nodes[id];
                foreach (var w in graph.Wires) if (inSel.Contains(w.fromNode) && inSel.Contains(w.toNode)) chip.Wires.Add(w);
                var next = sel.Max() + 1;
                float minX = sel.Min(id => graph.Nodes[id].X), maxX = sel.Max(id => graph.Nodes[id].X) + NodeW, minY = sel.Min(id => graph.Nodes[id].Y);
                var taken = new HashSet<string>();
                string Unique(string n) { var u = n; for (int k = 2; !taken.Add(u); k++) u = n + k; return u; }

                // An input pin per outside output feeding the picked nodes (one pin may feed several of them).
                var outerIn = graph.Wires.Where(w => !inSel.Contains(w.fromNode) && inSel.Contains(w.toNode)).ToList();
                var inPins = new Dictionary<(int, string), (int id, string pin)>();
                var row = 0;
                foreach (var w in outerIn)
                {
                    var key = (w.fromNode, w.fromPort);
                    if (!inPins.TryGetValue(key, out var pin))
                    {
                        pin = (next++, Unique(PortName(w.fromPort)));
                        var node = new Node { Id = pin.id, Type = Types["chip in"], X = Snap(minX - NodeW - 90), Y = Snap(minY + row++ * 80), Placed = true };
                        node.Settings["name"] = pin.pin;
                        node.Settings["kind"] = PortKind(graph.Nodes[w.fromNode], w.fromPort);
                        chip.Nodes[pin.id] = node;
                        inPins[key] = pin;
                    }
                    chip.Wires.Add((pin.id, pin.pin, w.toNode, w.toPort, 0));
                }
                // An output pin per picked output wired out (one pin may feed several outside nodes).
                var outerOut = graph.Wires.Where(w => inSel.Contains(w.fromNode) && !inSel.Contains(w.toNode)).ToList();
                var outPins = new Dictionary<(int, string), string>();
                row = 0;
                foreach (var w in outerOut)
                {
                    var key = (w.fromNode, w.fromPort);
                    if (outPins.ContainsKey(key)) continue;
                    var pinName = Unique(PortName(w.fromPort));
                    var node = new Node { Id = next++, Type = Types["chip out"], X = Snap(maxX + 90), Y = Snap(minY + row++ * 80), Placed = true };
                    node.Settings["name"] = pinName;
                    node.Settings["kind"] = PortKind(graph.Nodes[w.fromNode], w.fromPort);
                    chip.Nodes[node.Id] = node;
                    chip.Wires.Add((w.fromNode, w.fromPort, node.Id, pinName, 0));
                    outPins[key] = pinName;
                }

                try
                {
                    Directory.CreateDirectory(NodesDir);
                    var text = $"# help: made from {sel.Count} node{(sel.Count == 1 ? "" : "s")} of {Name}.circuit (change this line to say what it does)\n" + Write(chip, name);
                    File.WriteAllText(path, text.Replace("\n", Environment.NewLine));
                }
                catch (Exception e) { message = "Couldn't save the chip: " + e.Message; return; }
                var type = LoadChip(path, out var error);
                if (type == null) { message = $"Saved {name}.circuit but it won't load as a chip: {error}"; return; }
                Types[name] = type;
                ScanNodesSoon();

                // On this board: the chip where the nodes were, wired as they were.
                var chipNode = new Node { Id = graph.Nodes.Keys.Max() + 1, Type = type, X = Snap(minX), Y = Snap(minY), Placed = true };
                graph.Wires.RemoveAll(w => inSel.Contains(w.fromNode) || inSel.Contains(w.toNode));
                foreach (var id in sel) graph.Nodes.Remove(id);
                graph.Nodes[chipNode.Id] = chipNode;
                foreach (var pin in inPins) graph.Wires.Add((pin.Key.Item1, pin.Key.Item2, chipNode.Id, pin.Value.pin, 0));
                foreach (var w in outerOut) graph.Wires.Add((chipNode.Id, outPins[(w.fromNode, w.fromPort)], w.toNode, w.toPort, 0));
                picked.Clear();
                picked.Add(chipNode.Id);
                selected = chipNode.Id;
                chipName = "";
                message = $"Made the chip {name}: {inPins.Count} in, {outPins.Count} out. It's in the node menu under Chips; double-click it to open it.";
            }

            // ---- Templates: what a new graph starts as ----

            static string template;          // the chosen template's path; null: the first one
            static bool showTemplates;

            static List<string> Templates()
            {
                try { return Directory.GetFiles(TemplatesDir, "*.circuit").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { return new List<string>(); }
            }

            // "2 fuel screen.graph" -> "fuel screen": the number only sets the order.
            static string TemplateName(string path) =>
                path == null ? "empty" : Regex.Replace(Path.GetFileNameWithoutExtension(path), @"^\d+\s*", "");

            static void DrawTemplatePicker()
            {
                var all = Templates();
                if (all.Count == 0) { template = null; return; }
                if (template == null || !all.Any(p => Same(p, template))) template = all[0];
                if (GUILayout.Button("new from: " + TemplateName(template) + (showTemplates ? "  ▴" : "  ▾"), GUI.skin.label)) showTemplates = !showTemplates;
                if (!showTemplates) return;
                foreach (var path in all)
                    if (GUILayout.Button((Same(path, template) ? "  > " : "     ") + TemplateName(path), GUI.skin.label))
                    {
                        template = path;
                        showTemplates = false;
                    }
                GUILayout.Space(6);
            }

            static void Save()
            {
                try
                {
                    var text = Write(graph, Name);
                    File.WriteAllText(file, text.Replace("\n", Environment.NewLine));
                    saved = text;
                    fileTime = File.GetLastWriteTimeUtc(file);
                    message = "Saved " + DateTime.Now.ToString("HH:mm:ss") + (IsChipFile(file) ? ". Circuits using this chip update in a second." : ". Computers running it reload in a second.");
                    Lua.RescanSoon();
                    if (IsChipFile(file)) ScanNodesSoon();
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
                WorkOutNotes();
                GUILayout.BeginHorizontal();
                GUILayout.Label(Name + ".circuit" + (Dirty ? "  (unsaved)" : ""), GUILayout.ExpandWidth(true));
                GUILayout.Label("rate", GUILayout.Width(30));
                var rate = GUILayout.TextField(rateText, GUILayout.Width(45));
                if (rate != rateText)
                {
                    rateText = rate;
                    if (double.TryParse(rate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0) graph.Rate = r;
                }
                if (GUILayout.Button("View: reset", GUILayout.Width(110))) { pan = new Vector2(20, 20); zoom = HomeZoom; }
                if (GUILayout.Button(new GUIContent("Tidy", "Moves nodes that sit on top of each other apart (down their column)."), GUILayout.Width(60))) Tidy();
                showLua = GUILayout.Toggle(showLua, "Show Lua", GUI.skin.button, GUILayout.Width(100));
                showPreview = GUILayout.Toggle(showPreview, "Preview", GUI.skin.button, GUILayout.Width(90));
                showGuide = GUILayout.Toggle(showGuide, "Guide", GUI.skin.button, GUILayout.Width(80));
                var auto = GUILayout.Toggle(Core.GraphAutoRun, "Auto-run", GUI.skin.button, GUILayout.Width(95));
                if (auto != Core.GraphAutoRun) { Core.GraphAutoRun = auto; message = auto ? AutoRunDoc : "Auto-run off: Ctrl+S runs your changes."; }
                if (Event.current.type == EventType.Repaint) overAutoRun = GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition);
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
                DrawNotOnComputer();

                // A selected lua node's code opens under the canvas.
                Node coding = null;
                if (picked.Count <= 1 && selected >= 0 && graph.Nodes.TryGetValue(selected, out var maybe) && maybe.Type.Name == "lua" && maybe.Type.Source == null) coding = maybe;
                DropDeadWires();

                // As wide as the screen allows, and as tall as leaves room for the lines under it.
                var notOnComputer = runner == null && !Lua.IsMirror(file);
                var height = Mathf.Max(260, screen.height - 290 - (showLua ? 230 : 0) - (coding != null ? CodeH + 26 : 0) - (showGuide ? 225 : 0) - (notOnComputer ? 44 : 0));
                var area = GUILayoutUtility.GetRect(300, 10000, height, height, GUILayout.ExpandWidth(true));
                DrawCanvas(area);
                if (coding != null)
                {
                    GUILayout.Label($"node {coding.Id}'s Lua: inputs.get(1) is in1, outputs.set(1, v) sets out1; tick(dt) and on_input(i, value) run each circuit tick. Its ports: inputs= and outputs= on the node.", Wrapped());
                    var code = Lua.CodeView.Draw(coding.Script ?? "", ref codeScroll, CodeH, "EvoCoreGraphLua", codeError, out _);
                    if (code != (coding.Script ?? "")) coding.Script = code;
                }

                UpdateCompile();
                GUILayout.Label(StatusLine(), Rich());
                if (showGuide) DrawGuide();
                if (hoverDoc != null)
                    GUILayout.Label("<color=#9fd8ff>" + hoverDoc + "</color>", Rich());
                else if (picked.Count > 1)
                    GUILayout.Label($"{picked.Count} nodes picked.  Drag a title: move them.  Ctrl+C copy, Ctrl+D duplicate, Ctrl+G comment box around them, Delete remove.", Wrapped());
                else if (selected >= 0 && graph.Nodes.TryGetValue(selected, out var chipSel) && chipSel.Type.File != null && IsChipFile(chipSel.Type.File))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"chip {chipSel.Type.Name}: {chipSel.Type.Help}.  Double-click its name to open what's inside.");
                    if (GUILayout.Button("Unpack", GUILayout.Width(100))) unpack = chipSel.Id;
                    GUILayout.EndHorizontal();
                }
                else if (selected >= 0 && graph.Nodes.TryGetValue(selected, out var sel))
                    GUILayout.Label($"node {sel.Id}, {sel.Type.Name}: {sel.Type.Help}.", Wrapped());
                else if (pickedComment != null)
                    GUILayout.Label("Comment box: drag its title to move it with what's inside, its corner to resize; double-click the title to rename; Delete removes it.", Wrapped());
                else
                    GUILayout.Label("Right-click: add.  Drag a right-edge pin to a left-edge pin: wire (blue number, green on/off, amber 0-1).  Drop a wire on empty space: new node.  Hover for help.  Ctrl+Z/Y, Ctrl+S.", Wrapped());
                if (picked.Count > 0 && !IsChipFile(file)) DrawMakeChip();
                if (unpack >= 0) { Unpack(unpack); unpack = -1; }
                if (message != "") GUILayout.Label(message, Wrapped());
                var runs = Lua.StatusOf(file);
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

            // A graph in the scripts folder runs on nothing until it's put on a computer (or a computer is labelled
            // with its name): say so where it can't be missed, with the button to fix it.
            static void DrawNotOnComputer()
            {
                if (runner != null || Lua.IsMirror(file) || IsChipFile(file)) return;
                var c = Lua.AttachedComputer;
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label("<color=#ffb84d><b>This circuit only exists as a file: no computer runs it yet.</b></color>  " +
                    (c != null ? "Put it on the computer you opened to run it there." : $"Aim at a Lua Computer and press use to open it, then put this circuit on it (or label a computer \"{Name}\")."), Rich());
                if (c != null && GUILayout.Button("Put on this computer", GUILayout.Width(190), GUILayout.Height(30)))
                {
                    if (Dirty) Save();
                    message = Lua.PutOnComputer(file);
                }
                GUILayout.EndHorizontal();
            }

            // ---- The guide: a first screen, step by step, each ticked off by looking at the graph itself ----

            static bool showGuide;

            static void DrawGuide()
            {
                bool Has(string type, Func<Node, bool> ok = null) => graph.Nodes.Values.Any(n => n.Type.Name == type && (ok == null || ok(n)));
                bool Wired(string fromType, string toType, string toPort) => graph.Wires.Any(w =>
                    graph.Nodes.TryGetValue(w.fromNode, out var a) && a.Type.Name == fromType &&
                    graph.Nodes.TryGetValue(w.toNode, out var b) && b.Type.Name == toType && w.toPort == toPort);
                var meter = Wired("ship", "meter", "value");
                var steps = new (string text, bool done)[]
                {
                    ("Open a Lua Computer: aim at it and press use.", Lua.AttachedComputer != null || runner != null),
                    ("Put this circuit on it: \"Put on this computer\" in the orange bar above the canvas.", runner != null),
                    ("Link a monitor to the computer with the link tool: hold on the computer, let go on the monitor.", runner != null && Monitors.Count(runner) > 0),
                    ("Right-click empty canvas, add a ship node, and pick fuel from its value list.", Has("ship", n => SettingOf(n, "value") == "fuel")),
                    ("Add a meter (Display group). Drag from the pin on the ship's right edge to the meter's value pin, on its left.", meter),
                    ("Press Ctrl+S. The fuel bar shows on the monitor, and in the preview at the top right.", meter && !Dirty && runner != null),
                    ("Add a readout: text FUEL {v}, percent on, and wire the ship into it. Then drag it under the bar on the preview.", Wired("ship", "readout", "value")),
                    ("Add a button (mode: toggle) and a lamp, wire the button into the lamp, save, and press use on the monitor's button.", Wired("button", "lamp", "value") && !Dirty),
                };
                var next = Array.FindIndex(steps, s => !s.done);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("<b>Guide: your first screen</b>  " + (next < 0 ? "<color=#80ff80>all done. Try the other templates under \"new from\" next.</color>" : $"<color=#9fd8ff>step {next + 1} of {steps.Length}</color>"), Rich());
                for (int i = 0; i < steps.Length; i++)
                {
                    var s = steps[i];
                    var line = s.done ? $"<color=#80ff80>✓ {s.text}</color>" : i == next ? $"<color=#ffd27a><b>> {s.text}</b></color>" : $"<color=#8a9099>   {s.text}</color>";
                    GUILayout.Label(line, Rich());
                }
                GUILayout.EndVertical();
            }

            // One line saying where the graph runs and what's wrong, in plain words.
            static string StatusLine()
            {
                var parts = new List<string>();
                if (IsChipFile(file))
                {
                    parts.Add(compileStatus);
                    parts.Add($"<color=#9fd8ff>a chip: chip in nodes are its input pins, chip out nodes its output pins. Save it and every circuit using it updates.</color>");
                    if (Dirty) parts.Add("<color=#ffb84d>unsaved: Ctrl+S</color>");
                    return string.Join("  ·  ", parts.ToArray());
                }
                if (compileStatus.Contains("Won't run")) parts.Add(compileStatus);
                if (runner != null)
                {
                    parts.Add($"<color=#80ff80>Running on {Lua.Describe(runner)}</color>");
                    var screens = Monitors.Count(runner);
                    var drawsSomething = graph.Nodes.Values.Any(n => n.Type.Category == "Display");
                    if (screens == 0 && drawsSomething) parts.Add("<color=#ffb84d>no monitor linked to it, so the Display nodes have nowhere to draw: link one with the link tool</color>");
                    else parts.Add(screens == 1 ? "1 monitor" : $"{screens} monitors");
                }
                else if (Lua.IsMirror(file)) parts.Add("<color=#ffb84d>this computer's program, but the computer isn't running it (is it still there?)</color>");
                else parts.Add("<color=#ffb84d>not running anywhere</color>");
                if (Dirty) parts.Add("<color=#ffb84d>unsaved: " + (!Core.GraphAutoRun ? "Ctrl+S to run your changes" : AutoRunHeld ? "auto-run is waiting for it to compile" : "auto-run saves it in a moment") + "</color>");
                else if (runner != null) parts.Add("saved");
                var warnings = notes.Values.Count(x => x.warn);
                if (warnings > 0) parts.Add($"<color=#ffb84d>{warnings} node{(warnings == 1 ? "" : "s")} need{(warnings == 1 ? "s" : "")} something (see the orange lines)</color>");
                if (!compileStatus.Contains("Won't run") && warnings == 0 && runner != null && !Dirty) parts.Add("<color=#80ff80>all good</color>");
                return string.Join("  ·  ", parts.ToArray());
            }

            // Wires into or out of ports a lua node no longer has (its inputs= or outputs= went down) are dropped.
            static void DropDeadWires()
            {
                RenameOldOutputs(graph);   // a ship node switched from fuel to speed keeps its wires
                graph.Wires.RemoveAll(w =>
                    graph.Nodes.TryGetValue(w.fromNode, out var from) && from.Type.OutsOf != null && !from.Outs.Contains(w.fromPort) ||
                    graph.Nodes.TryGetValue(w.toNode, out var to) && to.Type.InsOf != null && !to.Ins.Contains(w.toPort));
            }

            static void UpdateCompile()
            {
                var text = Write(graph, Name);
                if (text == compiledFrom) return;
                compiledFrom = text;
                if (graph.Nodes.Count == 0)
                {
                    compiledLua = "";
                    compileStatus = "Empty: right-click the canvas to add a node.";
                    return;
                }
                if (IsChipFile(file))
                {
                    try
                    {
                        compiledLua = ChipModule(Parse(text), Name);
                        var ins = graph.Nodes.Values.Count(n => n.Type.Name == "chip in");
                        var outs = graph.Nodes.Values.Count(n => n.Type.Name == "chip out");
                        compileStatus = $"<color=#80ff80>Chip compiles: {ins} input pin{(ins == 1 ? "" : "s")}, {outs} output pin{(outs == 1 ? "" : "s")}.</color>";
                    }
                    catch (GraphException e) { compiledLua = e.Message; compileStatus = "<color=#ff8080>Won't work as a chip: " + e.Message + "</color>"; }
                    return;
                }
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
                    if (picked.Count > 0) { GUIUtility.systemCopyBuffer = Fragment(); message = $"Copied {picked.Count} node(s) as .circuit text."; }
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
                catch (GraphException) { message = "The clipboard doesn't hold circuit nodes."; return; }
                if (g.Nodes.Count == 0) { message = "The clipboard doesn't hold circuit nodes."; return; }
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
                Unstack(picked.ToList());
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

            static string PortDoc(Node n, string port, bool output)
            {
                // A renamed single output (ship.fuel) keeps the doc written for "out".
                var doc = n.Type.Docs.TryGetValue(port, out var d) ? d : output && n.Outs.Length == 1 && n.Type.Docs.TryGetValue("out", out d) ? d : output ? "an output" : "an input";
                var value = Live(n.Id, port, out var v) ? $"  (now {Show(v)})" : "";
                return $"{n.Type.Name} {(output ? "output" : "input")} {port}, {KindWords(PortKind(n, port))}: {doc}{value}";
            }

            // ---- Port kinds on the canvas: a short coloured tag beside each port, wires in their kind's colour ----

            static string KindTag(string kind) => kind == "onoff" ? "on/off" : kind == "level" ? "0-1" : kind == "number" ? "num" : kind;

            static Color KindColour(string kind)
            {
                switch (kind)
                {
                    case "onoff": return new Color(0.45f, 0.95f, 0.55f);
                    case "level": return new Color(1f, 0.76f, 0.32f);
                    case "number": return new Color(0.55f, 0.76f, 1f);
                    default: return new Color(0.9f, 0.6f, 1f);
                }
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
                    Fill(area, new Color(0.03f, 0.1f, 0.065f, 1f));   // a circuit board
                    Border(area, new Color(1, 1, 1, 0.08f));
                }
                GUI.BeginGroup(area);
                var view = new Rect(0, 0, area.width, area.height);
                if (Event.current.type != EventType.Layout) canvasView = view;
                if (view.Contains(Event.current.mousePosition)) lastMouse = Event.current.mousePosition;   // where Ctrl+V pastes
                if (Event.current.type == EventType.Repaint) DrawGrid(view);

                hoverDoc = overAutoRun ? AutoRunDoc : null;
                // An open choice list takes its clicks before anything under it (drawn on top later, at Repaint).
                if (Event.current.type != EventType.Repaint) DrawChoices();
                var rects = graph.Nodes.Values.ToDictionary(n => n.Id, NodeRect);
                HandleMouse(view, rects);
                rects = graph.Nodes.Values.ToDictionary(n => n.Id, NodeRect);
                // A port under the mouse: what it means, and its value while running.
                var mouse = Event.current.mousePosition;
                if (drag == Drag.None && view.Contains(mouse))
                {
                    if (HitIn(mouse, rects, out var hn, out var hp)) hoverDoc = PortDoc(graph.Nodes[hn], hp, false);
                    else if (HitOut(mouse, rects, out hn, out hp)) hoverDoc = PortDoc(graph.Nodes[hn], hp, true);
                }
                foreach (var c in graph.Comments.ToList()) DrawComment(c);

                if (Event.current.type == EventType.Repaint)
                {
                    foreach (var w in graph.Wires)
                        if (rects.ContainsKey(w.fromNode) && rects.ContainsKey(w.toNode))
                        {
                            // Live: on wires bright and thick, off wires dimmed.
                            var colour = Color.Lerp(KindColour(PortKind(graph.Nodes[w.fromNode], w.fromPort)), new Color(0.6f, 0.62f, 0.68f), 0.25f);
                            var width = 1f;
                            if (Live(w.fromNode, w.fromPort, out var v))
                            {
                                if (v > 0.5) { colour = Color.Lerp(colour, Color.white, 0.45f); width = 1.5f; }
                                else colour = new Color(colour.r, colour.g, colour.b, 0.45f);
                            }
                            Curve(OutPort(w.fromNode, w.fromPort, rects), InPort(w.toNode, w.toPort, rects), colour, width);
                        }
                    if (drag == Drag.Wire && graph.Nodes.ContainsKey(wireNode))
                        Curve(OutPort(wireNode, wirePort, rects), Event.current.mousePosition, KindColour(PortKind(graph.Nodes[wireNode], wirePort)));
                }
                foreach (var node in DrawOrder()) DrawNode(node, rects[node.Id]);
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
                DrawPreview(view);
                if (Event.current.type == EventType.Repaint) DrawChoices();
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
                var fine = new Color(0.4f, 1f, 0.6f, 0.025f);
                var strong = new Color(0.4f, 1f, 0.6f, 0.055f);
                var first = Mathf.Floor(-pan.x / step);
                for (var k = first; k * step + pan.x < view.width; k++)
                    Fill(new Rect(k * step + pan.x, 0, 1, view.height), Mathf.RoundToInt(k) % 5 == 0 ? strong : fine);
                first = Mathf.Floor(-pan.y / step);
                for (var k = first; k * step + pan.y < view.height; k++)
                    Fill(new Rect(0, k * step + pan.y, view.width, 1), Mathf.RoundToInt(k) % 5 == 0 ? strong : fine);
            }

            // A node's size in canvas units: a title, a row per input/output pair (inputs left, outputs right), then a
            // row per setting.
            // Under the title, a line saying what the node does; at the bottom, a note when there is one (what a link
            // goes to, or what's missing).
            static int PortRows(Node n) => Math.Max(ShownIns(n).Length, n.Outs.Length);

            // The inputs a node shows. Optional ones (a display's hide, a counter's reset) stay out of the way until
            // they're used: wired, given a value, the node is the one selected, or a wire is being dragged (so it can
            // be dropped on one).
            static string[] ShownIns(Node n) => ShownIns(n, Open(n) || drag == Drag.Wire);
            static string[] ShownIns(Node n, bool all)
            {
                var ins = n.Ins;
                var optional = n.Type.Optional;
                if (all || optional == null) return ins;
                return ins.Where(p => !optional.Contains(p) || n.Settings.ContainsKey(p) || graph.Wires.Any(w => w.toNode == n.Id && w.toPort == p)).ToArray();
            }
            // Compact: a node shows its settings (and the line saying what it does) only while it's the one selected;
            // otherwise one summary line with its main setting.
            static float Height(Node n) => Height(n, Open(n), PortRows(n));
            // Its height closed, with no wire being dragged: what it takes up on the board when left alone.
            static float RestHeight(Node n) => Height(n, false, Math.Max(ShownIns(n, false).Length, n.Outs.Length));
            static float Height(Node n, bool open, int portRows)
            {
                var settingRows = open ? Settings(n.Type).Count() : Summary(n) != null ? 1 : 0;
                return (open ? Body : Head) + Row * (portRows + settingRows) + (open && Settings(n.Type).Any() ? 6 : 0) + 6 + (notes.ContainsKey(n) ? Row : 0);
            }
            static bool expandAll;   // the Nodes tab's preview shows everything
            static bool Open(Node n) => expandAll || n.Id == selected && picked.Count <= 1;
            static float BodyOf(Node n) => Open(n) ? Body : Head;

            // The setting that says most about a node, shown on its summary line: its name and value, or null.
            static readonly string[] summaryKeys = { "label", "title", "text", "port", "channel", "bus", "fields", "key", "op", "mode", "value", "index", "seconds" };
            static (string key, string value)? Summary(Node n)
            {
                if (n.Type.Name == "ship") return null;   // its output already says which value
                foreach (var k in summaryKeys)
                {
                    if (!n.Type.Settings.ContainsKey(k)) continue;
                    var v = SettingOf(n, k);
                    if (v != "" && v != "none") return (k, v);
                }
                return null;
            }

            // Drawn bottom to top; the selected node last, so its open settings sit over its neighbours.
            static IEnumerable<Node> DrawOrder() => graph.Nodes.Values.Where(n => n.Id != selected).Concat(graph.Nodes.Values.Where(n => n.Id == selected)).ToList();
            static IEnumerable<string> Settings(NodeType t) => t.Settings.Keys;
            const float Desc = 15, Body = Head + Desc;

            // ---- Notes on nodes: what links go to, and what's missing ----

            static readonly Dictionary<Node, (string text, bool warn)> notes = new Dictionary<Node, (string, bool)>();
            static LuaComputer runner;   // the computer running the open graph, worked out once a frame

            static void WorkOutNotes()
            {
                notes.Clear();
                runner = Lua.RunnerOf(file);
                if (graph == null) return;
                foreach (var n in graph.Nodes.Values)
                {
                    var note = NoteOf(n);
                    if (note.text != null) notes[n] = note;
                }
            }

            static string SettingOf(Node n, string key) =>
                n.Settings.TryGetValue(key, out var v) && v != "" ? v : n.Type.Settings.TryGetValue(key, out var d) ? d : "";

            static (string text, bool warn) NoteOf(Node n)
            {
                var t = n.Type;
                bool Wired(string port) => graph.Wires.Any(w => w.toNode == n.Id && w.toPort == port);
                switch (t.Name)
                {
                    case "input":
                    case "output":
                    {
                        if (runner == null) return (null, false);
                        var index = SettingOf(n, "index");
                        var outgoing = t.Name == "output";
                        var name = Lua.LinkName(runner, outgoing, index);
                        return name != null ? ($"link {index} = {name}", false) : ($"no link {index} {(outgoing ? "out of" : "into")} the computer", true);
                    }
                    case "port in":
                    case "port out":
                    {
                        var port = SettingOf(n, "port");
                        if (port == "") return ("give it a port name", true);
                        if (runner == null) return (null, false);
                        var name = Lua.LinkName(runner, t.Name == "port out", port);
                        return name != null ? ($"{port} = {name}", false) : ("empty: link a block, then pick this port", true);
                    }
                    case "screen":
                        if (!graph.Nodes.Values.Any(x => x != n && x.Type.Category == "Display"))
                            return ("draws nothing alone: add a meter, readout...", true);
                        return (null, false);
                }
                if (t.Category == "Display")
                {
                    var feeds = n.Ins.Where(p => n.Type.Optional?.Contains(p) != true).ToList();
                    if (feeds.Count > 0 && !feeds.Any(Wired)) return ($"wire something into {feeds[0]}", true);
                }
                return (null, false);
            }

            // The node's help cut to its first part, for the line under the title.
            static string ShortHelp(NodeType t)
            {
                var h = t.Help ?? "";
                var cut = h.IndexOfAny(new[] { ':', ';', '(' });
                return (cut > 12 ? h.Substring(0, cut) : h).Trim();
            }

            static Rect NodeRect(Node n) => new Rect(n.X * zoom + pan.x, n.Y * zoom + pan.y, NodeW * zoom, Height(n) * zoom);

            static Vector2 InPort(int id, string port, Dictionary<int, Rect> rects)
            {
                var i = Array.IndexOf(ShownIns(graph.Nodes[id]), port);
                return new Vector2(rects[id].x, rects[id].y + (BodyOf(graph.Nodes[id]) + 3 + Row * i + Row / 2) * zoom);
            }

            static Vector2 OutPort(int id, string port, Dictionary<int, Rect> rects)
            {
                var i = Array.IndexOf(graph.Nodes[id].Outs, port);
                return new Vector2(rects[id].xMax, rects[id].y + (BodyOf(graph.Nodes[id]) + 3 + Row * i + Row / 2) * zoom);
            }

            static bool Near(Vector2 a, Vector2 b) => (a - b).sqrMagnitude <= Mathf.Pow(Mathf.Max(8f, Port * zoom), 2);

            static bool HitOut(Vector2 m, Dictionary<int, Rect> rects, out int id, out string port)
            {
                foreach (var n in DrawOrder().Reverse())
                    foreach (var p in n.Outs)
                        if (Near(m, OutPort(n.Id, p, rects))) { id = n.Id; port = p; return true; }
                id = -1; port = null;
                return false;
            }

            static bool HitIn(Vector2 m, Dictionary<int, Rect> rects, out int id, out string port)
            {
                foreach (var n in DrawOrder().Reverse())
                    foreach (var p in ShownIns(n))
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
                        if (choiceNode != null)
                        {
                            if (ChoiceRect().Contains(m)) return;
                            choiceNode = null;   // a click elsewhere closes the list and goes on as usual
                        }
                        if (menuOpen)
                        {
                            if (MenuRect().Contains(m)) return;   // the menu's own controls take it
                            menuOpen = false;
                            e.Use();
                            return;
                        }
                        // A click away from the comment title being typed ends the typing.
                        if (editingComment != null && !CommentHead(editingComment).Contains(m)) { editingComment = null; GUIUtility.keyboardControl = 0; }
                        if (e.button == 0 && PreviewMouseDown(m)) { e.Use(); return; }
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
                            var hit = DrawOrder().Reverse().FirstOrDefault(n => rects[n.Id].Contains(m));
                            if (hit != null && e.clickCount == 2 && hit.Type.File != null && IsChipFile(hit.Type.File) &&
                                new Rect(rects[hit.Id].x, rects[hit.Id].y, rects[hit.Id].width, Head * zoom).Contains(m))
                            {
                                var chip = hit.Type;
                                Action inside = () => { Open(chip.File); message = $"Inside the chip {chip.Name}. Save it and every circuit using it updates."; };
                                if (Leave("open the chip " + chip.Name, false, inside)) inside();
                                e.Use();
                                return;
                            }
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
                        else if (drag == Drag.Place) { PreviewMouseDrag(m); e.Use(); }
                        else if (drag == Drag.Wire || drag == Drag.Box) e.Use();
                        break;

                    case EventType.MouseUp:
                        if (drag == Drag.Place)
                        {
                            drag = Drag.None;
                            PreviewMouseUp();
                            e.Use();
                        }
                        else if (drag == Drag.Wire)
                        {
                            if (HitIn(m, rects, out var toNode, out var toPort) && !Fits(PortKind(graph.Nodes[wireNode], wirePort), PortKind(graph.Nodes[toNode], toPort)))
                                message = $"{wirePort} carries {KindWords(PortKind(graph.Nodes[wireNode], wirePort))}, and {toPort} takes {KindWords(PortKind(graph.Nodes[toNode], toPort))}: they don't fit.";
                            else if (HitIn(m, rects, out toNode, out toPort))
                            {
                                graph.Wires.RemoveAll(w => w.toNode == toNode && w.toPort == toPort);
                                graph.Wires.Add((wireNode, wirePort, toNode, toPort, 0));
                                graph.Nodes[toNode].Settings.Remove(toPort);   // a wired input has no typed value
                            }
                            else if (!graph.Nodes.Values.Any(n => rects[n.Id].Contains(m)))
                            {
                                // Dropped on empty space: the node menu, and whatever is picked gets this wire.
                                pendingWire = (wireNode, wirePort);
                                OpenMenu(m);
                            }
                            drag = Drag.None;
                            e.Use();
                        }
                        else if (drag == Drag.Pan)
                        {
                            if (!dragMoved && e.button == 1) { pendingWire = null; OpenMenu(m); }
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
                        else if (drag != Drag.None)
                        {
                            if (drag == Drag.Node && dragFrom.Any(f => graph.Nodes.TryGetValue(f.Key, out var n) && (n.X != f.Value.x || n.Y != f.Value.y)))
                                Unstack(dragFrom.Keys);
                            drag = Drag.None;
                            e.Use();
                        }
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

            // Nodes just dropped, added or pasted (`moved`, kept together) slide the shortest way off any node they
            // landed on, so nothing ends up hidden under something else.
            const float Gap = 10;

            // The room a node needs: its resting size, plus the line a link node gets under it once its graph runs.
            static Rect Room(Node n)
            {
                var noteLater = !notes.ContainsKey(n) && (n.Type.Name == "input" || n.Type.Name == "output" || n.Type.Name == "port in" || n.Type.Name == "port out");
                return new Rect(n.X, n.Y, NodeW, RestHeight(n) + (noteLater ? Row : 0));
            }

            // The Tidy button: top to bottom, each node that sits on one above it moves down until it's clear.
            static void Tidy()
            {
                var placed = new List<Rect>();
                var count = 0;
                foreach (var n in graph.Nodes.Values.OrderBy(n => n.Y).ThenBy(n => n.X).ToList())
                {
                    var from = n.Y;
                    for (int tries = 0; tries < 200; tries++)
                    {
                        var r = Room(n);
                        var hit = placed.Where(p => p.Overlaps(new Rect(r.x - Gap, r.y - Gap, r.width + 2 * Gap, r.height + 2 * Gap))).ToList();
                        if (hit.Count == 0) break;
                        n.Y = Mathf.Ceil((hit.Max(p => p.yMax) + Gap) / 10) * 10;
                    }
                    if (n.Y != from) count++;
                    placed.Add(Room(n));
                }
                message = count == 0 ? "Nothing overlaps." : $"Tidied: moved {count} node{(count == 1 ? "" : "s")} down. Ctrl+Z puts them back.";
            }

            static void Unstack(IEnumerable<int> moved)
            {
                var group = moved.Where(graph.Nodes.ContainsKey).Select(id => graph.Nodes[id]).ToList();
                var others = graph.Nodes.Values.Except(group).Select(n => { var r = Room(n); return new Rect(r.x - Gap, r.y - Gap, r.width + 2 * Gap, r.height + 2 * Gap); }).ToList();
                if (group.Count == 0 || others.Count == 0) return;
                var mine = group.Select(Room).ToList();
                bool Clear(Vector2 by) => !mine.Any(a => others.Any(b => b.Overlaps(new Rect(a.x + by.x, a.y + by.y, a.width, a.height))));
                if (Clear(Vector2.zero)) return;
                var ways = new[] { Vector2.up, Vector2.right, Vector2.down, Vector2.left };   // up is down the screen here
                for (int step = 1; step <= 80; step++)
                    foreach (var way in ways)
                    {
                        var by = way * (step * 10);
                        if (!Clear(by)) continue;
                        foreach (var n in group) { n.X += by.x; n.Y += by.y; }
                        return;
                    }
            }
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
                    // Silkscreen on the board: a thin white outline and lettering.
                    Fill(r, new Color(1, 1, 1, 0.025f));
                    Border(r, new Color(0.9f, 0.95f, 0.9f, 0.45f));
                    Fill(new Rect(head.x, head.yMax - 1, head.width, 1), new Color(0.9f, 0.95f, 0.9f, 0.3f));
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
                    // A chip on the board: black epoxy, a pin-1 dot, its kind's colour as a stripe under the name.
                    Rounded(new Rect(r.x + 3 * zoom, r.y + 5 * zoom, r.width, r.height), new Color(0, 0, 0, 0.5f));   // shadow
                    if (picked.Contains(n.Id)) Rounded(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(1f, 0.78f, 0.3f));
                    Rounded(r, new Color(0.075f, 0.08f, 0.085f, 1f));
                    // The title bar: filled with its group's colour, or (Core.ColourHeaders off) dark with a stripe of it.
                    var filled = Core.ColourHeaders;
                    var bar = filled ? Color.Lerp(colour, Color.black, 0.15f) : new Color(0.11f, 0.115f, 0.12f, 1f);
                    Rounded(new Rect(r.x, r.y, r.width, headH), bar);
                    Fill(new Rect(r.x, r.y + headH - Mathf.Min(6, headH / 2), r.width, Mathf.Min(6, headH / 2)), bar);
                    if (!filled) Fill(new Rect(r.x + 6 * zoom, r.y + headH - 2 * zoom, r.width - 12 * zoom, 2 * zoom), colour);
                    Tinted(new Rect(r.x + 6 * zoom, r.y + headH / 2 - 3 * zoom, 6 * zoom, 6 * zoom), circle, filled ? new Color(0, 0, 0, 0.45f) : new Color(0.32f, 0.33f, 0.35f));
                    Border(r, n.Type.File != null && IsChipFile(n.Type.File) ? new Color(0.95f, 0.75f, 0.3f, 0.55f) : new Color(1, 1, 1, 0.07f));
                }
                GUI.Label(new Rect(r.x + 17 * zoom, r.y, r.width - 59 * zoom, headH), n.Type.Name.ToUpperInvariant(), head);
                GUI.Label(new Rect(r.xMax - 52 * zoom, r.y, 28 * zoom, headH), "U" + n.Id, idStyle);   // a board's part number
                if (GUI.Button(new Rect(r.xMax - 21 * zoom, r.y + 3 * zoom, 17 * zoom, headH - 6 * zoom), "×", close)) pendingDelete = n.Id;
                if (Open(n))
                {
                    var descRect = new Rect(r.x + 8 * zoom, r.y + headH, r.width - 14 * zoom, Desc * zoom);
                    GUI.Label(descRect, ShortHelp(n.Type), desc);
                    if (descRect.Contains(Event.current.mousePosition)) hoverDoc = $"{n.Type.Name}: {n.Type.Help}";
                }
                else if (new Rect(r.x, r.y, r.width, headH).Contains(Event.current.mousePosition))
                    hoverDoc = $"{n.Type.Name}: {n.Type.Help}  (click it for its settings)";

                var ins = ShownIns(n);
                var rows = Math.Max(ins.Length, n.Outs.Length);
                for (int i = 0; i < rows; i++)
                {
                    var y = r.y + (BodyOf(n) + 3 + Row * i) * zoom;
                    var rowH = Row * zoom;
                    var hasOut = i < n.Outs.Length;
                    if (i < ins.Length)
                    {
                        var p = ins[i];
                        PortDot(new Vector2(r.x, y + rowH / 2), KindColour(PortKind(n, p)), wired.Contains(p));
                        var nameW = label.CalcSize(new GUIContent(p)).x;
                        GUI.Label(new Rect(r.x + 10 * zoom, y, nameW + 2, rowH), p, label);
                        var inTag = KindTag(PortKind(n, p));
                        tag.normal.textColor = KindColour(PortKind(n, p));
                        var tagW = tag.CalcSize(new GUIContent(inTag)).x;
                        GUI.Label(new Rect(r.x + 12 * zoom + nameW, y, tagW + 2, rowH), inTag, tag);
                        if (!wired.Contains(p))
                        {
                            // An unwired input's value, typed in; empty means the node's default (shown greyed).
                            n.Settings.TryGetValue(p, out var v);
                            var fieldX = Mathf.Max(52 * zoom, 16 * zoom + nameW + tagW);
                            var width = hasOut ? r.width * 0.5f - fieldX : r.width - fieldX - 12 * zoom;
                            var box = new Rect(r.x + fieldX, y + 3 * zoom, width, rowH - 6 * zoom);
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
                        var outTag = KindTag(PortKind(n, p));
                        tag.normal.textColor = KindColour(PortKind(n, p));
                        var outNameW = labelRight.CalcSize(new GUIContent(p)).x;
                        var outTagW = tag.CalcSize(new GUIContent(outTag)).x;
                        GUI.Label(new Rect(r.xMax - 14 * zoom - outNameW - outTagW, y, outTagW + 2, rowH), outTag, tag);
                        if (Live(n.Id, p, out var value) && !outWired.Contains(p))
                        {
                            // An output with no wire shows its value beside its name.
                            var nameW = labelRight.CalcSize(new GUIContent(p)).x;
                            GUI.Label(new Rect(r.x + 10 * zoom, y, r.width - 22 * zoom - nameW - outTagW - 8 * zoom, rowH), Show(value), liveText);
                        }
                        PortDot(new Vector2(r.xMax, y + rowH / 2), KindColour(PortKind(n, p)), outWired.Contains(p));
                    }
                }
                var settings = Open(n) ? Settings(n.Type).ToList() : new List<string>();
                var top = r.y + (BodyOf(n) + 3 + Row * rows + 3) * zoom;
                if (ins.Length < n.Ins.Length)
                {
                    // Inputs kept out of the way: a small dim pin under the others stands for them.
                    var more = new Rect(r.x - 3 * zoom, top - 5 * zoom, 6 * zoom, 4 * zoom);
                    if (Event.current.type == EventType.Repaint) Fill(more, new Color(1, 1, 1, 0.3f));
                    if (new Rect(more.x - 4, more.y - 4, more.width + 8, more.height + 8).Contains(Event.current.mousePosition))
                        hoverDoc = $"{n.Type.Name} has more inputs you don't have to use ({string.Join(", ", n.Ins.Except(ins).ToArray())}): click the node, or drag a wire, to see them.";
                }
                if (settings.Count > 0)
                {
                    if (Event.current.type == EventType.Repaint) Fill(new Rect(r.x + 8 * zoom, top - 2 * zoom, r.width - 16 * zoom, 1), new Color(1, 1, 1, 0.07f));
                    // Labels as wide as the longest name needs (within reason), so "background" isn't cut short.
                    var labelW = Mathf.Clamp(settings.Max(s => dim.CalcSize(new GUIContent(s)).x) + 6 * zoom, 56 * zoom, 92 * zoom);
                    for (int i = 0; i < settings.Count; i++)
                    {
                        var s = settings[i];
                        var y = top + Row * i * zoom;
                        var labelRect = new Rect(r.x + 10 * zoom, y, labelW, Row * zoom);
                        GUI.Label(labelRect, s, dim);
                        if (labelRect.Contains(Event.current.mousePosition))
                            hoverDoc = n.Type.Docs.TryGetValue(s, out var doc) ? $"{s}: {doc}" : $"{s}: a setting of {n.Type.Name}";
                        var value = n.Settings.TryGetValue(s, out var v) ? v : n.Type.Settings[s];
                        var box = new Rect(r.x + 12 * zoom + labelW, y + 3 * zoom, r.width - 24 * zoom - labelW, (Row - 6) * zoom);
                        var choices = ChoicesOf(n.Type, s);
                        if (choices == null)
                        {
                            var typed = GUI.TextField(box, value, field);
                            if (typed != value) n.Settings[s] = typed;
                        }
                        else if (choices.Length == 2 && choices[0] == "0" && choices[1] == "1")
                        {
                            // On/off: a click flips it.
                            var on = value == "1";
                            if (GUI.Button(box, on ? "  on" : "  off", choiceButton)) n.Settings[s] = on ? "0" : "1";
                            if (Event.current.type == EventType.Repaint)
                            {
                                var mark = new Rect(box.xMax - box.height, box.y + 2 * zoom, box.height - 4 * zoom, box.height - 4 * zoom);
                                Tinted(mark, on ? circle : ring, on ? new Color(0.45f, 1f, 0.6f) : new Color(1, 1, 1, 0.35f));
                            }
                        }
                        else
                        {
                            // A list to pick from (colours show a swatch).
                            var swatch = IsColour(s);
                            if (GUI.Button(box, (swatch ? "     " : "") + value + "  ▾", choiceButton))
                            {
                                choiceNode = n; choiceKey = s; choiceItems = choices;
                                choiceAt = new Rect(box.x, box.yMax + 2, Mathf.Max(box.width, 120 * zoom), 0);
                            }
                            if (swatch && Event.current.type == EventType.Repaint && SwatchColour(value, out var sc))
                                Fill(new Rect(box.x + 4 * zoom, box.y + 3 * zoom, 12 * zoom, box.height - 6 * zoom), sc);
                        }
                    }
                    top += Row * settings.Count * zoom + 6 * zoom;
                }
                else if (Summary(n) is (string key, string value))
                {
                    GUI.Label(new Rect(r.x + 10 * zoom, top, 60 * zoom, Row * zoom), key, dim);
                    GUI.Label(new Rect(r.x + 15 * zoom + dim.CalcSize(new GUIContent(key)).x, top, r.width - 27 * zoom - dim.CalcSize(new GUIContent(key)).x, Row * zoom), value, label);
                    top += Row * zoom + 3 * zoom;
                }
                else top -= 3 * zoom;
                if (notes.TryGetValue(n, out var note))
                    GUI.Label(new Rect(r.x + 10 * zoom, top, r.width - 18 * zoom, Row * zoom), (note.warn ? "! " : "") + note.text, note.warn ? warnText : noteText);
            }

            // ---- The monitor preview: what the running graph draws, in the canvas's corner ----
            // Display nodes show as outlines on it; drag one to move it (its x and y), and the graph saves when you let
            // go, so the monitor shows it there a moment later.

            static bool showPreview = true;
            static Rect previewRect, previewPicture;
            static Monitors.Display previewDisplay;
            static Node placing;
            static Vector2 placeStart, placeFrom;
            const float PreviewW = 250, PreviewTitle = 22;

            static bool DrawsOnScreen(Node n) => n.Type.Category == "Display" && n.Type.Name != "screen";

            // The screen the preview shows: the selected Display node's, else the first.
            static string PreviewScreen()
            {
                if (selected >= 0 && graph.Nodes.TryGetValue(selected, out var n) && n.Type.Settings.ContainsKey("screen")) return SettingOf(n, "screen");
                return "1";
            }

            static Monitors.Display PreviewDisplay()
            {
                if (!showPreview || runner == null || !graph.Nodes.Values.Any(DrawsOnScreen)) return null;
                var which = PreviewScreen();
                var key = double.TryParse(which, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    ? MoonSharp.Interpreter.DynValue.NewNumber(number) : MoonSharp.Interpreter.DynValue.NewString(which);
                try { return Monitors.Get(runner, key); } catch { return null; }
            }

            // Where a Display node draws, in the monitor's canvas units, worked out from its settings.
            static Rect? PlaceBox(Node n, float screenW)
            {
                var s = n.Type.Settings;
                if (!s.ContainsKey("x") && !s.ContainsKey("y")) return null;
                float F(string k, float def) => float.TryParse(SettingOf(n, k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
                float x = F("x", 0), y = F("y", 0);
                if (n.Type.Name == "alert") return new Rect(0, y, screenW, F("height", 22));
                if (s.ContainsKey("r")) { var r = F("r", 40); return new Rect(x - r, y - r, r * 2, r * 2); }
                if (s.ContainsKey("w")) return new Rect(x, y, F("w", 200), s.ContainsKey("h") ? F("h", 80) : 24);
                if (n.Type.Name == "readout")
                {
                    var size = F("size", 14);
                    var w = Mathf.Max(20, SettingOf(n, "text").Replace("{v}", "000").Length * size * 0.6f);
                    var align = SettingOf(n, "align");
                    return new Rect(align == "center" ? x - w / 2 : align == "right" ? x - w : x, y, w, size * 1.2f);
                }
                return new Rect(x, y, 40, 20);
            }

            static Rect ToPicture(Rect box, Monitors.Display d) => new Rect(
                previewPicture.x + box.x / d.Width * previewPicture.width, previewPicture.y + box.y / d.Height * previewPicture.height,
                box.width / d.Width * previewPicture.width, box.height / d.Height * previewPicture.height);

            // The Display node under a point on the preview: the smallest box there, so a meter wins over its panel.
            static Node PlacedAt(Vector2 m)
            {
                var d = previewDisplay;
                if (d == null || !previewPicture.Contains(m)) return null;
                var screen = PreviewScreen();
                return graph.Nodes.Values.Where(n => DrawsOnScreen(n) && SettingOf(n, "screen") == screen)
                    .Select(n => (n, box: PlaceBox(n, d.Width))).Where(p => p.box.HasValue && ToPicture(p.box.Value, d).Contains(m))
                    .OrderBy(p => p.box.Value.width * p.box.Value.height).Select(p => p.n).FirstOrDefault();
            }

            static void DrawPreview(Rect view)
            {
                previewDisplay = PreviewDisplay();
                if (previewDisplay == null) { previewRect = Rect.zero; return; }
                var d = previewDisplay;
                var aspect = d.Width > 0 && d.Height > 0 ? d.Height / d.Width : 1f;
                previewRect = new Rect(view.width - PreviewW - 10, 10, PreviewW, PreviewTitle + PreviewW * aspect + 8);
                previewPicture = new Rect(previewRect.x + 4, previewRect.y + PreviewTitle, PreviewW - 8, (PreviewW - 8) * aspect);
                if (Event.current.type != EventType.Repaint) return;
                Rounded(new Rect(previewRect.x + 3, previewRect.y + 5, previewRect.width, previewRect.height), new Color(0, 0, 0, 0.5f));
                Rounded(previewRect, new Color(0.12f, 0.13f, 0.155f, 1f));
                GUI.Label(new Rect(previewRect.x + 8, previewRect.y + 2, PreviewW - 16, PreviewTitle - 2), $"MONITOR {PreviewScreen()}  ·  drag to move", menuHeader);
                Fill(previewPicture, Color.black);
                if (d.Shown != null) GUI.DrawTexture(previewPicture, d.Shown, ScaleMode.StretchToFill, false);
                else GUI.Label(previewPicture, "  nothing drawn yet", hint);
                var hot = drag == Drag.Place ? placing : PlacedAt(Event.current.mousePosition);
                var screen = PreviewScreen();
                foreach (var n in graph.Nodes.Values.Where(x => DrawsOnScreen(x) && SettingOf(x, "screen") == screen))
                {
                    var box = PlaceBox(n, d.Width);
                    if (!box.HasValue) continue;
                    var r = ToPicture(box.Value, d);
                    var lit = n == hot || picked.Contains(n.Id);
                    Border(r, lit ? new Color(1f, 0.78f, 0.3f, 0.95f) : new Color(1, 1, 1, 0.18f));
                    if (lit) GUI.Label(new Rect(r.x + 2, r.yMax, 140, 14), n.Type.Name + " #" + n.Id, menuHeader);
                }
            }

            // Mouse on the preview: press on an outline to pick that node and start moving it.
            static bool PreviewMouseDown(Vector2 m)
            {
                if (previewRect == Rect.zero || !previewRect.Contains(m)) return false;
                var n = PlacedAt(m);
                if (n != null)
                {
                    picked.Clear();
                    picked.Add(n.Id);
                    selected = n.Id;
                    placing = n;
                    placeStart = m;
                    float.TryParse(SettingOf(n, "x"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out placeFrom.x);
                    float.TryParse(SettingOf(n, "y"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out placeFrom.y);
                    drag = Drag.Place;
                    GUIUtility.keyboardControl = 0;
                }
                return true;
            }

            static void PreviewMouseDrag(Vector2 m)
            {
                var d = previewDisplay;
                if (placing == null || d == null || previewPicture.width <= 0) return;
                var units = (m - placeStart) / previewPicture.width * d.Width;
                string Round(float v) => Mathf.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (placing.Type.Name != "alert") placing.Settings["x"] = Round(Mathf.Clamp(placeFrom.x + units.x, -20, d.Width + 20));
                placing.Settings["y"] = Round(Mathf.Clamp(placeFrom.y + units.y, -20, d.Height + 20));
            }

            static void PreviewMouseUp()
            {
                if (placing != null && Dirty && runner != null)
                {
                    Save();
                    message = $"Moved {placing.Type.Name} to x={SettingOf(placing, "x")} y={SettingOf(placing, "y")}, and saved.";
                }
                placing = null;
            }

            // ---- Settings with fixed choices: a list, an on/off flip, or a colour palette ----

            static Node choiceNode;
            static string choiceKey;
            static string[] choiceItems;
            static Rect choiceAt;
            static string hoverDoc;   // what the mouse is over (a setting's name, a port), shown under the canvas

            static readonly string[] Palette = { "theme", "green", "amber", "red", "blue", "cyan", "white", "grey", "dim", "black" };
            static bool IsColour(string setting) => setting == "color" || setting == "colour" || setting == "background";

            static string[] ChoicesOf(NodeType t, string setting)
            {
                if (t.Choices != null && t.Choices.TryGetValue(setting, out var list)) return list;
                if (IsColour(setting) && t.Source != null) return setting == "background" ? new[] { "none" }.Concat(Palette.Skip(1)).ToArray() : Palette;
                return null;
            }

            // The colour names the displays know (Monitors.cs), roughly, for the swatches; #rrggbb too.
            static bool SwatchColour(string name, out Color c)
            {
                switch (name)
                {
                    case "green": c = new Color(0.26f, 1f, 0.55f); return true;
                    case "amber": c = new Color(1f, 0.69f, 0.23f); return true;
                    case "red": c = new Color(1f, 0.3f, 0.35f); return true;
                    case "blue": c = new Color(0.3f, 0.55f, 1f); return true;
                    case "cyan": c = new Color(0.26f, 1f, 0.91f); return true;
                    case "white": c = Color.white; return true;
                    case "grey": c = new Color(0.55f, 0.55f, 0.58f); return true;
                    case "dim": c = new Color(0.2f, 0.25f, 0.22f); return true;
                    case "black": c = Color.black; return true;
                }
                return ColorUtility.TryParseHtmlString(name ?? "", out c);
            }

            static Rect ChoiceRect() => new Rect(choiceAt.x, choiceAt.y, choiceAt.width, Mathf.Min(choiceItems.Length, 12) * (Row * zoom) + 6);

            // The open list, drawn over everything on the canvas; a pick sets the setting.
            static void DrawChoices()
            {
                if (choiceNode == null) return;
                if (!graph.Nodes.ContainsValue(choiceNode)) { choiceNode = null; return; }
                var box = ChoiceRect();
                if (Event.current.type == EventType.Repaint)
                {
                    Rounded(new Rect(box.x + 2, box.y + 4, box.width, box.height), new Color(0, 0, 0, 0.5f));
                    Rounded(box, new Color(0.12f, 0.13f, 0.155f, 1f));
                    Border(box, new Color(1, 1, 1, 0.15f));
                }
                var rowH = Row * zoom;
                var current = SettingOf(choiceNode, choiceKey);
                for (int i = 0; i < Mathf.Min(choiceItems.Length, 12); i++)
                {
                    var row = new Rect(box.x + 3, box.y + 3 + i * rowH, box.width - 6, rowH);
                    var item = choiceItems[i];
                    if (Event.current.type == EventType.Repaint)
                    {
                        if (row.Contains(Event.current.mousePosition)) Rounded(row, new Color(1, 1, 1, 0.1f));
                        else if (item == current) Rounded(row, new Color(1f, 0.78f, 0.3f, 0.18f));
                        if (IsColour(choiceKey) && SwatchColour(item, out var c)) Fill(new Rect(row.x + 5, row.y + 4 * zoom, 12 * zoom, rowH - 8 * zoom), c);
                    }
                    if (GUI.Button(row, (IsColour(choiceKey) ? "      " : "  ") + item, label))
                    {
                        choiceNode.Settings[choiceKey] = item;
                        choiceNode = null;
                        Event.current.Use();
                        return;
                    }
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
                expandAll = true;
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
                    expandAll = false;
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
                    case "Display": return new Color(0.16f, 0.42f, 0.56f);
                    case "Controls": return new Color(0.56f, 0.5f, 0.16f);
                    case "Shortcuts": return new Color(0.6f, 0.28f, 0.22f);
                    case "Bus": return new Color(0.24f, 0.3f, 0.62f);
                    case "Encode": return new Color(0.4f, 0.42f, 0.2f);
                    case "Ship OS": return new Color(0.14f, 0.5f, 0.34f);
                    case "Chips": case "Chip": return new Color(0.6f, 0.46f, 0.14f);
                    case "Script": return new Color(0.42f, 0.24f, 0.56f);
                    default: return new Color(0.32f, 0.33f, 0.37f);
                }
            }

            static Color WireColour(NodeType t) => Color.Lerp(Header(t), Color.white, 0.35f);

            // A pin: a pad sticking out of the chip's edge, filled when a trace is soldered to it. Inputs on the left
            // edge, outputs on the right, in the colour of what they carry (KindColour), like their wires.
            static void PortDot(Vector2 at, Color colour, bool wired)
            {
                if (Event.current.type != EventType.Repaint) return;
                var w = 9f * zoom;
                var h = 7f * zoom;
                var r = new Rect(at.x - w / 2, at.y - h / 2, w, h);
                Fill(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), new Color(0.02f, 0.05f, 0.035f, 1f));
                Fill(r, wired ? colour : new Color(colour.r * 0.25f, colour.g * 0.25f, colour.b * 0.25f, 1f));
                if (!wired) Border(r, colour);
                else Fill(new Rect(r.x + 1, r.y + 1, r.width - 2, Mathf.Max(1, r.height * 0.35f)), new Color(1, 1, 1, 0.25f));   // a shine on the solder
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
                if (type.Name == "lua" && type.Source == null) node.Script = LuaNodeExample;
                graph.Nodes[id] = node;
                // Added from a wire dropped on empty space: plug the wire into the input that fits best.
                if (pendingWire.HasValue && graph.Nodes.ContainsKey(pendingWire.Value.node))
                {
                    var ins = node.Ins;
                    var into = ins.FirstOrDefault(p => p == pendingWire.Value.port) ??
                               new[] { "value", "x", "in", "a", "in1" }.FirstOrDefault(ins.Contains) ?? ins.FirstOrDefault(p => type.Optional?.Contains(p) != true);
                    if (into != null) graph.Wires.Add((pendingWire.Value.node, pendingWire.Value.port, id, into, 0));
                }
                pendingWire = null;
                Unstack(new[] { id });
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

            // A wire is a circuit-board trace: out of the pin to the right, 45 degree bends, a straight run, into the pin
            // from the left. A wire running backwards goes round, under the two pins.
            static List<Vector2> Route(Vector2 a, Vector2 b)
            {
                var lead = 14f * zoom;
                var pts = new List<Vector2> { a };
                if (b.x - a.x >= lead * 2)
                {
                    var xm = (a.x + b.x) / 2;
                    var dy = b.y - a.y;
                    var s = Mathf.Sign(dy);
                    var h = Mathf.Max(0, Mathf.Min(Mathf.Abs(dy) / 2, (b.x - a.x) / 2 - lead));
                    pts.Add(new Vector2(xm - h, a.y));
                    pts.Add(new Vector2(xm, a.y + s * h));
                    pts.Add(new Vector2(xm, b.y - s * h));
                    pts.Add(new Vector2(xm + h, b.y));
                }
                else
                {
                    var under = Mathf.Max(a.y, b.y) + 30f * zoom;
                    pts.Add(new Vector2(a.x + lead, a.y));
                    pts.Add(new Vector2(a.x + lead, under));
                    pts.Add(new Vector2(b.x - lead, under));
                    pts.Add(new Vector2(b.x - lead, b.y));
                }
                pts.Add(b);
                return pts;
            }

            static Vector2 CurvePoint(Vector2 a, Vector2 b, float t)
            {
                var pts = Route(a, b);
                float total = 0;
                for (int i = 1; i < pts.Count; i++) total += (pts[i] - pts[i - 1]).magnitude;
                var want = total * t;
                for (int i = 1; i < pts.Count; i++)
                {
                    var len = (pts[i] - pts[i - 1]).magnitude;
                    if (want <= len && len > 0) return Vector2.Lerp(pts[i - 1], pts[i], want / len);
                    want -= len;
                }
                return b;
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

            // thickness above 1 means the wire is on: brighter, with a glow around it.
            static void Curve(Vector2 a, Vector2 b, Color c, float thickness = 1f)
            {
                MakeTextures();
                var pts = Route(a, b);
                var w = Mathf.Max(2f, 3f * zoom) * Mathf.Min(thickness, 1.3f);
                for (int i = 1; i < pts.Count; i++)
                {
                    if (thickness > 1f) Line(pts[i - 1], pts[i], w * 3.2f, new Color(c.r, c.g, c.b, 0.18f));
                    Line(pts[i - 1], pts[i], w + 2f, new Color(0.01f, 0.05f, 0.03f, 0.75f));   // the board's mask around the copper
                }
                for (int i = 1; i < pts.Count; i++) Line(pts[i - 1], pts[i], w, c);
                // a via where the trace bends
                for (int i = 1; i < pts.Count - 1; i++)
                    if (Vector2.Angle(pts[i] - pts[i - 1], pts[i + 1] - pts[i]) > 1f) Tinted(new Rect(pts[i].x - w * 0.6f, pts[i].y - w * 0.6f, w * 1.2f, w * 1.2f), circle, c);
            }

            static GUIStyle idStyle, close, dim, hint, menuField, hintMenu, menuHeader, menuItem, menuHelp, pill, liveText, commentTitle, desc, noteText, warnText, choiceButton, tag;

            static void MakeStyles()
            {
                MakeTextures();
                if (Mathf.Approximately(stylesFor, zoom) && label != null) return;
                stylesFor = zoom;
                int size = Mathf.Max(6, Mathf.RoundToInt(12 * zoom)), small = Mathf.Max(5, Mathf.RoundToInt(10 * zoom));
                Color text = new Color(0.88f, 0.9f, 0.94f), muted = new Color(0.72f, 0.75f, 0.8f);
                label = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0), wordWrap = false, clipping = TextClipping.Clip };
                label.normal.textColor = text;
                labelRight = new GUIStyle(label) { alignment = TextAnchor.MiddleRight };
                dim = new GUIStyle(label);
                dim.normal.textColor = muted;
                head = new GUIStyle(label) { font = GameLook.DisplayFont, fontSize = Mathf.Max(6, Mathf.RoundToInt(12 * zoom)) };   // silkscreen lettering
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
                pill = new GUIStyle(label) { font = GameLook.CodeFont, fontSize = Mathf.Max(6, Mathf.RoundToInt(11 * zoom)), alignment = TextAnchor.MiddleCenter };
                liveText = new GUIStyle(labelRight) { font = GameLook.CodeFont, fontSize = Mathf.Max(6, Mathf.RoundToInt(11 * zoom)) };
                liveText.normal.textColor = new Color(0.63f, 1f, 0.88f);
                commentTitle = new GUIStyle(label) { fontStyle = FontStyle.Bold, fontSize = Mathf.Max(6, Mathf.RoundToInt(13 * zoom)) };
                commentTitle.normal.textColor = new Color(1, 1, 1, 0.85f);
                hint = new GUIStyle(label) { fontSize = size };
                hint.normal.textColor = new Color(1, 1, 1, 0.25f);
                desc = new GUIStyle(label) { fontSize = small, fontStyle = FontStyle.Italic };
                desc.normal.textColor = new Color(1, 1, 1, 0.45f);
                noteText = new GUIStyle(label) { fontSize = small };
                noteText.normal.textColor = new Color(0.55f, 0.85f, 1f);
                warnText = new GUIStyle(label) { fontSize = small };
                warnText.normal.textColor = new Color(1f, 0.72f, 0.3f);
                choiceButton = new GUIStyle(field) { alignment = TextAnchor.MiddleLeft };
                tag = new GUIStyle(label) { fontSize = Mathf.Max(5, Mathf.RoundToInt(10 * zoom)) };
                choiceButton.hover.textColor = Color.white;

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
            static GUIStyle wrapped;
            static GUIStyle Wrapped() => wrapped ?? (wrapped = new GUIStyle(GUI.skin.label) { wordWrap = true });
            static GUIStyle Rich() => rich ?? (rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

            static bool Same(string a, string b) =>
                a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
