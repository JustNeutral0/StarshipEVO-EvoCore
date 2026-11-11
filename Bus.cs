using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Ship buses (the plan in the Visual scripting notes, "Typed wires, bundles and ship buses"): named sets of values
    // that any computer on a ship can publish to and read, with no links, like a car's CAN bus. The ship's mainframe
    // hosts them: with no mainframe on the ship that's running and powered, the buses are quiet (reads give nothing,
    // writes are dropped) but keep what they held, so they come back with the power.
    // Values are plain copies (numbers, text, true/false, tables of them), as channel messages are, so no table is
    // shared between two scripts. Single-player for now: nothing here goes over the network.
    // Lua: `bus` on every computer's script:
    //   bus.ok / bus.why               whether this ship's buses work, and why not
    //   bus.set(name, field, value)    one value on a bus (nil removes it)
    //   bus.publish(name, table)       several at once: every key of the table
    //   bus.get(name, field)           one value, or nil
    //   bus.read(name)                 a copy of the whole bus as a table (empty when quiet)
    //   bus.list()                     the names of this ship's buses
    public static class Buses
    {
        public const int MaxFields = 64;       // per bus
        public const int MaxBuses = 32;        // per ship
        const int ValueBudget = 2000;          // plain values per write, as for channel messages

        static readonly Dictionary<ParentEntity, Dictionary<string, Dictionary<string, object>>> ships =
            new Dictionary<ParentEntity, Dictionary<string, Dictionary<string, object>>>();
        static readonly Dictionary<ParentEntity, string> why = new Dictionary<ParentEntity, string>();
        static int checkedFrame = -1;

        static bool testHost;   // SelfTest: buses work without a mainframe

        // Null when the ship's buses work, else why not. Worked out once a frame per ship.
        public static string Problem(ParentEntity ship)
        {
            if (ship == null) return "not on a ship";
            if (testHost) return null;
            if (checkedFrame != Time.frameCount) { why.Clear(); checkedFrame = Time.frameCount; }
            if (why.TryGetValue(ship, out var known)) return known;
            var mainframes = Mainframes.On(ship);
            string problem = null;
            if (mainframes.Count == 0) problem = "no Mainframe on this ship";
            else if (!mainframes.Any(m => !m.Unpowered && Lua.Runs(m))) problem = "the Mainframe is off or has no power";
            why[ship] = problem;
            return problem;
        }

        static Dictionary<string, object> Bus(ParentEntity ship, string name, bool make)
        {
            if (!ships.TryGetValue(ship, out var buses))
            {
                if (!make) return null;
                ships[ship] = buses = new Dictionary<string, Dictionary<string, object>>();
            }
            if (!buses.TryGetValue(name, out var bus))
            {
                if (!make) return null;
                if (buses.Count >= MaxBuses) throw new ScriptRuntimeException($"bus: this ship has {MaxBuses} buses already");
                buses[name] = bus = new Dictionary<string, object>();
            }
            return bus;
        }

        static void Set(ParentEntity ship, string name, string field, DynValue value)
        {
            var bus = Bus(ship, name, true);
            if (value.IsNil()) { bus.Remove(field); return; }
            if (!bus.ContainsKey(field) && bus.Count >= MaxFields) throw new ScriptRuntimeException($"bus {name}: {MaxFields} fields at most");
            var budget = ValueBudget;
            var plain = Lua.Plain(value, ref budget);
            if (plain != null) bus[field] = plain;
        }

        // Ships that are gone take their buses with them (from Mainframes.Tick, twice a second).
        internal static void Prune()
        {
            foreach (var gone in ships.Keys.Where(s => s == null || s.Equals(null)).ToList()) ships.Remove(gone);
        }

        // For DevBridge: every ship's buses and what's on them.
        public static string Report() => ships.Count == 0 ? "No buses." : string.Join("\n", ships.Select(s =>
            $"{s.Key?.EntityName}: {Problem(s.Key) ?? "ok"}\n" + string.Join("\n", s.Value.Select(b =>
                $"  {b.Key}: " + string.Join(", ", b.Value.Select(f => $"{f.Key}={Describe(f.Value)}").ToArray())).ToArray())).ToArray());

        static string Describe(object v) => v is double d ? d.ToString("0.###") : v is Dictionary<object, object> t ? $"{{{t.Count}}}" : v?.ToString();

        internal static void Expose() => Lua.Expose("bus", c => new BusApi(c));

        // For DevBridge: two scripts on one real ship, through the real Lua engine. First as things are (a ship
        // without a mainframe should say so), then with the mainframe check skipped, a round trip of every kind of
        // value. Leaves nothing behind.
        public static string SelfTest()
        {
            ParentEntity ship = null;
            foreach (var c in CommandManager.commands.Datas.OfType<LuaComputer>())
            {
                try { ship = c.brickCoord.entity?.Parent; } catch { }
                if (ship != null) break;
            }
            if (ship == null) return "no ship with a Lua Computer";
            Script Make()
            {
                var s = new Script(CoreModules.Preset_SoftSandbox);
                s.Globals["bus"] = new BusApi(new Lua.Context { Ship = ship, Name = "bus self-test" });
                return s;
            }
            var a = Make();
            var b = Make();
            var lines = new List<string> { $"ship {ship.EntityName}, mainframes {Mainframes.On(ship).Count}" };
            lines.Add("as is: " + a.DoString("return tostring(bus.ok) .. ' / ' .. bus.why .. ' / set gives ' .. tostring(bus.set('selftest', 'x', 1))").String);
            testHost = true;
            why.Clear();
            try
            {
                a.DoString("bus.publish('selftest', { fuel = 0.5, name = 'Fenrir', nav = { x = 1, y = 2 } }); bus.set('selftest', 'gear', true)");
                lines.Add("b reads: " + b.DoString(
                    "local t = bus.read('selftest'); return string.format('fuel %s name %s nav.x %s gear %s get %s missing %s', " +
                    "t.fuel, t.name, t.nav.x, tostring(t.gear), bus.get('selftest', 'name'), tostring(bus.get('selftest', 'nope')))").String);
                b.DoString("bus.set('selftest', 'fuel', nil)");
                lines.Add("after b clears fuel, a reads fuel " + a.DoString("return tostring(bus.get('selftest', 'fuel'))").String);
                lines.Add("list: " + a.DoString("return table.concat(bus.list(), ',')").String);
                lines.Add("a copy, not shared: " + b.DoString("local t = bus.read('selftest'); t.nav.x = 99; return tostring(bus.read('selftest').nav.x)").String);
            }
            catch (InterpreterException e) { lines.Add("ERROR " + (e.DecoratedMessage ?? e.Message)); }
            finally
            {
                testHost = false;
                why.Clear();
                if (ships.TryGetValue(ship, out var buses)) { buses.Remove("selftest"); if (buses.Count == 0) ships.Remove(ship); }
            }
            return string.Join("\n", lines.ToArray());
        }

        public class BusApi
        {
            readonly Lua.Context context;
            public BusApi(Lua.Context c) { context = c; }
            ParentEntity Ship => context.Ship;

            public bool ok => Problem(Ship) == null;
            public string why => Problem(Ship) ?? "";

            public bool set(string name, string field, DynValue value)
            {
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(field)) throw new ScriptRuntimeException("bus.set(name, field, value)");
                if (!ok) return false;
                Set(Ship, name, field, value);
                return true;
            }

            public bool publish(string name, Table values)
            {
                if (string.IsNullOrEmpty(name) || values == null) throw new ScriptRuntimeException("bus.publish(name, { field = value, ... })");
                if (!ok) return false;
                foreach (var pair in values.Pairs)
                    if (pair.Key.Type == DataType.String) Set(Ship, name, pair.Key.String, pair.Value);
                return true;
            }

            public DynValue get(ScriptExecutionContext ctx, string name, string field)
            {
                if (!ok || Ship == null) return DynValue.Nil;
                var bus = Bus(Ship, name ?? "", false);
                return bus != null && field != null && bus.TryGetValue(field, out var v) ? Lua.ToLua(v, ctx.GetScript()) : DynValue.Nil;
            }

            public Table read(ScriptExecutionContext ctx, string name)
            {
                var t = new Table(ctx.GetScript());
                if (!ok || Ship == null) return t;
                var bus = Bus(Ship, name ?? "", false);
                if (bus != null) foreach (var pair in bus) t[pair.Key] = Lua.ToLua(pair.Value, ctx.GetScript());
                return t;
            }

            public Table list(ScriptExecutionContext ctx)
            {
                var t = new Table(ctx.GetScript());
                if (Ship != null && ships.TryGetValue(Ship, out var buses))
                    foreach (var name in buses.Keys) t.Append(DynValue.NewString(name));
                return t;
            }
        }
    }
}
