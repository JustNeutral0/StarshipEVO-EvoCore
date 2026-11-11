using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;
using VoxelEngine.Crafting;
using static Game.Rarity.RarityLevels;
using Prop = World.WorldPropCommon.WorldPropId;
using static EvoCore.PartsSurvival;

namespace EvoCore
{
    // The Salvage Processor (user ask 2026-11-10: "the machines to process them"): a block that works the salvage in
    // your backpack (Salvage.cs) three ways:
    //  - Break down: one piece of scrap into base materials. General gives plates and construction parts, Automotive
    //    mechanical parts and metals, Electronics copper, silicon, electric parts and gold; rarer scrap, rarer output.
    //  - Refine: four pieces of one rarity into one of the next rarity, same kind.
    //  - Extract: breaking down Rare or Epic Electronics or Automotive scrap may pop out a computer chip (Parts.cs).
    // It works at once (nothing half-done to lose on a save); a timed, conveyor-fed version can come later. Survival
    // unlocks its recipe with the "Salvage Processing" research (tier 1: salvage is there from the crash).
    // It's a Lua Computer underneath like the Expansion Slot and PC Workstation (Expansion.cs), but runs no program;
    // its screen is the Hardware tab's processor panel (HardwareEditor.cs).
    public class SalvageProcessor : LuaComputer
    {
        public override string OverTextIndirect => "Salvage Processor";
        public override string OverText => "Salvage Processor: use it to break down, refine or strip the salvage in your backpack.";
        public override void OnKeyUp(PlayerCommandManager playerManager) => Lua.OpenHardware(this);
    }

    public static class Processing
    {
        // What one piece of each kind and rarity breaks down into.
        static readonly Dictionary<(Salvage.Kind, Rarity.RarityLevels), PartsSurvival.Cost[]> breakDown =
            new Dictionary<(Salvage.Kind, Rarity.RarityLevels), PartsSurvival.Cost[]>
        {
            { (Salvage.Kind.General, common), new[] { Need(Prop.plate, 1f) } },
            { (Salvage.Kind.General, uncommon), new[] { Need(Prop.plate, 1f), Need(Prop.component_construction, 1f) } },
            { (Salvage.Kind.General, rare), new[] { Need(Prop.component_construction, 2f), Need(Metal.Cobalt, 1f) } },
            { (Salvage.Kind.General, epic), new[] { Need(Prop.component_construction, 2f), Need(Metal.Silver, 2f) } },
            { (Salvage.Kind.Automotive, common), new[] { Need(Prop.component_mechanical, 1f) } },
            { (Salvage.Kind.Automotive, uncommon), new[] { Need(Prop.component_mechanical, 1f), Need(Metal.Iron, 1f) } },
            { (Salvage.Kind.Automotive, rare), new[] { Need(Prop.component_mechanical, 2f), Need(Metal.Cobalt, 1f) } },
            { (Salvage.Kind.Automotive, epic), new[] { Need(Prop.component_mechanical, 2f), Need(Prop.pyron_shard_item, 1f) } },
            { (Salvage.Kind.Electronics, common), new[] { Need(Metal.Copper, 1f) } },
            { (Salvage.Kind.Electronics, uncommon), new[] { Need(Prop.component_electric, 1f), Need(Metal.Silicon, 1f) } },
            { (Salvage.Kind.Electronics, rare), new[] { Need(Prop.component_electric, 1f), Need(Metal.Gold, 1f) } },
            { (Salvage.Kind.Electronics, epic), new[] { Need(Metal.Gold, 2f), Need(Prop.crystal_sunstone_item, 1f) } },
        };

        public const int RefineCount = 4;

        // Chance per piece broken down that a chip comes out (the chips are definitions in Parts.cs).
        static float ExtractChance(Salvage.Kind kind, Rarity.RarityLevels r) =>
            kind == Salvage.Kind.General ? 0f : r == rare ? 0.05f : r == epic ? 0.12f : 0f;

        static Parts.Def ExtractWhat(Salvage.Kind kind, Rarity.RarityLevels r, float roll) =>
            kind == Salvage.Kind.Automotive ? (r == epic ? Parts.Find(Parts.OverchargeId) : null) :
            Parts.Find(roll < 0.5f ? 10 : 11);   // Electronics: a memory chip or a clock crystal

        public static string Yields(Salvage.Kind kind, Rarity.RarityLevels r) =>
            string.Join(", ", breakDown[(kind, r)].Select(o => $"{o.Count:0} {o.Name}").ToArray()) +
            (ExtractChance(kind, r) > 0f ? $", {ExtractChance(kind, r) * 100f:0}% a chip" : "");

        // The salvage in the backpack, per kind and rarity.
        public static List<(Salvage.Kind Kind, Rarity.RarityLevels Rarity, float Count)> Held()
        {
            var counts = new Dictionary<(Salvage.Kind, Rarity.RarityLevels), float>();
            foreach (var (_, data) in PartsSurvival.Backpack())
            {
                if (!Salvage.IsOurs(data)) continue;
                var key = ((Salvage.Kind)((data.contextualDataId >> 4) & 0xF), (Rarity.RarityLevels)(data.contextualDataId & 0xF));
                counts[key] = (counts.TryGetValue(key, out var n) ? n : 0f) + data.quantity;
            }
            return counts.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2).Select(p => (p.Key.Item1, p.Key.Item2, p.Value)).ToList();
        }

