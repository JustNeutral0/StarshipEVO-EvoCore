using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // The Links tab of the Computer screen: the attached computer's declared ports (Ports.cs) in three columns, inputs,
    // outputs and two-way channels, each with the block in it. Pick lists the computer's actual links to choose from
    // (for an input, then what to read: the block's state or one of its readouts; for a channel, then the other
    // computer's channel); Clear empties the port. Links that aren't in any port are listed under them.
    internal static class LinksEditor
    {
        static string pickKind, pickPort;
        static ICommand pickBlock;
        static Vector2 scroll;
        static GUIStyle head, port, dim;

        static void Styles()
        {
            if (head != null) return;
            head = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            head.normal.textColor = new Color(0.95f, 0.75f, 0.35f);
            port = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            port.normal.textColor = new Color(0.55f, 0.85f, 1f);
            dim = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            dim.normal.textColor = new Color(0.6f, 0.63f, 0.67f);
        }

        internal static void Draw(float height)
        {
            Styles();
            var c = Lua.Attached != null && Lua.Attached.brickCoord.entity != null ? Lua.Attached : null;
            if (c == null) { GUILayout.Label("Open a Lua Computer (the activate key on it) to see its ports."); return; }
            var d = Lua.DeclaredOf(c);
            GUILayout.Label($"Ports of {c.ScriptName}: which linked block is in which port. Saved in the computer, so they don't depend on link order.", dim);
            if (d == null || !d.Any)
                GUILayout.Label("This program declares no ports yet. In Lua: ports.declare{ inputs = {\"throttle\"}, outputs = {\"lights\"}, channels = {\"nav\"} }. " +
                                "In a graph, port in, port out, send and receive nodes declare theirs.", dim);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(height));
            GUILayout.BeginHorizontal();
            Column(c, "INPUTS", "in", d?.Inputs ?? new List<string>(), c.Inputs);
            GUILayout.Space(20);
            Column(c, "OUTPUTS", "out", d?.Outputs ?? new List<string>(), c.Outputs);
            GUILayout.Space(20);
            Column(c, "CHANNELS (two-way, to another Lua Computer)", "ch", d?.Channels ?? new List<string>(),
                c.Inputs.Concat(c.Outputs).OfType<LuaComputer>().Cast<ICommand>().Distinct().ToList());
            GUILayout.EndHorizontal();

            GUILayout.Space(14);
            var used = new HashSet<string>(Lua.LinksOf(c).Select(l => l.Key));
            var loose = c.Inputs.Select(x => ("in from", x)).Concat(c.Outputs.Select(x => ("out to", x))).Where(x => !used.Contains(Lua.KeyOf(x.Item2))).ToList();
            GUILayout.Label("LINKS NOT IN A PORT", head);
            if (loose.Count == 0) GUILayout.Label("none", dim);
            foreach (var (dir, x) in loose) GUILayout.Label($"{dir} {Lua.Describe(x)}", dim);
            GUILayout.EndScrollView();
        }

        static void Column(LuaComputer c, string title, string kind, List<string> ports, List<ICommand> candidates)
        {
            GUILayout.BeginVertical(GUILayout.Width(360));
            GUILayout.Label(title, head);
            var links = Lua.LinksOf(c).Where(l => l.Kind == kind).ToList();
            if (ports.Count == 0) GUILayout.Label("none declared", dim);
            // Declared ports, then any saved ones the program no longer declares (so they can be cleared).
            foreach (var name in ports.Concat(links.Select(l => l.Port).Where(p => !ports.Contains(p))).ToList())
            {
                var link = links.FirstOrDefault(l => l.Port == name);
                var target = Lua.TargetOf(c, link);
                GUILayout.BeginHorizontal();
                GUILayout.Label(name + (ports.Contains(name) ? "" : "  (not declared)"), port, GUILayout.Width(150));
                if (GUILayout.Button(pickKind == kind && pickPort == name ? "Cancel" : "Pick", GUILayout.Width(70)))
                {
                    var same = pickKind == kind && pickPort == name;
                    pickKind = same ? null : kind;
                    pickPort = same ? null : name;
                    pickBlock = null;
                }
                if (link != null && GUILayout.Button("Clear", GUILayout.Width(60))) Lua.Clear(c, kind, name);
                GUILayout.EndHorizontal();
                var shown = link == null ? "empty" : target == null ? "its link is gone (link the block again)" :
                    Lua.Describe(target) + (kind == "in" && link.Extra != "state" ? " · reads " + link.Extra :
                                            kind == "ch" ? " · their " + link.Extra : "");
                GUILayout.Label("   " + shown, dim);
                if (pickKind == kind && pickPort == name) Choices(c, kind, name, candidates);
                GUILayout.Space(6);
            }
            GUILayout.EndVertical();
        }

        // What can go in the port being picked: first a block, then (inputs) what to read or (channels) whose channel.
        static void Choices(LuaComputer c, string kind, string name, List<ICommand> candidates)
        {
            if (pickBlock == null)
            {
                if (candidates.Count == 0)
                    GUILayout.Label(kind == "ch" ? "   Link this computer and another Lua Computer first (either way round)." :
                                    kind == "in" ? "   Link a block into this computer first." : "   Link this computer to a block first.", dim);
                foreach (var x in candidates)
                    if (GUILayout.Button(Lua.Describe(x), GUILayout.Width(330)))
                    {
                        if (kind == "out") { Lua.AssignOut(c, name, x); Done(); }
                        else pickBlock = x;
                    }
                return;
            }
            if (kind == "in")
            {
                GUILayout.Label($"   Read from {Lua.Describe(pickBlock)}:", dim);
                foreach (var value in new[] { "state" }.Concat(Lua.Names(pickBlock)))
                    if (GUILayout.Button(value == "state" ? "its on/off state" : value, GUILayout.Width(330))) { Lua.AssignIn(c, name, pickBlock, value); Done(); }
                return;
            }
            // A channel: the other computer's channel to pair with.
            var other = (LuaComputer)pickBlock;
            var theirs = Lua.DeclaredOf(other)?.Channels ?? new List<string>();
            GUILayout.Label($"   Pair with which channel on {Lua.Describe(other)}?", dim);
            if (theirs.Count == 0) GUILayout.Label("   It declares no channels; pairing uses the same name.", dim);
            foreach (var t in theirs.Count > 0 ? theirs : new List<string> { name })
                if (GUILayout.Button($"{name}  <->  {t}", GUILayout.Width(330))) { Lua.Pair(c, name, other, t); Done(); }
        }

        static void Done() { pickKind = pickPort = null; pickBlock = null; }
    }
}
