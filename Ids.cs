using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using VoxelEngine;
using VoxelEngine.Crafting;

namespace EvoCore
{
    public enum IdSpace { Brick, Schematic, Research, Material }

    // One place that hands out the ids mods add to the game's tables. Ids end up in ship and inventory saves, so once
    // a key has an id it keeps it for good: the registry (UserData\EvoCore\ids.txt) remembers every claim ever made.
    // A mod asks for the id it has always used; if another key already holds it, it gets the next free one and the
    // clash is logged and shown in the Core tab. Claim before the game builds its tables (a static field is fine).
    //
    // What is actually in the tables is checked too: every brick records the assembly that made it, and the stock
    // recipes and research are noted before any mod adds its own, so the Core tab can show clashes with the game or
    // with mods that don't use the registry (the UCS loader, for one).
    public static class Ids
    {
        public static readonly Dictionary<IdSpace, int> TableSize = new Dictionary<IdSpace, int>
        {
            { IdSpace.Brick, Limits.Bricks }, { IdSpace.Schematic, Limits.Recipes }, { IdSpace.Research, 65536 }, { IdSpace.Material, Limits.Materials },
        };

        // Where a new key with no free preferred id starts looking.
        static readonly Dictionary<IdSpace, int> FirstModId = new Dictionary<IdSpace, int>
        {
            { IdSpace.Brick, 500 }, { IdSpace.Schematic, 196 }, { IdSpace.Research, 1000 }, { IdSpace.Material, 800 },
        };

        public class IdClaim
        {
            public IdSpace Space;
            public string Key;
            public int Id, Preferred;
            public bool ThisSession;
        }

        static readonly Dictionary<(IdSpace, string), IdClaim> registry = new Dictionary<(IdSpace, string), IdClaim>();
        static bool loaded;
        public static readonly List<string> Problems = new List<string>();

        static string FilePath => Path.Combine(Path.Combine(Paths.UserData, "EvoCore"), "ids.txt");

        public static IEnumerable<IdClaim> Claims => registry.Values;

        // The id a key keeps for good. Use a short, stable key such as "Aspects.EmptyVial".
        public static int Claim(IdSpace space, string key, int preferred)
        {
            lock (registry)
            {
                Load();
                if (registry.TryGetValue((space, key), out var known))
                {
                    known.ThisSession = true;
                    if (known.Id != preferred)
                        Note($"{space} {key} keeps id {known.Id} from the registry (the mod asked for {preferred})");
                    return known.Id;
                }
                var id = preferred;
                var holder = Holder(space, id);
                if (holder != null || id < 0 || id >= TableSize[space])
                {
                    id = Enumerable.Range(FirstModId[space], TableSize[space] - FirstModId[space]).First(i => Holder(space, i) == null && !StockHolds(space, i));
                    Note($"{space} {key} wanted {preferred}, held by {holder ?? "nothing (out of range)"}; given {id}");
                }
                registry[(space, key)] = new IdClaim { Space = space, Key = key, Id = id, Preferred = preferred, ThisSession = true };
                Save();
                return id;
            }
        }

        static string Holder(IdSpace space, int id) => registry.Values.FirstOrDefault(c => c.Space == space && c.Id == id)?.Key;

        static bool StockHolds(IdSpace space, int id) =>
            space == IdSpace.Brick ? id < Brick.BRICK_INDEX.Length && Brick.BRICK_INDEX[id] != null : stock.TryGetValue(space, out var s) && s.Contains(id);

        // The stock table sizes. A claim above these only works while the tables are grown (see Limits).
        static readonly Dictionary<IdSpace, int> StockSize = new Dictionary<IdSpace, int>
        {
            { IdSpace.Brick, 1024 }, { IdSpace.Schematic, 256 }, { IdSpace.Research, 65536 }, { IdSpace.Material, 1024 },
        };

