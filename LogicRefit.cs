using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VoxelEngine;

namespace EvoCore
{
    // Step 2 of the logic converter: refits a ship's logic onto one of its Lua Computers, and back.
    //  - Converts the ship's logic again (so it matches the links as they are now), saves an undo record, then
    //    self-tests the graph against the old logic before switching over (see "Refit with a self-test" below): if
    //    every output matches, the old gates and timers are deleted; otherwise they stay, unlinked. Whatever the game
    //    refuses (a block that both feeds and is driven by the logic: the game allows no loops) is reported.
    //  - Undo puts every original link back, removes the computer's new links and restores its program and ports.
    // Links go through the game's own CommandCommon.LinkToCommand, which toggles a link (and marks the ship for
    // saving), so every call checks first whether the link is there.
    // The undo record: scripts\<ship> logic.undo (tab separated: computer, program kind and text and ports as base64,
    // then "link from to" for each original link and "added from to" for each new one; blocks by Lua.KeyOf).
    public static class LogicRefit
    {
        static string UndoPath(ParentEntity ship)
        {
            var name = new string((ship.EntityName ?? "ship").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
            return Path.Combine(Lua.ScriptsDir, name + " logic.undo");
        }

        public static bool CanUndo(ParentEntity ship) => ship != null && File.Exists(UndoPath(ship));

        static Dictionary<string, ICommand> Blocks(ParentEntity ship) =>
            CommandManager.commands.Datas.OfType<ICommand>().Where(c => c is Command && c.brickCoord.entity?.Parent == ship)
                .GroupBy(Lua.KeyOf).ToDictionary(g => g.Key, g => g.First());

        static bool Linked(ICommand from, ICommand to) => from is ICommandLinkSource s && s.targets.Contains(to);

        static bool Link(ICommand from, ICommand to)
        {
            if (Linked(from, to)) return true;
            if (!(from is ICommandLinkSource s) || !CommandCommon.CanLinkToCommand(s, to)) return false;
            CommandCommon.LinkToCommand(s, to, onLoad: false, onInit: false);
            return Linked(from, to);
        }

        static void Unlink(ICommand from, ICommand to)
        {
            if (Linked(from, to)) CommandCommon.LinkToCommand((ICommandLinkSource)from, to, onLoad: false, onInit: false);
        }

        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        static string FromB64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

        // ---- Refit with a self-test ----
        // Prepare: cut the old logic's links to the blocks it drove (nothing real moves from here on), link the input
        // blocks into the computer as well (the old gates keep theirs, so both see the same inputs), put the graph on
        // the computer. Test (Update, a few seconds): feed both the same input patterns by setting the input blocks'
        // states, let timers settle, and compare what each old chain and the graph would send to every output. Finish:
        // put the input blocks back, link the computer to the outputs, and either delete the old gates and timers (every
        // output matched: permanent) or keep them unlinked and report what differed (Undo still works).

        class Test
        {
            public LuaComputer Computer;
            public ParentEntity Ship;
            public LogicConverter.Result Result;
            public List<string[]> Plan;
            public Dictionary<string, ICommand> Blocks;
            public List<ICommand> Logic = new List<ICommand>();
            public List<(ICommand block, string port)> Inputs = new List<(ICommand, string)>();          // feeding both, testable
            public List<(ICommand block, string port, List<ICommand> drivers, string wire)> Outputs = new List<(ICommand, string, List<ICommand>, string)>();
            public List<string> Untestable = new List<string>(), Differences = new List<string>(), Notes = new List<string>();
            public Dictionary<ICommand, float> Real = new Dictionary<ICommand, float>();
            public List<float[]> Cases = new List<float[]>();
            public int Case = -1, Checked;
            public float Settle, Next, GiveUp;
            public StringBuilder Added = new StringBuilder();
            public int OldLinks;
        }

        static Test test;
        public static int MaxLogic = 100, MaxOutputs = 64;
        public static bool Testing => test != null;
        static string lastResult = "";
        public static string LastResult => lastResult;

        public static string Refit(LuaComputer computer)
        {
            var ship = computer?.brickCoord.entity?.Parent;
            if (ship == null) return "no computer";
            if (test != null) return "a refit test is already running";
            if (CanUndo(ship)) return $"{ship.EntityName} already has a refit; undo it first.";
            var wait = LogicConverter.NotReady(ship);
            if (wait != null) return wait;
            var r = LogicConverter.Convert(ship);
            if (r.Gates + r.Delays == 0) return r.Report;
            // Without blocks on both edges there's nothing to test, and refitting would only disconnect the ship's logic.
            if (r.Inputs == 0 || r.Outputs == 0)
                return $"Not refitting {ship.EntityName}: the converter found {r.Inputs} block(s) feeding its logic and {r.Outputs} it drives, " +
                       "so the result couldn't be tested. Nothing was changed.\n" + r.Report;
            // Refitting Fenrir M2-Elkhound (359 gates and timers, 835 outputs) crashed the game natively right after the
            // computer took over, without a managed error. Until that's understood, big refits are refused.
            if (r.Gates + r.Delays > MaxLogic || r.Outputs > MaxOutputs)
                return $"Not refitting {ship.EntityName}: {r.Gates + r.Delays} gates and timers and {r.Outputs} outputs is more than a refit " +
                       $"handles safely yet (up to {MaxLogic} and {MaxOutputs}). Nothing was changed; the converted graph is in scripts.";
            var t = new Test { Computer = computer, Ship = ship, Result = r, Blocks = Blocks(ship) };
            t.Plan = r.Refit.Split('\n').Select(l => l.TrimEnd('\r').Split('\t')).Where(w => w.Length >= 3).ToList();
            t.Logic = t.Plan.Where(w => w[0] == "logic").Select(w => t.Blocks.TryGetValue(w[2], out var b) ? b : null).Where(b => b != null).ToList();
            var logicSet = new HashSet<ICommand>(t.Logic);

            // Every link into or out of the converted logic, as it is now (the undo record, written before any change).
            var original = new List<(ICommand from, ICommand to)>();
            foreach (var b in t.Blocks.Values)
                if (b is ICommandLinkSource s)
                    foreach (var item in s.targets.Items)
                        if (item.Command != null && (logicSet.Contains(b) || logicSet.Contains(item.Command)) && item.Command.brickCoord.entity?.Parent == ship)
                            original.Add((b, item.Command));
            t.OldLinks = original.Count;
            var record = new StringBuilder();
            record.AppendLine($"computer\t{Lua.KeyOf(computer)}\t{computer.ProgramKind}\t{(computer.Program == null ? "-" : B64(computer.Program))}\t{(computer.PortMap == null ? "-" : B64(computer.PortMap))}");
            foreach (var (from, to) in original) record.AppendLine($"link\t{Lua.KeyOf(from)}\t{Lua.KeyOf(to)}");
            File.WriteAllText(UndoPath(ship), record.ToString());

            // The old logic drives nothing from here on: cut its links to the blocks it drove.
            var drivenKeys = new HashSet<string>(t.Plan.Where(w => w[0] == "out").Select(w => w[2]));
            foreach (var (from, to) in original) if (logicSet.Contains(from) && drivenKeys.Contains(Lua.KeyOf(to))) Unlink(from, to);
            computer.SetPorts(null);
            computer.SetProgram("graph", r.Graph);

            // Inputs: linked into the computer too. A block that allows only one link out can't feed both: it moves to
            // the computer and the outputs it reaches can't be tested.
            foreach (var w in t.Plan.Where(w => w[0] == "in"))
            {
                if (!t.Blocks.TryGetValue(w[2], out var b)) { t.Notes.Add($"input {w[1]}: its block is gone"); continue; }
                if (Link(b, computer)) { t.Inputs.Add((b, w[1])); }
                else
                {
                    foreach (var (from, to) in original.Where(x => x.from == b)) Unlink(from, to);
                    if (Link(b, computer)) t.Untestable.Add($"input {w[1]} ({Lua.Describe(b)}) can't feed the old logic and the computer at once");
                    else { t.Notes.Add($"input {w[1]} ({Lua.Describe(b)}): the game wouldn't link it into the computer"); continue; }
                }
                t.Added.AppendLine($"added\t{Lua.KeyOf(b)}\t{Lua.KeyOf(computer)}");
                Lua.AssignIn(computer, w[1], b, "state");
            }

            // Outputs: what the graph would send is the wire into its port out node; what the old logic would send is
            // the state of the gate or timer that drove the block. Outputs fed by repeat or toggle timers pulse, so
            // there's no steady value to compare.
            var graph = Graphs.Parse(r.Graph);
            var preds = new Dictionary<ICommand, List<ICommand>>();
            foreach (var (from, to) in original) { if (!preds.TryGetValue(to, out var l)) preds[to] = l = new List<ICommand>(); l.Add(from); }
            bool Pulses(ICommand c, HashSet<ICommand> seen)
            {
                if (!seen.Add(c) || !logicSet.Contains(c)) return false;
                if (c is CommandDelay d && d.CurrentRepeatMode != CommandDelay.RepeatMode.None) return true;
                return preds.TryGetValue(c, out var l) && l.Any(p => Pulses(p, seen));
            }
            foreach (var w in t.Plan.Where(w => w[0] == "out"))
            {
                if (!t.Blocks.TryGetValue(w[2], out var b)) { t.Notes.Add($"output {w[1]}: its block is gone"); continue; }
                var node = graph.Nodes.Values.FirstOrDefault(n => n.Type.Name == "port out" && n.Settings.TryGetValue("port", out var p) && p == w[1]);
                var wire = node == null ? null : graph.Wires.Where(x => x.toNode == node.Id).Select(x => $"{x.fromNode}.{x.fromPort}").FirstOrDefault();
                var drivers = preds.TryGetValue(b, out var l) ? l.Where(logicSet.Contains).ToList() : new List<ICommand>();
                if (wire == null || drivers.Count == 0) { t.Untestable.Add($"output {w[1]}: no wire to compare"); continue; }
                if (drivers.Any(d => Pulses(d, new HashSet<ICommand>()))) { t.Untestable.Add($"output {w[1]} ({Lua.Describe(b)}) is driven by a repeating timer (pulses, no steady value)"); continue; }
                t.Outputs.Add((b, w[1], drivers, wire));
            }

            // The cases: every combination for up to 4 inputs, else 12 spread-out ones; the all-off case first.
            var n = t.Inputs.Count;
            if (n <= 4) for (int k = 0; k < (1 << n); k++) t.Cases.Add(Enumerable.Range(0, n).Select(i => (float)((k ^ (k >> 1)) >> i & 1)).ToArray());
            else
            {
                var rng = new Random(n * 7919 + t.Outputs.Count);
                t.Cases.Add(new float[n]);
                for (int k = 0; k < 11; k++) t.Cases.Add(Enumerable.Range(0, n).Select(_ => (float)rng.Next(2)).ToArray());
            }
            foreach (var (b, _) in t.Inputs) t.Real[b] = b.State;
            var longest = t.Logic.OfType<CommandDelay>().Select(d => d.delay).DefaultIfEmpty(0f).Max();
            t.Settle = UnityEngine.Mathf.Clamp(2f * longest + 0.5f, 0.6f, 4f);
            Lua.RescanSoon();
            test = t;
            t.Next = UnityEngine.Time.time + 1.5f;                     // the graph needs a moment to start
            t.GiveUp = UnityEngine.Time.time + 30f + t.Cases.Count * t.Settle;
            if (t.Outputs.Count == 0 || t.Inputs.Count == 0) t.Case = t.Cases.Count;   // nothing to test: finish next frame
            return $"Testing the refit of {ship.EntityName}: {t.Cases.Count} input case(s) x {t.Outputs.Count} output(s), about " +
                   $"{UnityEngine.Mathf.CeilToInt(1.5f + t.Cases.Count * t.Settle)} s. Nothing on the ship moves meanwhile.";
        }

        // Sets an input block's state and passes it to the old gates and timers it feeds, as the game would. Only to
        // those: anything else it's linked to (a door straight from the switch) must not move during the test.
        static void Feed(ICommand block, float value, HashSet<ICommand> logic)
        {
            block.State = value;
            try { ((Command)block).OnStateUpdated(onLoad: false); } catch { }
            if (block is ICommandLinkSource s)
            {
                var targets = new List<ICommand>();
                foreach (var i in s.targets.Items) if (i.Command != null) targets.Add(i.Command);
                foreach (var target in targets) if (logic.Contains(target)) target.Activate(false, Manager.Dependency.PlayerManager, value);
            }
        }

        // Every frame (from Lua.Update): steps the test, then finishes.
        internal static void Update()
        {
            var t = test;
            if (t == null) return;
            try
            {
                if (t.Computer.brickCoord.entity == null) { test = null; lastResult = "The refit test stopped: the computer is gone."; return; }
                if (UnityEngine.Time.time < t.Next) return;
                if (t.Case >= 0 && t.Case < t.Cases.Count) Compare(t);
                t.Case++;
                if (t.Case < t.Cases.Count && UnityEngine.Time.time < t.GiveUp)
                {
                    var logic = new HashSet<ICommand>(t.Logic);
                    for (int i = 0; i < t.Inputs.Count; i++) Feed(t.Inputs[i].block, t.Cases[t.Case][i], logic);
                    t.Next = UnityEngine.Time.time + t.Settle;
                    return;
                }
                Finish(t);
            }
            catch (Exception e)
            {
                Core.Log.Error("Refit test failed: " + e);
                t.Differences.Add("the test itself failed: " + e.Message);
                try { Finish(t); } catch { }
            }
        }

        static void Compare(Test t)
        {
            if (!Lua.Runs(t.Computer)) { t.Differences.Add($"case {t.Case + 1}: the graph wasn't running"); return; }
            var inputs = string.Join(", ", t.Inputs.Select((x, i) => $"{x.port}={t.Cases[t.Case][i]:0}").ToArray());
            foreach (var (block, port, drivers, wire) in t.Outputs)
            {
                var graph = Lua.GraphValue(t.Computer, wire) ?? double.NaN;
                // Several drivers: the block takes whichever reached it last, so any one of them counts.
                var match = drivers.Any(d => Math.Abs(d.State - graph) < 0.01);
                t.Checked++;
                if (!match && t.Differences.Count < 12)
                    t.Differences.Add($"{port} ({Lua.Describe(block)}) with {inputs}: old logic {string.Join("/", drivers.Select(d => d.State.ToString("0.##")).ToArray())}, graph {graph:0.##}");
            }
        }

        static void Finish(Test t)
        {
            test = null;
            var c = t.Computer;
            // The input blocks as they really are again.
            var logicSet = new HashSet<ICommand>(t.Logic);
            foreach (var kv in t.Real) Feed(kv.Key, kv.Value, logicSet);

            // The old logic hears nothing more; the computer drives the outputs.
            foreach (var (b, _) in t.Inputs)
                if (b is ICommandLinkSource s)
                {
                    var into = new List<ICommand>();
                    foreach (var i in s.targets.Items) if (i.Command != null && logicSet.Contains(i.Command)) into.Add(i.Command);
                    foreach (var target in into) Unlink(b, target);
                }
            int outs = 0;
            foreach (var w in t.Plan.Where(w => w[0] == "out"))
            {
                if (!t.Blocks.TryGetValue(w[2], out var b)) continue;
                if (Link(c, b)) { t.Added.AppendLine($"added\t{Lua.KeyOf(c)}\t{Lua.KeyOf(b)}"); Lua.AssignOut(c, w[1], b); outs++; }
                else t.Notes.Add($"output {w[1]} ({Lua.Describe(b)}): the game wouldn't link the computer to it" +
                                 (c.predecessors.Contains(b) ? " (it also feeds the computer, and the game allows no loops)" : " (it may take only one input)"));
            }
            File.AppendAllText(UndoPath(t.Ship), t.Added.ToString());
            Lua.RescanSoon();

            var report = new StringBuilder();
            report.Append($"Refitted {t.Ship.EntityName}: {t.Result.Gates} gate(s) and {t.Result.Delays} timer(s) now run as a graph on this computer; " +
                          $"{outs} of {t.Result.Outputs} output(s) linked. ");
            var same = t.Differences.Count == 0 && t.Untestable.Count == 0 && t.Notes.Count == 0 && t.Checked > 0;
            if (same)
            {
                // Every output matched: the old gates and timers go, and with them the way back.
                var builder = Manager.Dependency.PlayerManager.NetworkedBuilder;
                foreach (var b in t.Logic)
                    builder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.delete, brickData = b.brickCoord.brickData, entityID = b.brickCoord.entity.ID });
                var path = UndoPath(t.Ship);
                var dir = Path.Combine(Lua.ScriptsDir, "deleted");
                Directory.CreateDirectory(dir);
                File.Move(path, Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} {DateTime.Now:yyyy-MM-dd HHmmss}.undo"));
                report.Append($"The test matched on all {t.Checked} check(s) ({t.Cases.Count} input case(s)), so the old {t.Logic.Count} gate(s) and timer(s) were removed. This can't be undone.");
            }
            else
            {
                report.Append($"The old gates and timers stay on the ship, unlinked; Undo puts everything back. ");
                if (t.Checked == 0 && t.Differences.Count == 0) report.Append("Nothing could be tested. ");
                foreach (var x in t.Differences) report.Append("Differs: " + x + ". ");
                foreach (var x in t.Untestable) report.Append("Not tested: " + x + ". ");
                foreach (var x in t.Notes) report.Append(x + ". ");
            }
            lastResult = report.ToString();
            Core.Log.Msg("Refit: " + lastResult);
            Graphs.Canvas.Say(lastResult);
        }

