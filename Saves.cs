using System;
using System.Collections.Generic;
using System.IO;
using Common.SkyApplication;
using HarmonyLib;
using Manager.Save;
using UnityEngine;

namespace EvoCore
{
    // Mod data that belongs to one character's save, kept in that save's folder under mods\<mod>\ so it is copied,
    // backed up and deleted along with the save. Mods read it on Loaded and write it on Saving, which fires each time
    // the game itself saves (every minute and on quitting to the menu), so mod state never runs ahead of or behind
    // the game's own files. Only the host or a solo player has a save; a multiplayer client sees no events.
    public static class Saves
    {
        // The loaded character, or null in the menus.
        public static string Character { get; private set; }
        public static bool Active => Character != null;

        public static event Action Loaded, Saving, Unloaded;

        public static string LastEvent = "";
        static readonly Dictionary<string, int> failures = new Dictionary<string, int>();

        public static string Root => Active ? Path.Combine(Path.Combine(Common.SkyApplication.Paths.SAVE_CHARACTER, Character), "mods") : null;

        // This save's folder for one mod, created on first use.
        public static string Dir(string mod)
        {
            if (!Active) throw new InvalidOperationException($"{mod} asked for save data with no save loaded");
            var dir = Path.Combine(Root, mod);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string File(string mod, string name) => Path.Combine(Dir(mod), name);

        // Null when the file isn't there yet.
        public static string[] ReadLines(string mod, string name)
        {
            var path = File(mod, name);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path) : null;
        }

        // Written to a side file first, so a crash mid-write leaves the last good copy.
        public static void WriteLines(string mod, string name, IEnumerable<string> lines)
        {
            var path = File(mod, name);
            System.IO.File.WriteAllLines(path + ".tmp", new List<string>(lines).ToArray());
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(path + ".tmp", path);
        }

        // For mods that kept one global file before saves had their own data: the first save loaded after the update
        // takes the old file over, and the old one is renamed so no other save picks it up. Returns true if it moved.
        public static bool AdoptLegacy(string mod, string name, string legacyPath)
        {
            var path = File(mod, name);
            if (System.IO.File.Exists(path) || !System.IO.File.Exists(legacyPath)) return false;
            System.IO.File.Copy(legacyPath, path);
            var moved = $"{legacyPath}.moved-to-{Character}";
            if (System.IO.File.Exists(moved)) System.IO.File.Delete(moved);
            System.IO.File.Move(legacyPath, moved);
            Core.Log.Msg($"{mod}: moved {Path.GetFileName(legacyPath)} into save \"{Character}\" (old copy kept as {Path.GetFileName(moved)})");
            return true;
        }

        public static int Failures => failures.Count;

        static void Raise(Action handlers, string what)
        {
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try { handler(); }
                catch (Exception e)
                {
                    var who = handler.Method.DeclaringType?.FullName ?? "?";
                    failures[who + " " + what] = failures.TryGetValue(who + " " + what, out var n) ? n + 1 : 1;
                    Core.Log.Error($"Save {what} handler {who} failed: {e}");
                }
                Perf.Record($"save {what}: {handler.Method.DeclaringType?.Namespace}", watch.Elapsed.TotalMilliseconds);
            }
        }

        [HarmonyPatch(typeof(PlayerSave_mp), nameof(PlayerSave_mp.Load))]
        static class OnLoad
        {
            static void Postfix(PlayerSave_mp __instance, bool loadWorldState)
            {
                if (!loadWorldState || string.IsNullOrEmpty(__instance.characterName)) return;
                if (Active) Unload();
                Character = __instance.characterName;
                LastEvent = $"Loaded save \"{Character}\" at {DateTime.Now:HH:mm:ss}";
                Core.Log.Msg(LastEvent);
                Raise(Loaded, "load");
            }
        }

        [HarmonyPatch(typeof(PlayerSave_mp), nameof(PlayerSave_mp.SaveToDisk))]
        static class OnSave
        {
            static void Postfix(string characterName)
            {
                if (characterName != Character) return; // a save we never loaded data for
                LastEvent = $"Saved \"{Character}\" at {DateTime.Now:HH:mm:ss}";
                Raise(Saving, "save");
            }
        }

        // The title screen resets the game before the next load.
        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.ResetGame))]
        static class OnReset
        {
            static void Prefix() { if (Active) Unload(); }
        }

        static void Unload()
        {
            Raise(Unloaded, "unload");
            LastEvent = $"Left save \"{Character}\" at {DateTime.Now:HH:mm:ss}";
            Character = null;
        }
    }
}
