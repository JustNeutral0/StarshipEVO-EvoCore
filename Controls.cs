using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using UnityEngine;

namespace EvoCore
{
    // Keyboard, mouse buttons and joysticks for scripts: the `keys` and `joystick` globals.
    //   keys.down(key)            held now
    //   keys.pressed(key)         went down since this script last asked (so a tap between ticks isn't missed)
    //   keys.released(key)        came up since this script last asked
    //   function on_key(key, down)            each key going down or up
    //   joystick.count / joystick.name(n)     connected sticks and pads, 1 to 4
    //   joystick.axis(n, a)                   axis a (1-28) of stick n, -1 to 1
    //   joystick.button(n, b)                 button b (1-20) of stick n held
    //   function on_button(stick, button, down)
    // Key names: letters, digits ("5" is the top row, "keypad5" the number pad), "space", "enter", "esc", "tab",
    // "backspace", "up", "down", "left", "right", "f1".."f15", "shift", "ctrl", "alt" (either side), "leftshift"...,
    // "mouse0".."mouse6", or any Unity KeyCode name. While a menu is open (the game's, the Computer screen, the link
    // picker) every key reads as up and no events fire, so typing doesn't fly the ship. The game reads joystick axes
    // as "Joystick n Axis a" from its own input settings (4 sticks, 28 axes); buttons go through Unity's key codes,
    // which stop at 20 buttons per stick.
    public static partial class Lua
    {
        const int Sticks = 4, StickAxes = 28, StickButtons = 20;
        static readonly KeyCode[] allKeys = ((KeyCode[])Enum.GetValues(typeof(KeyCode))).Distinct().Where(k => k != KeyCode.None).ToArray();
        static readonly Dictionary<KeyCode, string> keyNames = allKeys.ToDictionary(k => k, KeyName);
        static readonly HashSet<KeyCode> held = new HashSet<KeyCode>();
        // Every press and release counted, so each script can see what happened since it last looked.
        static readonly Dictionary<KeyCode, (int presses, int releases)> counts = new Dictionary<KeyCode, (int, int)>();
        static readonly List<(KeyCode key, bool down)> changes = new List<(KeyCode, bool)>();
        static int lastControlsFrame = -1;

        static bool InputBlocked => PlayerState.InMenu || Graphs.Canvas.IsOpen || LinkPicker.IsOpen;

        // Once a frame, before scripts run: which keys and buttons went down or up.
        static void ScanControls()
        {
            if (Time.frameCount == lastControlsFrame) return;
            lastControlsFrame = Time.frameCount;
            changes.Clear();
            if (!Input.anyKey && held.Count == 0) return;
            foreach (var k in allKeys)
            {
                var down = Input.GetKey(k);
                if (down == held.Contains(k)) continue;
                if (down) held.Add(k); else held.Remove(k);
                counts.TryGetValue(k, out var c);
                counts[k] = down ? (c.presses + 1, c.releases) : (c.presses, c.releases + 1);
                changes.Add((k, down));
            }
        }

        // Each script's on_key / on_button for this frame's changes.
        static void DispatchControls(Running r)
        {
            if (changes.Count == 0 || InputBlocked) return;
            var onKey = r.Script.Globals.Get("on_key");
            var onButton = r.Script.Globals.Get("on_button");
            if (onKey.Type != DataType.Function && onButton.Type != DataType.Function) return;
            foreach (var (k, down) in changes)
            {
                if (r.Script == null) return;
                if (StickButton(k, out var stick, out var button))
                {
                    if (onButton.Type == DataType.Function) Call(r, onButton, DynValue.NewNumber(stick), DynValue.NewNumber(button), DynValue.NewBoolean(down));
                }
                else if (onKey.Type == DataType.Function) Call(r, onKey, DynValue.NewString(keyNames[k]), DynValue.NewBoolean(down));
            }
        }

        // Joystick n button b (both from 1) as Unity numbers them: Joystick1Button0 onwards, 20 to a stick.
        static bool StickButton(KeyCode k, out int stick, out int button)
        {
            var i = (int)k - (int)KeyCode.Joystick1Button0;
            stick = i / StickButtons + 1;
            button = i % StickButtons + 1;
            return i >= 0 && stick <= 8;
        }

