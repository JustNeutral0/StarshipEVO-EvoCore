using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Emit;
using HarmonyLib;
using Unity.Collections;
using VoxelEngine;

namespace EvoCore
{
    // Grows the game's id tables past their stock sizes, so mods have room: bricks 1024 -> 2048, materials
    // 1024 -> 2048, recipes 256 -> 512. Proven in game (IdProbe, 2026-11-03): a brick at 1500 with its material at
    // 1100 renders and survives a save. Brick ids are a ushort in saves, so nothing about the save format changes;
    // but a save holding a block above 1023 needs EvoCore to load.
    //
    // Grow() runs from EvoCore's EarlyInit, before any mod's Harmony patches compile game methods: Mono bakes a
    // static readonly field's value (CraftingSchematic.Index) into each method it compiles.
    public static class Limits
    {
        public const int Bricks = 2048, Materials = 2048, Recipes = 512;
        public static readonly List<string> Notes = new List<string>();
        public static bool BrickPropertiesGrown, CollidersGrown, MaterialsGrown;

        public static bool Complete => BrickPropertiesGrown && CollidersGrown && MaterialsGrown;

        // Escape hatch for tracking down a problem: UserData\EvoCore\no-grow.txt leaves every table at its stock size.
        public static readonly bool Off = File.Exists(Path.Combine(Paths.UserData, "EvoCore", "no-grow.txt"));

        static void Note(string s) { Notes.Add(s); Core.Log.Msg(s); }

        internal static void Grow()
        {
            if (Off) { Note("Id tables left at stock sizes (no-grow.txt)"); return; }
            try
            {
                if (Brick.BRICK_INDEX.Length < Bricks)
                {
                    var bigger = new Brick[Bricks];
                    Array.Copy(Brick.BRICK_INDEX, bigger, Brick.BRICK_INDEX.Length);
                    Brick.BRICK_INDEX = bigger;
                }
                var index = AccessTools.Field(typeof(CraftingSchematic), nameof(CraftingSchematic.Index));
                var recipes = (CraftingSchematic[])index.GetValue(null);
                if (recipes.Length < Recipes)
                {
                    var more = new CraftingSchematic[Recipes];
                    Array.Copy(recipes, more, recipes.Length);
                    index.SetValue(null, more);
                }
                Note($"Id tables: bricks {Brick.BRICK_INDEX.Length}, recipes {((CraftingSchematic[])index.GetValue(null)).Length}");
            }
            catch (Exception e) { Note("Couldn't grow the id tables: " + e); }
        }

        // The baked material table is 1024 long; keep its entries in a longer array.
        [HarmonyPatch(typeof(VoxelMaterialResources), nameof(VoxelMaterialResources.SetMaterialParamDatas))]
        static class GrowMaterials
        {
            static void Prefix(ref VoxelMaterial[] newData)
            {
                if (newData == null || Off) return;
                if (newData.Length < Materials) Array.Resize(ref newData, Materials);
                MaterialsGrown = true;
            }
        }

        // BrickProperties is allocated with a literal 1024 inside LoadAllResourcesRuntime; size it from the brick table.
        // If a game update moves the literal, the method is left as it was and the Core tab says so.
        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class GrowBrickProperties
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var index = AccessTools.Field(typeof(Brick), nameof(Brick.BRICK_INDEX));
                var list = new List<CodeInstruction>(code);
                if (Off) return list;
                var at = list.FindIndex(op => op.opcode == OpCodes.Ldc_I4 && op.operand is int n && n == 1024);
                if (at < 0) return list;
                var labels = list[at].labels;
                list.RemoveAt(at);
                list.InsertRange(at, new[]
                {
                    new CodeInstruction(OpCodes.Ldsfld, index).WithLabels(labels),
                    new CodeInstruction(OpCodes.Ldlen),
                    new CodeInstruction(OpCodes.Conv_I4),
                });
                BrickPropertiesGrown = true;
                return list;
            }
        }

        // Collider slots 0..1023 are brick ids, and a brick's extra sub-shapes are numbered from 1024 in a 2048 table.
        // With brick ids to 2047, sub-shapes start at 2048 in a table twice that.
        [HarmonyPatch(typeof(BrickCollider), nameof(BrickCollider.AllocateResources))]
        static class GrowColliders
        {
            static void Postfix()
            {
                if (Off) return;
                try
                {
                    BrickCollider.Datas.Dispose();
                    BrickCollider.Datas = new NativeArray<BrickCollider.Data>(Brick.BRICK_INDEX.Length * 2, Allocator.Persistent);
                    AccessTools.Field(typeof(BrickCollider), "ShapeCount").SetValue(null, Brick.BRICK_INDEX.Length);
                    CollidersGrown = true;
                }
                catch (Exception e) { Note("Couldn't grow the collider table: " + e); }
            }
        }

        // Bricks take material rows from the baked table in load (id) order, through one cursor. Stock bricks sit at
        // ids up to 504, so a mod brick loading among them (the Lua Computer at 500) used to take the next stock
        // brick's rows and shift every one after it: Round Tile, Cone, Drill and Drill Head Frame drew the wrong
        // materials. Mod bricks now take their rows from the grown part of the table, from 1024 up, and the stock
        // cursor carries on untouched. A mod brick is one that wasn't in the brick table when loading began, or
        // one claimed through Ids. Mods' own row-copying prefixes run inside this, so they need no change.
        public const int ModRowStart = 1024;
        public static int ModRowCursor = ModRowStart;
        static readonly HashSet<int> stockBricks = new HashSet<int>();
        static int stockCursor = -1;

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class NoteStockBricks
        {
            [HarmonyPriority(Priority.First)] // before any mod's prefix adds its bricks
            static void Prefix()
            {
                stockBricks.Clear();
                for (int i = 0; i < Brick.BRICK_INDEX.Length; i++)
                    if (Brick.BRICK_INDEX[i] != null) stockBricks.Add(i);
                foreach (var claim in Ids.Claims)
                    if (claim.Space == IdSpace.Brick) stockBricks.Remove(claim.Id);
                ModRowCursor = ModRowStart;
            }
        }

        [HarmonyPatch(typeof(Brick), "LoadResourcesRuntime")]
        static class ModRows
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix(Brick __instance)
            {
                stockCursor = -1;
                if (Off || !MaterialsGrown || stockBricks.Count == 0 || stockBricks.Contains(__instance.brickID)) return;
                stockCursor = VoxelMaterialResources.MaterialCount;
                VoxelMaterialResources.MaterialCount = ModRowCursor;
            }

            // A finalizer, so the stock cursor comes back even if the brick's load throws.
            [HarmonyPriority(Priority.Last)]
            static Exception Finalizer(Exception __exception)
            {
                if (stockCursor < 0) return __exception;
                ModRowCursor = VoxelMaterialResources.MaterialCount;
                VoxelMaterialResources.MaterialCount = stockCursor;
                stockCursor = -1;
                return __exception;
            }
        }

        public static string Status() => Complete
            ? $"Id tables grown: bricks {Brick.BRICK_INDEX.Length}, materials {VoxelMaterialResources.materialParamDatas?.Length} " +
              $"(stock rows to {VoxelMaterialResources.MaterialCount}, mod rows {ModRowStart}-{ModRowCursor}), recipes {CraftingSchematic.Index.Length}"
            : $"Id tables only partly grown (brick properties {BrickPropertiesGrown}, colliders {CollidersGrown}, materials {MaterialsGrown}): keep mod ids under 1024";
    }
}
