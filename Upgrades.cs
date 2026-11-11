using System;
using System.Collections.Generic;
using System.Linq;
using Manager;
using MoonSharp.Interpreter;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Upgrade nodes (the Lua chips notes, "upgrades are nodes"): a chip on the computer's board (Parts.cs) lets one node
    // of its kind run in the program, and the node acts on the blocks linked into an output port. The first one is
    // Overcharge: thrusters in the port run past full power. Plain links stay clamped to 0-1 (LuaComputer.Signal); going
    // past 1 is what the chip is for, and the thruster draws power for it like any state above full.
    // Lua, on computers: upgrades.overcharge(port, throttle, boost) -> true, or false and why.
    public static partial class Lua
    {
        static void ExposeUpgrades() => Expose("upgrades", c => c.Host is LuaComputer computer ? new UpgradesApi(c, computer) : null);

        // DevBridge tests: links the computer with this label to the first thruster on its ship (output 1), and reads
        // that thruster back: its state and thrust.
        public static string LinkFirstThruster(string label)
        {
            var c = CommandManager.commands.Datas.OfType<LuaComputer>().FirstOrDefault(x => x.brickCoord.entity != null && x.ScriptName == label);
            if (c == null) return "no computer " + label;
            var t = CommandManager.commands.Datas.OfType<SystemThruster>().FirstOrDefault(x => x.brickCoord.entity != null && x.brickCoord.entity.Parent == c.brickCoord.entity.Parent);
            if (t == null) return "no thruster on its ship";
            if (c.Outputs.Contains(t)) return "already linked";
            CommandCommon.LinkToCommand((ICommandLinkSource)c, t, onLoad: false, onInit: false);
            return "linked to a thruster; outputs now " + c.Outputs.Count;
        }

        public static string ThrusterReport(string label)
        {
            var c = CommandManager.commands.Datas.OfType<LuaComputer>().FirstOrDefault(x => x.brickCoord.entity != null && x.ScriptName == label);
            var t = c?.Outputs.OfType<SystemThruster>().FirstOrDefault();
            if (t == null) return "no linked thruster";
            // The game only refreshes a thruster's thrust while the ship is flown; work it out now.
            HarmonyLib.AccessTools.Method(typeof(SystemThruster), "UpdateThrustVectors")?.Invoke(t, null);
            return $"state {t.State:0.###}, forward thrust {t.ForwardThrust.magnitude:N0}";
        }

        public class UpgradesApi
        {
            readonly LuaComputer computer;
            readonly Context context;
            // Ports this program overcharged in the last second, and what each was last sent.
            readonly Dictionary<string, float> lastUse = new Dictionary<string, float>();
            readonly Dictionary<string, float> sent = new Dictionary<string, float>();
            string warned;

            public UpgradesApi(Context c, LuaComputer host)
            {
                context = c;
                computer = host;
                // When the program stops, put the thrusters back to full at most.
                c.Stopped += () =>
                {
                    foreach (var port in sent.Keys.ToList())
                    {
                        var target = Resolve(computer, true, DynValue.NewString(port), out _);
                        if (target != null && sent[port] > 1f) LuaComputer.Send(target, 1f);
                    }
                };
            }

            static int Chips(LuaComputer c, int defId) => Parts.ChipsFor(c).Count(p => p.Def.Id == defId);

            public DynValue overcharge(DynValue port, double throttle, double boost)
            {
                var key = port.Type == DataType.String ? port.String : port.CastToString() ?? "";
                var chips = Chips(computer, Parts.OverchargeId);
                if (chips == 0) return Drop(port, key, throttle, "needs an Overcharge Chip on the computer's board (Hardware tab)");
                lastUse[key] = Time.unscaledTime;
                var inUse = lastUse.Count(p => Time.unscaledTime - p.Value < 1f);
                if (inUse > chips) return Drop(port, key, throttle, $"{inUse} overcharge nodes, {chips} Overcharge Chip(s): one chip runs one node");
                var target = Resolve(computer, true, port, out _);
                if (target == null) return Refuse($"nothing in output port {key} (Links tab)");
                if (!(target is SystemThruster)) return Refuse($"port {key} holds a {target.GetType().Name}, not a thruster");
                var max = 1f + 0.2f * chips;
                var state = Mathf.Clamp01(float.IsNaN((float)throttle) ? 0f : (float)throttle) * Mathf.Clamp((float)boost, 1f, max);
                // Only when it changes, as port out does.
                if (!sent.TryGetValue(key, out var last) || Mathf.Abs(last - state) > 0.0001f)
                {
                    var player = Dependency.RenderPipeline != null ? Dependency.RenderPipeline.commandManager : null;
                    target.Activate(manualActivation: false, player, state);
                    sent[key] = state;
                }
                warned = null;
                return DynValue.True;
            }

            // Without its chip the node stops boosting: a port it pushed past full goes back to the plain throttle.
            // (Also a thruster saved past full while its chip was still in, found that way after a reload.)
            DynValue Drop(DynValue port, string key, double throttle, string why)
            {
                var target = Resolve(computer, true, port, out _);
                if (target != null && ((sent.TryGetValue(key, out var last) && last > 1f) || target.State > 1f))
                {
                    var plain = Mathf.Clamp01(float.IsNaN((float)throttle) ? 0f : (float)throttle);
                    LuaComputer.Send(target, plain);
                    sent[key] = plain;
                }
                return Refuse(why);
            }

            DynValue Refuse(string why)
            {
                if (warned != why) { warned = why; Core.Log.Msg($"Lua {context.Name}: overcharge: {why}"); }
                return DynValue.NewTuple(DynValue.False, DynValue.NewString(why));
            }
        }
    }
}