        static string KeyName(KeyCode k)
        {
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
            if (k >= KeyCode.A && k <= KeyCode.Z) return k.ToString().ToLowerInvariant();
            switch (k)
            {
                case KeyCode.Return: return "enter";
                case KeyCode.Escape: return "esc";
                case KeyCode.UpArrow: return "up";
                case KeyCode.DownArrow: return "down";
                case KeyCode.LeftArrow: return "left";
                case KeyCode.RightArrow: return "right";
                case KeyCode.LeftControl: return "leftctrl";
                case KeyCode.RightControl: return "rightctrl";
                default: return k.ToString().ToLowerInvariant();
            }
        }

        static readonly Dictionary<string, KeyCode[]> keyAliases = new Dictionary<string, KeyCode[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["shift"] = new[] { KeyCode.LeftShift, KeyCode.RightShift },
            ["ctrl"] = new[] { KeyCode.LeftControl, KeyCode.RightControl },
            ["control"] = new[] { KeyCode.LeftControl, KeyCode.RightControl },
            ["alt"] = new[] { KeyCode.LeftAlt, KeyCode.RightAlt },
            ["return"] = new[] { KeyCode.Return, KeyCode.KeypadEnter },
            ["escape"] = new[] { KeyCode.Escape },
        };

        // A key name from a script: our short names, the aliases above, or a Unity KeyCode name.
        static KeyCode[] Keys(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ScriptRuntimeException("a key name, like \"space\" or \"f\"");
            if (keyAliases.TryGetValue(name, out var many)) return many;
            var lower = name.ToLowerInvariant();
            foreach (var pair in keyNames) if (pair.Value == lower) return new[] { pair.Key };
            if (Enum.TryParse(name, true, out KeyCode code)) return new[] { code };
            throw new ScriptRuntimeException($"no key \"{name}\" (try \"space\", \"f\", \"5\", \"shift\", \"f1\", \"keypad5\" or a Unity KeyCode name)");
        }

        static void ExposeControls()
        {
            Expose("keys", c => new KeysApi());
            Expose("joystick", c => new JoystickApi());
        }

        public class KeysApi
        {
            readonly Dictionary<KeyCode, (int presses, int releases)> seen = new Dictionary<KeyCode, (int, int)>();

            public bool down(string key) => !InputBlocked && Keys(key).Any(held.Contains);

            // Each key's press and release counts as this script last saw them: true once per new press or release.
            bool Since(string key, bool presses)
            {
                var any = false;
                foreach (var k in Keys(key))
                {
                    counts.TryGetValue(k, out var now);
                    var known = seen.TryGetValue(k, out var before);
                    seen[k] = now;
                    if (known && (presses ? now.presses > before.presses : now.releases > before.releases)) any = true;
                }
                return any && !InputBlocked;
            }

            public bool pressed(string key) => Since(key, true);
            public bool released(string key) => Since(key, false);
        }

        public class JoystickApi
        {
            public int count => Input.GetJoystickNames().Count(n => !string.IsNullOrEmpty(n));

            public string name(int n)
            {
                var names = Input.GetJoystickNames();
                return n >= 1 && n <= names.Length ? names[n - 1] : "";
            }

            public double axis(int n, int a)
            {
                if (n < 1 || n > Sticks || a < 1 || a > StickAxes || InputBlocked) return 0;
                try { return Input.GetAxisRaw($"Joystick {n} Axis {a}"); }
                catch { return 0; }
            }

            public bool button(int n, int b)
            {
                if (n < 1 || n > 8 || b < 1 || b > StickButtons || InputBlocked) return false;
                return Input.GetKey((KeyCode)((int)KeyCode.Joystick1Button0 + (n - 1) * StickButtons + (b - 1)));
            }

            // For tuning: every axis of a stick that isn't at rest, as "axis=value" text.
            public string moving(int n)
            {
                var parts = new List<string>();
                for (int a = 1; a <= StickAxes; a++)
                {
                    var v = axis(n, a);
                    if (Math.Abs(v) > 0.05) parts.Add($"{a}={v:0.00}");
                }
                for (int b = 1; b <= StickButtons; b++) if (button(n, b)) parts.Add("b" + b);
                return string.Join(" ", parts.ToArray());
            }
        }

        // ---- For DevBridge ----

        // Connected sticks and what each reports right now (axes away from rest, held buttons).
        public static string JoystickReport()
        {
            var names = Input.GetJoystickNames();
            if (names.Length == 0) return "no joysticks";
            var api = new JoystickApi();
            return string.Join("\n", names.Select((n, i) => $"{i + 1}: \"{n}\" {api.moving(i + 1)}").ToArray());
        }
    }
}
