using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;
using Prop = World.WorldPropCommon.WorldPropId;
using static EvoCore.PartsSurvival;
using static Game.Rarity.RarityLevels;

namespace EvoCore
{
    // Computer parts (the Lua chips notes, "Circuit board all the way"): boards and chips as real items. A board goes
    // into a Lua Computer and brings sockets; chips go into the sockets and change what the computer can do. Without a
    // board a computer works as before. First parts: three boards, a memory chip (+1 app slot) and a clock crystal
    // (ticks faster). Installed parts are kept in the computer block (LuaComputer.Hardware), so they save and travel
    // with the ship; taking one out puts the item back in your inventory.
    // Items use the game's systemChip item type, as the dormant upgrade chips do (ChipProbe proved spawn and save):
    // seed = the part's own seed, contextualDataId = Marker | definition << 16 | flags << 24. Marker keeps them apart
    // from the game's own chips (whose type, rarity and equipped flag sit in the low 9 bits, all 0 here).
    public static class Parts
    {
        public const int Marker = 1 << 30;
        public const int PrefabId = 13;   // component_electric: only so a dropped part has a shape
        public const int Damaged = 1;     // flags: a damaged part (the starter board in the wreck); works, with faults later

        public class Def
        {
            public int Id;
            public string Name, Kind, About;   // Kind: "board" or "chip"
            public int Tier, Sockets, Pins;    // Sockets: a board's; Pins: a chip's footprint (fits a socket with as many)
            public int AppSlots;               // a memory chip's extra app slots
            public float Speed;                // a clock's tick speed bonus (0.5 = half again as fast)
            public PartsSurvival.Research Research;   // what unlocks building it in survival; null: salvage only
            public PartsSurvival.Cost[] Cost = new PartsSurvival.Cost[0];
        }

        // Ids are saved in items and computers: never renumber, only add.
        public static readonly Def[] All =
        {
            new Def { Id = 1, Kind = "board", Name = "Starter Board", Tier = 0, Sockets = 2, Pins = 8,
                About = "A small logic board out of an old ship computer. Two small sockets. Found in wrecked ship computers." },
            new Def { Id = 2, Kind = "board", Name = "Logic Board Mk1", Tier = 1, Sockets = 4, Pins = 8,
                About = "A plain logic board. Four small sockets.",
                Research = PartsSurvival.Boards, Cost = new[] { Need(Prop.component_electric, 2f), Need(Prop.plate, 2f), Need(Salvage.Kind.Electronics, common, 3f) } },
            new Def { Id = 3, Kind = "board", Name = "Logic Board Mk2", Tier = 2, Sockets = 6, Pins = 16,
                About = "A bigger logic board. Six sockets that take small or large chips.",
                Research = PartsSurvival.AdvancedBoards, Cost = new[] { Need(Prop.component_electric, 3f), Need(Metal.Gold, 2f), Need(Salvage.Kind.Electronics, uncommon, 3f) } },
            new Def { Id = 4, Kind = "daughter", Name = "Daughter Board", Tier = 2, Sockets = 2, Pins = 8,
                About = "A small board for an Expansion Slot. Two small sockets; its chips count for the Lua Computer the slot is linked to.",
                Research = PartsSurvival.AdvancedBoards, Cost = new[] { Need(Prop.component_electric, 1f), Need(Prop.plate, 1f), Need(Salvage.Kind.Electronics, common, 2f) } },
            new Def { Id = 10, Kind = "chip", Name = "Memory Chip", Tier = 1, Pins = 8, AppSlots = 1,
                About = "More storage: the computer holds one more app.",
                Research = PartsSurvival.Boards, Cost = new[] { Need(Prop.component_electric, 1f), Need(Salvage.Kind.Electronics, common, 2f) } },
            new Def { Id = 11, Kind = "chip", Name = "Clock Crystal", Tier = 1, Pins = 8, Speed = 0.5f,
                About = "A faster clock: the computer's program ticks half again as often.",
                Research = PartsSurvival.Boards, Cost = new[] { Need(Prop.crystal_quartz_item, 1f), Need(Salvage.Kind.Electronics, uncommon, 1f) } },
            new Def { Id = OverchargeId, Kind = "chip", Name = "Overcharge Chip", Tier = 2, Pins = 16,
                About = "Lets one Overcharge node push the thrusters in an output port past full power (up to 120%, +20% per extra chip). A large chip: needs a Mk2 board.",
                Research = PartsSurvival.AdvancedBoards, Cost = new[] { Need(Prop.component_electric, 2f), Need(Salvage.Kind.Automotive, rare, 2f), Need(Salvage.Kind.Electronics, uncommon, 1f) } },
        };

