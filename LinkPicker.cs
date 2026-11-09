using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // The link tool's port picker: when you link a block and a Lua Computer that declares ports (Ports.cs) with the
    // multitool, a small window asks where the link goes: into one of the computer's input ports (then what to read:
    // the block's state or a readout), out of one of its output ports, or, between two Lua Computers, which channels
    // to pair two ways. The game's link is made as always; this only records the port. Skip (or Esc) leaves the link
    // out of every port, and the Links tab can place it later. Only links made with your own tool ask.
    internal static class LinkPicker
    {
        class Choice
        {
            public string Label;
            public Action Pick;                  // finishes
            public Func<List<Choice>> Then;      // or leads to more choices
        }

        static bool fromTool, open, blockingGame;
        static string title;
        static List<Choice> choices;
        static Vector2 scroll;
        static readonly System.Reflection.PropertyInfo inMenu = AccessTools.Property(typeof(PlayerState), nameof(PlayerState.InMenu));

        [HarmonyPatch(typeof(ActionLink), nameof(ActionLink.LeftClickUp))]
        static class ToolClick
        {
            static void Prefix() => fromTool = true;
            static void Finalizer() => fromTool = false;
        }

        [HarmonyPatch(typeof(CommandCommon), nameof(CommandCommon.LinkToCommand))]
        static class Linked
        {
            static void Postfix(ICommandLinkSource command, ICommand target, bool onLoad)
            {
                if (!fromTool || onLoad || open) return;
                try
                {
                    if (!command.targets.Contains(target)) return;   // that click removed a link
                    var list = ChoicesFor(command, target);
                    if (list.Count == 0) return;
                    title = $"{Lua.Describe(command)}  →  {Lua.Describe(target)}";
                    Show(list);
                }
                catch (Exception e) { Core.Log.Warning("Link picker: " + e.Message); }
            }
        }

        static List<Choice> ChoicesFor(ICommand from, ICommand to)
        {
            var list = new List<Choice>();
            var source = from as LuaComputer;
            var dest = to as LuaComputer;
            // Two computers: a two-way channel first, as that's what linking two computers is mostly for.
            if (source != null && dest != null)
            {
                var mine = Lua.DeclaredOf(source)?.Channels ?? new List<string>();
                var theirs = Lua.DeclaredOf(dest)?.Channels ?? new List<string>();
                foreach (var p in mine)
                    foreach (var q in theirs.Count > 0 ? theirs : new List<string> { p })
                        if (theirs.Count == 0 || mine.Count == 1 || theirs.Count == 1 || p == q)
                            list.Add(new Choice { Label = $"Two-way channel: {p}  <->  {q}", Pick = () => Lua.Pair(source, p, dest, q) });
            }
            // A computer to a monitor or display surface: which display it is.
            if (source != null && Monitors.IsDisplay(to))
                foreach (var p in Lua.DeclaredOf(source)?.Displays ?? new List<string>())
                    list.Add(new Choice { Label = $"Display: {p}", Pick = () => Lua.AssignDisplay(source, p, to) });
            if (dest != null)
                foreach (var p in Lua.DeclaredOf(dest)?.Inputs ?? new List<string>())
                {
                    var names = Lua.Names(from);
                    if (names.Count == 0) list.Add(new Choice { Label = $"Into input port: {p}", Pick = () => Lua.AssignIn(dest, p, from, "state") });
                    else list.Add(new Choice
                    {
                        Label = $"Into input port: {p}  …",
                        Then = () => new[] { "state" }.Concat(names).Select(v => new Choice
                        {
                            Label = v == "state" ? "its on/off state" : "its " + v,
                            Pick = () => Lua.AssignIn(dest, p, from, v),
                        }).ToList(),
                    });
                }
            if (source != null)
                foreach (var p in Lua.DeclaredOf(source)?.Outputs ?? new List<string>())
                    list.Add(new Choice { Label = $"Out of output port: {p}", Pick = () => Lua.AssignOut(source, p, to) });
            return list;
        }

        // For DevBridge: the picker as it would open for a link from one block to another.
        internal static string Demo(ICommand from, ICommand to)
        {
            var list = ChoicesFor(from, to);
            if (list.Count == 0) return "no ports to offer";
            title = $"{Lua.Describe(from)}  →  {Lua.Describe(to)}";
            Show(list);
            return $"picker open with {list.Count} choice(s): " + string.Join(" | ", list.Select(c => c.Label).ToArray());
        }

        internal static bool IsOpen => open;

        internal static void CloseDemo() { if (open) Close(); }

        static void Show(List<Choice> list)
        {
            choices = list;
            scroll = Vector2.zero;
            if (!open)
            {
                open = true;
                // The game's own in-menu flag stops the camera and movement and frees the mouse, as its menus do.
                blockingGame = !PlayerState.InMenu && inMenu?.GetSetMethod(true) != null;
                if (blockingGame) inMenu.SetValue(null, true, null);
            }
        }

        static void Close()
        {
            open = false;
            choices = null;
            if (blockingGame && PlayerState.InMenu) inMenu.SetValue(null, false, null);
            blockingGame = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        internal static void OnGui()
        {
            if (!open) return;
            // The game's look is made in LateTick (it needs the GPU); until then the plain skin will do.
            var old = GUI.skin;
            if (GameLook.Ready) GUI.skin = GameLook.Skin();
            try
            {
                var e = Event.current;
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { e.Use(); Close(); return; }
                var w = 460f;
                var h = Mathf.Min(120f + 34f * choices.Count, Screen.height * 0.7f);
                var r = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
                var tint = GUI.color;
                GUI.color = new Color(0.07f, 0.08f, 0.1f, 0.96f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = tint;
                GUI.Box(r, GUIContent.none);
                GUILayout.BeginArea(new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20));
                GUILayout.Label("Which port is this link?");
                GUILayout.Label(title);
                scroll = GUILayout.BeginScrollView(scroll);
                foreach (var c in choices.ToList())
                    if (GUILayout.Button(c.Label))
                    {
                        if (c.Then != null) { choices = c.Then(); break; }
                        c.Pick?.Invoke();
                        Close();
                        break;
                    }
                GUILayout.EndScrollView();
                if (open && GUILayout.Button("Skip: no port (the Links tab can place it later)")) Close();
                GUILayout.EndArea();
            }
            finally { GUI.skin = old; }
        }
    }
}
