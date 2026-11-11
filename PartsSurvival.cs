using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common.RNG;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;
using VoxelEngine.Crafting;
using Prop = World.WorldPropCommon.WorldPropId;

namespace EvoCore
{
    // How a survival player gets computer parts (Parts.cs) from nothing:
    //  - Salvage: breaking a ship computer on a wreck with the multitool drops a Damaged Starter Board (the crashed
    //    starter ship has one); logic gates on wrecks sometimes drop a chip. Hooked where the game drops wreck loot
    //    (EntityBricks.EmitMiningLoot), so it's seeded per block like the game's own drops.
    //  - Research: two rows in the Research Station, "Computer Boards" (tier 1: Mk1 board, memory, clock) and
    //    "Advanced Boards" (tier 2: Mk2 board). They unlock building the parts from the Hardware tab with materials
    //    from your backpack (Parts.Build). Like other modded research (ids from 1000), they don't hold back the
    //    game's own next tier.
    // Everything is set up right after the game's recipes exist (CraftingSchematic.AllocateResources), as
    // TieredParts does: touching ResearchCommon earlier breaks crafting for the whole run.
    public static class PartsSurvival
    {
        public class Research
        {
            public string Key, Name, About;
            public int Preferred, Tier, Cost;
            public float Seconds;
            public ResearchId Id;
            public bool Unlocked => ready && ResearchCommon.IsUnlocked(Id);
        }

        public static readonly Research Boards = new Research
        {
            Key = "EvoCore.ComputerBoards", Preferred = 1100, Tier = 1, Cost = 200, Seconds = 45f, Name = "Computer Boards",
            About = "Build logic boards, memory chips and clock crystals for Lua Computers, from the computer's Hardware tab.",
        };
        public static readonly Research AdvancedBoards = new Research
        {
            Key = "EvoCore.AdvancedBoards", Preferred = 1101, Tier = 2, Cost = 300, Seconds = 60f, Name = "Advanced Boards",
            About = "Build the Logic Board Mk2: six sockets that take large chips too.",
        };
        public static readonly Research SalvageProcessing = new Research
        {
            Key = "EvoCore.SalvageProcessing", Preferred = 1102, Tier = 1, Cost = 150, Seconds = 30f, Name = "Salvage Processing",
            About = "Build a Salvage Processor: break salvage down into materials, refine it to rarer grades, and strip chips out of rare scrap.",
        };
        static readonly Research[] all = { Boards, AdvancedBoards, SalvageProcessing };
        static bool ready;

        public static bool IsOurs(ResearchId id) => ready && all.Any(r => r.Id == id);

        // ---- Paying with what's in the backpack (creative: free), the way TieredParts' upgrades pay ----

        internal static List<(int id, ItemInstanceData data)> Backpack()
        {
            var items = new List<(int, ItemInstanceData)>();
            var context = StorageCommon.CONTEXT;
            var storageId = Dependency.PlayerManager?.PlayerInventory?.storageId ?? 0;
            if (storageId == 0) return items;
            int id = context.GetStorageData(storageId).rootItemId, guard = 0;
            while (id != 0 && guard++ < 500)
            {
                var data = context.GetItemData(id);
                items.Add((id, data));
                id = data.nextElementId;
            }
            return items;
        }

        public static string ItemName(Prop p) => p.ToString().Replace("_item", "").Replace('_', ' ');

        // One input of a cost: an ordinary crafting item, or salvage of one kind and rarity (Salvage.cs).
        public struct Cost
        {
            public CraftingElement Element;
            public string Name;
            public float Count => Element.quantity;
        }

        public static Cost Need(Prop p, float count) => new Cost { Element = new CraftingElement(p, count), Name = ItemName(p) };

        // Ingots aren't crafting props: the game's ingot item is id 1 with the metal in contextualDataId (iron 5, gold
        // 6, cobalt 7, silver 8, copper 9), and silicon is id 20. Built from a prop id they'd be an item nothing uses.
        public enum Metal { Iron = 5, Gold = 6, Cobalt = 7, Silver = 8, Copper = 9, Silicon = 20 }

