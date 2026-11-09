using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;

namespace EvoCore
{
    // The script editor in the Scripts tab of the Mod Settings window (F11). The window holds the game's controls while
    // open, so typing doesn't fly the ship. Pick a script on the left, edit it, Save (or Ctrl+S): the file is written
    // and every screen or computer running it reloads within a second, as when it's saved from any other editor.
    // Saving checks the syntax first and shows the error with its line, but saves anyway.
    // Unsaved edits survive closing the window. A file changed on disk while there are none is reloaded.
    // Using a Lua Computer opens the window here on its script (OpenInEditor), creating the file if it's missing.
    public static partial class Lua
    {
        public static void OpenInEditor(string name)
        {
            try
            {
                // The Computer screen (GraphEditor.cs): its Graph tab for a computer running a graph, else its Script tab.
                if (File.Exists(Path.Combine(ScriptsDir, name + ".graph"))) { Graphs.OpenCanvas(name); return; }
                Editor.OpenByName(name);
                Graphs.OpenScriptTab();
            }
            catch (Exception e) { Core.Log.Error("Opening the script editor failed: " + e); }
        }

        // The script editor inside the Computer screen's Script tab.
        // height: the code area's, from the Computer screen; the Mod Settings tab uses the default.
        internal static void DrawScriptEditor(float height = 330)
        {
            Editor.EditorHeight = Mathf.Max(200, height);
            Editor.Draw();
        }

        static class Editor
        {
            static string file;             // full path of the open script, or null
            static string text = "", saved = "";
            static DateTime fileTime;
            static string newName = "", message = "", syntax = "";
            static Vector2 listScroll, textScroll;
            static GUIStyle code;
            static int cursorLine = 1;
            const string Control = "EvoCoreLuaEditor";
            static readonly Regex validName = new Regex(@"^[\w\-. ]+$");

            static bool Dirty => file != null && text != saved;

            internal static void Draw()
            {
                if (code == null) MakeStyle();
                HandleKeys();
                GUILayout.BeginHorizontal();
                DrawList();
                GUILayout.BeginVertical();
                if (file == null) GUILayout.Label("Pick a script on the left, or make a new one.");
                else DrawEditor();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }

            static void MakeStyle()
            {
                code = new GUIStyle(GUI.skin.textArea) { wordWrap = false, richText = false };
                try
                {
                    var mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 14);
                    if (mono != null) code.font = mono;
                }
                catch { }
            }