        public const int OverchargeId = 12;

        // A damaged board works with one socket fewer until it's repaired (RepairCost, from the Hardware tab).
        public static int SocketsOf(Def board, int flags) => Math.Max(1, board.Sockets - ((flags & Damaged) != 0 ? 1 : 0));
        public static readonly PartsSurvival.Cost[] RepairCost = { Need(Salvage.Kind.Electronics, common, 1f) };

        public static Def Find(int id) => All.FirstOrDefault(d => d.Id == id);

        // ---- Items ----

        public static bool IsPart(ItemInstanceData item) => item.itemType == WorldItemType.systemChip && (item.contextualDataId & Marker) != 0;
        public static Def DefOf(ItemInstanceData item) => IsPart(item) ? Find((item.contextualDataId >> 16) & 0xFF) : null;
        static int FlagsOf(ItemInstanceData item) => (item.contextualDataId >> 24) & 0xF;

        // How a part looks in an inventory slot (Salvage.cs draws it): a short name in the corner, and an outline by
        // kind: boards green like a circuit board, daughter boards teal, chips amber; red when damaged, cyan for a
        // board that carries a program.
        public static bool SlotLook(ItemInstanceData item, out string label, out Color32 colour)
        {
            label = null;
            colour = default;
            var d = DefOf(item);
            if (d == null) return false;
            label = d.Id == 1 ? "STR" : d.Id == 2 ? "MK1" : d.Id == 3 ? "MK2" : d.Id == 4 ? "DTR" : d.Id == 10 ? "MEM" : d.Id == 11 ? "CLK" : d.Id == OverchargeId ? "OVC" : d.Name.Substring(0, 3).ToUpper();
            if ((FlagsOf(item) & Damaged) != 0) colour = new Color32(220, 60, 50, 255);
            else if (d.Kind == "board" && BoardStore.Peek(item.seed) != null) colour = new Color32(60, 220, 230, 255);
            else colour = d.Kind == "board" ? new Color32(70, 200, 90, 255) : d.Kind == "daughter" ? new Color32(60, 170, 160, 255) : new Color32(235, 170, 40, 255);
            return true;
        }

        public static string NameOf(Def d, int flags) => (flags & Damaged) != 0 ? "Damaged " + d.Name : d.Name;

        internal static ItemInstanceData MakeItem(Def d, uint seed, int flags) => new ItemInstanceData
        {
            itemType = WorldItemType.systemChip,
            prefabId = PrefabId,
            seed = seed,
            contextualDataId = Marker | d.Id << 16 | (flags & 0xF) << 24,
            color = d.Kind == "board" ? new Color(0.15f, 0.45f, 0.2f) : new Color(0.12f, 0.12f, 0.14f),
            scale = 1f,
            quantity = 1f,
        };

        // Puts a new part item in the player's inventory (or the ship's cargo when that's full), as mission rewards do.
        public static bool Give(Def d, uint seed, int flags, ParentEntity ship = null)
        {
            var item = MakeItem(d, seed, flags);
            item.RegisterAndPushItself(StorageCommon.CONTEXT);
            if (StorageCommon.TryToAddToPlayerShipInventory(null, item, addToPlayer: true)) return true;
            if (ship != null && StorageCommon.TryToAddToPlayerShipInventory(ship, item, addToPlayer: false, onlyAddToCargo: true)) return true;
            StorageCommon.CONTEXT.GetItemData(item.id).ReleaseComponentsAndItemId();
            return false;
        }