        public static CraftingElement Ingot(Metal m, float count)
        {
            switch (m)
            {
                case Metal.Silicon: return new CraftingElement(WorldItemType.ingot, 20, count, 0, World.Terrain.MaterialGradientGenerator.COLOR_SILICON);
                case Metal.Gold: return new CraftingElement(WorldItemType.ingot, 1, count, 6, World.Terrain.MaterialGradientGenerator.COLOR_GOLD);
                case Metal.Cobalt: return new CraftingElement(WorldItemType.ingot, 1, count, 7, World.Terrain.MaterialGradientGenerator.COLOR_COBALT);
                case Metal.Silver: return new CraftingElement(WorldItemType.ingot, 1, count, 8, World.Terrain.MaterialGradientGenerator.COLOR_SILVER);
                case Metal.Copper: return new CraftingElement(WorldItemType.ingot, 1, count, 9, World.Terrain.MaterialGradientGenerator.COLOR_COPPER);
                default: return new CraftingElement(WorldItemType.ingot, 1, count, 5, World.Terrain.MaterialGradientGenerator.COLOR_IRON);
            }
        }

        public static Cost Need(Metal m, float count) => new Cost { Element = Ingot(m, count), Name = m.ToString().ToLower() + " ingot" };
        public static Cost Need(Salvage.Kind kind, Rarity.RarityLevels rarity, float count) =>
            new Cost { Element = Salvage.Element(kind, rarity, count), Name = Salvage.Name(kind, rarity) };

        public static float Have(Cost c) =>
            Backpack().Where(x => c.Element.IsMatching(x.data, StorageCommon.CONTEXT)).Sum(x => x.data.quantity);

        public static string CostText(Cost[] cost) =>
            string.Join(", ", cost.Select(c => $"{c.Count:0} {c.Name} (have {Have(c):0})").ToArray());

        // Null when paid, else what's missing. Free in creative unless `always` (a machine's input is taken in any mode).
        public static string Pay(Cost[] cost, bool always = false)
        {
            if (!PlayerState.IsSurvival && !always) return null;
            var missing = cost.Where(c => Have(c) < c.Count).Select(c => $"{c.Count - Have(c):0} more {c.Name}").ToList();
            if (missing.Count > 0) return "You need " + string.Join(", ", missing.ToArray()) + ".";
            var storageId = Dependency.PlayerManager.PlayerInventory.storageId;
            foreach (var c in cost)
            {
                var element = c.Element;
                float left = c.Count;
                foreach (var (id, data) in Backpack())
                {
                    if (left <= 0f) break;
                    if (!element.IsMatching(data, StorageCommon.CONTEXT)) continue;
                    var amount = Mathf.Min(left, data.quantity);
                    StorageCommon.PopupItemQuantityAndRelease(storageId, id, amount, StorageCommon.CONTEXT);
                    left -= amount;
                }
            }
            return null;
        }

        // DevBridge test: runs the salvage drop on the first ship computer in the world as if its wreck were being
        // broken up (skips the wreck check), so the drop path can be tried without a wreck and a multitool.
        public static string TestDrop()
        {
            var cmd = CommandManager.commands.Datas.OfType<Command>()
                .FirstOrDefault(c => c.brickCoord.entity != null && c.brickData.brickId == TabLogic.SPACESHIP_COMPUTER.brickID);
            if (cmd == null) return "no ship computer in the world";
            Setup.Drop(cmd.brickCoord.entity.Bricks, new ProjectileDamage { localNormal = Vector3.up }, cmd.brickData, force: true);
            return "drop run on the ship computer of " + cmd.brickCoord.entity.Parent?.EntityName + "; " + Setup.LastDrop;
        }

        // ---- Set-up, once the game's recipes exist ----

