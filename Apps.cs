using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Serialization.Json;

namespace EvoCore
{
    // Apps (the Lua chips notes, "Apps"): programs installed on a Lua Computer next to its own program, which the ship
    // OS (lib\shipos.lua) runs as tabs. Graphs first: an app is normally a .graph in UserData\EvoCore\apps, whose
    // Display nodes draw in the tab and whose button nodes take the tab's touches ("# title: NAV" / "# short: NAV"
    // lines name the tab), with Lua only as lua nodes inside it. A .lua app (returning { title, short, tick(dt),
    // draw(d, x, y, w, h, scr), touch(button, x, y, scr) }) still works for what graphs can't do yet. The id is the
    // file name; a .graph wins over a .lua of the same name. Installing copies the source into the computer block, so
    // it travels with the ship and its blueprints. Each app runs in its own environment (its globals stay its own; it
    // still sees ship, ui, shipos and the rest) and gets `data`, a table saved with the computer (as JSON: number keys
    // come back as strings), which a graph app's lua nodes can use.
    // Lua, on computers only (nil on screens):
    //   apps.slots, apps.used, apps.fresh   how many it holds, how many are in, and whether it never had any
    //   apps.list()                         installed: { id, size } each, in install order
    //   apps.available()                    the apps folder: { id, about, size, installed } each
    //   apps.install(id)                    true, or false and why; installing again updates the source, keeps data
    //   apps.remove(id)                     true if it was installed (its data goes with it)
    //   apps.load(id)                       the app's chunk compiled into its own environment, not run; nil and the
    //                                       error when it doesn't compile
    public class InstalledApp
    {
        public string Id, Kind = "lua", Source, Data;   // Kind: "graph" or "lua"
    }

    public static class Apps
    {
        public static int ComputerSlots = 2, MainframeSlots = 4, SlotsPerRack = 1, MaxSource = 32 * 1024;
        public static string Dir => Path.Combine(Path.Combine(Paths.UserData, "EvoCore"), "apps");

        static readonly Regex validId = new Regex(@"^[\w\-]+$");

        // Memory chips on the computer's board (Parts.cs) add one slot each.
        public static int Slots(LuaComputer c) => (c is Mainframe m ? MainframeSlots + SlotsPerRack * m.Racks : ComputerSlots) + Parts.AppSlots(c);

        // The running apps' data tables, per computer: written back into InstalledApp.Data when the computer saves.
        static readonly Dictionary<LuaComputer, Dictionary<string, Table>> live = new Dictionary<LuaComputer, Dictionary<string, Table>>();

        internal static void Expose() => Lua.Expose("apps", c => c.Host is LuaComputer computer ? new AppsApi(c, computer) : null);

        // Saved after the computer's program: "apps1\n", then per app "<id> <kind> <source length> <data length>\n<source><data>".
        internal static string Write(List<InstalledApp> apps)
        {
            if (apps == null) return "";
            var sb = new StringBuilder("apps1\n");
            foreach (var a in apps)
                sb.Append(a.Id).Append(' ').Append(a.Kind).Append(' ').Append(a.Source.Length).Append(' ').Append((a.Data ?? "").Length).Append('\n')
                  .Append(a.Source).Append(a.Data ?? "");
            return sb.ToString();
        }

        internal static List<InstalledApp> Read(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith("apps1\n")) return null;
            var apps = new List<InstalledApp>();
            var at = "apps1\n".Length;
            while (at < text.Length)
            {
                var end = text.IndexOf('\n', at);
                if (end < 0) break;
                var head = text.Substring(at, end - at).Split(' ');
                if (head.Length < 4) break;
                int sourceLength = int.Parse(head[2]), dataLength = int.Parse(head[3]);
                at = end + 1;
                var source = text.Substring(at, sourceLength);
                var data = text.Substring(at + sourceLength, dataLength);
                at += sourceLength + dataLength;
                apps.Add(new InstalledApp { Id = head[0], Kind = head[1] == "graph" ? "graph" : "lua", Source = source, Data = data.Length == 0 ? null : data });
            }
            return apps;
        }