        static PartsSurvival.Cost[] Input(Salvage.Kind kind, Rarity.RarityLevels r, float count) =>
            new[] { PartsSurvival.Need(kind, r, count) };

        static bool GiveItem(CraftingElement element)
        {
            var item = element.ToItemData;
            item.quantity = element.quantity;
            item.disposeTime = 0f;
            item.RegisterAndPushItself(StorageCommon.CONTEXT);
            if (StorageCommon.TryToAddToPlayerShipInventory(null, item, addToPlayer: true)) return true;
            var ship = ParentEntity.PlayerUltimateParentEntity;
            if (ship != null && StorageCommon.TryToAddToPlayerShipInventory(ship, item, addToPlayer: false, onlyAddToCargo: true)) return true;
            StorageCommon.CONTEXT.GetItemData(item.id).ReleaseComponentsAndItemId();
            return false;
        }

        // Breaks down `count` pieces (taken first; what can't fit afterwards is reported as lost).
        public static string BreakDown(Salvage.Kind kind, Rarity.RarityLevels r, int count)
        {
            if (count <= 0) return "Nothing to break down.";
            var why = PartsSurvival.Pay(Input(kind, r, count), always: true);
            if (why != null) return why;
            var outputs = breakDown[(kind, r)];
            var lost = 0;
            foreach (var o in outputs)
                { var e = o.Element; e.quantity = o.Count * count; if (!GiveItem(e)) lost++; }
            var chips = new List<string>();
            var chance = ExtractChance(kind, r);
            for (int i = 0; i < count && chance > 0f; i++)
            {
                if (UnityEngine.Random.value >= chance) continue;
                var def = ExtractWhat(kind, r, UnityEngine.Random.value);
                if (def == null) continue;
                if (Parts.Give(def, (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue), 0)) chips.Add(def.Name);
                else lost++;
            }
            var made = string.Join(", ", outputs.Select(o => $"{o.Count * count:0} {o.Name}").ToArray());
            return $"Broke down {count} {Salvage.Name(kind, r)}: {made}" +
                   (chips.Count > 0 ? $". Found inside: {string.Join(", ", chips.ToArray())}!" : ".") +
                   (lost > 0 ? " Some of it didn't fit and was lost." : "");
        }

        // Refines RefineCount pieces into one of the next rarity, `times` times.
        public static string Refine(Salvage.Kind kind, Rarity.RarityLevels r, int times)
        {
            if (r >= epic) return "Epic is the top: it can't be refined further.";
            if (times <= 0) return $"Refining takes {RefineCount} at a time.";
            var why = PartsSurvival.Pay(Input(kind, r, RefineCount * times), always: true);
            if (why != null) return why;
            var next = r + 1;
            if (!GiveItem(Salvage.Element(kind, next, times))) return "It didn't fit and was lost.";
            return $"Refined {RefineCount * times} {Salvage.Name(kind, r)} into {times} {Salvage.Name(kind, next)}.";
        }
    }

    static class ProcessorBlock
    {
        public static readonly int Id = Ids.Claim(IdSpace.Brick, "EvoCore.SalvageProcessor", 516);

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class Register
        {
            static void Prefix()
            {
                try
                {
                    var turret = TabLogic.TURRET_COMPUTER;
                    var block = new CommandBrick<SalvageProcessor>(Id, "Salvage Processor", "gates/gate_turret", ResizeMode.NoneBrick,
                        "Gates/gate_large_frame", DynData_Pixel.glowFactor, AlphaMode.glowPaint, "Gates/gate_spaceship_screen", AlphaMode.holo);
                    block.PreviewPaint = turret.PreviewPaint;
                    block.defaultPaint = turret.defaultPaint;
                    block.functionType = BrickFunctionType.crafter;
                    block.cost = turret.cost;
                    block.massForUnitShape = turret.massForUnitShape;
                    block.boundsOverride = turret.boundsOverride;
                    block.gridLevel = turret.gridLevel;
                    block.gridLockOffset = turret.gridLockOffset;
                    block.hasGlowPaint = turret.hasGlowPaint;
                    block.Desc("Works the salvage in your backpack: break it down into materials, refine four into one of the next rarity, and sometimes strip a chip out of rare scrap.")
                         .Tab(UI.Codex.CodexTab.COMMANDS);
                    Core.Log.Msg($"Registered Salvage Processor as brick {Id}");
                }
                catch (Exception e) { Core.Log.Error("Salvage Processor registration failed: " + e); }
            }
        }

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
                    var materials = TabLogic.TURRET_COMPUTER.VoxelMaterials;
                    if (start + materials.Length > table.Length) { Core.Log.Error($"No room for Salvage Processor materials: table {table.Length}, start {start}"); return; }
                    for (int i = 0; i < materials.Length; i++)
                    {
                        var material = materials[i];
                        material.Id = start + i;
                        table[start + i] = material;
                    }
                }
                catch (Exception e) { Core.Log.Error("Salvage Processor materials failed: " + e); }
            }
        }
    }
}
