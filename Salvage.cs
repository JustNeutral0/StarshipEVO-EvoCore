using System;
using System.Collections.Generic;
using System.Linq;
using Common.RNG;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;
using VoxelEngine.Crafting;
using World;
using Prop = World.WorldPropCommon.WorldPropId;

namespace EvoCore
{
    // Salvage materials (the user's salvage tree, 2026-11-10): scrap in three kinds, General, Automotive and
    // Electronics, at four rarities, Common to Epic. Breaking up a wreck's blocks with the multitool drops the kind that
    // fits the block (logic, controls, displays and power: Electronics; thrusters, mechanisms, transport, crafters and
    // weapons: Automotive; the rest: General), seeded per block like the game's own wreck loot. Higher-tier recipes
    // ask for them (computer parts here; TieredParts' upper tiers).
    // Items: the game's own scrap item type, which stacks only with scrap of the same contextualDataId; the game's
    // plain scrap uses 5. Ours use Marker | kind << 4 | rarity, so each kind and rarity is its own stack, and a recipe
    // can ask for exactly one. The furnace would take any scrap but only smelts the plain kind, so it refuses ours.
    public static class Salvage
    {
        public enum Kind { General, Automotive, Electronics }
        public const int Marker = 0x5C00;

        public static int Code(Kind kind, Rarity.RarityLevels rarity) => Marker | (int)kind << 4 | (int)rarity;
        public static bool IsOurs(ItemInstanceData item) => item.itemType == WorldItemType.scrap && (item.contextualDataId & 0xFF00) == Marker;
        static Kind KindOf(int code) => (Kind)((code >> 4) & 0xF);
        static Rarity.RarityLevels RarityOf(int code) => (Rarity.RarityLevels)(code & 0xF);

        public static string Name(Kind kind, Rarity.RarityLevels rarity) =>
            $"{char.ToUpper(rarity.ToString()[0])}{rarity.ToString().Substring(1)} {kind} Scrap";

        // An item's salvage name, or null when it isn't our salvage.
        public static string NameOf(ItemInstanceData item) => IsOurs(item) ? Name(KindOf(item.contextualDataId), RarityOf(item.contextualDataId)) : null;

        // A recipe or cost input: so many of one kind and rarity.
        public static CraftingElement Element(Kind kind, Rarity.RarityLevels rarity, float count) =>
            new CraftingElement(WorldItemType.scrap, 0, count, Code(kind, rarity), Rarity.GetColor(rarity));

        static ItemInstanceData MakeItem(Kind kind, Rarity.RarityLevels rarity, float count, ref SkeetoHash hash) => new ItemInstanceData
        {
            itemType = WorldItemType.scrap,
            prefabId = (int)PropSelectionLogic.SCRAP.GetPropId(hash.GetFloat()),
            contextualDataId = Code(kind, rarity),
            color = Rarity.GetColor(rarity),
            scale = 0.5f,
            quantity = count,
        };

        // Creative and testing: some of each into the inventory.
        public static string Give(Kind kind, Rarity.RarityLevels rarity, float count)
        {
            var hash = new SkeetoHash((uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue));
            var item = MakeItem(kind, rarity, count, ref hash);
            item.disposeTime = 0f;
            item.RegisterAndPushItself(StorageCommon.CONTEXT);
            if (StorageCommon.TryToAddToPlayerShipInventory(null, item, addToPlayer: true)) return $"gave {count} {Name(kind, rarity)}";
            StorageCommon.CONTEXT.GetItemData(item.id).ReleaseComponentsAndItemId();
            return "no room";
        }

        public static string GiveAll(float count) => string.Join("; ", Enum.GetValues(typeof(Kind)).Cast<Kind>()
            .SelectMany(k => new[] { Rarity.RarityLevels.common, Rarity.RarityLevels.uncommon, Rarity.RarityLevels.rare, Rarity.RarityLevels.epic }
                .Select(r => Give(k, r, count))).ToArray());

        // ---- Wreck drops ----

        public static float DropChance = 0.3f;

        // Kind of salvage a block gives.
        public static Kind KindFor(Brick brick)
        {
            var type = brick.GetType();
            if (type.IsGenericType && typeof(SystemThruster).IsAssignableFrom(type.GetGenericArguments()[0])) return Kind.Automotive;
            switch (brick.functionType)
            {
                case BrickFunctionType.logicalComponent:
                case BrickFunctionType.control:
                case BrickFunctionType.display:
                case BrickFunctionType.powerProducer:
                    return Kind.Electronics;
                case BrickFunctionType.mechanism:
                case BrickFunctionType.itemTransport:
                case BrickFunctionType.fluidTransport:
                case BrickFunctionType.crafter:
                case BrickFunctionType.ingotProducer:
                case BrickFunctionType.weapon:
                    return Kind.Automotive;
                default:
                    return Kind.General;
            }
        }