        [HarmonyPatch(typeof(CraftingSchematic), nameof(CraftingSchematic.AllocateResources))]
        static class Setup
        {
            static void Postfix()
            {
                if (ready) return;
                try
                {
                    // The two blocks get recipes copied from the stock blocks they look like; each research unlocks one.
                    var pc = AddBlockRecipe("EvoCore.WorkstationRecipe", 240, TabLogic.HOVER_COMPUTER.brickID, ExpansionBlocks.WorkstationId);
                    var slot = AddBlockRecipe("EvoCore.ExpansionSlotRecipe", 241, TabLogic.LOGIC_GATE.brickID, ExpansionBlocks.SlotId);
                    var processor = AddBlockRecipe("EvoCore.SalvageProcessorRecipe", 242, TabLogic.TURRET_COMPUTER.brickID, ProcessorBlock.Id);
                    if (pc >= 0 || slot >= 0 || processor >= 0) computeScrap?.Invoke(null, null);
                    AddResearch(Boards, pc >= 0 ? new[] { pc } : new int[0]);
                    AddResearch(AdvancedBoards, slot >= 0 ? new[] { slot } : new int[0]);
                    AddResearch(SalvageProcessing, processor >= 0 ? new[] { processor } : new int[0]);
                    var h = new HarmonyLib.Harmony("evo.core.partsurvival");
                    h.Patch(AccessTools.Method(typeof(ResearchCommon), nameof(ResearchCommon.IsTierOpen)),
                        postfix: new HarmonyMethod(typeof(Setup), nameof(TierOpen)));
                    h.Patch(AccessTools.Method(typeof(AssetRepoUI), nameof(AssetRepoUI.GetResearchIcon)),
                        prefix: new HarmonyMethod(typeof(Setup), nameof(Icon)));
                    h.Patch(AccessTools.Method(typeof(EntityBricks), "EmitMiningLoot"),
                        postfix: new HarmonyMethod(typeof(Setup), nameof(Salvage)));
                    ready = true;
                }
                catch (Exception e) { Core.Log.Error("Parts: research or salvage set-up failed: " + e); }
            }

            static readonly MethodInfo computeScrap = AccessTools.Method(typeof(CraftingSchematic), "ComputeBrickScrapData");

            // A recipe for one of our blocks: the stock block's recipe, making ours instead (TieredParts' AddSchematic).
            // -1 when the stock recipe can't be found.
            static int AddBlockRecipe(string key, int preferred, int stockBrickId, int ourBrickId)
            {
                var stockId = Array.FindIndex(CraftingSchematic.Index, s => s.IsValid && s.output.itemType == WorldItemType.brick && s.output.id == Brick.BRICK_INDEX[stockBrickId].ItemID);
                if (stockId < 0) { Core.Log.Error($"Parts: no stock recipe for brick {stockBrickId}; brick {ourBrickId} gets none"); return -1; }
                var id = Ids.Claim(IdSpace.Schematic, key, preferred);
                var schematic = CraftingSchematic.Index[stockId];
                schematic.id = id;
                schematic.output = new CraftingElement((uint)ourBrickId, 1f);
                CraftingSchematic.Index[id] = schematic;
                schematic.Register();
                CraftingSchematic.schematicsNativeArray[id] = CraftingSchematic.Index[id];
                Core.Log.Msg($"Parts: recipe {id} makes brick {ourBrickId} (copied from recipe {stockId})");
                return id;
            }

            // A new row in the Research Station, after the last entry of its tier (TieredParts' AddResearch).
            static void AddResearch(Research r, int[] schematicIds)
            {
                r.Id = (ResearchId)Ids.Claim(IdSpace.Research, r.Key, r.Preferred);
                // Read and write through reflection: Mono may bake the readonly array's old value into compiled code.
                var field = AccessTools.Field(typeof(ResearchCommon), nameof(ResearchCommon.Definitions));
                var definitions = ((ResearchDefinition[])field.GetValue(null)).ToList();
                if (definitions.Any(d => d.Id == r.Id)) return;
                var research = new ResearchDefinition(r.Id, r.Tier, r.Cost, r.Seconds, r.Name, r.About,
                    schematicIds.Select(id => CraftingSchematic.Index[id]).ToArray());
                definitions.Insert(definitions.FindLastIndex(d => d.Tier <= r.Tier) + 1, research);
                field.SetValue(null, definitions.ToArray());
                // Add to the game's lookup rather than rebuilding it, as TieredParts does.
                var lookup = (Dictionary<int, ResearchId>)AccessTools.Field(typeof(ResearchCommon), "researchBySchematicId").GetValue(null);
                foreach (var id in schematicIds) lookup[id] = r.Id;
                Core.Log.Msg($"Parts: research \"{r.Name}\" is id {(int)r.Id}, tier {r.Tier}, recipes {string.Join(",", schematicIds.Select(i => i.ToString()).ToArray())}");
            }