        public static string Undo(ParentEntity ship)
        {
            if (test != null && test.Ship == ship) return "wait for the refit test to finish";
            if (!CanUndo(ship)) return "nothing to undo";
            var blocks = Blocks(ship);
            var lines = File.ReadAllLines(UndoPath(ship)).Select(l => l.Split('\t')).ToList();
            var head = lines.FirstOrDefault(w => w[0] == "computer");
            var computer = head != null && blocks.TryGetValue(head[1], out var c) ? c as LuaComputer : null;
            int removed = 0, restored = 0, missing = 0;
            foreach (var w in lines.Where(w => w[0] == "added"))
                if (blocks.TryGetValue(w[1], out var from) && blocks.TryGetValue(w[2], out var to) && Linked(from, to)) { Unlink(from, to); removed++; }
            var back = new List<(ICommand from, ICommand to)>();
            foreach (var w in lines.Where(w => w[0] == "link"))
            {
                if (blocks.TryGetValue(w[1], out var from) && blocks.TryGetValue(w[2], out var to) && Link(from, to)) { restored++; back.Add((from, to)); }
                else missing++;
            }
            // The game only recomputes a block when a signal reaches it, so the reconnected gates and timers still hold
            // whatever they had before the refit: send each restored link its source's state once to settle them.
            foreach (var (from, to) in back) to.Activate(false, Manager.Dependency.PlayerManager, from.State);
            if (computer != null)
            {
                var kind = head[2];
                if (head[3] == "-") computer.Program = null;
                else computer.SetProgram(kind, FromB64(head[3]));
                computer.SetPorts(head[4] == "-" ? null : FromB64(head[4]));
            }
            var path = UndoPath(ship);
            var dir = Path.Combine(Lua.ScriptsDir, "deleted");
            Directory.CreateDirectory(dir);
            File.Move(path, Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} {DateTime.Now:yyyy-MM-dd HHmmss}.undo"));
            Lua.RescanSoon();
            return $"Undid the refit of {ship.EntityName}: {restored} original link(s) back, {removed} computer link(s) removed" +
                   (missing > 0 ? $", {missing} couldn't be restored (blocks gone?)" : "") +
                   (computer != null ? ", the computer's program and ports as they were." : "; the computer is gone.");
        }