        // Common 62%, uncommon 27%, rare 9%, epic 2%.
        static Rarity.RarityLevels Roll(float x) =>
            x < 0.62f ? Rarity.RarityLevels.common : x < 0.89f ? Rarity.RarityLevels.uncommon : x < 0.98f ? Rarity.RarityLevels.rare : Rarity.RarityLevels.epic;

        // Called from the wreck-loot postfix (PartsSurvival): maybe drops salvage for this block. Returns what dropped.
        internal static string Drop(EntityBricks bricks, ProjectileDamage projDmg, BrickInstanceData bd, System.Reflection.MethodInfo spawn)
        {
            var hash = new SkeetoHash((uint)bd.instanceId ^ 0x5A17A6Eu);
            if (hash.GetFloat() >= DropChance) return null;
            var kind = KindFor(bd.Brick);
            var rarity = Roll(hash.GetFloat());
            var size = bd.GetQuantity();
            var count = 1f + Mathf.Floor(hash.GetFloat() * Mathf.Min(3f, size / 4f));
            var item = MakeItem(kind, rarity, count, ref hash);
            item.disposeTime = 0f;   // the game's plain scrap vanishes after two minutes in survival; salvage stays
            object[] args = { item, bd, projDmg, 1f, hash };
            spawn.Invoke(bricks, args);
            return $"{count} {Name(kind, rarity)}";
        }

        // DevBridge test: forces a salvage drop from the first block of each kind found (a ship computer, a thruster, a
        // hull), as if its wreck were broken up, and says what dropped.
        public static string TestDrops()
        {
            var spawn = AccessTools.Method(typeof(EntityBricks), "SpawnMiningLootItem");
            var chance = DropChance;
            DropChance = 1f;
            try
            {
                var lines = new List<string>();
                foreach (var pick in new Func<Command, bool>[] { c => c.brickData.brickId == TabLogic.SPACESHIP_COMPUTER.brickID, c => c is SystemThruster })
                {
                    var cmd = CommandManager.commands.Datas.OfType<Command>().FirstOrDefault(c => c.brickCoord.entity != null && pick(c));
                    if (cmd == null) { lines.Add("none found"); continue; }
                    var what = Drop(cmd.brickCoord.entity.Bricks, new ProjectileDamage { localNormal = Vector3.up }, cmd.brickData, spawn);
                    lines.Add($"{cmd.brickData.Brick.name} ({KindFor(cmd.brickData.Brick)}): {what ?? "nothing"}");
                }
                return string.Join("; ", lines.ToArray());
            }
            finally { DropChance = chance; }
        }

        // DevBridge test, for screenshots: drops one stack of each kind, at rising rarity, on the ground a couple of
        // metres in front of the player.
        public static string DropInFront()
        {
            var player = Dependency.PlayerManager;
            if (player == null) return "no player";
            var frame = SkyTransformData.datas[player.SkyTransform.id];
            var picks = new[] { (Kind.General, Rarity.RarityLevels.common), (Kind.Automotive, Rarity.RarityLevels.uncommon),
                                (Kind.Electronics, Rarity.RarityLevels.rare), (Kind.Automotive, Rarity.RarityLevels.epic) };
            for (int i = 0; i < picks.Length; i++)
            {
                var hash = new SkeetoHash((uint)(i + 1) * 7919u);
                var item = MakeItem(picks[i].Item1, picks[i].Item2, 3f, ref hash);
                item.disposeTime = 0f;
                var at = frame.LocalToSystem(new Vector3((i - 1.5f) * 0.7f, 1.2f, 2.2f));
                item.Register();
                item.AllocateAllComponents(new SkyTransformData { systemPosition = at, rotation = Quaternion.identity, scale = 1f });
            }
            return $"dropped {picks.Length} stacks in front of you";
        }

        // ---- Item patches, once the item tables exist ----

