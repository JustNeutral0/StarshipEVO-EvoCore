using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EvoCore
{
    // The Help tab of the Computer screen: every graph node (what each input means, what each output gives, each
    // setting), every Lua global a script sees, and every function in the lib\*.lua files.
    //  - Nodes describe themselves: NodeType.Help is the one-line summary, NodeType.Docs a line per input, output and
    //    setting. A node file sets them with help = "..." and docs = { x = "...", out = "..." }; a C# mod fills
    //    Docs on the NodeType Graphs.Register returns.
    //  - Lua globals are described by whoever adds them: Help.Global(name, summary, "member", "what it does", ...).
    //  - Lib functions are read from the files: the -- comment lines straight above each `function name(...)`.
    public static class Help
    {
        public class Topic
        {
            public string Name, Summary, From;
            public readonly List<(string Member, string Doc)> Members = new List<(string, string)>();
        }

        static readonly Dictionary<string, Topic> globals = new Dictionary<string, Topic>(StringComparer.OrdinalIgnoreCase);

        // Describes a Lua global (or adds to its description): pairs of member and what it does.
        public static void Global(string name, string summary, params string[] memberThenDoc)
        {
            if (!globals.TryGetValue(name, out var t)) globals[name] = t = new Topic { Name = name, Summary = summary ?? "" };
            else if (!string.IsNullOrEmpty(summary)) t.Summary = summary;
            for (int i = 0; i + 1 < memberThenDoc.Length; i += 2) t.Members.Add((memberThenDoc[i], memberThenDoc[i + 1]));
        }

        // ---- Lib files: functions and the comments above them ----

        static List<Topic> libs = new List<Topic>();
        static float nextLibScan;
        static readonly Regex function = new Regex(@"^\s*(?:local\s+)?function\s+([\w\.:]+)\s*\(([^)]*)\)");

        static void ScanLibs()
        {
            if (Time.unscaledTime < nextLibScan) return;
            nextLibScan = Time.unscaledTime + 2f;
            var found = new List<Topic>();
            try
            {
                foreach (var path in Directory.Exists(Lua.LibDir) ? Directory.GetFiles(Lua.LibDir, "*.lua").OrderBy(p => p, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>())
                {
                    var t = new Topic { Name = Path.GetFileNameWithoutExtension(path), From = "lib\\" + Path.GetFileName(path) };
                    var comment = new List<string>();
                    var header = true;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("--"))
                        {
                            comment.Add(line.TrimStart('-').Trim());
                            continue;
                        }
                        // The file's first comment block is its summary.
                        if (header && comment.Count > 0) { t.Summary = string.Join(" ", comment.ToArray()); comment.Clear(); }
                        header = false;
                        var m = function.Match(line);
                        // Only functions others can call (not local helpers).
                        if (m.Success && !line.StartsWith("local")) t.Members.Add(($"{m.Groups[1].Value}({m.Groups[2].Value.Trim()})", string.Join(" ", comment.ToArray())));
                        // A comment belongs to the next function, across helper lines like `local cache = {}`, until a blank line.
                        if (line == "" || m.Success) comment.Clear();
                    }
                    if (t.Summary == null) t.Summary = "";
                    found.Add(t);
                }
            }
            catch (Exception e) { Core.Log.Warning("Help: couldn't read the lib files: " + e.Message); }
            libs = found;
        }

        // ---- The tab ----

        static string search = "", picked = "node:compare";
        static Vector2 listScroll, pageScroll;
        static GUIStyle head, sub, item, body, port, muted, title;

        static void Styles()
        {
            if (head != null) return;
            head = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            head.normal.textColor = new Color(0.95f, 0.75f, 0.35f);
            sub = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            sub.normal.textColor = new Color(0.55f, 0.58f, 0.62f);
            item = new GUIStyle(GUI.skin.label) { fontSize = 13, padding = new RectOffset(10, 2, 1, 1) };
            body = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, richText = true };
            port = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
            port.normal.textColor = new Color(0.55f, 0.85f, 1f);
            try
            {
                var mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 14);
                if (mono != null) port.font = mono;
            }
            catch { }
            muted = new GUIStyle(body);
            muted.normal.textColor = new Color(0.6f, 0.63f, 0.67f);
            title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, richText = true };
        }

        static IEnumerable<Graphs.NodeType> Nodes() =>
            Graphs.Types.Values.Distinct().Where(t => t.Code != null).OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

        static bool Matches(params string[] texts) =>
            search == "" || texts.Any(s => s != null && s.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

        static bool Matches(Graphs.NodeType t) => Matches(new[] { t.Name, t.Help, t.Category }.Concat(t.Ins).Concat(t.Outs).Concat(t.Docs.Values).ToArray());
        static bool Matches(Topic t) => Matches(new[] { t.Name, t.Summary }.Concat(t.Members.Select(m => m.Member)).Concat(t.Members.Select(m => m.Doc)).ToArray());

        internal static void Draw(float height)
        {
            Styles();
            ScanLibs();
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(240));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Find", GUILayout.Width(36));
            search = GUILayout.TextField(search);
            GUILayout.EndHorizontal();
            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(height));
            GUILayout.Label("GRAPH NODES", head);
            foreach (var group in Nodes().Where(Matches).GroupBy(t => t.Category))
            {
                GUILayout.Label(group.Key == "" ? "Other" : group.Key, sub);
                foreach (var t in group) Pick("node:" + t.Name, t.Name);
            }
            GUILayout.Space(8);
            GUILayout.Label("LUA", head);
            foreach (var t in globals.Values.Where(Matches).OrderBy(t => t.Name == "script" ? "" : t.Name, StringComparer.OrdinalIgnoreCase))
                Pick("lua:" + t.Name, t.Name);
            GUILayout.Space(8);
            GUILayout.Label("LUA LIBRARIES", head);
            foreach (var t in libs.Where(Matches)) Pick("lib:" + t.Name, t.Name);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(16);
            GUILayout.BeginVertical();
            pageScroll = GUILayout.BeginScrollView(pageScroll, GUILayout.Height(height + 28));
            var colon = picked.IndexOf(':');
            var kind = picked.Substring(0, colon);
            var name = picked.Substring(colon + 1);
            if (kind == "node" && Graphs.Types.TryGetValue(name, out var node)) NodePage(node);
            else if (kind == "lua" && globals.TryGetValue(name, out var g)) TopicPage(g, "Lua global");
            else if (kind == "lib" && libs.FirstOrDefault(l => l.Name == name) is Topic lib) TopicPage(lib, "Lua library, " + lib.From);
            else GUILayout.Label("Pick something on the left.", muted);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        static void Pick(string key, string label)
        {
            if (GUILayout.Button((picked == key ? "> " : "") + label, item)) { picked = key; pageScroll = Vector2.zero; }
        }

        // Unity only reads its own tags (<b>, <color=...>), so text with < and > in it shows as written.
        static string Esc(string s) => s ?? "";
        static string Code(string s) => (s ?? "").Replace("`", "");

        static void NodePage(Graphs.NodeType t)
        {
            GUILayout.Label(Esc(t.Name) + "  <size=13><color=#8a9099>" + Esc(t.Category) + "</color></size>", title);
            var from = t.File != null ? "custom node, nodes\\" + Path.GetFileName(t.File) : t.Source == null && !Builtin(t) ? "added by a mod" : "built in";
            GUILayout.Label(from, muted);
            GUILayout.Space(4);
            GUILayout.Label(Esc(Code(t.Help)) + ".", body);
            GUILayout.Space(10);

            Section("Inputs", t.Ins, p =>
            {
                var unwired = t.InDefaults.TryGetValue(p, out var d) ? d : 0;
                return Doc(t, p) + $"  <color=#8a9099>(unwired: {unwired:0.###})</color>";
            });
            Section("Outputs", t.Outs, p => Doc(t, p));
            Section("Settings", t.Settings.Keys.ToArray(), p =>
            {
                var d = t.Settings[p];
                return Doc(t, p) + "  <color=#8a9099>(" + (d == "" ? "required" : "default " + Esc(d)) + ")</color>";
            });

            GUILayout.Space(14);
            GUILayout.Label("Values: a wire carries a number. On/off counts as on above 0.5, and on/off outputs give 1 or 0.", muted);
            if (t.File != null && t.Docs.Count == 0)
                GUILayout.Label("This node's file has no docs yet. Add a line per input, output and setting to describe them:\n" +
                                "  docs = { x = \"the value to watch\", out = \"1 while on\", low = \"turns off below this\" },", muted);
        }

        static readonly HashSet<string> builtinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static void NoteBuiltins() { foreach (var t in Graphs.Types.Values) builtinNames.Add(t.Name); }
        static bool Builtin(Graphs.NodeType t) => builtinNames.Contains(t.Name);

        static string Doc(Graphs.NodeType t, string p) =>
            t.Docs.TryGetValue(p, out var d) && !string.IsNullOrEmpty(d) ? Esc(Code(d)) : "<color=#8a9099>no description</color>";

        static void Section(string name, string[] ports, Func<string, string> line)
        {
            if (ports.Length == 0) return;
            GUILayout.Label(name.ToUpperInvariant(), head);
            foreach (var p in ports)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Esc(p), port, GUILayout.Width(110));
                GUILayout.Label(line(p), body);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);
        }

        static void TopicPage(Topic t, string what)
        {
            GUILayout.Label(Esc(t.Name), title);
            GUILayout.Label(what, muted);
            GUILayout.Space(4);
            if (!string.IsNullOrEmpty(t.Summary)) GUILayout.Label(Esc(Code(t.Summary)), body);
            GUILayout.Space(10);
            foreach (var (member, doc) in t.Members)
            {
                if (!Matches(member, doc) && !Matches(t.Name, t.Summary)) continue;
                GUILayout.Label(Esc(member), port);
                if (!string.IsNullOrEmpty(doc)) GUILayout.Label("    " + Esc(Code(doc)), body);
                GUILayout.Space(4);
            }
        }

        // ---- EvoCore's own globals ----

        internal static void Init()
        {
            Global("script", "How a script runs. Its body runs once when it starts; tick(dt) then runs every `rate` seconds. " +
                             "Each call may only run so many Lua steps; a script that goes over is stopped with an error. " +
                             "Errors and print() show in the Scripts tab. Files in lib load first.",
                "function tick(dt)", "runs every `rate` seconds; dt is the seconds since the last tick",
                "rate", "how often tick runs, in seconds (0.1 unless the script sets it)",
                "function on_input(i, value)", "runs when an input changes: i is the port's name once input ports are declared, else the link's number",
                "function on_message(port, msg)", "runs for each message another computer sends on a two-way channel (see ports)",
                "function on_key(key, down)", "runs when a key goes down or up (see keys)",
                "function on_button(stick, button, down)", "runs when a joystick button goes down or up (see joystick)",
                "print(...)", "shows a line in the Scripts tab",
                "time()", "seconds since the game started");
            Global("ship", "The script's own ship, read only. Levels are 0 to 1.",
                "name", "the ship's name",
                "speed", "metres per second",
                "velocity()", "speed along the ship's right, up and forward axes: local r, u, f = ship.velocity()",
                "mass", "the ship's mass",
                "fuel / fuel_stored / fuel_capacity", "fuel level, and the raw amounts",
                "power / power_stored / power_capacity", "stored charge level, and the raw amounts",
                "power_in / power_use", "power coming in, and being used",
                "stalled", "true when the ship has run out of power",
                "heat", "heat level",
                "hull / hull_points / hull_max", "hull integrity (0 = lost), and the raw points",
                "shield / shield_on", "shield level, and whether it's up",
                "warping / cruising", "in warp, or in light cruise",
                "planet / altitude", "the nearest planet, moon or star, and the height above its sea level in metres",
                "target", "the selected target (.name, .distance in metres, .speed), or nil",
                "block(label)", "the first logic block with that Tag label, or nil (see block)",
                "blocks([label])", "every block with that label, or every labelled block");
            Global("block", "A logic block found with ship.block(label), inputs.block(i) or outputs.block(i).",
                "name / kind / type", "its Tag label, the kind of block, its game type",
                "state", "its value, 0 to 1",
                "on", "read or set: is it on",
                "set(value)", "sets it (a number or true/false)",
                "toggle()", "flips it",
                "read(name)", "one of its readouts (a thruster's thrust, a tank's level...)",
                "values()", "every readout it has, as a table");
            Global("inputs", "A Lua Computer's inputs. i is a port name (see ports), or a number: the declared input ports in " +
                             "order once there are any, else the links in the order they were made.",
                "count", "how many",
                "get(i)", "the value: the block's state, or the readout its port reads",
                "on(i)", "is it on (above 0.5)",
                "name(i)", "the linked block's kind",
                "block(i)", "the linked block (see block)");
            Global("outputs", "A Lua Computer's outputs. i is a port name or a number, as for inputs.",
                "count / get(i) / on(i) / name(i) / block(i)", "as for inputs",
                "set(i, value)", "sends a value (a number or true/false) to that block",
                "all(value)", "sends it down every link");
            Global("keys", "The keyboard and mouse buttons. While a menu is open (the game's, this screen, the link picker) " +
                           "every key reads as up and no events fire, so typing doesn't fly the ship.",
                "down(key)", "is it held now",
                "pressed(key) / released(key)", "did it go down / come up since this script last asked (a tap between ticks still counts)",
                "key names", "letters, digits (top row; keypad5 for the number pad), space, enter, esc, tab, backspace, up, down, " +
                             "left, right, f1-f15, shift, ctrl, alt (either side), leftshift..., mouse0-mouse6, or a Unity KeyCode name");
            Global("player", "You, as this script's ship sees you (single-player: always this machine's camera). " +
                             "The ship's axes are its pilot seat's when it has one, else the ship's own.",
                "seated", "sitting in a seat of this ship",
                "aboard", "seated in it or standing on it",
                "third_person", "the camera is in third person (it orbits the ship, so look() gives the orbit, not your head)",
                "look()", "yaw, pitch: degrees from the ship's forward; yaw + right, pitch + up",
                "aim()", "the look direction along the ship's right, up and forward (three numbers, -1 to 1)");
            Global("joystick", "Joysticks, HOTAS and gamepads, as the game sees them: up to 4 devices, 28 axes and 20 buttons each.",
                "count", "how many are connected",
                "name(n)", "device n's name",
                "axis(n, a)", "axis a (1-28) of device n, -1 to 1",
                "button(n, b)", "is button b (1-20) of device n held",
                "moving(n)", "every axis away from rest and every held button, as text: handy for finding which number is which");
            Global("ports", "Named link slots, so a program never depends on link order. Declare them, then put linked blocks in them " +
                            "with the link tool (it asks which port) or the Links tab. Saved in the computer. Channels are two-way " +
                            "between two linked Lua Computers and carry whole messages.",
                "declare{ inputs = {...}, outputs = {...}, displays = {...}, channels = {...} }", "the ports this program has, by name (adds to any declared before)",
                "send(port, msg)", "a message (number, text, true/false, or a table of them) to the computer at the other end of a channel; false if it isn't connected",
                "last(port)", "the last message that came in on a channel, or nil",
                "linked(port)", "the name of what's in a port, or nil",
                "list()", "every declared port: { name, kind, linked }");
            Global("screen", "On a text screen's script only.",
                "text", "what the screen shows; setting it changes the picture, not the saved text");
            Global("pilot", "Flight input added to yours while you fly the ship from its seat (never under the autopilot).",
                "move(right, up, forward)", "held until changed; each -1 to 1",
                "turn(pitch, yaw, roll)", "the same for turning",
                "mode", "standard, boost, cruise, warp or dropoff; set boost or standard",
                "release()", "let go of everything",
                "flying / autopilot", "whether you're flying it, and whether the autopilot has it");
            Global("display", "The computer's first display: its first declared display port (see ports), else its first linked monitor or display surface. Coordinates are canvas units from the top left, " +
                              "240 tall. Colours: names (green, amber, red, blue, cyan, white, grey, dim, black), \"#rrggbb\", or 0xrrggbb. " +
                              "Drawing stays until clear(): clear first in tick to animate.",
                "present / surface", "is it there, and is it a see-through display surface",
                "width / height", "the canvas size",
                "crt / glow", "the CRT look on or off, and the glass brightness (about 1 plain, higher glows)",
                "clear([color])", "wipes it (black, or see-through on a surface)",
                "rect(x, y, w, h, color)", "a filled box",
                "box(x, y, w, h, color, thickness)", "an outline",
                "line(x1, y1, x2, y2, color, thickness)", "a line",
                "arc(x, y, r, from, to, color, thickness)", "part of a ring; degrees clockwise from straight up",
                "ring(x, y, r, color, thickness)", "a whole ring",
                "circle(x, y, r, color)", "a filled circle",
                "tri(x1, y1, x2, y2, x3, y3, color)", "a filled triangle",
                "bar(x, y, w, h, fill, color, back)", "a level bar, fill 0 to 1",
                "text(x, y, s, color, size, align, font)", "text with its top at y; align left, center or right; font code, display or text",
                "measure(s, size, font)", "how wide a text would be");
            Global("displays", "The computer's displays: by display port name, or by number (the declared display ports once there are any, else link order).",
                "count", "how many",
                "get(i)", "a display port's name, or a number (see display)");
            Global("anim", "Poses and clips for the ship's rotators, hinges, pistons and rails, saved in the computer. " +
                           "A joint's value is 0 to 1 across its range.",
                "joints()", "the list: id, name, kind, value (and angle for rotators)",
                "get(joint) / set(joint, v)", "a joint's value by its Tag label, its default name or its id",
                "capture(name [, joints])", "saves where the joints are now as a pose",
                "poses() / clips()", "the names",
                "forget(name)", "deletes a pose or clip",
                "clip(name, keys [, loop])", "keys: { {0, 'rest'}, {0.5, 'up', 'smooth'} } (time, pose, ease)",
                "play(name [, opts])", "opts: loop, speed, layer, fade (seconds), restart",
                "stop([name [, fade]])", "fades a clip out (all with no name, and the gait)",
                "pose(name [, seconds [, ease]])", "moves to a pose from wherever the joints are",
                "playing(name) / speed(name, s) / time(name [, t])", "is it playing, its speed, its time",
                "gait{ walk=, stride=, idle=, run=, run_stride=, run_at=, turn=, turn_step=, fall=, land=, land_speed=, land_time=, reach=, weight=, layer= }",
                "walks the clips from the ship's own movement, so the feet keep pace with the ground",
                "gait() / gait(false)", "the gait's state (state, speed, forward, side, turn, grounded, clearance, phase, impact, land), or stop it",
                "eases", "smooth (default), linear, in, out, step");
            NoteBuiltins();
        }
    }
}
