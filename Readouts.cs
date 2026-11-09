using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using VoxelEngine;

namespace EvoCore
{
    // A block's readouts: the named values the game keeps beside its link signal, the same ones its own gauges and
    // displays read (a piston's extension_pct, a tank's storage_percentage, a sensor's is_player_detected, a
    // reactor's power_percentage). Each is a CommandStateData field on the block's command; the value itself lives
    // in one shared table indexed by the field's id, so even a stale copy of a struct command reads live.
    // Names are the game's own label for the field when it has one ("Storage Percentage"), else the field's name,
    // both in snake_case. Some may only update while the ship is in use (a parked ship's computer read hull_integrity 0
    // in the first test). Lookups ignore case, spaces and underscores: "Storage Percentage", "storagepercentage"
    // and the raw field name all work. Read only for now.
    public static partial class Lua
    {
        internal class Readout
        {
            public FieldInfo Field;
            public string Name;   // snake_case, what values() lists
            public string Key;    // lower case letters and digits, what lookups compare
            public string Alias;  // the field's own name, same form as Key
        }

        static readonly Dictionary<Type, List<Readout>> readoutsByType = new Dictionary<Type, List<Readout>>();

        // Every CommandStateData field on a command type and its base types, found once per type.
        internal static List<Readout> ReadoutsOf(Type type)
        {
            if (readoutsByType.TryGetValue(type, out var list)) return list;
            list = new List<Readout>();
            var seen = new HashSet<string>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(CommandStateData) || !seen.Add(field.Name)) continue;
                    var tag = field.GetCustomAttributes(typeof(CommandStateData.CommandStateDataField), true)
                        .OfType<CommandStateData.CommandStateDataField>().FirstOrDefault();
                    var name = Snake(!string.IsNullOrEmpty(tag?.label) ? tag.label : field.Name);
                    // Two fields can share a label (a piston's extension in metres and in percent are both "Current
                    // Extension"): the later one goes by its field name instead (extension_pct).
                    if (list.Any(r => r.Name == name)) name = Snake(field.Name);
                    list.Add(new Readout { Field = field, Name = name, Key = Squash(name), Alias = Squash(field.Name) });
                }
            }
            readoutsByType[type] = list;
            return list;
        }

        // "On Item Picked-up" -> on_item_picked_up, "extensionPct" -> extension_pct.
        static string Snake(string text)
        {
            text = Regex.Replace(text, @"([a-z0-9])([A-Z])", "$1_$2");
            text = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "_");
            return text.Trim('_');
        }

        static string Squash(string text)
        {
            var sb = new StringBuilder();
            foreach (var ch in text) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        // The readout's current value, or null if the block or the game's state table isn't there right now.
        static double? ReadValue(ICommand command, Readout readout)
        {
            try
            {
                var data = (CommandStateData)readout.Field.GetValue(command);
                if (data.id == 0 || CommandStateData.stateDatas.Disposed) return null;
                return data.Value;
            }
            catch { return null; }
        }

        internal static double? Read(ICommand command, string name)
        {
            if (command == null || string.IsNullOrEmpty(name)) return null;
            var key = Squash(name);
            var readout = ReadoutsOf(command.GetType()).FirstOrDefault(r => r.Key == key || r.Alias == key);
            return readout != null ? ReadValue(command, readout) : null;
        }

        // The readouts a block has right now, by name (for the port pickers).
        internal static List<string> Names(ICommand command) =>
            command == null ? new List<string>() : ReadoutsOf(command.GetType()).Where(r => ReadValue(command, r).HasValue).Select(r => r.Name).ToList();

        internal static Table Values(Script script, ICommand command)
        {
            var table = new Table(script);
            if (command == null) return table;
            foreach (var readout in ReadoutsOf(command.GetType()))
            {
                var value = ReadValue(command, readout);
                if (value.HasValue) table[readout.Name] = value.Value;
            }
            return table;
        }
    }
}
