using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Ports: named link slots on a Lua Computer, so a program never has to trust link order.
    //  - The program declares them: ports.declare{ inputs = {"throttle"}, outputs = {"lights"}, channels = {"nav"} }
    //    (a graph declares the ones its port in / port out / send / receive nodes use).
    //  - Which linked block sits in which port is chosen with the link tool's picker or the Computer screen's Links tab,
    //    and saved in the computer block (LuaComputer.PortMap), keyed by the block itself, not by link order.
    //  - Inputs and outputs: inputs.get("throttle"), outputs.set("lights", true). An input port reads its block's
    //    on/off state or one of its readouts (a thruster's thrust...). With ports declared, numbers count the declared
    //    ports (inputs.get(1) is the first declared input); without, they count links, oldest first, as before.
    //  - Channels are two-way between two linked computers: one ordinary game link (the game allows no loops, so
    //    there can't be one each way) is the cable, and both ends send whole messages over it (numbers, text,
    //    tables): ports.send("nav", { heading = 90 }) on one, on_message(port, msg) / ports.last("nav") on the other.
    // PortMap lines, tab separated: in <port> <block> <value> / out <port> <block> / ch <port> <computer> <their port>,
    // where a block is "<child entity id>.<instance id>" on the computer's own ship.
    public static partial class Lua
    {
        internal class Declared
        {
            public readonly List<string> Inputs = new List<string>(), Outputs = new List<string>(), Channels = new List<string>();
            public bool Any => Inputs.Count + Outputs.Count + Channels.Count > 0;
        }

        internal class PortLink
        {
            public string Kind, Port, Key, Extra;   // Extra: an input's value ("state" or a readout), a channel's far port
        }

        static readonly Dictionary<LuaComputer, Declared> declared = new Dictionary<LuaComputer, Declared>();
        static readonly Dictionary<LuaComputer, (string text, List<PortLink> links)> portLinks = new Dictionary<LuaComputer, (string, List<PortLink>)>();
        static readonly Dictionary<LuaComputer, Queue<(string port, object msg)>> inbox = new Dictionary<LuaComputer, Queue<(string, object)>>();
        static readonly Dictionary<LuaComputer, Dictionary<string, object>> lastMessage = new Dictionary<LuaComputer, Dictionary<string, object>>();
        static readonly Regex portName = new Regex(@"^[A-Za-z_][\w\-]{0,31}$");

        internal static Declared DeclaredOf(LuaComputer c) => c != null && declared.TryGetValue(c, out var d) ? d : null;

        // The block a link is to, as a key that survives saving: its child entity and brick instance on the ship.
        internal static string KeyOf(ICommand c)
        {
            try { return $"{c.brickCoord.entity.ChildrenID}.{c.brickCoord.brickData.instanceId}"; }
            catch { return ""; }
        }

        // A block as people know it: its Tag label, else its kind.
        internal static string Describe(ICommand c)
        {
            if (c == null) return "(nothing)";
            string label = null;
            try { label = c.brickCoord.TagData.label; } catch { }
            var kind = c.OverTextIndirect ?? c.GetType().Name;
            return string.IsNullOrWhiteSpace(label) ? kind : $"{label.Trim()} ({kind})";
        }

        // ---- Saved assignments ----

        internal static List<PortLink> LinksOf(LuaComputer c)
        {
            var text = c.PortMap ?? "";
            if (portLinks.TryGetValue(c, out var cached) && ReferenceEquals(cached.text, text)) return cached.links;
            var list = new List<PortLink>();
            foreach (var line in text.Split('\n'))
            {
                var w = line.Split('\t');
                if (w.Length < 3 || (w[0] != "in" && w[0] != "out" && w[0] != "ch")) continue;
                list.Add(new PortLink { Kind = w[0], Port = w[1], Key = w[2], Extra = w.Length > 3 ? w[3] : "" });
            }
            portLinks[c] = (text, list);
            return list;
        }

        static void Store(LuaComputer c, List<PortLink> links)
        {
            var sb = new StringBuilder();
            foreach (var l in links) sb.Append(l.Kind).Append('\t').Append(l.Port).Append('\t').Append(l.Key).Append('\t').Append(l.Extra ?? "").Append('\n');
            c.SetPorts(sb.ToString());
        }

        // Puts a block in a port (an input with the value it reads), replacing whatever was there.
        internal static void AssignIn(LuaComputer c, string port, ICommand from, string value)
        {
            var links = LinksOf(c).Where(l => !(l.Kind == "in" && l.Port == port)).ToList();
            links.Add(new PortLink { Kind = "in", Port = port, Key = KeyOf(from), Extra = string.IsNullOrEmpty(value) ? "state" : value });
            Store(c, links);
        }

        internal static void AssignOut(LuaComputer c, string port, ICommand to)
        {
            var links = LinksOf(c).Where(l => !(l.Kind == "out" && l.Port == port)).ToList();
            links.Add(new PortLink { Kind = "out", Port = port, Key = KeyOf(to) });
            Store(c, links);
        }

        // A two-way channel: both computers remember the other end.
        internal static void Pair(LuaComputer a, string aPort, LuaComputer b, string bPort)
        {
            void One(LuaComputer self, string mine, LuaComputer other, string theirs)
            {
                var links = LinksOf(self).Where(l => !(l.Kind == "ch" && l.Port == mine)).ToList();
                links.Add(new PortLink { Kind = "ch", Port = mine, Key = KeyOf(other), Extra = theirs });
                Store(self, links);
            }
            One(a, aPort, b, bPort);
            One(b, bPort, a, aPort);
        }

        internal static void Clear(LuaComputer c, string kind, string port)
        {
            var link = LinksOf(c).FirstOrDefault(l => l.Kind == kind && l.Port == port);
            if (link == null) return;
            if (kind == "ch" && PartnerOf(c, link) is LuaComputer other)
                Store(other, LinksOf(other).Where(l => !(l.Kind == "ch" && l.Port == link.Extra && l.Key == KeyOf(c))).ToList());
            Store(c, LinksOf(c).Where(l => l != link).ToList());
        }

        // The linked block a port's link points at, while the game link is still there.
        internal static ICommand TargetOf(LuaComputer c, PortLink link)
        {
            if (link == null) return null;
            var list = link.Kind == "in" ? c.Inputs : link.Kind == "out" ? c.Outputs : c.Inputs.Concat(c.Outputs).ToList();
            return list.FirstOrDefault(x => KeyOf(x) == link.Key);
        }

        internal static LuaComputer PartnerOf(LuaComputer c, PortLink link) => link?.Kind == "ch" ? TargetOf(c, link) as LuaComputer : null;

        static PortLink Port(LuaComputer c, string kind, string port) => LinksOf(c).FirstOrDefault(l => l.Kind == kind && l.Port == port);

        // What each computer last sent down each of its links (outputs.set). A link's value in the game is the sending
        // block's own state, but a computer sets the receiver's state and never its own, so a computer reading another
        // computer gets what that one sent it from here.
        static readonly Dictionary<(ICommand from, ICommand to), float> sent = new Dictionary<(ICommand, ICommand), float>();

        internal static void NoteSent(ICommand from, ICommand to, float value) => sent[(from, to)] = value;

        // A linked block's plain value as `reader` sees it.
        internal static float StateOf(ICommand block, LuaComputer reader) =>
            block is LuaComputer && sent.TryGetValue((block, reader), out var v) ? v : block.State;

        // An input port's value: its block's state, or the readout it was set to read.
        internal static float ValueOf(ICommand block, PortLink link, LuaComputer reader)
        {
            if (block == null) return 0f;
            if (link == null || string.IsNullOrEmpty(link.Extra) || link.Extra == "state") return StateOf(block, reader);
            var found = new Found { Snapshot = block };
            return (float)(Read(found.Now, link.Extra) ?? 0.0);
        }

        // ---- Messages between scripts: copied as plain values, so no table is shared between two Lua states ----

        static object Plain(DynValue v, ref int budget, int depth = 0)
        {
            if (--budget < 0) return null;
            switch (v.Type)
            {
                case DataType.Number: return v.Number;
                case DataType.String: return v.String;
                case DataType.Boolean: return v.Boolean;
                case DataType.Table:
                    if (depth > 8) return null;
                    var d = new Dictionary<object, object>();
                    foreach (var pair in v.Table.Pairs)
                    {
                        var key = Plain(pair.Key, ref budget, depth + 1);
                        var value = Plain(pair.Value, ref budget, depth + 1);
                        if (key != null && value != null) d[key] = value;
                    }
                    return d;
                default: return null;
            }
        }

        static DynValue ToLua(object o, Script s)
        {
            switch (o)
            {
                case double n: return DynValue.NewNumber(n);
                case string t: return DynValue.NewString(t);
                case bool b: return DynValue.NewBoolean(b);
                case Dictionary<object, object> d:
                    var table = new Table(s);
                    foreach (var pair in d) table.Set(ToLua(pair.Key, s), ToLua(pair.Value, s));
                    return DynValue.NewTable(table);
                default: return DynValue.Nil;
            }
        }

        // Each frame: a computer's waiting messages, each to on_message(port, msg); the last one per port is kept.
        static void Deliver(Running r, LuaComputer computer)
        {
            if (!inbox.TryGetValue(computer, out var queue) || queue.Count == 0) return;
            var handler = r.Script.Globals.Get("on_message");
            for (int n = 0; n < 64 && queue.Count > 0 && r.Script != null; n++)
            {
                var (port, msg) = queue.Dequeue();
                if (handler.Type == DataType.Function) Call(r, handler, DynValue.NewString(port), ToLua(msg, r.Script));
            }
        }

        // ---- The `ports` global on Lua Computers ----

        static void ExposePorts() => Expose("ports", c =>
        {
            if (!(c.Host is LuaComputer computer)) return null;
            c.Stopped += () => { declared.Remove(computer); inbox.Remove(computer); };
            return new PortsApi(computer);
        });

        // ports.declare{ inputs = {...}, outputs = {...}, channels = {...} }   the ports this program has
        // ports.send(port, msg)          a message (number, text, true/false or a table of them) to the far end of a
        //                                channel; false when the channel isn't connected
        // ports.last(port)               the last message that came in on a channel, or nil
        // ports.linked(port)             what's in a port: the block's name, or nil
        // ports.list()                   every declared port: { name, kind = "input"/"output"/"channel", linked }
        // function on_message(port, msg) runs for each message that comes in
        public class PortsApi
        {
            readonly LuaComputer computer;
            public PortsApi(LuaComputer host) { computer = host; }

            public void declare(Table spec)
            {
                var d = new Declared();
                void Read(string key, List<string> into)
                {
                    var v = spec.Get(key);
                    if (v.IsNil()) return;
                    if (v.Type != DataType.Table) throw new ScriptRuntimeException($"ports.declare: {key} is a list of names, like {{ \"a\", \"b\" }}");
                    foreach (var item in v.Table.Values)
                    {
                        var name = item.CastToString();
                        if (name == null || !portName.IsMatch(name)) throw new ScriptRuntimeException($"ports.declare: \"{name}\" isn't a port name (letters, numbers, _ and -, starting with a letter)");
                        if (!into.Contains(name)) into.Add(name);
                    }
                }
                Read("inputs", d.Inputs);
                Read("outputs", d.Outputs);
                Read("channels", d.Channels);
                declared[computer] = d;
            }

            public bool send(string port, DynValue msg)
            {
                var link = Port(computer, "ch", port);
                var other = PartnerOf(computer, link);
                if (other == null) return false;
                var budget = 2000;
                var plain = Plain(msg, ref budget);
                if (!inbox.TryGetValue(other, out var queue)) inbox[other] = queue = new Queue<(string, object)>();
                if (queue.Count >= 256) return false;
                queue.Enqueue((link.Extra, plain));
                if (!lastMessage.TryGetValue(other, out var last)) lastMessage[other] = last = new Dictionary<string, object>();
                last[link.Extra] = plain;
                return true;
            }

            public DynValue last(ScriptExecutionContext context, string port) =>
                lastMessage.TryGetValue(computer, out var last) && last.TryGetValue(port, out var msg) ? ToLua(msg, context.GetScript()) : DynValue.Nil;

            public string linked(string port)
            {
                var link = LinksOf(computer).FirstOrDefault(l => l.Port == port);
                var target = TargetOf(computer, link);
                return target != null ? Describe(target) : null;
            }

            public Table list(ScriptExecutionContext context)
            {
                var script = context.GetScript();
                var t = new Table(script);
                var d = DeclaredOf(computer);
                if (d == null) return t;
                void Add(string name, string kind, string code)
                {
                    var row = new Table(script);
                    row["name"] = name;
                    row["kind"] = kind;
                    var target = TargetOf(computer, Port(computer, code, name));
                    if (target != null) row["linked"] = Describe(target);
                    t.Append(DynValue.NewTable(row));
                }
                foreach (var p in d.Inputs) Add(p, "input", "in");
                foreach (var p in d.Outputs) Add(p, "output", "out");
                foreach (var p in d.Channels) Add(p, "channel", "ch");
                return t;
            }
        }

        // ---- inputs / outputs by port name ----

        // Which block an inputs/outputs index means: a port name, a declared port's number, or (with no ports
        // declared) a link's number, oldest link first. The link record comes back too, for an input's value.
        static ICommand Resolve(LuaComputer c, bool outgoing, DynValue i, out PortLink link)
        {
            link = null;
            var kind = outgoing ? "out" : "in";
            var d = DeclaredOf(c);
            var names = d == null ? null : outgoing ? d.Outputs : d.Inputs;
            string name = null;
            if (i.Type == DataType.String) name = i.String;
            else if (i.Type == DataType.Number && names != null && names.Count > 0)
            {
                var n = (int)i.Number;
                if (n < 1 || n > names.Count) return null;
                name = names[n - 1];
            }
            if (name != null)
            {
                link = Port(c, kind, name);
                return TargetOf(c, link);
            }
            if (i.Type != DataType.Number) return null;
            var all = outgoing ? c.Outputs : c.Inputs;
            var k = (int)i.Number;
            return k >= 1 && k <= all.Count ? all[k - 1] : null;
        }

        // What on_input sees: with input ports declared, one entry per port (by name); without, one per link.
        static IEnumerable<(DynValue id, string key, float value)> InputValues(LuaComputer c)
        {
            var d = DeclaredOf(c);
            if (d != null && d.Inputs.Count > 0)
            {
                foreach (var name in d.Inputs)
                {
                    var link = Port(c, "in", name);
                    yield return (DynValue.NewString(name), "p:" + name, ValueOf(TargetOf(c, link), link, c));
                }
                yield break;
            }
            var all = c.Inputs;
            for (int i = 0; i < all.Count; i++) yield return (DynValue.NewNumber(i + 1), "l:" + (i + 1), StateOf(all[i], c));
        }
    }
}