        static void Note(string problem)
        {
            Problems.Add(problem);
            Core.Log?.Warning(problem);
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var p = line.Split('\t');
                if (p.Length < 3 || line.StartsWith("#")) continue;
                if (!Enum.IsDefined(typeof(IdSpace), p[0])) continue;
                var space = (IdSpace)Enum.Parse(typeof(IdSpace), p[0]);
                var id = int.Parse(p[2], CultureInfo.InvariantCulture);
                registry[(space, p[1])] = new IdClaim { Space = space, Key = p[1], Id = id, Preferred = id };
            }
        }

        static void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var lines = new List<string> { "# Ids handed out to mods. Saves depend on them: don't edit unless you know why. space<TAB>key<TAB>id" };
            lines.AddRange(registry.Values.OrderBy(c => c.Space).ThenBy(c => c.Id).Select(c => $"{c.Space}\t{c.Key}\t{c.Id}"));
            File.WriteAllLines(FilePath, lines.ToArray());
        }

        // ---- What is really in the tables ----

        // Every brick, by id: the assemblies that constructed one at that id, in order. More than one is a clash.
        public static readonly Dictionary<int, List<(string owner, string name)>> BrickMakers = new Dictionary<int, List<(string, string)>>();

        // Stock recipe and research ids, noted before any mod's additions.
        static readonly Dictionary<IdSpace, HashSet<int>> stock = new Dictionary<IdSpace, HashSet<int>>();

        static readonly Assembly Game = typeof(Brick).Assembly;

        [HarmonyPatch(typeof(Brick), MethodType.Constructor, typeof(int), typeof(string), typeof(string), typeof(ResizeMode), typeof(object[]))]
        static class WatchBricks
        {
            static void Prefix(int id, string name)
            {
                try
                {
                    if (!BrickMakers.TryGetValue(id, out var makers)) BrickMakers[id] = makers = new List<(string, string)>();
                    makers.Add((Maker(), name));
                }
                catch { }
            }

            // The first frame outside the brick constructors, Harmony and this class. Harmony's patched copy of the
            // constructor is a plain method named DMD<...::.ctor> on Brick, so it's skipped by name.
            static string Maker()
            {
                foreach (var frame in new StackTrace(2, false).GetFrames() ?? new StackFrame[0])
                {
                    var method = frame.GetMethod();
                    var type = method?.DeclaringType;
                    if (type == null || typeof(Brick).IsAssignableFrom(type) && (method.IsConstructor || method.Name.StartsWith("DMD<"))) continue;
                    var asm = type.Assembly;
                    // Skip only this watcher, not all of EvoCore: EvoCore registers blocks of its own (the Lua Computer).
                    if (type == typeof(WatchBricks) || asm == typeof(HarmonyLib.Harmony).Assembly) continue;
                    return asm == Game ? "game" : asm.GetName().Name;
                }
                return "?";
            }
        }

        // Runs before every other mod's postfix on the same method, so only the game's own recipes are in the table.
        [HarmonyPatch(typeof(CraftingSchematic), nameof(CraftingSchematic.AllocateResources))]
        static class NoteStockRecipes
        {
            [HarmonyPriority(Priority.First)]
            static void Postfix()
            {
                if (stock.ContainsKey(IdSpace.Schematic)) return;
                stock[IdSpace.Schematic] = new HashSet<int>(Enumerable.Range(0, CraftingSchematic.Index.Length).Where(i => CraftingSchematic.Index[i].IsValid));
                var definitions = (ResearchDefinition[])AccessTools.Field(typeof(ResearchCommon), nameof(ResearchCommon.Definitions)).GetValue(null);
                stock[IdSpace.Research] = new HashSet<int>(definitions.Select(d => (int)d.Id));
            }
        }

        public class TableReport
        {
            public IdSpace Space;
            public int Size, Used, Stock, Claimed;
            public int Free => Size - Used;
            public readonly List<string> Problems = new List<string>();
            public readonly List<string> Strangers = new List<string>(); // filled by a mod that doesn't use the registry
        }

        // Checks the live tables against the claims. Recipes and research are only known once the game has set them up.
        public static List<TableReport> Check()
        {
            var reports = new List<TableReport>();
            var ours = registry.Values.Where(c => c.ThisSession).ToList();

            var bricks = new TableReport { Space = IdSpace.Brick, Size = Brick.BRICK_INDEX.Length };
            for (int i = 0; i < Brick.BRICK_INDEX.Length; i++)
            {
                var brick = Brick.BRICK_INDEX[i];
                BrickMakers.TryGetValue(i, out var makers);
                var claim = ours.FirstOrDefault(c => c.Space == IdSpace.Brick && c.Id == i);
                if (brick != null) bricks.Used++;
                if (makers != null && makers.Count > 1)
                    bricks.Problems.Add($"brick {i} made {makers.Count} times: " + string.Join(", then ", makers.Select(m => $"{m.name} by {m.owner}").ToArray()));
                var first = makers?.FirstOrDefault().owner;
                if (first == "game") bricks.Stock++;
                if (claim != null)
                {
                    bricks.Claimed++;
                    if (first == "game") bricks.Problems.Add($"{claim.Key} claimed brick {i}, which is the game's {makers[0].name}");
                }
                else if (brick != null && first != null && first != "game")
                    bricks.Strangers.Add($"{i} {brick.name} ({first})");
            }
            reports.Add(bricks);

            if (stock.ContainsKey(IdSpace.Schematic))
            {
                var recipes = new TableReport { Space = IdSpace.Schematic, Size = CraftingSchematic.Index.Length };
                for (int i = 0; i < CraftingSchematic.Index.Length; i++)
                {
                    if (!CraftingSchematic.Index[i].IsValid) continue;
                    recipes.Used++;
                    var isStock = stock[IdSpace.Schematic].Contains(i);
                    if (isStock) recipes.Stock++;
                    var claim = ours.FirstOrDefault(c => c.Space == IdSpace.Schematic && c.Id == i);
                    if (claim != null)
                    {
                        recipes.Claimed++;
                        if (isStock) recipes.Problems.Add($"{claim.Key} claimed recipe {i}, which the game already uses");
                    }
                    else if (!isStock) recipes.Strangers.Add(i.ToString());
                }
                reports.Add(recipes);

                var definitions = (ResearchDefinition[])AccessTools.Field(typeof(ResearchCommon), nameof(ResearchCommon.Definitions)).GetValue(null);
                var research = new TableReport { Space = IdSpace.Research, Size = definitions.Length, Used = definitions.Length };
                foreach (var d in definitions)
                {
                    var id = (int)d.Id;
                    var isStock = stock[IdSpace.Research].Contains(id);
                    if (isStock) research.Stock++;
                    var claim = ours.FirstOrDefault(c => c.Space == IdSpace.Research && c.Id == id);
                    if (claim != null)
                    {
                        research.Claimed++;
                        if (isStock) research.Problems.Add($"{claim.Key} claimed research {id}, which the game already uses");
                    }
                    else if (!isStock) research.Strangers.Add($"{id} {d.Name}");
                }
                foreach (var dup in definitions.GroupBy(d => d.Id).Where(g => g.Count() > 1))
                    research.Problems.Add($"research {(int)dup.Key} is listed {dup.Count()} times");
                reports.Add(research);
            }

            if (!Limits.Complete)
                foreach (var claim in ours.Where(c => c.Id >= StockSize[c.Space]))
                    bricks.Problems.Add($"{claim.Key} holds {claim.Space} {claim.Id}, past the stock table, but the tables aren't fully grown");
            foreach (var claim in ours.Where(c => c.Space == IdSpace.Brick && Brick.BRICK_INDEX[c.Id] == null))
                bricks.Problems.Add($"{claim.Key} claimed brick {claim.Id} but never made it");
            return reports;
        }
    }
}
