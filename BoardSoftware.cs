using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EvoCore
{
    // A board carries its software: the program (graph or Lua) and the installed apps with their data (user,
    // 2026-11-10: "boards"). Pull a board out of a computer and its program goes with it; put it in another computer
    // and that computer runs it. The computer keeps what belongs to the ship: its port wiring and its animations.
    //  - A computer still has built-in memory, so boardless computers (and every ship from before) keep working.
    //    Putting a board in parks the built-in program (kept in the computer's hardware record, Parts.Hardware.Parked)
    //    and runs the board's; a blank board takes a copy of the computer's current program. Taking the board out
    //    puts the parked program back.
    //  - A loose board's software lives in this save's mods\EvoCore\boards.txt, keyed by the board item's seed.
    public class Software
    {
        public string Kind = "lua", Program, Apps = "";
        public bool Blank => Program == null && string.IsNullOrEmpty(Apps);

        // "<kind> <program length, -1 for none> <apps length>\n<program><apps>"
        public string Write() => $"{Kind} {(Program == null ? -1 : Program.Length)} {Apps.Length}\n{Program}{Apps}";

        // Reads one from text at `at`; returns where it ends.
        public static Software Read(string text, ref int at)
        {
            var end = text.IndexOf('\n', at);
            var head = text.Substring(at, end - at).Split(' ');
            int programLength = int.Parse(head[1]), appsLength = int.Parse(head[2]);
            at = end + 1;
            var s = new Software { Kind = head[0] == "graph" ? "graph" : "lua" };
            if (programLength >= 0) { s.Program = text.Substring(at, programLength); at += programLength; }
            s.Apps = text.Substring(at, appsLength);
            at += appsLength;
            return s;
        }

        // What a computer runs now (its apps' live data saved into it first).
        public static Software Of(LuaComputer c)
        {
            EvoCore.Apps.Flush(c);
            return new Software { Kind = c.ProgramKind, Program = c.Program, Apps = EvoCore.Apps.Write(c.InstalledApps) };
        }

        // Makes the computer run this. Its scripts restart on their own (the program's mirror file changes).
        public void ApplyTo(LuaComputer c)
        {
            EvoCore.Apps.DropLive(c);
            c.SetSoftware(Kind, Program, EvoCore.Apps.Read(Apps));
            Lua.RescanSoon();
        }
    }

    public static class BoardStore
    {
        static readonly Dictionary<uint, Software> loose = new Dictionary<uint, Software>();
        const string Mod = "EvoCore", FileName = "boards.txt";

        internal static void Init()
        {
            Saves.Loaded += Load;
            Saves.Saving += Save;
            Saves.Unloaded += () => loose.Clear();
        }

        public static Software Take(uint seed)
        {
            if (!loose.TryGetValue(seed, out var s)) return null;
            loose.Remove(seed);
            return s;
        }

        public static void Put(uint seed, Software s)
        {
            if (s == null || s.Blank) loose.Remove(seed);
            else loose[seed] = s;
        }

        public static Software Peek(uint seed) => loose.TryGetValue(seed, out var s) ? s : null;

        // "boards1\n" then per board "<seed>\n" + Software.Write().
        static void Load()
        {
            loose.Clear();
            try
            {
                var path = Saves.File(Mod, FileName);
                if (!System.IO.File.Exists(path)) return;
                var text = System.IO.File.ReadAllText(path);
                if (!text.StartsWith("boards1\n")) return;
                var at = "boards1\n".Length;
                while (at < text.Length)
                {
                    var end = text.IndexOf('\n', at);
                    if (end < 0) break;
                    var seed = uint.Parse(text.Substring(at, end - at));
                    at = end + 1;
                    loose[seed] = Software.Read(text, ref at);
                }
                Core.Log.Msg($"Boards: {loose.Count} loose board(s) with programs in this save");
            }
            catch (Exception e) { Core.Log.Error("Boards: couldn't read boards.txt: " + e.Message); }
        }

        static void Save()
        {
            var sb = new StringBuilder("boards1\n");
            foreach (var pair in loose) sb.Append(pair.Key).Append('\n').Append(pair.Value.Write());
            var path = Saves.File(Mod, FileName);
            System.IO.File.WriteAllText(path + ".tmp", sb.ToString());
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(path + ".tmp", path);
        }

        public static string Report() => loose.Count == 0 ? "no loose boards with programs" :
            string.Join("\n", loose.Select(p => $"board {p.Key}: {p.Value.Kind}, {(p.Value.Program ?? "").Length} chars, apps {(p.Value.Apps.Length > 0 ? "yes" : "no")}").ToArray());
    }
}