        [HarmonyPatch(typeof(CraftingSchematic), nameof(CraftingSchematic.AllocateResources))]
        static class Patches
        {
            static bool done;
            static void Postfix()
            {
                if (done) return;
                done = true;
                try
                {
                    var h = new HarmonyLib.Harmony("evo.core.salvage");
                    h.Patch(AccessTools.Method(typeof(ItemInstanceData), nameof(ItemInstanceData.Name)),
                        postfix: new HarmonyMethod(typeof(Patches), nameof(ItemName)));
                    h.Patch(AccessTools.Method(typeof(ItemInstanceData), nameof(ItemInstanceData.Description)),
                        postfix: new HarmonyMethod(typeof(Patches), nameof(ItemDescription)));
                    h.Patch(AccessTools.Method(typeof(FurnaceCommandData), nameof(FurnaceCommandData.IsItemValidInput)),
                        postfix: new HarmonyMethod(typeof(Patches), nameof(Furnace)));
                    h.Patch(AccessTools.Method(typeof(ItemInstanceData), nameof(ItemInstanceData.GetUnitaryVolumeInLiter)),
                        postfix: new HarmonyMethod(typeof(Patches), nameof(Volume)));
                    h.Patch(AccessTools.Method(typeof(ItemSlot), "SetNewItem"),
                        postfix: new HarmonyMethod(typeof(Patches), nameof(Slot)));
                    // The slot renders the item's icon a moment later and decides the outline again then (cargo only).
                    h.Patch(AccessTools.Method(typeof(ItemSlot), "SetIconRenderTexture"),
                        postfix: new HarmonyMethod(typeof(Patches), nameof(Slot)));
                }
                catch (Exception e) { Core.Log.Error("Salvage: item patches failed: " + e); }
            }

            // Salvage packs smaller than the game's plain scrap (15 L): 3 L a piece. Storage keeps its books in managed
            // code (StorageCommon), so this stays consistent; machines that only check room (collectors, Burst) see
            // the old figure, which just makes them cautious.
            public static float VolumePerPiece = 3f;
            static void Volume(ref ItemInstanceData __instance, ref float __result)
            {
                if (IsOurs(__instance)) __result = VolumePerPiece;
            }

            static readonly AccessTools.FieldRef<ItemSlot, UnityEngine.UI.RawImage> outline = AccessTools.FieldRefAccess<ItemSlot, UnityEngine.UI.RawImage>("sprite_itemOutline");
            static readonly AccessTools.FieldRef<ItemSlot, TMPro.TextMeshProUGUI> corner = AccessTools.FieldRefAccess<ItemSlot, TMPro.TextMeshProUGUI>("topLeftText");
            static readonly AccessTools.FieldRef<ItemSlot, TMPro.TextMeshProUGUI> cornerShadow = AccessTools.FieldRefAccess<ItemSlot, TMPro.TextMeshProUGUI>("topLeftShadowText");

            // In an inventory slot: the rarity's colour as the outline (as the game does for cargo), and the kind in the
            // corner (as it does for ingots), so stacks tell apart at a glance.
            // Computer parts (Parts.cs) get the same treatment from Parts.SlotLook: a short name and a colour by kind.
            static void Slot(ItemSlot __instance)
            {
                try
                {
                    var item = __instance.ItemData;
                    string label;
                    Color32 colour;
                    if (IsOurs(item))
                    {
                        colour = Rarity.GetColor(RarityOf(item.contextualDataId));
                        label = KindOf(item.contextualDataId) == Kind.Electronics ? "ELEC" : KindOf(item.contextualDataId) == Kind.Automotive ? "AUTO" : "GEN";
                    }
                    else if (!Parts.SlotLook(item, out label, out colour)) return;
                    var o = outline(__instance);
                    if (o != null) { o.color = colour; o.enabled = true; }
                    var c = corner(__instance);
                    if (c != null) { c.text = label; c.enabled = true; }
                    var s = cornerShadow(__instance);
                    if (s != null) { s.text = label; s.enabled = true; }
                }
                catch (Exception e) { Core.Log.Warning("Salvage: slot look failed: " + e.Message); }
            }

            static void ItemName(ref ItemInstanceData __instance, ref string __result)
            {
                if (IsOurs(__instance)) __result = Name(KindOf(__instance.contextualDataId), RarityOf(__instance.contextualDataId));
            }

            static void ItemDescription(ref ItemInstanceData __instance, ref string __result)
            {
                if (!IsOurs(__instance)) return;
                var kind = KindOf(__instance.contextualDataId);
                __result = $"Salvage from wrecked {(kind == Kind.Electronics ? "electronics, logic and power blocks" : kind == Kind.Automotive ? "thrusters and machinery" : "hulls and fittings")}. " +
                           "Higher-tier parts and upgrades need it.";
            }

            static void Furnace(ItemInstanceData itemData, ref bool __result)
            {
                if (__result && IsOurs(itemData)) __result = false;
            }
        }
    }
}