        // Copies the running apps' data into the computer's saved apps.
        internal static void Flush(LuaComputer c)
        {
            if (c.InstalledApps == null || !live.TryGetValue(c, out var tables)) return;
            foreach (var app in c.InstalledApps)
            {
                if (!tables.TryGetValue(app.Id, out var table)) continue;
                try { app.Data = table.Length == 0 && !table.Pairs.Any() ? null : JsonTableConverter.TableToJson(table); }
                catch (Exception e) { Core.Log.Warning($"App {app.Id}: couldn't save its data: {e.Message}"); }
            }
        }

        // Drops the running apps' data tables without saving them (the computer's apps are being replaced).
        internal static void DropLive(LuaComputer c) => live.Remove(c);

        internal static void Forget(LuaComputer c)
        {
            Flush(c);
            live.Remove(c);
        }

        // The apps folder, one file per id: a .graph wins over a .lua of the same name (graphs first).
        internal static IEnumerable<string> Files()
        {
            if (!Directory.Exists(Dir)) return Enumerable.Empty<string>();
            return Directory.GetFiles(Dir, "*.circuit").Concat(Directory.GetFiles(Dir, "*.lua"))
                .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase);
        }

        static string KindOf(string path) => path.EndsWith(".circuit", StringComparison.OrdinalIgnoreCase) ? "graph" : "lua";

        // An app file's first comment line (-- for Lua, # for a graph, minus a graph's "name.graph:"), for the list.
        static string About(string source)
        {
            foreach (var line in source.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("--")) return t.Substring(2).Trim();
                if (t.StartsWith("#"))
                {
                    t = t.Substring(1).Trim();
                    foreach (var ending in new[] { ".circuit:", ".graph:" })   // .graph: what they were called up to 0.14
                    {
                        var colon = t.IndexOf(ending, StringComparison.OrdinalIgnoreCase);
                        if (colon >= 0) return t.Substring(colon + ending.Length).Trim();
                    }
                    return t;
                }
                return "";
            }
            return "";
        }

        // A graph app's "# title: NAME" / "# short: NAM" comment lines.
        static string Header(string source, string key)
        {
            var m = Regex.Match(source, @"^\s*#\s*" + key + @"\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        static string LuaString(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

        // A graph app as one Lua chunk for its own environment: its own copy of lib\nodeui.lua (so its Display nodes
        // queue and touch on their own), the compiled graph, then shipos.graph_app, which turns its tick and drawing
        // into a tab (lib\shipos.lua).
        internal static string GraphChunk(InstalledApp app)
        {
            var nodeui = Path.Combine(Lua.LibDir, "nodeui.lua");
            var title = Header(app.Source, "title") ?? app.Id.ToUpperInvariant();
            var shortTitle = Header(app.Source, "short");
            // Its port nodes use the computer's links as they are; it doesn't declare (and so replace) the computer's ports.
            var compiled = Regex.Replace(Graphs.Compile(app.Source, app.Id), @"^if ports then ports\.declare.*$", "", RegexOptions.Multiline);
            return (File.Exists(nodeui) ? File.ReadAllText(nodeui) : "") + "\n" + compiled + "\n" +
                $"return shipos.graph_app({LuaString(title)}, {(shortTitle != null ? LuaString(shortTitle) : "nil")}, tick, on_touch, " +
                "function(d, ds) display, displays = d, ds end)\n";
        }

        public class AppsApi
        {
            readonly Lua.Context context;
            readonly LuaComputer computer;

            public AppsApi(Lua.Context c, LuaComputer host)
            {
                context = c;
                computer = host;
                c.Stopped += () => Forget(computer);
            }

            public int slots => Slots(computer);
            public int used => computer.InstalledApps?.Count ?? 0;
            public bool fresh => computer.InstalledApps == null;

            public Table list(ScriptExecutionContext ctx)
            {
                var t = new Table(ctx.GetScript());
                foreach (var a in computer.InstalledApps ?? new List<InstalledApp>())
                {
                    var row = new Table(ctx.GetScript());
                    row["id"] = a.Id;
                    row["kind"] = a.Kind;
                    row["size"] = a.Source.Length;
                    row["about"] = About(a.Source);
                    t.Append(DynValue.NewTable(row));
                }
                return t;
            }

            public Table available(ScriptExecutionContext ctx)
            {
                var t = new Table(ctx.GetScript());
                foreach (var path in Files())
                {
                    var id = Path.GetFileNameWithoutExtension(path);
                    if (!validId.IsMatch(id)) continue;
                    string source;
                    try { source = File.ReadAllText(path); } catch { continue; }
                    var row = new Table(ctx.GetScript());
                    row["id"] = id;
                    row["kind"] = KindOf(path);
                    row["size"] = source.Length;
                    row["about"] = About(source);
                    row["installed"] = computer.InstalledApps?.Any(a => a.Id == id) ?? false;
                    t.Append(DynValue.NewTable(row));
                }
                return t;
            }

            public DynValue install(string id)
            {
                id = (id ?? "").Trim();
                if (!validId.IsMatch(id)) return Refuse("not an app name: " + id);
                var path = Files().FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), id, StringComparison.OrdinalIgnoreCase));
                if (path == null) return Refuse("no app called " + id + " in " + Dir);
                id = Path.GetFileNameWithoutExtension(path);
                var source = File.ReadAllText(path);
                if (source.Length > MaxSource) return Refuse($"{id} is {source.Length / 1024} KB; an app may be {MaxSource / 1024} KB");
                var apps = computer.InstalledApps ?? new List<InstalledApp>();
                var old = apps.FirstOrDefault(a => a.Id == id);
                if (old == null && apps.Count >= Slots(computer)) return Refuse($"no free slot ({apps.Count} of {Slots(computer)} used)");
                Flush(computer);
                if (old != null) { old.Source = source; old.Kind = KindOf(path); }
                else apps.Add(new InstalledApp { Id = id, Kind = KindOf(path), Source = source });
                computer.SetApps(apps);
                return DynValue.True;
            }

            public bool remove(string id)
            {
                var apps = computer.InstalledApps;
                var old = apps?.FirstOrDefault(a => a.Id == id);
                if (old == null) return false;
                apps.Remove(old);
                if (live.TryGetValue(computer, out var tables)) tables.Remove(id);
                computer.SetApps(apps);
                return true;
            }

            public DynValue load(ScriptExecutionContext ctx, string id)
            {
                var app = computer.InstalledApps?.FirstOrDefault(a => a.Id == id);
                if (app == null) return DynValue.NewTuple(DynValue.Nil, DynValue.NewString("not installed: " + id));
                var script = ctx.GetScript();
                if (!live.TryGetValue(computer, out var tables)) live[computer] = tables = new Dictionary<string, Table>();
                // Kept across loads, so loading an app again (after an update) keeps what it had.
                if (!tables.TryGetValue(id, out var data))
                {
                    data = null;
                    if (app.Data != null)
                    {
                        try { data = JsonTableConverter.JsonToTable(app.Data, script); }
                        catch (Exception e) { Core.Log.Warning($"App {id}: its saved data didn't read back ({e.Message}); starting empty"); }
                    }
                    tables[id] = data = data ?? new Table(script);
                }
                var env = new Table(script);
                var meta = new Table(script);
                meta["__index"] = script.Globals;
                env.MetaTable = meta;
                env["data"] = data;
                string code;
                if (app.Kind == "graph")
                {
                    try { code = GraphChunk(app); }
                    catch (Graphs.GraphException e) { return DynValue.NewTuple(DynValue.Nil, DynValue.NewString("circuit error: " + e.Message)); }
                }
                else code = app.Source;
                try { return script.LoadString(code, env, "app/" + id); }
                catch (InterpreterException e) { return DynValue.NewTuple(DynValue.Nil, DynValue.NewString(e.DecoratedMessage ?? e.Message)); }
            }

            static DynValue Refuse(string why) => DynValue.NewTuple(DynValue.False, DynValue.NewString(why));
        }
    }
}
