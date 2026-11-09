using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Turns a ship's logic into a graph for one Lua Computer, without touching the ship (step 1 of 2; the refit that
    // rewires the ship comes later and reads the .refit file written here).
    //  - Converted exactly: logic gates (CommandLogic: OR, AND, XOR, NOR, NAND, XNOR, any number of inputs) become
    //    `gate` nodes and timer gates (CommandDelay: none, repeat, toggle) become `delay` nodes.
    //  - Every other block linked to them is the edge of the converted logic and keeps its own game behaviour: a block
    //    feeding the logic becomes a `port in`, a block the logic drives a `port out` (a block that's both is both).
    //    That includes event gates and sequencers, which stay as they are in this version.
    //  - A block or timer fed by several links takes whichever signal reached it last, so its inputs go through a
    //    `gate op=LATEST`.
    // Written to scripts\<ship> logic.graph, with <ship> logic.refit (which block goes in which port) and a report.
    public static class LogicConverter
    {
        static bool Convertible(ICommand c) => c is CommandLogic || c is CommandDelay;

        static string GateOp(CommandLogic g)
        {
            try { return ((CommandLogic.Type)g.Data).ToString(); }
            catch { return "OR"; }
        }

        static float DelaySeconds(CommandDelay d) => Mathf.Max(0.1f, d.delay);

        static string DelayMode(CommandDelay d)
        {
            switch (d.CurrentRepeatMode)
            {
                case CommandDelay.RepeatMode.RepeatHigh: return "repeat";
                case CommandDelay.RepeatMode.RepeatToggle: return "toggle";
                default: return "none";
            }
        }

        static readonly Regex unsafeChars = new Regex(@"[^\w\-]+");

        // A port name from a block: its Tag label, else its kind, made unique ("door", "door_2").
        static string PortName(ICommand c, HashSet<string> used)
        {
            string label = null;
            try { label = c.brickCoord.TagData.label; } catch { }
            var kind = c.OverTextIndirect;
            if (string.IsNullOrWhiteSpace(kind) || kind == "Command")
                try { kind = c.brickCoord.brickData.Brick.name; } catch { kind = c.GetType().Name; }
            var raw = unsafeChars.Replace((string.IsNullOrWhiteSpace(label) ? kind : label).Trim().ToLowerInvariant(), "_").Trim('_');
            if (raw.Length == 0 || !char.IsLetter(raw[0])) raw = "b_" + raw;
            if (raw.Length > 28) raw = raw.Substring(0, 28);
            var name = raw;
            for (int n = 2; used.Contains(name); n++) name = $"{raw}_{n}";
            used.Add(name);
            return name;
        }

        public class Result
        {
            public string Graph, Refit, Report, GraphPath;
            public int Gates, Delays, Inputs, Outputs;
        }

        // A ship's bricks (and so its gates) exist before its links do: the game applies the saved links a frame or more
        // later and only then marks the ship initialized. Converting before that sees a half-linked network.
        public static string NotReady(ParentEntity ship) =>
            ship.IsInitialized ? null : $"{ship.EntityName} is still loading (its links aren't all in place yet); try again in a moment.";

        public static Result Convert(ParentEntity ship)
        {
            var all = CommandManager.commands.Datas.OfType<ICommand>()
                .Where(c => c is Command && c.brickCoord.entity != null && c.brickCoord.entity.Parent == ship).ToList();
            var logic = all.Where(Convertible).ToList();
            var r = new Result();
            var report = new StringBuilder();
            report.AppendLine($"Logic of {ship.EntityName}: {logic.Count(c => c is CommandLogic)} gate(s), {logic.Count(c => c is CommandDelay)} timer gate(s).");

            List<ICommand> Preds(ICommand c) { var l = new List<ICommand>(); foreach (var i in c.predecessors.Items) if (i.Command != null && i.Command.brickCoord.entity?.Parent == ship) l.Add(i.Command); l.Reverse(); return l; }
            List<ICommand> Targets(ICommand c)
            {
                var l = new List<ICommand>();
                if (c is ICommandLinkSource s) foreach (var i in s.targets.Items) if (i.Command != null && i.Command.brickCoord.entity?.Parent == ship) l.Add(i.Command);
                l.Reverse();
                return l;
            }

            // The edges: blocks feeding the logic (ports in) and blocks it drives (ports out).
            var logicSet = new HashSet<ICommand>(logic);
            var feeders = logic.SelectMany(Preds).Where(p => !logicSet.Contains(p)).Distinct().ToList();
            var driven = logic.SelectMany(Targets).Where(t => !logicSet.Contains(t)).Distinct().ToList();

            var g = new Graphs.Graph();
            var id = 1;
            var nodeOf = new Dictionary<ICommand, int>();
            var usedIn = new HashSet<string>();
            var usedOut = new HashSet<string>();
            var refit = new StringBuilder();
            refit.AppendLine($"# which block goes in which port when {ship.EntityName}'s logic is refitted onto a computer (LogicConverter)");
            Graphs.Node Add(string type, float x, float y)
            {
                var n = new Graphs.Node { Id = id++, Type = Graphs.Types[type], X = x, Y = y, Placed = true };
                g.Nodes[n.Id] = n;
                return n;
            }

            // Depth: how many logic steps from the edge, for the layout (columns left to right).
            var depth = new Dictionary<ICommand, int>();
            int Depth(ICommand c, int guard = 0)
            {
                if (depth.TryGetValue(c, out var d)) return d;
                if (!logicSet.Contains(c) || guard > 200) return 0;
                depth[c] = 1;
                var deepest = Preds(c).Select(p => Depth(p, guard + 1)).DefaultIfEmpty(0).Max();
                return depth[c] = deepest + 1;
            }
            foreach (var c in logic) Depth(c);
            var maxDepth = logic.Select(c => depth[c]).DefaultIfEmpty(0).Max();
            // Each column stacks its nodes by their real height (a title, a row per port pair, a row per setting).
            var nextY = new Dictionary<int, float>();
            (float x, float y) Spot(int column, int rows)
            {
                nextY.TryGetValue(column, out var y);
                nextY[column] = y + 42 + 22 * rows + 20;
                return (column * 240f, y);
            }

            foreach (var f in feeders)
            {
                var name = PortName(f, usedIn);
                var (x, y) = Spot(0, 2);
                var n = Add("port in", x, y);
                n.Settings["port"] = name;
                nodeOf[f] = n.Id;
                refit.AppendLine($"in\t{name}\t{Lua.KeyOf(f)}");
                r.Inputs++;
            }
            foreach (var c in logic.OrderBy(c => depth[c]))
            {
                var (x, y) = Spot(depth[c], c is CommandLogic ? Math.Max(1, Math.Min(Graphs.MaxLuaPorts, Preds(c).Count)) + 2 : 3);
                Graphs.Node n;
                if (c is CommandLogic gate)
                {
                    n = Add("gate", x, y);
                    n.Settings["op"] = GateOp(gate);
                    n.Settings["inputs"] = Math.Max(1, Math.Min(Graphs.MaxLuaPorts, Preds(c).Count)).ToString();
                    r.Gates++;
                }
                else
                {
                    var d = (CommandDelay)c;
                    n = Add("delay", x, y);
                    n.Settings["mode"] = DelayMode(d);
                    n.Settings["seconds"] = DelaySeconds(d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    r.Delays++;
                }
                nodeOf[c] = n.Id;
                refit.AppendLine($"logic\t{n.Id}\t{Lua.KeyOf(c)}");
            }

            // A node's inputs from its predecessors; one input fed by several goes through a LATEST gate.
            void Feed(Graphs.Node to, string port, List<ICommand> from, float x, float y)
            {
                var sources = from.Where(nodeOf.ContainsKey).ToList();
                if (sources.Count == 0) return;
                if (sources.Count == 1) { g.Wires.Add((nodeOf[sources[0]], "out", to.Id, port, 0)); return; }
                var latest = Add("gate", x, y);
                latest.Settings["op"] = "LATEST";
                latest.Settings["inputs"] = Math.Min(Graphs.MaxLuaPorts, sources.Count).ToString();
                for (int k = 0; k < sources.Count && k < Graphs.MaxLuaPorts; k++) g.Wires.Add((nodeOf[sources[k]], "out", latest.Id, "in" + (k + 1), 0));
                g.Wires.Add((latest.Id, "out", to.Id, port, 0));
            }
            foreach (var c in logic)
            {
                var n = g.Nodes[nodeOf[c]];
                var preds = Preds(c);
                if (c is CommandLogic)
                    for (int k = 0; k < preds.Count && k < Graphs.MaxLuaPorts; k++)
                    {
                        if (nodeOf.TryGetValue(preds[k], out var from)) g.Wires.Add((from, "out", n.Id, "in" + (k + 1), 0));
                    }
                else Feed(n, "in", preds, n.X - 120, n.Y + 50);
            }
            var alsoDirect = new List<string>();
            foreach (var t in driven)
            {
                var name = PortName(t, usedOut);
                var (x, y) = Spot(maxDepth + 1, Preds(t).Count(logicSet.Contains) > 1 ? 6 : 2);
                var n = Add("port out", x, y);
                n.Settings["port"] = name;
                Feed(n, "in", Preds(t).Where(logicSet.Contains).ToList(), x - 120, y + 50);
                refit.AppendLine($"out\t{name}\t{Lua.KeyOf(t)}");
                r.Outputs++;
                var direct = Preds(t).Where(p => !logicSet.Contains(p)).ToList();
                if (direct.Count > 0) alsoDirect.Add($"{Lua.Describe(t)} is also linked straight from {string.Join(", ", direct.Select(Lua.Describe).ToArray())}");
            }

            // Blocks also fed straight by blocks outside the logic: those links stay; the same line repeated is counted once.
            foreach (var line in alsoDirect.GroupBy(x => x))
                report.AppendLine($"- {line.Key}{(line.Count() > 1 ? $" (x{line.Count()})" : "")}; those links stay as they are.");
            var kept = all.Where(c => c is CommandEventGate || c is CommandSequencerController).ToList();
            var keptLinked = kept.Where(c => feeders.Contains(c) || driven.Contains(c)).ToList();
            report.AppendLine($"Converted: {r.Gates} gate(s) and {r.Delays} timer gate(s) into nodes; {r.Inputs} block(s) feed it (ports in), {r.Outputs} block(s) it drives (ports out).");
            if (kept.Count > 0)
                report.AppendLine($"Kept as game blocks: {kept.Count(c => c is CommandEventGate)} event gate(s), {kept.Count(c => c is CommandSequencerController)} sequencer(s)" +
                                  (keptLinked.Count > 0 ? $"; {keptLinked.Count} of them talk to the converted logic through ports." : "."));
            if (logic.Any(c => c is CommandDelay)) report.AppendLine("Timing: the graph runs every 0.1 s (its rate), so timers and chains settle to the nearest tick; the game's logic reacts within a frame.");
            if (logic.Count == 0) report.AppendLine("Nothing to convert: no logic gates or timer gates on this ship.");

            r.Graph = Graphs.Write(g, ship.EntityName + " logic");
            r.Refit = refit.ToString();
            r.Report = report.ToString();
            return r;
        }

        // Converts and writes the files; returns the report (and the graph's path).
        public static string ConvertAndSave(ParentEntity ship, out string graphPath)
        {
            graphPath = null;
            if (ship == null) return "no ship";
            var wait = NotReady(ship);
            if (wait != null) return wait;
            var r = Convert(ship);
            if (r.Gates + r.Delays == 0) return r.Report;
            var name = new string((ship.EntityName ?? "ship").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim() + " logic";
            Directory.CreateDirectory(Lua.ScriptsDir);
            graphPath = Path.Combine(Lua.ScriptsDir, name + ".graph");
            File.WriteAllText(graphPath, r.Graph);
            File.WriteAllText(Path.Combine(Lua.ScriptsDir, name + ".refit"), r.Refit);
            File.WriteAllText(Path.Combine(Lua.ScriptsDir, name + " report.txt"), r.Report);
            return r.Report + $"Written: scripts\\{name}.graph (and .refit, report.txt). Nothing on the ship was changed.";
        }

        // ---- For DevBridge ----

        // Ships near the player with logic, and how much.
        public static string Survey() =>
            string.Join("\n", CommandManager.commands.Datas.OfType<ICommand>()
                .Where(c => c is Command && c.brickCoord.entity?.Parent != null && (Convertible(c) || c is CommandEventGate || c is CommandSequencerController))
                .GroupBy(c => c.brickCoord.entity.Parent)
                .Select(grp => $"{grp.Key.EntityName}: {grp.Count(c => c is CommandLogic)} gates, {grp.Count(c => c is CommandDelay)} timers, " +
                               $"{grp.Count(c => c is CommandEventGate)} event gates, {grp.Count(c => c is CommandSequencerController)} sequencers")
                .ToArray());

        // Where a ship's gates and timers link to and from, by kind of block, to see what the converter counts as an edge.
        public static string Edges(string shipName)
        {
            var ship = CommandManager.commands.Datas.OfType<ICommand>().Select(c => c.brickCoord.entity?.Parent)
                .FirstOrDefault(p => p != null && p.EntityName == shipName);
            if (ship == null) return "no ship named " + shipName;
            var logic = CommandManager.commands.Datas.OfType<ICommand>()
                .Where(c => c is Command && c.brickCoord.entity?.Parent == ship && Convertible(c)).ToList();
            var logicSet = new HashSet<ICommand>(logic);
            string Kind(ICommand c)
            {
                if (c == null) return "(no block)";
                var where = c.brickCoord.entity?.Parent == ship ? "" : $" on {c.brickCoord.entity?.Parent?.EntityName ?? "nothing"}";
                return (logicSet.Contains(c) ? "logic" : c.GetType().Name) + where;
            }
            var outs = new Dictionary<string, int>();
            var ins = new Dictionary<string, int>();
            void Count(Dictionary<string, int> d, string k) { d.TryGetValue(k, out var n); d[k] = n + 1; }
            foreach (var c in logic)
            {
                if (c is ICommandLinkSource s) foreach (var i in s.targets.Items) Count(outs, Kind(i.Command));
                foreach (var i in c.predecessors.Items) Count(ins, Kind(i.Command));
            }
            string List(Dictionary<string, int> d) => string.Join("\n", d.OrderByDescending(x => x.Value).Select(x => $"  {x.Value} x {x.Key}").ToArray());
            var sources = logic.Count(c => c is ICommandLinkSource);
            return $"{ship.EntityName}: {logic.Count} gates and timers ({sources} can link out)\nlinks out to:\n{List(outs)}\nlinks in from:\n{List(ins)}";
        }

        public static string ConvertShip(string shipName)
        {
            var ship = CommandManager.commands.Datas.OfType<ICommand>().Select(c => c.brickCoord.entity?.Parent)
                .FirstOrDefault(p => p != null && p.EntityName == shipName);
            return ship == null ? "no ship named " + shipName : ConvertAndSave(ship, out _);
        }
    }
}
