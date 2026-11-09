using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Game;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // The Lua Computer: a logic block that runs a Lua script (Lua.cs) and is wired up with the game's own link tool.
    // Everything linked into it is an input, everything it's linked to an output, in the order they were linked.
    // Its script is UserData\EvoCore\scripts\<label>.lua, where the label is the one the multitool's Tag mode gives
    // the block (computer.lua when it has none). It has the Spaceship Computer's 2x2 gate base, whose model, materials
    // and size it borrows; its own look (Looks\EvoCore.LuaComputer.obj) puts a Lua logo where that block's symbol is.
    // It never passes its own state on by itself: the script decides what each output gets.
    // Using it (the activate key) opens its program in the editor instead of changing its state; the first time, the
    // program it runs is stored in the block (ComputerPrograms.cs).
    public class LuaComputer : Command
    {
        public override bool AllowMultipleInputs => true;
        protected override bool CanPropagate => false;
        public override bool CanActivateManually(PlayerCommandManager commandManager) => true;

        // Called on the local player's key release only, so this never goes over the network.
        public override void OnKeyUp(PlayerCommandManager playerManager) => Lua.OpenComputer(this);

        // Its own program (ComputerPrograms.cs), saved in the block: "graph" or "lua", and an id naming its working
        // file. Null until the computer is first opened; until then it runs scripts\<label>.
        public string Program, ProgramKind = "lua", ProgramId;
        public bool HasProgram => Program != null;
        // Its poses and clips (Animation.cs), as saved text; null when it has none.
        public string Anims;
        // Which linked block sits in which named port (Ports.cs), as saved text; null when none are set.
        public string PortMap;

        public void SetProgram(string kind, string text)
        {
            ProgramKind = kind == "graph" ? "graph" : "lua";
            Program = text ?? "";
            if (ProgramId == null) NewProgramId();
            OnEdited(); // saved with the ship, and sent to the server like a text screen's text
        }

        public void SetAnims(string text)
        {
            Anims = string.IsNullOrEmpty(text) ? null : text;
            if (ProgramId == null) NewProgramId();
            OnEdited();
        }

        public void SetPorts(string text)
        {
            PortMap = string.IsNullOrEmpty(text) ? null : text;
            if (ProgramId == null) NewProgramId();
            OnEdited();
        }

        public void NewProgramId() => ProgramId = Guid.NewGuid().ToString("N").Substring(0, 6);

        // evoprog3\n<id>\n<kind>\n<program length, -1 for none>\n<anims length>\n<program><anims><ports>.
        // (evoprog2 had no ports and no anims length; evoprog1 no anims and no length.)
        public override byte[] GetCustomData(bool isBlueprintExport) =>
            Program == null && Anims == null && PortMap == null ? null :
            Encoding.UTF8.GetBytes($"evoprog3\n{ProgramId}\n{ProgramKind}\n{(Program == null ? -1 : Program.Length)}\n{(Anims ?? "").Length}\n{Program}{Anims}{PortMap}");

        public override void OnSaveLoaded(byte[] customData, bool onInit)
        {
            base.OnSaveLoaded(customData, onInit);
            if (customData == null || customData.Length == 0) return;
            try
            {
                var text = Encoding.UTF8.GetString(customData);
                if (text.StartsWith("evoprog1\n"))
                {
                    var parts = text.Split(new[] { '\n' }, 4);
                    if (parts.Length < 4) return;
                    ProgramId = parts[1];
                    ProgramKind = parts[2] == "graph" ? "graph" : "lua";
                    Program = parts[3];
                    Anims = null;
                }
                else if (text.StartsWith("evoprog3\n"))
                {
                    var parts = text.Split(new[] { '\n' }, 6);
                    if (parts.Length < 6) return;
                    ProgramId = parts[1];
                    ProgramKind = parts[2] == "graph" ? "graph" : "lua";
                    var length = int.Parse(parts[3]);
                    var animsLength = int.Parse(parts[4]);
                    var rest = parts[5];
                    var at = Math.Max(0, length);
                    Program = length < 0 ? null : rest.Substring(0, length);
                    var anims = rest.Substring(at, animsLength);
                    Anims = anims.Length == 0 ? null : anims;
                    var ports = rest.Substring(at + animsLength);
                    PortMap = ports.Length == 0 ? null : ports;
                }
                else if (text.StartsWith("evoprog2\n"))
                {
                    var parts = text.Split(new[] { '\n' }, 5);
                    if (parts.Length < 5) return;
                    ProgramId = parts[1];
                    ProgramKind = parts[2] == "graph" ? "graph" : "lua";
                    var length = int.Parse(parts[3]);
                    var rest = parts[4];
                    Program = length < 0 ? null : rest.Substring(0, length);
                    var anims = length < 0 ? rest : rest.Substring(length);
                    Anims = anims.Length == 0 ? null : anims;
                }
                Anim.Reload(this);
            }
            catch (Exception e) { Core.Log.Error("Lua Computer: couldn't read its stored program: " + e.Message); }
        }
        public override string OverText => Lua.ComputerStatus(this);
        public override string OverTextIndirect => "Lua Computer";

        // The script's name: the block's Tag-tool label, or "computer".
        public string ScriptName
        {
            get
            {
                try
                {
                    var label = brickCoord.TagData.label;
                    return string.IsNullOrEmpty(label) ? "computer" : label.Trim();
                }
                catch { return "computer"; }
            }
        }

        // Linked blocks, oldest link first. (The game keeps a computer's links newest first, and that order survives
        // saving; named ports (Ports.cs) are the way not to depend on it at all.)
        public List<ICommand> Inputs => Linked(predecessors);
        public List<ICommand> Outputs => Linked(targets);

        static List<ICommand> Linked(CommandLinkData links)
        {
            var list = new List<ICommand>();
            foreach (var item in links.Items)
                if (item.Command != null) list.Add(item.Command);
            list.Reverse();
            return list;
        }

        // Sends a value down one link, the way a block passes its state on: the target runs as if signalled, and
        // passes it on through its own links.
        public static void Send(ICommand target, float value)
        {
            var player = Dependency.RenderPipeline != null ? Dependency.RenderPipeline.commandManager : null;
            target.Activate(manualActivation: false, player, value);
        }
    }

    static class LuaComputerBlock
    {
        public static readonly int BrickId = Ids.Claim(IdSpace.Brick, "EvoCore.LuaComputer", 500); // 500 since it first shipped; 507 is TieredParts' thruster

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class Register
        {
            // An exception here would stop every block from loading, so failures are logged and contained.
            static void Prefix()
            {
                try
                {
                    var stock = TabLogic.SPACESHIP_COMPUTER;
                    var brick = new CommandBrick<LuaComputer>(BrickId, "Lua Computer", "gates/gate_large", ResizeMode.NoneBrick,
                        "Gates/gate_large_frame", AlphaMode.glowPaint, "Gates/gate_spaceship_screen", AlphaMode.holo)
                    {
                        PreviewPaint = stock.PreviewPaint,
                        defaultPaint = stock.defaultPaint,
                        functionType = stock.functionType,
                        cost = stock.cost,
                        massForUnitShape = stock.massForUnitShape,
                        boundsOverride = stock.boundsOverride,
                        gridLevel = stock.gridLevel,
                        gridLockOffset = stock.gridLockOffset,
                        hasGlowPaint = stock.hasGlowPaint,
                    };
                    brick.Desc("Runs a Lua script. Link buttons, switches and sensors into it, and link it to lights, doors and thrusters: " +
                               "the script reads its inputs and sets its outputs. Name it with the multitool's Tag mode; the label picks the script.")
                         .Tab(UI.Codex.CodexTab.COMMANDS);
                    Looks.Register(BrickId, "EvoCore.LuaComputer"); // its own mesh, if Looks\EvoCore.LuaComputer.obj is there
                    Core.Log.Msg("Registered Lua Computer as brick " + BrickId);
                }
                catch (Exception e) { Core.Log.Error("Lua Computer registration failed: " + e); }
            }
        }

        // Materials are consumed from a baked table in ID order and spare entries are empty, so copy the Spaceship
        // Computer's rows into ours.
        [HarmonyPatch(typeof(Brick), "LoadResourcesRuntime")]
        static class CopyMaterials
        {
            static void Prefix(Brick __instance)
            {
                if (__instance.brickID != BrickId || __instance != Brick.BRICK_INDEX[BrickId]) return;
                try
                {
                    var table = VoxelMaterialResources.materialParamDatas;
                    var start = VoxelMaterialResources.MaterialCount;
                    var source = TabLogic.SPACESHIP_COMPUTER.VoxelMaterials;
                    if (start + source.Length > table.Length)
                    {
                        Core.Log.Error($"No room for Lua Computer materials: table {table.Length}, start {start}");
                        return;
                    }
                    for (int i = 0; i < source.Length; i++)
                    {
                        var material = source[i];
                        material.Id = start + i;
                        table[start + i] = material;
                    }
                }
                catch (Exception e) { Core.Log.Error("Lua Computer materials failed: " + e); }
            }
        }
    }
}
