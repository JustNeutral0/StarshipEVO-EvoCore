using System;
using System.Linq;
using HarmonyLib;
using Manager;
using VoxelEngine;

namespace EvoCore
{
    // Display Decal: a decal that is also a block you can link, so a Lua Computer draws straight onto the surface under
    // it, with no controller screen or colour matching (Cockpit Displays' decal surfaces need both). Cockpit Displays
    // draws it: each Display Decal is a [screen] display of its own, conforming to whatever faces lie under it.
    // A decal is any brick with IsDecal set; this one is the Quad decal's model and material on a block class of our
    // own. Not CommandDisplay: that drives per-block icon data a decal's material doesn't have.
    // DevBridge: get EvoCore.DisplayDecals.Report()
    public class DisplayDecal : Command
    {
        public override bool AllowMultipleInputs => true;
        protected override bool CanPropagate => false;
    }

    public static class DisplayDecals
    {
        public static readonly int Id = Ids.Claim(IdSpace.Brick, "EvoCore.DisplayDecal", 519);

        // Every Display Decal in the world: where, how big, and which Lua Computers link to it.
        public static string Report()
        {
            var all = CommandManager.commands.Datas.OfType<DisplayDecal>().Where(c => c.brickCoord.entity != null).ToList();
            if (all.Count == 0) return "No Display Decals.";
            var computers = CommandManager.commands.Datas.OfType<LuaComputer>().Where(c => c.brickCoord.entity != null).ToList();
            return string.Join("\n", all.Select(c =>
            {
                var b = c.brickCoord.brickData;
                var from = computers.Where(p => p.Outputs.Contains(c)).Select(p => p.ScriptName).ToList();
                return $"Display Decal on {c.brickCoord.entity.Parent?.EntityName} at {b.gridPosition}, scale {b.Scale}, decal {b.Brick.IsDecal}; " +
                       (from.Count > 0 ? "linked from " + string.Join(", ", from) : "not linked");
            }).ToArray());
        }

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class Register
        {
            // An exception here would stop every block from loading, so failures are logged and contained.
            static void Prefix()
            {
                try
                {
                    var stock = TabDecals.DECAL_QUAD;
                    var brick = new CommandBrick<DisplayDecal>(Id, "Display Decal", "decals/smallCube", ResizeMode.BlockFull, "Decals/full_quad", AlphaMode.decals)
                    {
                        PreviewPaint = stock.PreviewPaint,
                        defaultPaint = stock.defaultPaint,
                        IsDecal = true,
                        isDynamicGridSize = stock.isDynamicGridSize,
                    };
                    brick.Desc("A screen as a decal: lay it on any surface, link a Lua Computer to it, and the script draws there (needs Cockpit Displays).").Tab(UI.Codex.CodexTab.COMMANDS);
                    Core.Log.Msg($"Registered Display Decal as brick {Id}");
                }
                catch (Exception e) { Core.Log.Error("Display Decal registration failed: " + e); }
            }
        }

        // The link tool aims with the game's ordinary hit, which passes through decals (only paint and delete use the
        // decal hit), so it never offers a Display Decal. While linking from something, the decal hit is checked too.
        [HarmonyPatch(typeof(ActionLink), nameof(ActionLink.OnIsSelected))]
        static class LinkToDecal
        {
            static void Postfix()
            {
                try
                {
                    var from = ActionLink.CommandSelected.Value;
                    if (from == null) return;
                    var hit = Dependency.PlayerAction.hitDecal;
                    if (!hit.isHitBrick || !(hit.brickCoord.Command is DisplayDecal decal)) return;
                    if (decal != from) ActionLink.CommandOver.Value = decal;
                }
                catch { }
            }
        }

        // Materials are consumed from a baked table in ID order and spare entries are empty, so copy the Quad decal's
        // rows into ours.
        [HarmonyPatch(typeof(Brick), "LoadResourcesRuntime")]
        static class CopyMaterials
        {
            static void Prefix(Brick __instance)
            {
                if (__instance.brickID != Id || __instance != Brick.BRICK_INDEX[Id]) return;
                try
                {
                    var table = VoxelMaterialResources.materialParamDatas;
                    var start = VoxelMaterialResources.MaterialCount;
                    var source = TabDecals.DECAL_QUAD.VoxelMaterials;
                    if (start + source.Length > table.Length) { Core.Log.Error($"No room for Display Decal materials: table {table.Length}, start {start}"); return; }
                    for (int i = 0; i < source.Length; i++)
                    {
                        var material = source[i];
                        material.Id = start + i;
                        table[start + i] = material;
                    }
                }
                catch (Exception e) { Core.Log.Error("Display Decal materials failed: " + e); }
            }
        }
    }
}
