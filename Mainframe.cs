using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // The ship's mainframe (the ship network plan in the Lua chips notes, first step): a big Lua Computer that runs the
    // ship's heavy program (by default scripts\mainframe.lua, the ship OS), with capacity that grows with Server Racks.
    //  - Capacity: Lua steps per second. A mainframe has BaseCapacity, each Server Rack on the ship adds RackCapacity
    //    (shared out when a ship has more than one mainframe). Load = the steps its program ran over the last second
    //    over its capacity; above 1 its program ticks that much slower (Lua.cs). One call may run StepLimit steps.
    //  - Power: it draws IdleDraw, plus RackDraw per rack, plus LoadDraw at full load, from the ship's power like any
    //    system (the electrolyser's way: EntitySystems.Power.UpdateFlowOut). With the ship's power stalled it stops
    //    ticking until power is back.
    //  - Terminals: a plain Lua Computer linked to a mainframe (either way) that has no program of its own runs
    //    nothing itself; its monitors join the mainframe's displays (after the mainframe's own), and using it opens
    //    the mainframe. Giving it a program (Script tab: Put on computer) makes it a board again.
    // Lua: `mainframe` (nil on other computers): racks, capacity, load, steps, draw, unpowered, terminals.
    public class Mainframe : LuaComputer
    {
        public static float BaseCapacity = 300000f, RackCapacity = 300000f;
        public static float IdleDraw = 5f, RackDraw = 3f, LoadDraw = 20f;

        public int Racks;
        public float Capacity = BaseCapacity, Load, Steps, Draw;
        public bool Unpowered;
        public int StepLimit => Lua.InstructionLimit * (1 + Racks);

        // The power flow it adds to (its ship's), so it can be taken back off the same one.
        internal FlowBalance DrawnFrom;
        internal float Drawing;

        protected override string DefaultScript => "mainframe";
        public override string OverTextIndirect => "Mainframe";
        public override string OverText => Lua.ComputerStatus(this) +
            $"\nMainframe: {Racks} rack(s), load {Load * 100f:0}% of {Capacity / 1000f:0}k steps/s, {Draw:0.#} e/s" + (Unpowered ? "\nNo power: stopped" : "");

        public override void OnDestroy(BrickUnspawnMode unspawnMode)
        {
            Mainframes.StopDrawing(this);
            base.OnDestroy(unspawnMode);
        }
    }

    // Adds capacity to the ship's mainframe. Passive: nothing to link, nothing to use.
    public class ServerRack : Command
    {
        protected override bool CanPropagate => false;
        public override string OverTextIndirect => "Server Rack";
        public override string OverText
        {
            get
            {
                var ship = brickCoord.entity != null ? brickCoord.entity.Parent : null;
                var mainframes = Mainframes.On(ship).Count;
                return mainframes == 0 ? "Server Rack: no mainframe on this ship" : $"Server Rack: adds capacity to this ship's mainframe{(mainframes > 1 ? "s" : "")}";
            }
        }
    }

    public static class Mainframes
    {
        static float next;
        static readonly List<Mainframe> all = new List<Mainframe>();
        static int cachedFrame = -1;
        static readonly Dictionary<LuaComputer, Mainframe> terminalCache = new Dictionary<LuaComputer, Mainframe>();

        // The mainframe a computer is a terminal of: a Lua Computer (not itself a mainframe) without its own program,
        // linked to or from a mainframe. Null for anything else. Cached per frame: displays ask on every draw call.
        public static Mainframe TerminalOf(LuaComputer c)
        {
            if (c == null || c is Mainframe || c is ExpansionSlot || c is Workstation || c is SalvageProcessor || c.HasProgram) return null;
            if (cachedFrame != Time.frameCount) { terminalCache.Clear(); cachedFrame = Time.frameCount; }
            if (terminalCache.TryGetValue(c, out var m)) return m;
            try { m = c.Inputs.Concat(c.Outputs).OfType<Mainframe>().FirstOrDefault(); }
            catch { m = null; }
            terminalCache[c] = m;
            return m;
        }

        // A mainframe's terminals, in link order (its outputs, then its inputs).
        public static List<LuaComputer> Terminals(Mainframe m) =>
            m.Outputs.Concat(m.Inputs).OfType<LuaComputer>().Where(c => TerminalOf(c) == m).Distinct().ToList();

        internal static string TerminalStatus(Mainframe m) =>
            "Terminal of " + (string.IsNullOrEmpty(m.ScriptName) ? "the mainframe" : $"the mainframe ({m.ScriptName})") +
            "\nUse it to open the mainframe. Put a program on it to make it its own computer again.";

        // Mainframes and racks on a ship, read the way Blocks.cs reads logic blocks (cached a second).
        internal static List<Mainframe> On(ParentEntity ship) =>
            ship == null ? new List<Mainframe>() : Lua.ShipCommands(ship).Select(f => f.Snapshot).OfType<Mainframe>().ToList();

        static int RacksOn(ParentEntity ship) =>
            ship == null ? 0 : Lua.ShipCommands(ship).Count(f => f.Snapshot is ServerRack);

        internal static void Tick()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            Buses.Prune();
            all.Clear();
            foreach (var command in CommandManager.commands.Datas)
            {
                // A block still being set up (while a save loads) has no entity yet, and asking throws.
                if (!(command is Mainframe m)) continue;
                try { if (m.brickCoord.entity != null) all.Add(m); }
                catch { }
            }
            foreach (var group in all.GroupBy(m => m.brickCoord.entity.Parent))
            {
                var ship = group.Key;
                var racks = RacksOn(ship);
                var count = group.Count();
                foreach (var m in group)
                {
                    try { Update(m, ship, racks / (float)count); }
                    catch (Exception e) { Core.Log.Error("Mainframe update failed: " + e.Message); }
                }
            }
        }

        static void Update(Mainframe m, ParentEntity ship, float racks)
        {
            m.Racks = Mathf.FloorToInt(racks);
            m.Capacity = Mainframe.BaseCapacity + Mainframe.RackCapacity * racks;
            m.Steps = (float)Lua.StepsPerSecond(m);
            m.Load = m.Capacity > 0f ? m.Steps / m.Capacity : 0f;
            var systems = ship != null ? ship.Systems : null;
            m.Unpowered = systems != null && systems.IsPowerDepleted;
            var running = Lua.Runs(m);
            m.Draw = running ? Mainframe.IdleDraw + Mainframe.RackDraw * racks + Mainframe.LoadDraw * Mathf.Min(m.Load, 2f) : 0f;
            var flow = systems != null ? systems.Power : null;
            if (m.DrawnFrom != flow) StopDrawing(m);
            if (flow == null) return;
            flow.UpdateFlowOut(ref m.Drawing, m.Draw);
            m.DrawnFrom = flow;
        }

        internal static void StopDrawing(Mainframe m)
        {
            if (m.DrawnFrom != null) m.DrawnFrom.UpdateFlowOut(ref m.Drawing, 0f);
            m.DrawnFrom = null;
            m.Drawing = 0f;
        }

        // For DevBridge: every mainframe and what it measures.
        public static string Report() => all.Count == 0 ? "No mainframes." : string.Join("\n", all.Select(m =>
            $"{m.brickCoord.entity?.Parent?.EntityName}: {m.ScriptName}, racks {m.Racks}, steps {m.Steps:0}/s of {m.Capacity:0}, load {m.Load:0.00}, " +
            $"draw {m.Draw:0.#} e/s{(m.Unpowered ? ", unpowered" : "")}, terminals {Terminals(m).Count}").ToArray());

        internal static void Expose() => Lua.Expose("mainframe", c => c.Host is Mainframe m ? new MainframeApi(m) : null);

        public class MainframeApi
        {
            readonly Mainframe m;
            public MainframeApi(Mainframe mainframe) { m = mainframe; }
            public int racks => m.Racks;
            public double capacity => m.Capacity;
            public double steps => m.Steps;
            public double load => m.Load;
            public double draw => m.Draw;
            public bool unpowered => m.Unpowered;
            public int terminals => Terminals(m).Count;
        }
    }

    static class MainframeBlocks
    {
        // 501 and 502 until 0.9.84, which are the game's Round Tile and Cone: every stock one became a mainframe or rack.
        public static readonly int MainframeId = Ids.Claim(IdSpace.Brick, "EvoCore.Mainframe", 512);
        public static readonly int RackId = Ids.Claim(IdSpace.Brick, "EvoCore.ServerRack", 513);

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class Register
        {
            // An exception here would stop every block from loading, so failures are logged and contained.
            static void Prefix()
            {
                try
                {
                    // For now they borrow stock looks: the Space Station Computer's, and the Sequencer Gate's.
                    var station = TabLogic.STATION_COMPUTER;
                    var mainframe = new CommandBrick<Mainframe>(MainframeId, "Mainframe", "gates/gate_station", ResizeMode.NoneBrick,
                        "Gates/gate_large_frame", AlphaMode.glowPaint, "Gates/gate_spaceship_screen", AlphaMode.holo);
                    Copy(station, mainframe);
                    mainframe.Desc("The ship's main computer: runs the ship OS (scripts\\mainframe.lua) on every monitor linked to it or to a Lua Computer linked to it. " +
                                   "Server Racks on the ship add capacity; it draws more power the harder it works.")
                             .Tab(UI.Codex.CodexTab.COMMANDS);

                    var sequencer = TabLogic.SEQUENCER;
                    var rack = new CommandBrick<ServerRack>(RackId, "Server Rack", "gates/gate_sequencer", ResizeMode.NoneBrick,
                        "Gates/gate_logic_frame", AlphaMode.glowPaint, "Gates/gate_query_screen", AlphaMode.holo);
                    Copy(sequencer, rack);
                    rack.Desc("Adds capacity to the ship's mainframe: its programs can do more each second before they slow down. Each rack adds a little power draw.")
                        .Tab(UI.Codex.CodexTab.COMMANDS);
                    Core.Log.Msg($"Registered Mainframe as brick {MainframeId} and Server Rack as brick {RackId}");
                }
                catch (Exception e) { Core.Log.Error("Mainframe registration failed: " + e); }
            }

            static void Copy(Brick stock, Brick brick)
            {
                brick.PreviewPaint = stock.PreviewPaint;
                brick.defaultPaint = stock.defaultPaint;
                brick.functionType = stock.functionType;
                brick.cost = stock.cost;
                brick.massForUnitShape = stock.massForUnitShape;
                brick.boundsOverride = stock.boundsOverride;
                brick.gridLevel = stock.gridLevel;
                brick.gridLockOffset = stock.gridLockOffset;
                brick.hasGlowPaint = stock.hasGlowPaint;
            }
        }

        // Materials are consumed from a baked table in ID order and spare entries are empty, so copy the stock
        // blocks' rows into ours (as the Lua Computer does).
        [HarmonyPatch(typeof(Brick), "LoadResourcesRuntime")]
        static class CopyMaterials
        {
            static void Prefix(Brick __instance)
            {
                Brick source;
                if (__instance.brickID == MainframeId && __instance == Brick.BRICK_INDEX[MainframeId]) source = TabLogic.STATION_COMPUTER;
                else if (__instance.brickID == RackId && __instance == Brick.BRICK_INDEX[RackId]) source = TabLogic.SEQUENCER;
                else return;
                try
                {
                    var table = VoxelMaterialResources.materialParamDatas;
                    var start = VoxelMaterialResources.MaterialCount;
                    var materials = source.VoxelMaterials;
                    if (start + materials.Length > table.Length)
                    {
                        Core.Log.Error($"No room for {__instance.name} materials: table {table.Length}, start {start}");
                        return;
                    }
                    for (int i = 0; i < materials.Length; i++)
                    {
                        var material = materials[i];
                        material.Id = start + i;
                        table[start + i] = material;
                    }
                }
                catch (Exception e) { Core.Log.Error("Mainframe materials failed: " + e); }
            }
        }
    }
}