            static void DrawList()
            {
                GUILayout.BeginVertical(GUILayout.Width(190));
                var made = NewRow("script");
                if (made != null) Create(made);
                listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(EditorHeight));
                ComputerListEntry(file, false);
                foreach (var path in Scripts())
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var runs = running.Values.Count(r => SamePath(r.File, path));
                    var label = (SamePath(path, file) ? "> " : "") + name + (runs > 0 ? $"  ({runs} running)" : "");
                    if (GUILayout.Button(label, GUI.skin.label)) Open(path);
                }
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
            }

            static void DrawEditor()
            {
                CheckDisk();
                GUILayout.BeginHorizontal();
                GUILayout.Label(Path.GetFileName(file) + (Dirty ? "  (unsaved)" : ""), GUILayout.ExpandWidth(true));
                GUILayout.Label("Line " + cursorLine, GUILayout.Width(80));
                ComputerButtons(file);
                GUI.enabled = Dirty;
                if (GUILayout.Button("Save", GUILayout.Width(80))) Save();
                if (GUILayout.Button("Revert", GUILayout.Width(80))) { text = saved; message = "Reverted."; syntax = ""; }
                GUI.enabled = true;
                var gone = DeleteButton(file);
                GUILayout.EndHorizontal();
                if (gone != null)
                {
                    message = $"Deleted {Path.GetFileName(file)}: moved to {Path.GetFileName(Path.GetDirectoryName(gone))}\\{Path.GetFileName(gone)}.";
                    file = null;
                    text = saved = "";
                    syntax = "";
                    return;
                }

                LiveCheck();
                // A syntax error (checked as you type) wins; else the error a running copy stopped on.
                var runError = running.Values.Where(r => SamePath(r.File, file) && r.Error != null).Select(r => r.Error).FirstOrDefault();
                var errorText = syntax != "" ? syntax : runError;
                text = CodeView.Draw(text, ref textScroll, EditorHeight, Control, CodeView.ErrorLine(errorText), out cursorLine);

                if (!string.IsNullOrEmpty(errorText)) GUILayout.Label("<color=#ff8a80>" + (syntax != "" ? "Syntax: " : "Stopped: ") + errorText + "</color>", Rich());
                if (message != "") GUILayout.Label(message);
                DrawConsole();
            }

            // How tall the code area is: the Computer screen sets it from its own size.
            internal static float EditorHeight = 330;

            // Checks the syntax half a second after typing stops, so mistakes show while you write.
            static string checkedText;
            static float checkAt;
            static void LiveCheck()
            {
                if (text == checkedText) return;
                if (checkAt <= 0) { checkAt = Time.realtimeSinceStartup + 0.5f; return; }
                if (Time.realtimeSinceStartup < checkAt) return;
                checkedText = text;
                checkAt = 0;
                syntax = Check(text);
            }

            // The console: each running copy's status and its latest output, in the code font.
            static Vector2 consoleScroll;
            static GUIStyle consoleText;
            static void DrawConsole()
            {
                if (consoleText == null || consoleText.font != GameLook.CodeFont)
                {
                    consoleText = new GUIStyle(GUI.skin.label) { font = GameLook.CodeFont, fontSize = 13, wordWrap = false, richText = true, padding = new RectOffset(10, 6, 1, 1) };
                    consoleText.normal.textColor = new Color(0.8f, 0.83f, 0.87f);
                }
                var runs = running.Where(p => SamePath(p.Value.File, file)).ToList();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Console", GUILayout.ExpandWidth(true));
                for (int i = 0; i < runs.Count; i++)
                {
                    var pair = runs[i];
                    var what = (pair.Key is LuaComputer ? "computer" : "screen") + (runs.Count > 1 ? " " + (i + 1) : "");
                    if (GUILayout.Button("Restart " + what, GUILayout.Width(180))) { Stop(pair.Key, pair.Value); nextScan = 0f; }
                }
                GUILayout.EndHorizontal();
                var box = GUILayoutUtility.GetRect(100, 10000, 150, 150, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint)
                {
                    var old = GUI.color;
                    GUI.color = new Color(0.09f, 0.1f, 0.13f, 1f);
                    GUI.DrawTexture(box, Texture2D.whiteTexture);
                    GUI.color = old;
                }
                var lines = new List<string>();
                if (runs.Count == 0)
                    lines.Add("<color=#9aa1ad>-- not running. Label a Lua Computer \"" + Path.GetFileNameWithoutExtension(file) + "\" with the Tag tool, or put [lua: " + Path.GetFileNameWithoutExtension(file) + "] on a screen.</color>");
                for (int i = 0; i < runs.Count; i++)
                {
                    var pair = runs[i];
                    var r = pair.Value;
                    var where = (pair.Key is LuaComputer ? "computer" : "screen") + (runs.Count > 1 ? " " + (i + 1) : "");
                    var colour = r.Error != null ? "ff8a80" : r.Status == "running" ? "a1ffe0" : "cdcfd2";
                    lines.Add($"<color=#{colour}>-- {where} on {r.Context.Ship?.EntityName}: {r.Status} ({r.Ms:0.00} ms)</color>");
                    foreach (var line in r.Output) lines.Add(line.Replace("<", "‹"));   // keep printed text from reading as tags
                }
                var lineH = consoleText.lineHeight + 2;
                consoleScroll = GUI.BeginScrollView(box, consoleScroll, new Rect(0, 0, box.width - 14, Mathf.Max(box.height, 8 + lines.Count * lineH)));
                for (int i = 0; i < lines.Count; i++) GUI.Label(new Rect(0, 4 + i * lineH, 4000, lineH), lines[i], consoleText);
                GUI.EndScrollView();
            }

            // Tab indents (IMGUI would move focus instead), and Ctrl+S saves.
            static void HandleKeys()
            {
                var e = Event.current;
                if (e.type != EventType.KeyDown || file == null || !Focused) return;
                if ((e.keyCode == KeyCode.Tab || e.character == '\t') && Editing(out var editor))
                {
                    if (e.keyCode == KeyCode.Tab) editor.ReplaceSelection("  ");
                    text = editor.text;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.S && (e.control || e.command))
                {
                    if (Dirty) Save();
                    e.Use();
                }
            }

            static bool Focused => GUI.GetNameOfFocusedControl() == Control;

            static bool Editing(out TextEditor editor)
            {
                editor = GUIUtility.keyboardControl != 0
                    ? GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor
                    : null;
                return editor != null;
            }

            static IEnumerable<string> Scripts()
            {
                try { return Directory.GetFiles(ScriptsDir, "*.lua").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { return Enumerable.Empty<string>(); }
            }

            internal static void Open(string path)
            {
                if (SamePath(path, file)) return;
                if (Dirty) { message = "Save or revert " + Path.GetFileName(file) + " first."; return; }
                try
                {
                    text = saved = File.ReadAllText(path).Replace("\r\n", "\n");
                    file = path;
                    fileTime = File.GetLastWriteTimeUtc(path);
                    message = syntax = "";
                    textScroll = Vector2.zero;
                    cursorLine = 1;
                }
                catch (Exception e) { message = "Couldn't open it: " + e.Message; }
            }

            internal static void OpenByName(string name)
            {
                var path = Path.Combine(ScriptsDir, name + ".lua");
                if (File.Exists(path)) Open(path);
                else Create(name);
            }

            static void Create(string name)
            {
                if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (name == "" || !validName.IsMatch(name)) { message = "Use letters, numbers, spaces, - _ or . in the name."; return; }
                var path = Path.Combine(ScriptsDir, name + ".lua");
                if (File.Exists(path)) { Open(path); return; }
                if (Dirty) { message = "Save or revert " + Path.GetFileName(file) + " first."; return; }
                try
                {
                    File.WriteAllText(path, NewScript(name));
                    newName = "";
                    Open(path);
                    message = "Created " + name + ".lua.";
                }
                catch (Exception e) { message = "Couldn't create it: " + e.Message; }
            }

            static string NewScript(string name) =>
                "-- " + name + ".lua\n" +
                "-- On a Lua Computer: inputs and outputs are what's linked in and out; on_input(i, value) runs when one changes.\n" +
                "-- On a screen ([lua: " + name + "]): set screen.text.\n" +
                "rate = 0.5\n\n" +
                "function on_input(i, value)\n" +
                "end\n\n" +
                "function tick(dt)\n" +
                "end\n";

            static void Save()
            {
                syntax = Check(text);
                try
                {
                    File.WriteAllText(file, text.Replace("\n", Environment.NewLine));
                    saved = text;
                    fileTime = File.GetLastWriteTimeUtc(file);
                    message = "Saved " + DateTime.Now.ToString("HH:mm:ss") + (syntax != "" ? ", with an error." : ". Running copies reload in a second.");
                    nextScan = 0f;
                }
                catch (Exception e) { message = "Couldn't save: " + e.Message; }
            }

            // Compiles without running, for syntax errors.
            static string Check(string source)
            {
                try
                {
                    new Script(CoreModules.None).LoadString(source, null, Path.GetFileNameWithoutExtension(file));
                    return "";
                }
                catch (SyntaxErrorException e) { return e.DecoratedMessage ?? e.Message; }
                catch (Exception e) { return e.Message; }
            }

            // Another editor saved the file: take its version unless there are edits here, which win on Save.
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

            static int CountLines(string s, int upTo)
            {
                int n = 0;
                for (int i = 0; i < upTo && i < s.Length; i++) if (s[i] == '\n') n++;
                return n;
            }

            static GUIStyle rich;
            static GUIStyle Rich() => rich ?? (rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
        }
    }
}
