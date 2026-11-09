using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Blocks found by label instead of by link: ship.block("cargo door"), ship.blocks("Piston").
    // A label is what the multitool's Tag mode (or a block's own setup window) names a block. Matching ignores case
    // and spaces at the ends, and a name also matches the game's numbered labels: "Piston" finds "Piston #1",
    // "Piston #2". ship.blocks() with no name lists every labelled logic block on the ship.
    // Only logic blocks (anything the link tool can link) are found, on the script's own ship.
    public static partial class Lua
    {
        static readonly Regex numbered = new Regex(@"^(.*?)\s*#\d+$");
        static readonly Dictionary<ParentEntity, KeyValuePair<float, List<Found>>> shipCommands =
            new Dictionary<ParentEntity, KeyValuePair<float, List<Found>>>();

        // Where a logic block is. Lights and other newer blocks keep their state as command data (structs), and the
        // game hands out a copy each time it's asked, so a kept copy goes stale: Now asks again.
        internal class Found
        {
            public VoxelEntity Part;
            public int Index;
            public ICommand Snapshot;

            public ICommand Now
            {
                get
                {
                    try
                    {
                        var bricks = Part != null ? Part.Bricks : null;
                        if (bricks == null || Index >= bricks.brickDatas.Length) return Snapshot;
                        var brick = bricks.brickDatas[Index].Brick;
                        return brick?.GetCommand(bricks.brickIds[Index]) ?? Snapshot;
                    }
                    catch { return Snapshot; }
                }
            }
        }

        // Every logic block on a ship and its sub-constructs, gathered at most once a second however many scripts ask.
        // Read brick by brick, as links read them: the newer blocks never appear in CommandManager's list of Command
        // objects.
        internal static List<Found> ShipCommands(ParentEntity ship)
        {
            if (ship == null) return new List<Found>();
            if (shipCommands.TryGetValue(ship, out var cached) && Time.unscaledTime - cached.Key < 1f) return cached.Value;
            if (shipCommands.Count > 64) shipCommands.Clear();
            var list = new List<Found>();
            foreach (VoxelEntity part in ship.Tree.ChildrensAndSelf)
            {
                var bricks = part.Bricks;
                if (bricks == null) continue;
                for (int i = 0; i < bricks.brickDatas.Length && i < bricks.brickIds.Length; i++)
                {
                    var data = bricks.brickDatas[i];
                    var ids = bricks.brickIds[i];
                    if (!data.IsValid || ids.commandId == 0 || data.Brick == null) continue;
                    var command = data.Brick.GetCommand(ids);
                    if (command != null) list.Add(new Found { Part = part, Index = i, Snapshot = command });
                }
            }
            shipCommands[ship] = new KeyValuePair<float, List<Found>>(Time.unscaledTime, list);
            return list;
        }

        static string Label(ICommand command)
        {
            try { return (command.brickCoord.TagData.label ?? "").Trim(); }
            catch { return ""; }
        }

        static bool Matches(string label, string name)
        {
            if (label == "") return false;
            if (name == null) return true;
            if (string.Equals(label, name, StringComparison.OrdinalIgnoreCase)) return true;
            var m = numbered.Match(label);
            return m.Success && string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase);
        }

        // Natural order, so "Light #2" comes before "Light #10".
        static string SortKey(string label)
        {
            var m = Regex.Match(label, @"^(.*?)(\d+)$");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() + m.Groups[2].Value.PadLeft(8, '0') : label.ToLowerInvariant();
        }

        static List<BlockApi> FindBlocks(ParentEntity ship, string name)
        {
            name = name?.Trim();
            return ShipCommands(ship)
                .Select(c => new { c, label = Label(c.Snapshot) })
                .Where(x => Matches(x.label, name))
                .OrderBy(x => SortKey(x.label), StringComparer.Ordinal)
                .Select(x => new BlockApi(x.c))
                .ToList();
        }

        // One logic block. Setting it works like a link from the script: the block acts as if signalled and passes
        // the value on through its own links.
        public class BlockApi
        {
            readonly Found block;
            internal BlockApi(Found found) { block = found; }

            public string name => Label(block.Snapshot);
            public string kind => block.Snapshot.OverTextIndirect ?? "";
            // The game's own name for the block's code type (LightCommandData, PistonSystem), for blocks with no kind.
            public string type => block.Snapshot.GetType().Name;
            public double state => block.Now.State;
            // Readouts (Readouts.cs): read(name) is one value or nil, values() all of them as a name -> value table.
            public object read(string name) => Read(block.Now, name);
            public Table values(ScriptExecutionContext context) => Values(context.GetScript(), block.Now);
            public bool on
            {
                get => block.Now.State > 0.5f;
                set => LuaComputer.Send(block.Now, value ? 1f : 0f);
            }
            public void set(object value) => LuaComputer.Send(block.Now, LinksApi.ToState(value));
            public void toggle() => on = !on;
        }
    }
}
