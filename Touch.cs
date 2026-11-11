using System;
using System.Linq;
using Common.Settings;
using Game;
using HarmonyLib;
using Manager;
using MoonSharp.Interpreter;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Touch screens: aim at a monitor a Lua Computer draws on and press the game's use key (Activate, F here).
    //   function on_touch(x, y, screen, down, button)
    //     x, y      where on the canvas (the units the script draws in, y down)
    //     screen    the display's number (or display port name), as displays.get takes it
    //     down      true as the key goes down, false as it comes up (the up comes even if you looked away)
    //     button    the name of the display.button under the point, or nil
    //   display.button(name, x, y, w, h, label, color)   a framed button, lit while aimed at
    //   display.hover()                                  x, y where the player aims on it, or nil
    // Only monitors whose computer's script has on_touch take touches: the game shows its use prompt on them, and
    // pressing use there doesn't also switch the monitor. A small dot on the glass shows where you aim. Reach is the
    // game's own for using blocks (4 m). Cockpit glass surfaces (Cockpit Displays) don't take touches yet.
    public static partial class Lua
    {
        public static float TouchReach = 4f;
        static Monitors.Display hover;
        static Vector2 hoverAt;
        static Monitors.Display pressedOn;
        static Vector2 pressedAt;

        static bool HasTouch(LuaComputer c) =>
            c != null && running.TryGetValue(c, out var r) && r.Script != null && r.Script.Globals.Get("on_touch").Type == DataType.Function;

        // A monitor that takes touches right now (for the use-key patches below).
        internal static bool Touchable(Command block)
        {
            var d = Monitors.Of(block);
            return d != null && !d.External && HasTouch(d.Owner);
        }

        internal static Vector2? HoverOn(Monitors.Display d) => d != null && d == hover ? hoverAt : (Vector2?)null;

        // Once a frame, after ScanControls: what the player aims at, and presses.
        static void ScanTouch()
        {
            Monitors.Display now = null;
            var at = default(Vector2);
            var camera = Dependency.RenderPipeline != null ? Dependency.RenderPipeline.playerCamera : null;
            if (camera != null && !InputBlocked)
            {
                var ray = new Ray(camera.transform.position, camera.transform.forward);
                // The game's own pick (ActionActivate) says which block is really in front: nothing between you and it.
                var over = ActionActivate.CommandOver.Value as Command;
                // A display surface (decals) is never the block the game picks, so it's taken on the ray alone.
                if (Monitors.Hit(ray, TouchReach, d => (d.External || d.Block == over) && HasTouch(d.Owner), out var d, out var p)) { now = d; at = p; }
            }
            if (now != hover && hover != null) Light(hover, null);
            hover = now; hoverAt = at;
            if (hover != null) Light(hover, ButtonAt(hover, at));
            Monitors.ShowCursor(hover, hoverAt);

            if (hover != null && ControlKey.Activate.GetKeyDown && !InputBlocked)
            {
                pressedOn = hover; pressedAt = hoverAt;
                Touch(hover, hoverAt, true);
            }
            else if (pressedOn != null && !ControlKey.Activate.GetKey)
            {
                var d = pressedOn;
                pressedOn = null;
                Touch(d, d == hover ? hoverAt : pressedAt, false);
            }
        }

        static string ButtonAt(Monitors.Display d, Vector2 at)
        {
            for (int i = d.Buttons.Count - 1; i >= 0; i--) if (d.Buttons[i].rect.Contains(at)) return d.Buttons[i].name;
            return null;
        }

        static void Light(Monitors.Display d, string button)
        {
            if (d.Hovered == button) return;
            d.Hovered = button;
            d.Dirty = true;
        }

        static void Touch(Monitors.Display d, Vector2 at, bool down)
        {
            if (!running.TryGetValue(d.Owner, out var r) || r.Script == null) return;
            var f = r.Script.Globals.Get("on_touch");
            if (f.Type != DataType.Function) return;
            var button = ButtonAt(d, at);
            Call(r, f, DynValue.NewNumber(Math.Round(at.x, 1)), DynValue.NewNumber(Math.Round(at.y, 1)), Monitors.NameOf(d),
                 DynValue.NewBoolean(down), button == null ? DynValue.Nil : DynValue.NewString(button));
        }

        // The game's use prompt on touch monitors (it only offers use on monitors that enter a seat or run a kiosk).
        [HarmonyPatch(typeof(CommandDisplay), nameof(CommandDisplay.CanActivateManually))]
        static class TouchPrompt
        {
            static void Postfix(CommandDisplay __instance, ref bool __result)
            {
                if (!__result && Touchable(__instance)) __result = true;
            }
        }

        // ...and using one is a touch, not a switch of the monitor.
        [HarmonyPatch(typeof(Command), nameof(Command.OnKeyUp))]
        static class TouchNotSwitch
        {
            static bool Prefix(Command __instance) => !(__instance is CommandDisplay) || !Touchable(__instance);
        }

        // ---- For DevBridge ----

        public static string TouchReport() =>
            hover == null ? "aiming at no touch screen" : $"aiming at {Describe(hover.Block)} at {hoverAt.x:0.0}, {hoverAt.y:0.0} (button {ButtonAt(hover, hoverAt) ?? "none"})";

        // A touch at canvas x, y on the n-th display of the nearest computer that has on_touch (down then up).
        public static string TapNearest(double x, double y)
        {
            var me = Dependency.RenderPipeline?.playerCamera;
            var computer = running.Keys.OfType<LuaComputer>().Where(HasTouch)
                .OrderBy(c => me == null ? 0f : ((Vector3)c.brickCoord.TransformData.localToWorldMatrix.GetColumn(3) - me.transform.position).sqrMagnitude).FirstOrDefault();
            if (computer == null) return "no computer with on_touch";
            var d = Monitors.Get(computer, 1);
            if (d == null) return "it has no display";
            Touch(d, new Vector2((float)x, (float)y), true);
            Touch(d, new Vector2((float)x, (float)y), false);
            return $"tapped {Describe(d.Block)} at {x}, {y} (button {ButtonAt(d, new Vector2((float)x, (float)y)) ?? "none"})";
        }
    }
}