        // ---- For DevBridge ----

        static LuaComputer ComputerOn(string shipName, string label) => CommandManager.commands.Datas.OfType<LuaComputer>()
            .FirstOrDefault(c => c.brickCoord.entity?.Parent?.EntityName == shipName && (label == null || label == "-" || c.ScriptName == label));

        public static string RefitShip(string shipName, string label)
        {
            var c = ComputerOn(shipName, label);
            return c == null ? $"no Lua Computer on {shipName}" : Refit(c);
        }

        public static string UndoShip(string shipName)
        {
            var ship = CommandManager.commands.Datas.OfType<ICommand>().Select(x => x.brickCoord.entity?.Parent).FirstOrDefault(p => p != null && p.EntityName == shipName);
            return ship == null ? "no ship named " + shipName : Undo(ship);
        }

        // Every link on a ship, as sorted text (to compare before and after).
        public static string Links(string shipName)
        {
            var lines = new List<string>();
            foreach (var c in CommandManager.commands.Datas.OfType<ICommand>().Where(c => c is ICommandLinkSource && c.brickCoord.entity?.Parent?.EntityName == shipName))
                foreach (var i in ((ICommandLinkSource)c).targets.Items)
                    if (i.Command != null) lines.Add($"{Lua.Describe(c)} {Lua.KeyOf(c)} -> {Lua.Describe(i.Command)} {Lua.KeyOf(i.Command)}");
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines.ToArray());
        }
    }
}