            // A tier opens when every *stock* entry of the tier below is researched; modded rows (ids from 1000, ours
            // and other mods') never hold it back. Same rule as TieredParts' own gate.
            static void TierOpen(int tier, ref bool __result)
            {
                if (__result || tier <= 1) return;
                var definitions = (ResearchDefinition[])AccessTools.Field(typeof(ResearchCommon), nameof(ResearchCommon.Definitions)).GetValue(null);
                __result = definitions.All(d => d.Tier != tier - 1 || (int)d.Id >= 1000 || ResearchCommon.IsUnlocked(d.Id));
            }

            // The station's icon table only has the game's own rows: ours borrow logic's.
            static bool Icon(ResearchId researchId, ref Texture2D __result)
            {
                if (!IsOurs(researchId)) return true;
                __result = AssetRepoUI.GetResearchIcon(ResearchId.logic);
                return false;
            }

            static readonly MethodInfo spawnLoot = AccessTools.Method(typeof(EntityBricks), "SpawnMiningLootItem");

            // The killing multitool hit on a wreck's block: ship computers drop a Damaged Starter Board, logic gates
            // sometimes a chip. Seeded by the block, like the game's own wreck loot.
            static void Salvage(EntityBricks __instance, ProjectileDamage projDmg, BrickInstanceData bd) => Drop(__instance, projDmg, bd, false);

            internal static string LastDrop = "nothing dropped yet";

            internal static void Drop(EntityBricks __instance, ProjectileDamage projDmg, BrickInstanceData bd, bool force)
            {
                try
                {
                    if ((!force && !__instance.entity.Parent.isDestroyed) || bd.Brick.IsDecalDamage || bd.Brick.IsDecal) return;
                    var id = bd.Brick.brickID;
                    Parts.Def drop = null;
                    int flags = 0;
                    var hash = new SkeetoHash((uint)bd.instanceId ^ 0x9E3779B9u);
                    if (IsComputer(id)) { drop = Parts.Find(1); flags = Parts.Damaged; }
                    else if (IsGate(id) && hash.GetFloat() < 0.15f) drop = Parts.Find(hash.GetFloat() < 0.5f ? 10 : 11);
                    var scrap = force ? null : EvoCore.Salvage.Drop(__instance, projDmg, bd, spawnLoot);
                    if (scrap != null) LastDrop = "dropped " + scrap;
                    if (drop == null) return;
                    var item = Parts.MakeItem(drop, 0, flags);
                    object[] args = { item, bd, projDmg, 1f, hash };
                    spawnLoot.Invoke(__instance, args);
                    LastDrop = $"dropped {Parts.NameOf(drop, flags)} at block {bd.instanceId}";
                    Core.Log.Msg("Parts: " + LastDrop);
                }
                catch (Exception e) { Core.Log.Warning("Parts: salvage drop failed: " + e.Message); }
            }

            static bool IsComputer(int id) =>
                id == TabLogic.SPACESHIP_COMPUTER.brickID || id == TabLogic.HOVER_COMPUTER.brickID || id == TabLogic.MECH_COMPUTER.brickID ||
                id == TabLogic.TURRET_COMPUTER.brickID || id == TabLogic.STATION_COMPUTER.brickID || id == TabLogic.CREW_COMPUTER.brickID;

            static bool IsGate(int id) =>
                id == TabLogic.LOGIC_GATE.brickID || id == TabLogic.TIMER_GATE.brickID || id == TabLogic.SEQUENCER.brickID ||
                id == TabLogic.QUERY_GATE.brickID || id == TabLogic.WARNING_GATE.brickID || id == TabLogic.EVENT_GATE.brickID;
        }
    }
}
