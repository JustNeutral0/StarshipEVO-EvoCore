using System;
using UnityEngine;

namespace EvoCore
{
    // Whether the game is the build these mods were made for. The mods patch the game's own code, so on another
    // build they can break in ways that look like bugs: this says so up front, in the log, on screen for a while
    // and in Core.Report(). It only warns; nothing is switched off.
    // The game's build is its newest patch note ("26w45a": year, week, letter), which the main menu sets, so the
    // check waits for that. Other mods: GameVersion.Ok, GameVersion.Current.
    // DevBridge: get EvoCore.GameVersion.Status()
    public static class GameVersion
    {
        public const string Built = "26w45a";     // the build the mods were made and tested on: change with each game update
        const float ShowSeconds = 25f;

        public static string Current { get; private set; }      // null until the game has said
        public static bool Known => Current != null;
        public static bool Ok => !Known || Current == Built;

        static float shownAt;
        static GUIStyle style;

        internal static void Tick()
        {
            if (Known) return;
            var said = UI.UI_PatchNoteViewer.CurrentVersion;
            if (string.IsNullOrEmpty(said)) return;
            Current = said.Substring(said.LastIndexOf(' ') + 1);
            if (Ok) { Core.Log.Msg("Game build " + Current + ": the one these mods were made for"); return; }
            Core.Log.Warning(Status());
            shownAt = Time.realtimeSinceStartup;
        }

        // 1 when a is the newer build, -1 when b is, 0 when they're the same or don't read as builds.
        static int Compare(string a, string b)
        {
            return Parse(a, out var ka) && Parse(b, out var kb) ? ka.CompareTo(kb) : 0;
        }

        // "26w45a" to a number that sorts by year, week, letter.
        static bool Parse(string s, out int key)
        {
            key = 0;
            var w = s.IndexOf('w');
            if (w < 1 || !int.TryParse(s.Substring(0, w), out var year)) return false;
            var end = w + 1;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            if (end == w + 1 || !int.TryParse(s.Substring(w + 1, end - w - 1), out var week)) return false;
            key = (year * 100 + week) * 100 + (end < s.Length ? char.ToLowerInvariant(s[end]) - 'a' + 1 : 0);
            return true;
        }

        public static string Status()
        {
            if (!Known) return "Game build: not known yet (made for " + Built + ")";
            if (Ok) return "Game build: " + Current + ", the one these mods were made for";
            var order = Compare(Current, Built);
            return "Game build: " + Current + ", but these mods were made for " + Built + ". " +
                   (order > 0 ? "The game is newer: things may break until the mods are updated."
                    : order < 0 ? "The game is older: update the game, or use older mods."
                    : "Things may break.");
        }

        internal static void Gui()
        {
            if (Ok || !Core.VersionWarning || Time.realtimeSinceStartup - shownAt > ShowSeconds) return;
            if (style == null) style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 14, wordWrap = true, richText = false };
            var text = "EvoCore mods\n" + Status();
            var width = Mathf.Min(560f, Screen.width - 40f);
            var height = style.CalcHeight(new GUIContent(text), width) + 8f;
            GUI.Box(new Rect((Screen.width - width) / 2f, 60f, width, height), text, style);
        }
    }
}
