using System;
using System.Linq;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Two blocks that go with computer parts (Parts.cs):
    //  - Expansion Slot (1x1, the Logic Gate's look): takes a Daughter Board item, whose sockets take chips. Linked to a
    //    Lua Computer (either way), its chips count for that computer: app slots, clock speed, Overcharge. It runs
    //    nothing on its own. Put it next to the parts it serves.
    //  - PC Workstation (the Hovercraft Computer's look): using it lists the Lua Computers and Expansion Slots linked to
    //    it and opens any of them on the Computer screen, so a ship's computers can be programmed from one desk.
    // Both are Lua Computers underneath (so they keep hardware the same way), but never run a program.
    public class ExpansionSlot : LuaComputer
    {
        public override string OverTextIndirect => "Expansion Slot";
        public override string OverText
        {
            get
            {
                var hw = Parts.Of(this);
                var main = Parts.MainOf(this);
                return "Expansion Slot: " + (hw.Board == null ? "empty (put a Daughter Board in from its Hardware tab)" :
                    $"{hw.Board.Name}, {hw.Chips.Count()} of {hw.Sockets.Length} sockets used") +
                    "\n" + (main == null ? "Not linked to a Lua Computer: its chips do nothing." : $"Serves the computer {main.ScriptName}.");
            }
        }
        public override void OnKeyUp(PlayerCommandManager playerManager) => Lua.OpenHardware(this);
    }

    public class Workstation : LuaComputer
    {
        public override string OverTextIndirect => "PC Workstation";
        public override string OverText => $"PC Workstation: {Lua.WorkstationTargets(this).Count} computer(s) linked. Use it to open one.";
        public override void OnKeyUp(PlayerCommandManager playerManager) => Lua.OpenHardware(this);
    }

    public static partial class Lua
    {
        // Opens the Computer screen on the Hardware tab for a block with no program of its own (an Expansion Slot or
        // a PC Workstation): nothing is stored in it.
        internal static void OpenHardware(LuaComputer block)
        {
            Attached = block;
            Graphs.Canvas.ShowHardware();
            Graphs.Canvas.Show();
        }

        // The computers and slots a workstation can open: what's linked to it, either way.
        internal static System.Collections.Generic.List<LuaComputer> WorkstationTargets(Workstation w) =>
            w.Inputs.Concat(w.Outputs).OfType<LuaComputer>().Where(c => !(c is Workstation)).Distinct().ToList();

        // DevBridge tests: places one of our blocks (an Expansion Slot or Workstation) on the computer's ship, dx/dz
        // metres from it on the same deck; LinkBlockTo then links it to that computer once the game has made it.
        public static string PlaceBlockNear(string label, int brickId, int dx, int dz)
        {
            var c = CommandManager.commands.Datas.OfType<LuaComputer>().FirstOrDefault(x => x.brickCoord.entity != null && x.ScriptName == label);
            if (c == null) return "no computer " + label;
            var at = c.brickData.gridPosition;
            var brick = Brick.BRICK_INDEX[brickId];
            var b = new BrickInstanceData
            {
                brickId = (ushort)brick.ItemID, gridPosition = new Vector3i(at.x + dx * 32, at.y, at.z + dz * 32),
                color = brick.defaultPaint, rotation = c.brickData.rotation, healthScore = byte.MaxValue, gridLevel = (byte)brick.gridLevel,
            };
            Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = b, entityID = c.brickCoord.entity.ID });
            return $"placed brick {brickId} at {b.gridPosition}; link it in a moment with LinkBlockTo";
        }

        public static string LinkBlockTo(string label, int brickId)
        {
            var c = CommandManager.commands.Datas.OfType<LuaComputer>().FirstOrDefault(x => x.brickCoord.entity != null && x.ScriptName == label);
            if (c == null) return "no computer " + label;
            var block = CommandManager.commands.Datas.OfType<LuaComputer>()
                .FirstOrDefault(x => x.brickCoord.entity != null && x.brickCoord.entity.Parent == c.brickCoord.entity.Parent && x.brickData.brickId == brickId);
            if (block == null) return "no such block on its ship yet";
            if (c.Outputs.Contains(block) || block.Outputs.Contains(c)) return "already linked";
            CommandCommon.LinkToCommand((ICommandLinkSource)block, c, onLoad: false, onInit: false);
            return $"linked {block.OverTextIndirect} to {label}";
        }

        // DevBridge test: opens the first block of that brick id as its activate key would.
        public static string OpenBlock(int brickId)
        {
            var block = CommandManager.commands.Datas.OfType<LuaComputer>().FirstOrDefault(x => x.brickCoord.entity != null && x.brickData.brickId == brickId);
            if (block == null) return "none";
            block.OnKeyUp(null);
            return "opened " + block.OverTextIndirect;
        }

        // From a workstation: open that computer as its own activate key would.
        internal static void OpenFromWorkstation(LuaComputer c)
        {
            if (c is ExpansionSlot) OpenHardware(c);
            else OpenComputer(Mainframes.TerminalOf(c) ?? c);
        }
    }

    static class ExpansionBlocks
    {
        public static readonly int SlotId = Ids.Claim(IdSpace.Brick, "EvoCore.ExpansionSlot", 514);
        public static readonly int WorkstationId = Ids.Claim(IdSpace.Brick, "EvoCore.Workstation", 515);

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class Register
        {
            static void Prefix()
            {
                try
                {
                    var gate = TabLogic.LOGIC_GATE;
                    var slot = new CommandBrick<ExpansionSlot>(SlotId, "Expansion Slot", "gates/gate_logic", ResizeMode.NoneBrick,
                        "Gates/gate_logic_frame", DynData_Pixel.glowFactor, AlphaMode.glowPaint, "Gates/gate_logic_screen", AlphaMode.holo,
                        DynData_Vert.vertexUVOffset, "Gates/gate_logic_screenB", AlphaMode.holo, DynData_Vert.vertexUVOffset);
                    Copy(gate, slot);
                    slot.Desc("Takes a Daughter Board, whose sockets take chips. Link it to a Lua Computer and its chips count for that computer.")
                        .Tab(UI.Codex.CodexTab.COMMANDS);

                    var hover = TabLogic.HOVER_COMPUTER;
                    var pc = new CommandBrick<Workstation>(WorkstationId, "PC Workstation", "gates/gate_hovercraft", ResizeMode.NoneBrick,
                        "Gates/gate_logic_frame", AlphaMode.glowPaint, "Gates/gate_spaceship_screen", AlphaMode.holo);
                    Copy(hover, pc);
                    pc.Desc("A desk to program the ship's computers from: link Lua Computers and Expansion Slots to it, then use it to open them.")
                      .Tab(UI.Codex.CodexTab.COMMANDS);
                    Core.Log.Msg($"Registered Expansion Slot as brick {SlotId} and PC Workstation as brick {WorkstationId}");
                }
                catch (Exception e) { Core.Log.Error("Expansion block registration failed: " + e); }
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

        // Materials come from a baked table in id order; copy the stock blocks' rows into ours (as Mainframe.cs does).
        [HarmonyPatch(typeof(Brick), "LoadResourcesRuntime")]
        static class CopyMaterials
        {
            static void Prefix(Brick __instance)
            {
                Brick source;
                if (__instance.brickID == SlotId && __instance == Brick.BRICK_INDEX[SlotId]) source = TabLogic.LOGIC_GATE;
                else if (__instance.brickID == WorkstationId && __instance == Brick.BRICK_INDEX[WorkstationId]) source = TabLogic.HOVER_COMPUTER;
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
                catch (Exception e) { Core.Log.Error($"{__instance.name} materials failed: " + e); }
            }
        }
    }
}