        // Creative and testing: one of each part, or one by id.
        public static string Spawn(int id)
        {
            var d = Find(id);
            if (d == null) return "no part " + id;
            var seed = (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            return Give(d, seed, 0, ShipOfPlayer()) ? "gave " + d.Name : "couldn't give " + d.Name + " (inventory full?)";
        }

        public static string SpawnAll() => string.Join("; ", All.Select(d => Spawn(d.Id)).ToArray());

        static ParentEntity ShipOfPlayer()
        {
            try { return ParentEntity.PlayerUltimateParentEntity; } catch { return null; }
        }

        // ---- Where the player's parts are: their inventory, and the cargo of the ship they're on ----

        public class Held
        {
            public int ItemId, StorageId;
            public Def Def;
            public uint Seed;
            public int Flags;
            public string Where;
        }

        static IEnumerable<int> StorageItemIds(int storageId)
        {
            var ctx = StorageCommon.CONTEXT;
            int id = ctx.GetStorageData(storageId).rootItemId;
            int guard = 0;
            while (id != 0 && guard++ < 100000)
            {
                yield return id;
                id = ctx.GetItemData(id).nextElementId;
            }
        }

        public static List<Held> Reachable(ParentEntity ship)
        {
            var list = new List<Held>();
            var ctx = StorageCommon.CONTEXT;
            void From(int storageId, string where)
            {
                foreach (var id in StorageItemIds(storageId))
                {
                    var item = ctx.GetItemData(id);
                    var d = DefOf(item);
                    if (d != null) list.Add(new Held { ItemId = id, StorageId = storageId, Def = d, Seed = item.seed, Flags = FlagsOf(item), Where = where });
                }
            }
            try
            {
                var inv = Dependency.PlayerManager?.PlayerInventory;
                if (inv != null) From(inv.storageId, "you");
                if (ship != null)
                    foreach (var bc in ship.Systems.GetCommandsFor(StorageCommon.GetStorageTypes(WorldItemType.systemChip)).ToList())
                        if (bc.Command is ICommandDataStorage s) From(s.StorageId, "cargo");
            }
            catch (Exception e) { Core.Log.Warning("Parts: reading storage failed: " + e.Message); }
            return list;
        }

        // Takes a held part out of its storage and deletes the item (it now lives in the computer).
        static bool Take(Held h)
        {
            var ctx = StorageCommon.CONTEXT;
            if (!StorageCommon.RemoveItemFromStorage(h.StorageId, h.ItemId, ctx)) return false;
            var item = ctx.GetItemData(h.ItemId);
            item.ReleaseComponentsAndItemId();
            return true;
        }

        // ---- What's in a computer ----

        public class Part
        {
            public Def Def;
            public uint Seed;
            public int Flags;
            public string Name => NameOf(Def, Flags);
        }

        public class Hardware
        {
            public Part Board;
            public Part[] Sockets = new Part[0];
            public Software Parked;   // the computer's built-in program while a board is in (BoardSoftware.cs)
            public IEnumerable<Part> Chips => Sockets.Where(s => s != null);
        }

        // Saved text: "hw1\n", then "B <def> <seed> <flags>" and one "S <socket> <def> <seed> <flags>" per filled socket,
        // then, while a board is in, "parked\n" and the built-in program (Software.Write).
        public static string Write(Hardware hw)
        {
            if (hw?.Board == null) return null;
            var sb = new StringBuilder("hw1\n");
            sb.Append($"B {hw.Board.Def.Id} {hw.Board.Seed} {hw.Board.Flags}\n");
            for (int i = 0; i < hw.Sockets.Length; i++)
                if (hw.Sockets[i] != null) sb.Append($"S {i} {hw.Sockets[i].Def.Id} {hw.Sockets[i].Seed} {hw.Sockets[i].Flags}\n");
            if (hw.Parked != null) sb.Append("parked\n").Append(hw.Parked.Write());
            return sb.ToString();
        }

        public static Hardware Read(string text)
        {
            var hw = new Hardware();
            if (string.IsNullOrEmpty(text) || !text.StartsWith("hw1\n")) return hw;
            var parkedAt = text.IndexOf("\nparked\n", StringComparison.Ordinal);
            if (parkedAt >= 0)
            {
                try { var at = parkedAt + "\nparked\n".Length; hw.Parked = Software.Read(text, ref at); }
                catch (Exception e) { Core.Log.Warning("Parts: bad parked program: " + e.Message); }
                text = text.Substring(0, parkedAt + 1);
            }
            foreach (var line in text.Split('\n').Skip(1))
            {
                var p = line.Split(' ');
                try
                {
                    if (p[0] == "B" && p.Length >= 4 && Find(int.Parse(p[1])) is Def b)
                    {
                        hw.Board = new Part { Def = b, Seed = uint.Parse(p[2]), Flags = int.Parse(p[3]) };
                        hw.Sockets = new Part[SocketsOf(b, hw.Board.Flags)];
                    }
                    else if (p[0] == "S" && p.Length >= 5 && hw.Board != null && Find(int.Parse(p[2])) is Def c)
                    {
                        var i = int.Parse(p[1]);
                        if (i >= 0 && i < hw.Sockets.Length) hw.Sockets[i] = new Part { Def = c, Seed = uint.Parse(p[3]), Flags = int.Parse(p[4]) };
                    }
                }
                catch (Exception e) { Core.Log.Warning($"Parts: bad hardware line '{line}': {e.Message}"); }
            }
            return hw;
        }

        // Read once per change of the saved text (the effects are asked for every tick).
        static readonly Dictionary<LuaComputer, (string Text, Hardware Hw)> cache = new Dictionary<LuaComputer, (string, Hardware)>();

        public static Hardware Of(LuaComputer c)
        {
            if (c == null) return new Hardware();
            if (cache.TryGetValue(c, out var known) && ReferenceEquals(known.Text, c.Hardware)) return Copy(known.Hw);
            var hw = Read(c.Hardware);
            cache[c] = (c.Hardware, hw);
            return Copy(hw);
        }

        // Callers change what they get, so they get their own copy.
        static Hardware Copy(Hardware hw) => new Hardware { Board = hw.Board, Sockets = (Part[])hw.Sockets.Clone(), Parked = hw.Parked };

        // The chips working for a computer: on its own board, and on the boards of Expansion Slots linked to it.
        public static List<Part> ChipsFor(LuaComputer c)
        {
            var chips = Of(c).Chips.ToList();
            if (c == null || c is ExpansionSlot || c is Workstation) return chips;
            try { foreach (var slot in c.Inputs.Concat(c.Outputs).OfType<ExpansionSlot>().Distinct()) chips.AddRange(Of(slot).Chips); }
            catch { }
            return chips;
        }

        // The Lua Computer an Expansion Slot serves: the first one linked to it, either way.
        public static LuaComputer MainOf(ExpansionSlot slot)
        {
            try { return slot.Inputs.Concat(slot.Outputs).OfType<LuaComputer>().FirstOrDefault(c => !(c is ExpansionSlot) && !(c is Workstation)); }
            catch { return null; }
        }

        // Effects. Extra app slots from memory chips; tick speed from clocks (1 = normal).
        public static int AppSlots(LuaComputer c) => ChipsFor(c).Sum(p => p.Def.AppSlots);
        public static float Speed(LuaComputer c) => 1f + ChipsFor(c).Sum(p => p.Def.Speed);

        // Puts a held board or chip into the computer. A board needs an empty board slot; a chip a free socket that fits.
        public static string Install(LuaComputer c, Held h, ParentEntity ship)
        {
            var hw = Of(c);
            int socket = -1;
            if (c is SalvageProcessor) return "The Salvage Processor takes no parts.";
            if (c is Workstation && h.Def.Kind != "board") return "The workstation's bench takes a board to program, not chips or daughter boards.";
            if (h.Def.Kind == "board" || h.Def.Kind == "daughter")
            {
                if (c is ExpansionSlot && h.Def.Kind != "daughter") return "An Expansion Slot takes a Daughter Board, not a full board.";
                if (!(c is ExpansionSlot) && h.Def.Kind == "daughter") return "A Daughter Board goes in an Expansion Slot linked to the computer.";
                if (hw.Board != null) return "Take the board out first.";
            }
            else
            {
                if (hw.Board == null) return "Put a board in first: chips go in a board's sockets.";
                if (h.Def.Pins > hw.Board.Def.Pins) return $"{h.Def.Name} needs a {h.Def.Pins}-pin socket; this board's are {hw.Board.Def.Pins}-pin.";
                socket = Array.FindIndex(hw.Sockets, s => s == null);
                if (socket < 0) return "No free socket on this board.";
            }
            if (!Take(h)) return "Couldn't take it out of storage.";
            var part = new Part { Def = h.Def, Seed = h.Seed, Flags = h.Flags };
            if (socket >= 0)
            {
                hw.Sockets[socket] = part;
                c.SetHardware(Write(hw));
                return $"Installed {part.Name}.";
            }
            hw.Board = part;
            hw.Sockets = new Part[SocketsOf(h.Def, h.Flags)];
            if (h.Def.Kind != "board") { c.SetHardware(Write(hw)); return $"Installed {part.Name}."; }
            // A main board brings its program: the computer's own is parked until the board comes out. A blank board
            // takes a copy of what the computer runs now.
            var software = BoardStore.Take(part.Seed);
            hw.Parked = Software.Of(c);
            c.SetHardware(Write(hw));
            if (software != null)
            {
                software.ApplyTo(c);
                return $"Installed {part.Name}: running its {(software.Kind == "graph" ? "circuit" : "Lua")} program.";
            }
            return c is Workstation ? $"Installed {part.Name} on the bench: a blank board. Program it with the button below." :
                $"Installed {part.Name}: a blank board, so it took a copy of this computer's program.";
        }

        // Takes a chip (socket >= 0) or the board (socket -1, with every chip on it) out, back into the inventory.
        public static string Remove(LuaComputer c, int socket, ParentEntity ship)
        {
            var hw = Of(c);
            var back = new List<Part>();
            Software parked = null;
            var carried = false;
            if (socket < 0)
            {
                if (hw.Board == null) return "No board.";
                back.AddRange(hw.Chips);
                back.Add(hw.Board);
                // A main board takes its program with it; the computer gets its own back.
                if (hw.Board.Def.Kind == "board")
                {
                    var software = Software.Of(c);
                    BoardStore.Put(hw.Board.Seed, software);
                    carried = !software.Blank;
                    parked = hw.Parked ?? new Software();
                }
                hw = new Hardware();
            }
            else
            {
                if (socket >= hw.Sockets.Length || hw.Sockets[socket] == null) return "That socket is empty.";
                back.Add(hw.Sockets[socket]);
                hw.Sockets[socket] = null;
            }
            c.SetHardware(Write(hw));
            parked?.ApplyTo(c);
            var lost = back.Where(p => !Give(p.Def, p.Seed, p.Flags, ship)).Select(p => p.Name).ToList();
            var taken = $"Took out {string.Join(", ", back.Select(p => p.Name).ToArray())}" +
                        (carried ? "; its program went with the board" : "") + (parked != null && !(c is Workstation) ? ", and the computer's own program is back." : ".");
            return lost.Count == 0 ? taken : $"No room for {string.Join(", ", lost.ToArray())}: it's lost. Make room first next time.";
        }

        // Repairs the computer's damaged board: costs RepairCost (free in creative), and its missing socket comes back.
        public static string Repair(LuaComputer c)
        {
            var hw = Of(c);
            if (hw.Board == null || (hw.Board.Flags & Damaged) == 0) return "Nothing to repair.";
            var why = PartsSurvival.Pay(RepairCost);
            if (why != null) return why;
            var board = new Part { Def = hw.Board.Def, Seed = hw.Board.Seed, Flags = hw.Board.Flags & ~Damaged };
            var sockets = new Part[SocketsOf(board.Def, board.Flags)];
            Array.Copy(hw.Sockets, sockets, Math.Min(hw.Sockets.Length, sockets.Length));
            c.SetHardware(Write(new Hardware { Board = board, Sockets = sockets, Parked = hw.Parked }));
            return $"Repaired the {board.Name}: {sockets.Length} sockets now.";
        }

        // Builds a part from the materials in your backpack (survival: once its research is done; creative: free).
        public static string Build(Def d)
        {
            if (d.Research == null) return d.Name + " can't be built, only found.";
            if (PlayerState.IsSurvival && !d.Research.Unlocked) return $"Research \"{d.Research.Name}\" first.";
            var why = PartsSurvival.Pay(d.Cost);
            if (why != null) return why;
            var seed = (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            return Give(d, seed, 0, ShipOfPlayer()) ? $"Built a {d.Name}." : $"Built a {d.Name}, but there was no room for it: it's lost.";
        }

        // DevBridge: what every computer has in it.
        public static string Report() => string.Join("\n", CommandManager.commands.Datas.OfType<LuaComputer>()
            .Where(c => c.brickCoord.entity != null)
            .Select(c =>
            {
                var hw = Of(c);
                return $"{c.ScriptName} on {c.brickCoord.entity.Parent?.EntityName}: " + (hw.Board == null ? "no board" :
                    $"{hw.Board.Name}, sockets " + string.Join(", ", hw.Sockets.Select(s => s?.Name ?? "-").ToArray()) +
                    $" | apps +{AppSlots(c)}, speed x{Speed(c):0.##}");
            }).ToArray());

        // DevBridge tests: install the first held part of a definition into the computer with that label, or take out.
        public static string InstallHeld(string label, int defId)
        {
            var c = CommandManager.commands.Datas.OfType<LuaComputer>().FirstOrDefault(x => x.brickCoord.entity != null && x.ScriptName == label);
            if (c == null) return "no computer " + label;
            var ship = c.brickCoord.entity.Parent;
            var h = Reachable(ship).FirstOrDefault(x => x.Def.Id == defId);
            return h == null ? "you don't have part " + defId : Install(c, h, ship);
        }

        // DevBridge tests for boards carrying programs: by block (label, or brick id when label is ""), a held part by seed.
        static LuaComputer Block(string label, int brickId) => CommandManager.commands.Datas.OfType<LuaComputer>()
            .FirstOrDefault(x => x.brickCoord.entity != null && (label != "" ? x.ScriptName == label && !(x is Workstation) && !(x is ExpansionSlot) : x.brickData.brickId == brickId));

        public static string InstallSeed(string label, int brickId, string seed)
        {
            var c = Block(label, brickId);
            if (c == null) return "no such block";
            var h = Reachable(c.brickCoord.entity.Parent).FirstOrDefault(x => x.Seed.ToString() == seed);
            return h == null ? "you don't have part " + seed : Install(c, h, c.brickCoord.entity.Parent);
        }

        public static string RemoveFromBlock(string label, int brickId, int socket)
        {
            var c = Block(label, brickId);
            return c == null ? "no such block" : Remove(c, socket, c.brickCoord.entity.Parent);
        }

        public static string SetBenchProgram(string file)
        {
            var w = CommandManager.commands.Datas.OfType<Workstation>().FirstOrDefault(x => x.brickCoord.entity != null && Of(x).Board != null);
            if (w == null) return "no workstation with a board on its bench";
            var path = System.IO.Path.Combine(Lua.ScriptsDir, file);
            if (!System.IO.File.Exists(path)) return "no file " + path;
            w.SetProgram(file.EndsWith(".circuit") ? "graph" : "lua", System.IO.File.ReadAllText(path).Replace("\r\n", "\n"));
            return "the bench board now holds " + file;
        }

        public static string ProgramOf(string label) => Block(label, 0) is LuaComputer c ? $"{c.ProgramKind}: {(c.Program ?? "(none)").Split('\n')[0]}" : "no such computer";

        public static string RemoveFrom(string label, int socket)
        {
            var c = CommandManager.commands.Datas.OfType<LuaComputer>().FirstOrDefault(x => x.brickCoord.entity != null && x.ScriptName == label);
            return c == null ? "no computer " + label : Remove(c, socket, c.brickCoord.entity.Parent);
        }

        public static string HeldReport() => string.Join("\n", Reachable(ShipOfPlayer()).Select(h => $"{h.Where}: {NameOf(h.Def, h.Flags)} (item {h.ItemId}, seed {h.Seed})").ToArray());

        // ---- Item patches, installed once the game's item tables exist (as ChipProbe does) ----

        [HarmonyPatch(typeof(CraftingSchematic), nameof(CraftingSchematic.AllocateResources))]
        static class InstallItemPatches
        {
            static bool done;
            static void Postfix()
            {
                if (done) return;
                done = true;
                try
                {
                    var h = new HarmonyLib.Harmony("evo.core.parts");
                    h.Patch(AccessTools.Method(typeof(ItemInstanceCommon), nameof(ItemInstanceCommon.GetQuantityMode)),
                        postfix: new HarmonyMethod(typeof(InstallItemPatches), nameof(QuantityMode)));
                    h.Patch(AccessTools.Method(typeof(ItemInstanceData), nameof(ItemInstanceData.Name)),
                        postfix: new HarmonyMethod(typeof(InstallItemPatches), nameof(Name)));
                    h.Patch(AccessTools.Method(typeof(ItemInstanceData), nameof(ItemInstanceData.Description)),
                        postfix: new HarmonyMethod(typeof(InstallItemPatches), nameof(Description)));
                    h.Patch(AccessTools.Method(typeof(ItemInstanceData), "GetItemOrNull"),
                        postfix: new HarmonyMethod(typeof(InstallItemPatches), nameof(Icon)));
                }
                catch (Exception e) { Core.Log.Error("Parts: item patches failed: " + e); }
            }

            // Each part has its own seed, so parts never stack (the game's own chips need the same).
            static void QuantityMode(WorldItemType itemType, ref WorldItemQuantityMode __result)
            {
                if (itemType == WorldItemType.systemChip) __result = WorldItemQuantityMode.single;
            }

            static void Name(ref ItemInstanceData __instance, ref string __result)
            {
                var d = DefOf(__instance);
                if (d != null) __result = NameOf(d, FlagsOf(__instance)) + (d.Kind == "board" && BoardStore.Peek(__instance.seed) != null ? " (programmed)" : "");
            }

            // A chip item's inventory entry has no sprite of its own (the game never finished chips): show the electric
            // component's, which looks like a circuit. Dropped, a part already draws its shape with a glow in its colour.
            static void Icon(ref ItemInstanceData __instance, Item __result)
            {
                if (!(__result is WorldItemPlayer player) || player.SpriteItemID != 0 || !IsPart(__instance)) return;
                if (Item.WorldItemIndex.TryGetValue((World.WorldPropCommon.WorldPropId)PrefabId, out var codex) && codex is WorldItemPlayer stock)
                    player.SetSpriteItemID(stock.SpriteItemID, schematicOf(stock));
            }

            static readonly AccessTools.FieldRef<WorldItemPlayer, int> schematicOf = AccessTools.FieldRefAccess<WorldItemPlayer, int>("schematicId");

            static void Description(ref ItemInstanceData __instance, ref string __result)
            {
                var d = DefOf(__instance);
                if (d == null) return;
                __result = d.About + (d.Kind != "chip" ? $"\n{d.Sockets} sockets, {d.Pins}-pin." : $"\n{d.Pins}-pin chip.") +
                           (d.Kind == "daughter" ? "\nPut it in an Expansion Slot from its Hardware tab." : "\nPut it in a Lua Computer from its Hardware tab.");
            }
        }
    }
}
