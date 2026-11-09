using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EvoCore
{
    // Node graphs for the Lua Computer (visual scripting, step 1: the format and the compiler; the canvas comes later).
    // A Lua Computer labelled `name` runs scripts\name.graph if it exists, else name.lua. The graph is compiled to a
    // Lua script and run by the same runner, so the sandbox, step limit, APIs and live reload all work unchanged.
    //
    // A .graph file is plain text, one thing per line ('#' starts a comment):
    //   rate 0.1                              how often the graph is worked out, in seconds (optional)
    //   node 1 input index=1 at=40,120        node <id> <type> <setting=value>... ; quote values with spaces;
    //                                         at=x,y is where the canvas (GraphEditor.cs) draws it
    //   node 2 block label="cargo door" value=percentage_filled
    //   node 3 compare op=< b=0.2             a setting named like an input is that input's value when unwired
    //   node 4 output index=1
    //   wire 2.out 3.a                        wire <node>.<output> <node>.<input> (a "->" between them is fine)
    //   wire 3.out 4.in
    //   comment at=0,0 size=300,160 text="fuel warning"   a labelled box behind nodes (canvas only; doesn't run)
    //
    // Dataflow (Stormworks style): every wire carries a number, on/off is 1/0 (above 0.5 counts as on), and the whole
    // graph is worked out each tick, each node after the nodes that feed it. A wire back into an earlier node reads
    // last tick's value, so loops can't run forever. The generated script keeps every wire's value in the global V
    // ("node.port" -> value) for the editor's live view, and each node's memory (timers, latches) in S.
    // `get EvoCore.Graphs.ShowLua("name")` (DevBridge) shows what a graph compiles to.
    public static partial class Graphs
    {
        public class GraphException : Exception
        {
            public GraphException(string message) : base(message) { }
        }

        // What a node type looks like and the Lua it turns into. Code gets the node being compiled and returns Lua
        // statements; it reads inputs with e.In("a") and writes outputs to e.Out("out").
        public class NodeType
        {
            public string Name, Help, Category = "";   // Category groups the node menu
            public string[] Ins = new string[0], Outs = { "out" };
            public Dictionary<string, string> Settings = new Dictionary<string, string>();   // name -> default ("" = required)
            public Dictionary<string, double> InDefaults = new Dictionary<string, double>(); // unwired input -> value (else 0)
            // What each input, output and setting means, by name (the Help tab shows them).
            public Dictionary<string, string> Docs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Func<Emit, string> Code;
            public string Source, File;   // Lua-written nodes (NodeLibrary.cs): the node file's code and path
            // Ports that depend on the node's own settings (the lua node's inputs=/outputs=); null: the type's Ins/Outs.
            public Func<Node, string[]> InsOf, OutsOf;
        }

        public class Node
        {
            public int Id, Line;
            public float X, Y;      // where the canvas draws it (at=x,y in the file); Placed is false without one
            public bool Placed;
            public NodeType Type;
            public Dictionary<string, string> Settings = new Dictionary<string, string>();
            public string Script;   // the lua node's code (a `code <id>` block in the file)
            public string[] Ins => Type.InsOf?.Invoke(this) ?? Type.Ins;
            public string[] Outs => Type.OutsOf?.Invoke(this) ?? Type.Outs;
        }

        // One node as it's being compiled.
        public class Emit
        {
            public Node Node;
            internal Dictionary<string, string> wiredFrom;   // input -> "node.port" it's wired from

            public string In(string port)
            {
                if (wiredFrom.TryGetValue(port, out var from)) return $"V[\"{from}\"]";
                if (Node.Settings.TryGetValue(port, out var raw)) return Number(raw, port);
                return Node.Type.InDefaults.TryGetValue(port, out var d) ? Lit(d) : "0";
            }

            public string Out(string port) => $"V[\"{Node.Id}.{port}\"]";
            public string State => $"S[{Node.Id}]";
            public string Setting(string name) => Node.Settings.TryGetValue(name, out var v) ? v : Node.Type.Settings[name];
            public string Num(string name) => Number(Setting(name), name);
            public string Str(string name) => "\"" + Setting(name).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

            string Number(string raw, string what)
            {
                if (raw == "true" || raw == "on") return "1";
                if (raw == "false" || raw == "off") return "0";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new GraphException($"line {Node.Line}: node {Node.Id} {what}={raw} isn't a number");
                return Lit(d);
            }
        }

        internal static string LuaString(string v) => "\"" + (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

        static string Lit(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        public static readonly Dictionary<string, NodeType> Types = new Dictionary<string, NodeType>(StringComparer.OrdinalIgnoreCase);

        static void Add(string name, string help, string[] ins, Func<Emit, string> code, params string[] settings)
        {
            var t = new NodeType { Name = name, Help = help, Ins = ins, Code = code, Category = category };
            foreach (var s in settings)
            {
                var eq = s.IndexOf('=');
                t.Settings[eq < 0 ? s : s.Substring(0, eq)] = eq < 0 ? "" : s.Substring(eq + 1);
            }
            Types[name] = t;
        }

        static string category = "";   // set before each group of Add calls below
        static readonly string[] None = new string[0], A = { "a" }, AB = { "a", "b" }, In1 = { "in" };
        static readonly Dictionary<string, string> compareOps = new Dictionary<string, string>
        {
            ["<"] = "<", [">"] = ">", ["<="] = "<=", [">="] = ">=", ["="] = "==", ["=="] = "==", ["!="] = "~=", ["~="] = "~=",
        };
        static readonly string[] shipNumbers = { "speed", "mass", "fuel", "power", "heat", "hull", "shield", "altitude", "power_in", "power_use" };
        static readonly string[] shipFlags = { "stalled", "shield_on", "warping", "cruising" };

        static Graphs()
        {
            category = "Links";
            Add("input", "the value of the computer's link in number `index`", None, e => $"{e.Out("out")} = inputs.get({e.Num("index")})", "index=1");
            Add("output", "sends its input down the computer's link out number `index` (only when it changes)", In1,
                e => $"if {e.State}.sent ~= {e.In("in")} then {e.State}.sent = {e.In("in")}; outputs.set({e.Num("index")}, {e.In("in")}) end", "index=1");
            Types["output"].Outs = None;
            Add("constant", "always `value`", None, e => $"{e.Out("out")} = {e.Num("value")}", "value=0");

            category = "Logic";   // on/off: above 0.5 is on; results are 1 or 0
            Add("and", "on when a and b are both on", AB, e => $"{e.Out("out")} = n(b({e.In("a")}) and b({e.In("b")}))");
            Add("or", "on when a or b is on", AB, e => $"{e.Out("out")} = n(b({e.In("a")}) or b({e.In("b")}))");
            Add("xor", "on when exactly one of a and b is on", AB, e => $"{e.Out("out")} = n(b({e.In("a")}) ~= b({e.In("b")}))");
            Add("not", "on when a is off", A, e => $"{e.Out("out")} = n(not b({e.In("a")}))");
            Add("compare", "on when `a op b` holds; op is < > <= >= = !=", AB, e =>
            {
                if (!compareOps.TryGetValue(e.Setting("op"), out var op))
                    throw new GraphException($"line {e.Node.Line}: node {e.Node.Id} compare op={e.Setting("op")}; use < > <= >= = or !=");
                return $"{e.Out("out")} = n({e.In("a")} {op} {e.In("b")})";
            }, "op=>");
            Add("switch", "a when `if` is on, else b", new[] { "if", "a", "b" }, e => $"{e.Out("out")} = b({e.In("if")}) and {e.In("a")} or {e.In("b")}");

            category = "Maths";
            Add("add", "a + b", AB, e => $"{e.Out("out")} = {e.In("a")} + {e.In("b")}");
            Add("subtract", "a - b", AB, e => $"{e.Out("out")} = {e.In("a")} - {e.In("b")}");
            Add("multiply", "a * b", AB, e => $"{e.Out("out")} = {e.In("a")} * {e.In("b")}");
            Add("divide", "a / b, or 0 when b is 0", AB, e => $"if {e.In("b")} ~= 0 then {e.Out("out")} = {e.In("a")} / {e.In("b")} else {e.Out("out")} = 0 end");
            Add("min", "the smaller of a and b", AB, e => $"{e.Out("out")} = math.min({e.In("a")}, {e.In("b")})");
            Add("max", "the larger of a and b", AB, e => $"{e.Out("out")} = math.max({e.In("a")}, {e.In("b")})");
            Add("clamp", "x kept between lo and hi", new[] { "x", "lo", "hi" }, e => $"{e.Out("out")} = math.max({e.In("lo")}, math.min({e.In("hi")}, {e.In("x")}))");
            Types["clamp"].InDefaults["hi"] = 1;
            Add("abs", "a without its sign", A, e => $"{e.Out("out")} = math.abs({e.In("a")})");
            Types["multiply"].InDefaults["b"] = 1;
            Types["divide"].InDefaults["b"] = 1;

            // The game's own logic blocks, exactly (LogicConverter.cs turns a ship's logic into these).
            Add("gate", "the game's logic gate with any number of inputs: op OR (the highest), AND (the lowest), XOR, NOR, NAND, XNOR " +
                        "(1 minus OR / AND / XOR), or LATEST (whichever input changed last, as a block fed by several links)",
                None, GateCode, "op=OR", "inputs=2");
            Types["gate"].InsOf = n => LuaPorts(n, "inputs", "in");
            Add("delay", "the game's timer gate: mode none passes each change of `in` on after `seconds`; mode repeat sends a one-tick " +
                         "pulse every `seconds` while `in` is on; mode toggle flips on and off every `seconds` while `in` is on", In1,
                e =>
                {
                    string i = e.In("in"), st = e.State, o = e.Out("out"), secs = $"math.max(0.1, {e.Num("seconds")})";
                    switch (e.Setting("mode"))
                    {
                        case "none":
                            return $"do local q = {st}.q or {{}}; {st}.q = q; {st}.t = ({st}.t or 0) + dt; " +
                                   $"if {st}.last ~= {i} then {st}.last = {i}; q[#q + 1] = {{ {st}.t + {secs}, {i} }} end; " +
                                   $"while q[1] and q[1][1] <= {st}.t do {o} = q[1][2]; table.remove(q, 1) end end";
                        case "repeat":
                            // Timed from the tick it turned on, as the game times from activation.
                            return $"if b({i}) then if {st}.on then {st}.t = {st}.t + dt else {st}.on = true; {st}.t = 0 end; " +
                                   $"if {st}.t >= {secs} then {st}.t = {st}.t - {secs}; {o} = 1 else {o} = 0 end " +
                                   $"else {st}.on = false; {o} = 0 end";
                        case "toggle":
                            return $"if b({i}) then if {st}.on then {st}.t = {st}.t + dt else {st}.on = true; {st}.t = 0; {st}.p = 1 end; " +
                                   $"if {st}.t >= {secs} then {st}.t = {st}.t - {secs}; {o} = {st}.p; {st}.p = 1 - {st}.p end " +
                                   $"else {st}.on = false end";
                        default: throw new GraphException($"line {e.Node.Line}: node {e.Node.Id} delay mode={e.Setting("mode")}; use none, repeat or toggle");
                    }
                }, "mode=none", "seconds=1");
            Types["delay"].Category = "Logic";

            category = "Time and memory";
            Add("timer", "mode=on: on once `in` has been on for `seconds`; mode=off: stays on for `seconds` after `in` goes off", In1, e =>
            {
                string i = e.In("in"), s = e.State, o = e.Out("out"), secs = e.Num("seconds");
                switch (e.Setting("mode"))
                {
                    case "on": return $"if b({i}) then {s}.t = ({s}.t or 0) + dt else {s}.t = 0 end; {o} = n(b({i}) and {s}.t >= {secs})";
                    case "off": return $"if b({i}) then {s}.t = 0 else {s}.t = ({s}.t or math.huge) + dt end; {o} = n({s}.t < {secs})";
                    default: throw new GraphException($"line {e.Node.Line}: node {e.Node.Id} timer mode={e.Setting("mode")}; use on or off");
                }
            }, "mode=on", "seconds=1");
            Add("pulse", "on for one tick when `in` turns on", In1,
                e => $"do local x = b({e.In("in")}); {e.Out("out")} = n(x and not {e.State}.last); {e.State}.last = x end");
            Add("toggle", "flips on or off each time `in` turns on", In1,
                e => $"do local x = b({e.In("in")}); if x and not {e.State}.last then {e.State}.on = not {e.State}.on end; {e.State}.last = x; {e.Out("out")} = n({e.State}.on) end");
            Add("latch", "turns on when `set` is on, off when `reset` is on (reset wins)", new[] { "set", "reset" },
                e => $"if b({e.In("reset")}) then {e.State}.on = false elseif b({e.In("set")}) then {e.State}.on = true end; {e.Out("out")} = n({e.State}.on)");
            Add("counter", "counts each time `up` turns on; `reset` sets it back to 0", new[] { "up", "reset" },
                e => $"do local x = b({e.In("up")}); if b({e.In("reset")}) then {e.State}.c = 0 elseif x and not {e.State}.last then {e.State}.c = ({e.State}.c or 0) + 1 end; {e.State}.last = x; {e.Out("out")} = {e.State}.c or 0 end");

            category = "Ship";
            Add("ship", "one of the ship's values: " + string.Join(", ", shipNumbers.Concat(shipFlags).ToArray()), None, e =>
            {
                var v = e.Setting("value");
                if (shipNumbers.Contains(v)) return $"{e.Out("out")} = ship.{v}";
                if (shipFlags.Contains(v)) return $"{e.Out("out")} = n(ship.{v})";
                throw new GraphException($"line {e.Node.Line}: node {e.Node.Id} ship value={v} isn't one of {string.Join(", ", shipNumbers.Concat(shipFlags).ToArray())}");
            }, "value=speed");
            Add("block", "a block found by its Tag label: its state, or the readout named by `value`; 0 if it's not there", None, e =>
            {
                var read = e.Setting("value") == "state" ? "x.state" : $"(x.read({e.Str("value")}) or 0)";
                return $"do local x = ship.block({e.Str("label")}); {e.Out("out")} = x and {read} or 0 end";
            }, "label", "value=state");
            Add("set block", "sets the block with Tag label `label` to `in` (only when it changes)", In1,
                e => $"if {e.State}.sent ~= {e.In("in")} then local x = ship.block({e.Str("label")}); if x then x.set({e.In("in")}); {e.State}.sent = {e.In("in")} end end", "label");
            Types["set block"].Outs = None;
            Types["setblock"] = Types["set block"];
            Types["set_block"] = Types["set block"];

            // Keyboard and joysticks (Controls.cs).
            category = "Controls";
            Add("key", "1 while the key `key` is held (nothing counts while a menu is open)", None,
                e => $"{e.Out("out")} = n(keys.down({e.Str("key")}))", "key=space");
            Add("joystick axis", "axis `axis` of joystick `stick`, -1 to 1", None,
                e => $"{e.Out("out")} = joystick.axis({e.Num("stick")}, {e.Num("axis")})", "stick=1", "axis=1");
            Add("player look", "where you're looking, in degrees from the ship's forward (yaw + right, pitch + up), and whether you're seated in this ship", None,
                e => $"do local y, p = player.look(); {e.Out("yaw")} = y; {e.Out("pitch")} = p; {e.Out("seated")} = n(player.seated) end");
            Types["player look"].Outs = new[] { "yaw", "pitch", "seated" };
            Add("joystick button", "1 while button `button` of joystick `stick` is held", None,
                e => $"{e.Out("out")} = n(joystick.button({e.Num("stick")}, {e.Num("button")}))", "stick=1", "button=1");

            // Named ports (Ports.cs): the graph declares every port these nodes use.
            category = "Links";
            Add("port in", "the value of the computer's input port `port` (link a block into the port with the link tool or the Links tab)", None,
                e => $"{e.Out("out")} = inputs.get({e.Str("port")})", "port");
            Add("port out", "sends its input out of the computer's output port `port` (only when it changes)", In1,
                e => $"if {e.State}.sent ~= {e.In("in")} then {e.State}.sent = {e.In("in")}; outputs.set({e.Str("port")}, {e.In("in")}) end", "port");
            Types["port out"].Outs = None;
            Add("send", "sends its input to the computer at the other end of channel `port` (only when it changes)", In1,
                e => $"if {e.State}.sent ~= {e.In("in")} then {e.State}.sent = {e.In("in")}; ports.send({e.Str("port")}, {e.In("in")}) end", "port");
            Types["send"].Outs = None;
            Add("receive", "the last number that came in on channel `port` (0 until one does)", None,
                e => $"{e.Out("out")} = num(ports.last({e.Str("port")}))", "port");

            category = "Script";
            Add("lua", "runs a piece of Lua inside the graph, like a little computer of its own: its inputs in1, in2... are " +
                       "inputs.get(1), inputs.get(2)..., and outputs.set(1, v) sets out1; tick(dt) and on_input(i, value) work as in a " +
                       "script, and so do ship, display, anim and the rest. Select it to edit its code", None, LuaNodeTick, "inputs=1", "outputs=1");
            Types["lua"].Outs = None;
            Types["lua"].InsOf = n => LuaPorts(n, "inputs", "in");
            Types["lua"].OutsOf = n => LuaPorts(n, "outputs", "out");

            const string onOff = "counts as on above 0.5";
            Doc("input", "index", "which link in (1 = the first one linked)", "out", "that link's value: 0 to 1, or 1/0 for on/off blocks");
            Doc("output", "index", "which link out (1 = the first one linked)", "in", "the value to send");
            Doc("constant", "value", "the number it gives", "out", "value, always");
            foreach (var gate in new[] { "and", "or", "xor" }) Doc(gate, "a", onOff, "b", onOff, "out", "1 or 0");
            Doc("not", "a", onOff, "out", "1 when a is off, else 0");
            Doc("compare", "a", "the left side", "b", "the right side", "op", "< > <= >= = or !=", "out", "1 when `a op b` holds, else 0");
            Doc("switch", "if", "picks a when on (" + onOff + "), else b", "a", "passed on while `if` is on", "b", "passed on while `if` is off", "out", "a or b");
            foreach (var m in new[] { "add", "subtract", "multiply", "divide", "min", "max" }) Doc(m, "a", "the first number", "b", "the second number", "out", "the result");
            Doc("clamp", "x", "the number to limit", "lo", "the lowest it may be", "hi", "the highest it may be", "out", "x, kept between lo and hi");
            Doc("abs", "a", "any number", "out", "a without its sign");
            Doc("timer", "in", onOff, "mode", "on: wait before turning on; off: stay on after `in` goes off", "seconds", "how long", "out", "1 or 0");
            Doc("pulse", "in", onOff, "out", "1 for the one tick when `in` turns on, else 0");
            Doc("toggle", "in", "each time it turns on (" + onOff + "), the output flips", "out", "1 or 0");
            Doc("latch", "set", "turns it on", "reset", "turns it off (wins over set)", "out", "1 or 0");
            Doc("counter", "up", "adds one each time it turns on", "reset", "sets the count back to 0", "out", "the count");
            Doc("ship", "value", "which: " + string.Join(", ", shipNumbers.Concat(shipFlags).ToArray()), "out", "that value (levels 0 to 1; flags 1 or 0)");
            Doc("block", "label", "the block's Tag label", "value", "state, or the name of one of its readouts", "out", "the value, or 0 when there's no such block");
            Doc("set block", "label", "the block's Tag label", "in", "the value to set it to");
            Doc("key", "key", "a key name: a letter, a digit, space, shift, ctrl, alt, enter, up, f1, keypad5, mouse0...", "out", "1 while it's held, else 0 (0 while a menu is open)");
            Doc("joystick axis", "stick", "which stick or pad, 1 to 4", "axis", "which axis, 1 to 28 (the game's own numbering)", "out", "-1 to 1");
            Doc("player look", "yaw", "degrees right (+) or left (-) of the ship's forward", "pitch", "degrees up (+) or down (-)",
                "seated", "1 while you're sitting in a seat of this ship");
            Doc("joystick button", "stick", "which stick or pad, 1 to 4", "button", "which button, 1 to 20", "out", "1 while it's held, else 0");
            Doc("port in", "port", "the input port's name", "out", "its value: the linked block's state, or the readout picked for it");
            Doc("port out", "port", "the output port's name", "in", "the value to send to the block in that port");
            Doc("send", "port", "the channel's name", "in", "the number to send to the other computer");
            Doc("receive", "port", "the channel's name", "out", "the last number the other computer sent, 0 before any");
            Doc("gate", "op", "OR, AND, XOR, NOR, NAND, XNOR or LATEST", "inputs", "how many inputs (in1, in2...; up to 16)", "out", "the result, 0 to 1 like the game's gates");
            Doc("delay", "in", "the signal", "mode", "none, repeat or toggle (the game's timer gate modes)", "seconds", "the delay or repeat time (at least 0.1)", "out", "the delayed signal, the pulses, or the blinking");
            Doc("lua", "inputs", "how many inputs (in1, in2...; up to 16); the code reads in1 as inputs.get(1)",
                "outputs", "how many outputs (out1, out2...); the code sets out1 with outputs.set(1, value)");
        }

        // The game's gate maths (CommandLogic): OR the highest input, AND the lowest (0 with none), XOR folded as
        // max(min(x, 1 - v), min(1 - x, v)); NOR, NAND, XNOR are 1 minus those. LATEST passes on the input that changed
        // last, as a block fed by several links takes whichever signal reached it last.
        static string GateCode(Emit e)
        {
            var ins = e.Node.Ins.Select(e.In).ToArray();
            var o = e.Out("out");
            var op = e.Setting("op").ToUpperInvariant();
            if (ins.Length == 0) return $"{o} = {(op == "NOR" || op == "NAND" || op == "XNOR" ? "1" : "0")}";
            string fold;
            switch (op)
            {
                case "OR": case "NOR": fold = $"math.max({string.Join(", ", ins)})"; break;
                case "AND": case "NAND": fold = $"math.min({string.Join(", ", ins)})"; break;
                case "XOR": case "XNOR":
                    fold = "0";
                    foreach (var v in ins) fold = $"math.max(math.min({fold}, 1 - {v}), math.min(1 - {fold}, {v}))";
                    break;
                case "LATEST":
                {
                    var st = e.State;
                    var sb = new StringBuilder($"do local l = {st}.l or {{}}; {st}.l = l; ");
                    for (int k = 0; k < ins.Length; k++)
                        sb.Append($"if l[{k + 1}] ~= {ins[k]} then l[{k + 1}] = {ins[k]}; {st}.v = {ins[k]} end; ");
                    return sb.Append($"{o} = {st}.v or 0 end").ToString();
                }
                default: throw new GraphException($"line {e.Node.Line}: node {e.Node.Id} gate op={op}; use OR, AND, XOR, NOR, NAND, XNOR or LATEST");
            }
            return op == "NOR" || op == "NAND" || op == "XNOR" ? $"{o} = 1 - {fold}" : $"{o} = {fold}";
        }

        // ---- The lua node: a script inside the graph ----
        // Its code runs once when the graph starts, in an environment of its own (_ENV), so its tick, on_input, rate
        // and other globals don't touch the graph's or another lua node's; everything else (ship, display, anim, ui,
        // print...) is the script's as usual. There `inputs` and `outputs` are the node's own ports: in1.. read as
        // inputs.get(1).., out1.. set with outputs.set(1, v).. (name(i) and block(i) still answer for the computer's
        // own link i). Each graph tick it gets on_input(i, v) for every input that changed, then tick(dt); its
        // outputs go onto their wires. Its `rate` is the graph's.
        public const int MaxLuaPorts = 16;

        static string[] LuaPorts(Node n, string setting, string prefix)
        {
            var raw = n.Settings.TryGetValue(setting, out var v) ? v : n.Type.Settings[setting];
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);
            count = Mathf.Clamp(count, 0, MaxLuaPorts);
            var ports = new string[count];
            for (int i = 0; i < count; i++) ports[i] = prefix + (i + 1);
            return ports;
        }

        internal const string LuaNodeExample =
            "-- inputs.get(1) is in1; outputs.set(1, value) sets out1.\n" +
            "function on_input(i, value)\n" +
            "  outputs.set(i, value)\n" +
            "end\n";

        // Set up once, before the graph's tick: the node's environment, with its code run in it.
        static string LuaNodeSetup(Node node)
        {
            int ins = node.Ins.Length, outs = node.Outs.Length, id = node.Id;
            var code = (node.Script ?? "").Replace("\r", "");
            return
$@"L[{id}] = {{ ins = {{}}, last = {{}}, outs = {{}} }}
do
  local node, real_in, real_out = L[{id}], inputs, outputs
  local env = setmetatable({{}}, {{ __index = _ENV }})
  env.inputs = {{ count = {ins},
    get = function(i) return node.ins[i] or 0 end, on = function(i) return (node.ins[i] or 0) > 0.5 end,
    name = function(i) return real_in and real_in.name(i) or """" end, block = function(i) return real_in and real_in.block(i) end }}
  env.outputs = {{ count = {outs},
    get = function(i) return node.outs[i] or 0 end, on = function(i) return (node.outs[i] or 0) > 0.5 end,
    set = function(i, v) if i >= 1 and i <= {outs} then node.outs[i] = num(v) end end,
    all = function(v) for i = 1, {outs} do node.outs[i] = num(v) end end,
    name = function(i) return real_out and real_out.name(i) or """" end, block = function(i) return real_out and real_out.block(i) end }}
  local _ENV = env
  ;(function()
{code}
  end)()
  node.tick, node.on_input = rawget(env, ""tick""), rawget(env, ""on_input"")
end";
        }

        // Each tick: changed inputs to on_input, then tick, then the outputs onto their wires.
        static string LuaNodeTick(Emit e)
        {
            var n = e.Node;
            var sb = new StringBuilder($"do local node = L[{n.Id}]; ");
            var ins = n.Ins;
            for (int i = 1; i <= ins.Length; i++)
                sb.Append($"local v{i} = {e.In(ins[i - 1])}; if v{i} ~= node.last[{i}] then node.ins[{i}] = v{i}; node.last[{i}] = v{i}; if node.on_input then node.on_input({i}, v{i}) end end; ");
            sb.Append("if node.tick then node.tick(dt) end; ");
            var outs = n.Outs;
            for (int i = 1; i <= outs.Length; i++) sb.Append($"{e.Out(outs[i - 1])} = node.outs[{i}] or 0; ");
            return sb.Append("end").ToString();
        }

        // A lua node's code is checked when the graph compiles, so a typo points at the node and its line.
        static void CheckLuaNode(Node node)
        {
            try { new MoonSharp.Interpreter.Script(MoonSharp.Interpreter.CoreModules.Preset_SoftSandbox).LoadString(node.Script ?? "", null, "lua node " + node.Id); }
            catch (MoonSharp.Interpreter.SyntaxErrorException ex)
            {
                throw new GraphException($"node {node.Id} (lua): {ex.DecoratedMessage ?? ex.Message}");
            }
        }

        static void Doc(string node, params string[] nameThenDoc)
        {
            for (int i = 0; i + 1 < nameThenDoc.Length; i += 2) Types[node].Docs[nameThenDoc[i]] = nameThenDoc[i + 1];
        }

        // ---- Reading a .graph file ----

        public class Graph
        {
            public double Rate = 0.1;
            public readonly SortedDictionary<int, Node> Nodes = new SortedDictionary<int, Node>();
            public readonly List<(int fromNode, string fromPort, int toNode, string toPort, int line)> Wires = new List<(int, string, int, string, int)>();
            public readonly List<Comment> Comments = new List<Comment>();
        }

        // A labelled box behind nodes on the canvas, for grouping and notes. It doesn't run.
        public class Comment
        {
            public float X, Y, W = 300, H = 160;
            public string Text = "comment";
        }

        public static Graph Parse(string text)
        {
            var g = new Graph();
            var lines = text.Replace("\r", "").Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                var raw = lines[index];
                var lineNo = index + 1;
                var words = Words(raw, lineNo);
                if (words.Count == 0) continue;
                switch (words[0].ToLowerInvariant())
                {
                    case "rate":
                        if (words.Count != 2 || !double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g.Rate))
                            throw new GraphException($"line {lineNo}: write rate <seconds>, e.g. rate 0.1");
                        break;
                    case "node":
                        if (words.Count < 3 || !int.TryParse(words[1], out var id))
                            throw new GraphException($"line {lineNo}: write node <number> <type> [setting=value ...]");
                        // A type can be two words ("set block"): take words until the first setting.
                        var typeWords = words.Skip(2).TakeWhile(w => !w.Contains("=")).ToList();
                        var typeName = string.Join(" ", typeWords.ToArray());
                        if (!Types.TryGetValue(typeName, out var type))
                            throw new GraphException($"line {lineNo}: no node type \"{typeName}\"; known: {string.Join(", ", Types.Values.Distinct().Select(t => t.Name).ToArray())}");
                        if (g.Nodes.ContainsKey(id)) throw new GraphException($"line {lineNo}: node {id} is defined twice");
                        var node = new Node { Id = id, Line = lineNo, Type = type };
                        foreach (var w in words.Skip(2 + typeWords.Count))
                        {
                            var eq = w.IndexOf('=');
                            if (eq <= 0) throw new GraphException($"line {lineNo}: \"{w}\" should be setting=value");
                            var key = w.Substring(0, eq).ToLowerInvariant();
                            // The lua node's port counts were ins= and outs= in 0.9.48.
                            if (type.Name == "lua" && key == "ins") key = "inputs";
                            if (type.Name == "lua" && key == "outs") key = "outputs";
                            if (key == "at")
                            {
                                var xy = w.Substring(eq + 1).Split(',');
                                if (xy.Length != 2 || !float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out node.X) ||
                                    !float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out node.Y))
                                    throw new GraphException($"line {lineNo}: at={w.Substring(eq + 1)} should be at=x,y");
                                node.Placed = true;
                                continue;
                            }
                            if (!type.Settings.ContainsKey(key) && !type.Ins.Contains(key) && !(type.InsOf != null && Regex.IsMatch(key, @"^in\d+$")))
                                throw new GraphException($"line {lineNo}: {type.Name} has no setting \"{key}\"" + Known(type));
                            node.Settings[key] = w.Substring(eq + 1);
                        }
                        foreach (var required in type.Settings.Where(s => s.Value == "" && !node.Settings.ContainsKey(s.Key)))
                            throw new GraphException($"line {lineNo}: {type.Name} needs {required.Key}=...");
                        g.Nodes[id] = node;
                        break;
                    case "wire":
                        var ends = words.Skip(1).Where(w => w != "->").ToList();
                        if (ends.Count != 2) throw new GraphException($"line {lineNo}: write wire <node>.<output> <node>.<input>");
                        var from = End(ends[0], lineNo);
                        var to = End(ends[1], lineNo);
                        g.Wires.Add((from.node, from.port, to.node, to.port, lineNo));
                        break;
                    case "code":
                        // code <node>, then the node's Lua line by line, then a line `end code`.
                        if (words.Count != 2 || !int.TryParse(words[1], out var codeId) || !g.Nodes.TryGetValue(codeId, out var codeNode))
                            throw new GraphException($"line {lineNo}: write code <node> after that node's line, then its Lua, then end code");
                        var body = new List<string>();
                        while (++index < lines.Length && lines[index] != "end code") body.Add(lines[index]);
                        if (index >= lines.Length) throw new GraphException($"line {lineNo}: node {codeId}'s code has no `end code` line");
                        codeNode.Script = string.Join("\n", body.ToArray());
                        break;
                    case "comment":
                        // comment at=x,y size=w,h text="..."
                        var comment = new Comment();
                        foreach (var w in words.Skip(1))
                        {
                            var eq = w.IndexOf('=');
                            if (eq <= 0) throw new GraphException($"line {lineNo}: \"{w}\" should be at=x,y, size=w,h or text=\"...\"");
                            var key = w.Substring(0, eq).ToLowerInvariant();
                            var value = w.Substring(eq + 1);
                            if (key == "text") { comment.Text = value; continue; }
                            var xy = value.Split(',');
                            if (xy.Length != 2 || !float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ||
                                !float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                                throw new GraphException($"line {lineNo}: {key}={value} should be two numbers, like {key}=10,20");
                            if (key == "at") { comment.X = a; comment.Y = b; }
                            else if (key == "size") { comment.W = Mathf.Max(80, a); comment.H = Mathf.Max(50, b); }
                            else throw new GraphException($"line {lineNo}: a comment has at=, size= and text=, not {key}=");
                        }
                        g.Comments.Add(comment);
                        break;
                    default:
                        throw new GraphException($"line {lineNo}: \"{words[0]}\"? A line starts with node, wire, comment or rate");
                }
            }
            return g;
        }

        static string Known(NodeType t)
        {
            var all = t.Settings.Keys.Concat(t.Ins).ToArray();
            return all.Length > 0 ? $" (it has {string.Join(", ", all)})" : " (it has none)";
        }

        static (int node, string port) End(string word, int line)
        {
            var dot = word.IndexOf('.');
            if (dot <= 0 || !int.TryParse(word.Substring(0, dot), out var node))
                throw new GraphException($"line {line}: \"{word}\" should be <node>.<port>, e.g. 3.out");
            return (node, word.Substring(dot + 1).ToLowerInvariant());
        }

        // Splits a line into words; "double quotes" keep spaces, # starts a comment outside quotes.
        static List<string> Words(string line, int lineNo)
        {
            var words = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false, any = false;
            foreach (var ch in line)
            {
                if (ch == '"') { quoted = !quoted; any = true; continue; }
                if (!quoted && ch == '#') break;
                if (!quoted && char.IsWhiteSpace(ch))
                {
                    if (any) words.Add(sb.ToString());
                    sb.Length = 0; any = false;
                    continue;
                }
                sb.Append(ch); any = true;
            }
            if (quoted) throw new GraphException($"line {lineNo}: a \" isn't closed");
            if (any) words.Add(sb.ToString());
            return words;
        }

        // ---- Compiling to Lua ----

        public static string Compile(string text, string name)
        {
            var g = Parse(text);
            if (g.Nodes.Count == 0) throw new GraphException("the graph has no nodes");

            // Check the wires and note what feeds each input.
            var wiredFrom = g.Nodes.Keys.ToDictionary(id => id, id => new Dictionary<string, string>());
            foreach (var w in g.Wires)
            {
                if (!g.Nodes.TryGetValue(w.fromNode, out var from)) throw new GraphException($"line {w.line}: there's no node {w.fromNode}");
                if (!g.Nodes.TryGetValue(w.toNode, out var to)) throw new GraphException($"line {w.line}: there's no node {w.toNode}");
                if (!from.Outs.Contains(w.fromPort))
                    throw new GraphException($"line {w.line}: {from.Type.Name} (node {from.Id}) has no output \"{w.fromPort}\"" + (from.Outs.Length > 0 ? $"; it has {string.Join(", ", from.Outs)}" : "; it has none"));
                if (!to.Ins.Contains(w.toPort))
                    throw new GraphException($"line {w.line}: {to.Type.Name} (node {to.Id}) has no input \"{w.toPort}\"" + (to.Ins.Length > 0 ? $"; it has {string.Join(", ", to.Ins)}" : "; it has none"));
                if (wiredFrom[w.toNode].ContainsKey(w.toPort))
                    throw new GraphException($"line {w.line}: {w.toNode}.{w.toPort} already has a wire into it; an input takes one");
                wiredFrom[w.toNode][w.toPort] = $"{w.fromNode}.{w.fromPort}";
            }

            var order = Order(g);
            var lua = new StringBuilder();
            lua.AppendLine($"-- Made from {name}.graph by EvoCore. Don't edit this: change the graph and save it.");
            lua.AppendLine($"rate = {Lit(g.Rate)}");
            lua.AppendLine("V = {}  -- every wire's value, by \"node.port\"; kept between ticks, so a wire back into an earlier node reads last tick's");
            lua.AppendLine("S = {}  -- each node's own memory (timers, latches, counters)");
            lua.AppendLine("local function b(x) return x > 0.5 end           -- is this value on?");
            lua.AppendLine("local function n(c) if c then return 1 end return 0 end  -- on/off as 1/0");
            // Lua-written nodes this graph uses: each file's code once, called by every node of that type.
            var custom = g.Nodes.Values.Select(x => x.Type).Where(t => t.Source != null).Distinct().ToList();
            var scripts = g.Nodes.Values.Where(x => x.Type.Name == "lua" && x.Type.Source == null).ToList();
            foreach (var x in scripts) CheckLuaNode(x);
            lua.AppendLine("local function num(x) if x == true then return 1 elseif not x then return 0 end return tonumber(x) or 0 end");
            string Names(params string[] types) => string.Join(", ", g.Nodes.Values.Where(x => types.Contains(x.Type.Name))
                .Select(x => x.Settings.TryGetValue("port", out var v) ? v : "").Where(v => v != "").Distinct().Select(LuaString).ToArray());
            var portIns = Names("port in");
            var portOuts = Names("port out");
            var channels = Names("send", "receive");
            if (portIns.Length + portOuts.Length + channels.Length > 0)
                lua.AppendLine($"if ports then ports.declare{{ inputs = {{ {portIns} }}, outputs = {{ {portOuts} }}, channels = {{ {channels} }} }} end");
            if (scripts.Count > 0)
            {
                lua.AppendLine("L = {}  -- lua nodes: each one's ports, and its tick and on_input");
                foreach (var x in scripts) lua.AppendLine(LuaNodeSetup(x));
            }
            if (custom.Count > 0)
            {
                lua.AppendLine("N = {}  -- Lua-written nodes, from the nodes folder");
                foreach (var t in custom)
                    lua.AppendLine($"N[{LuaString(t.Name)}] = (function()\n{t.Source}\nend)()");
            }
            foreach (var node in g.Nodes.Values)
            {
                lua.Append($"S[{node.Id}] = {{}}");
                foreach (var port in node.Outs) lua.Append($"; V[\"{node.Id}.{port}\"] = 0");
                lua.AppendLine();
            }
            lua.AppendLine("function tick(dt)");
            foreach (var node in order)
            {
                var settings = string.Join(" ", node.Settings.Select(s => $"{s.Key}={s.Value}").ToArray());
                lua.AppendLine($"  -- node {node.Id}: {node.Type.Name}{(settings.Length > 0 ? " " + settings : "")}");
                lua.AppendLine("  " + node.Type.Code(new Emit { Node = node, wiredFrom = wiredFrom[node.Id] }));
            }
            lua.AppendLine("end");
            return lua.ToString();
        }

        // Each node after the nodes that feed it, lowest number first among equals. If wires form a loop, the lowest
        // numbered node still waiting goes next; its wires from later nodes read last tick's values.
        static List<Node> Order(Graph g)
        {
            var feeds = g.Nodes.Keys.ToDictionary(id => id, id => new HashSet<int>());
            foreach (var w in g.Wires) if (w.fromNode != w.toNode) feeds[w.toNode].Add(w.fromNode);
            var done = new HashSet<int>();
            var order = new List<Node>();
            while (order.Count < g.Nodes.Count)
            {
                var waiting = g.Nodes.Keys.Where(id => !done.Contains(id)).ToList();
                var next = waiting.Where(id => feeds[id].All(done.Contains)).DefaultIfEmpty(waiting[0]).First();
                done.Add(next);
                order.Add(g.Nodes[next]);
            }
            return order;
        }

        // A graph as .graph text, as the canvas saves it: rate, nodes in id order with their place, then wires.
        // Comments in a hand-written file aren't kept.
        public static string Write(Graph g, string name)
        {
            var sb = new StringBuilder();
            sb.Append($"# {name}.graph, saved by the graph editor\n");
            sb.Append($"rate {Lit(g.Rate)}\n");
            foreach (var node in g.Nodes.Values)
            {
                sb.Append($"node {node.Id} {node.Type.Name}");
                foreach (var s in node.Settings) sb.Append($" {s.Key}={Quote(s.Value)}");
                sb.Append($" at={Mathf.Round(node.X).ToString(CultureInfo.InvariantCulture)},{Mathf.Round(node.Y).ToString(CultureInfo.InvariantCulture)}\n");
                if (node.Script != null) sb.Append($"code {node.Id}\n{node.Script.Replace("\r", "").TrimEnd('\n')}\nend code\n");
            }
            foreach (var w in g.Wires) sb.Append($"wire {w.fromNode}.{w.fromPort} {w.toNode}.{w.toPort}\n");
            string f(float v) => Mathf.Round(v).ToString(CultureInfo.InvariantCulture);
            foreach (var c in g.Comments) sb.Append($"comment at={f(c.X)},{f(c.Y)} size={f(c.W)},{f(c.H)} text={Quote(c.Text)}\n");
            return sb.ToString();
        }

        // Values with spaces or # go in quotes; a quote can't be written inside one, so it's dropped.
        static string Quote(string v)
        {
            v = (v ?? "").Replace("\"", "");
            return v == "" || v.Any(c => char.IsWhiteSpace(c) || c == '#') ? "\"" + v + "\"" : v;
        }

        // DevBridge: what scripts\<name>.graph compiles to, or why it doesn't.
        public static string ShowLua(string name)
        {
            var path = Path.Combine(Lua.ScriptsDir, name + ".graph");
            if (!File.Exists(path)) return "no such file: " + path;
            try { return Compile(File.ReadAllText(path), name); }
            catch (GraphException e) { return $"{name}.graph: {e.Message}"; }
        }

        // DevBridge: every node type, its inputs, outputs, settings and what it does.
        public static string NodeList() => string.Join("\n", Types.Values.Distinct().Select(t =>
            $"{t.Name}: {t.Help}. in [{string.Join(", ", t.Ins)}] out [{string.Join(", ", t.Outs)}]" +
            (t.Settings.Count > 0 ? " settings " + string.Join(", ", t.Settings.Select(s => s.Value == "" ? s.Key : $"{s.Key}={s.Value}").ToArray()) : "")).ToArray());
    }
}
