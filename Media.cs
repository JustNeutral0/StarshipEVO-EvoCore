using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EvoCore
{
    // What the player's own computer is playing (Spotify, YouTube Music, a browser tab: anything in Windows'
    // now-playing list), for a ship radio. Windows' list is a WinRT API the game can't call, so a small helper
    // program (EvoMedia.exe, next to EvoCore.dll; needs the .NET 9 runtime) reads it and EvoCore talks to it over its
    // standard input and output. The helper starts only when a script first uses `media`, and ends with the game.
    // It is this machine's player only: nothing here goes over the network.
    // Lua: `media` on every script:
    //   media.ok / media.why           whether the helper runs, and why not
    //   media.active                   whether anything is in the now-playing list
    //   media.title, .artist, .album   the track ("" when unknown)
    //   media.app                      the player's name ("Spotify")
    //   media.playing                  true while it plays
    //   media.position, .duration      seconds; duration is 0 when the player doesn't say
    //   media.toggle(), play(), pause(), next(), previous()
    // DevBridge: get EvoCore.Media.Report()
    public static class Media
    {
        public static bool Enabled = true;      // off: `media` stays quiet and the helper is never started
        const float CommandGap = 0.25f;         // seconds between commands, so a script can't hammer the player

        class State
        {
            public bool Active, Playing;
            public string App = "", Title = "", Artist = "", Album = "";
            public double Position, Duration;
            public long At;   // Stopwatch ticks when it arrived
        }

        static readonly State nothing = new State();
        static volatile State now = nothing;
        static Process helper;
        static string problem = "not started";
        static bool tried;
        static float lastCommand = -10f;

        static string HelperPath => Path.Combine(Path.GetDirectoryName(typeof(Media).Assembly.Location), "EvoMedia.exe");

        static void Start()
        {
            if (tried) return;
            tried = true;
            if (!Enabled) { problem = "media is switched off"; return; }
            var path = HelperPath;
            if (!File.Exists(path)) { problem = "EvoMedia.exe is missing"; return; }
            try
            {
                helper = new Process
                {
                    StartInfo = new ProcessStartInfo(path)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    },
                    EnableRaisingEvents = true,
                };
                helper.OutputDataReceived += (_, e) => { if (e.Data != null) Line(e.Data); };
                helper.Exited += (_, __) => { problem = "the media helper stopped (it needs the .NET 9 runtime)"; now = nothing; };
                helper.Start();
                helper.BeginOutputReadLine();
                problem = null;
                UnityEngine.Application.quitting += Stop;
                Core.Log.Msg("Media helper started");
            }
            catch (Exception e)
            {
                helper = null;
                problem = "couldn't start the media helper: " + e.Message;
                Core.Log.Warning("Media: " + problem);
            }
        }

        static void Stop()
        {
            try { if (helper != null && !helper.HasExited) helper.Kill(); } catch { }
            helper = null;
        }

        // From the helper's reader thread: "none", or "now", app, state, position, duration, title, artist, album.
        static void Line(string line)
        {
            var f = line.Split('\t');
            if (f[0] != "now" || f.Length < 8) { now = nothing; return; }
            double.TryParse(f[3], out var position);
            double.TryParse(f[4], out var duration);
            now = new State
            {
                Active = true, Playing = f[2] == "playing", App = AppName(f[1]),
                Position = position, Duration = duration, Title = f[5], Artist = f[6], Album = f[7],
                At = Stopwatch.GetTimestamp(),
            };
        }

        // "Spotify.exe" and "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" both to "Spotify".
        static string AppName(string id)
        {
            var bang = id.LastIndexOf('!');
            if (bang >= 0 && bang < id.Length - 1) id = id.Substring(bang + 1);
            if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id.Substring(0, id.Length - 4);
            return id;
        }

        static void Send(string command)
        {
            Start();
            if (helper == null || problem != null) return;
            if (UnityEngine.Time.unscaledTime - lastCommand < CommandGap) return;
            lastCommand = UnityEngine.Time.unscaledTime;
            try { helper.StandardInput.WriteLine(command); helper.StandardInput.Flush(); } catch { }
        }

        public static string Report()
        {
            Start();
            var s = now;
            return (problem ?? "ok") + "; " + (s.Active
                ? $"{s.App}: {(s.Playing ? "playing" : "paused")} \"{s.Title}\" by {s.Artist} ({s.Album}), {s.Position:0}/{s.Duration:0}s"
                : "nothing playing");
        }

        internal static void Expose()
        {
            Lua.Expose("media", c => new MediaApi());
            Help.Global("media", "What this computer's own music player is playing (Spotify, YouTube Music, a browser tab), for a ship radio. " +
                                 "It is your machine's player: other players don't get it.",
                "ok", "whether the media helper runs; why says why not",
                "active", "whether anything is in Windows' now-playing list",
                "title, artist, album", "the track",
                "app", "the player's name",
                "playing", "true while it plays",
                "position, duration", "seconds; duration is 0 when the player doesn't say",
                "toggle(), play(), pause()", "play and pause",
                "next(), previous()", "skip tracks");
        }

        public class MediaApi
        {
            State S { get { Start(); return now; } }

            public bool ok { get { Start(); return problem == null; } }
            public string why { get { Start(); return problem ?? ""; } }
            public bool active => S.Active;
            public bool playing => S.Playing;
            public string title => S.Title;
            public string artist => S.Artist;
            public string album => S.Album;
            public string app => S.App;
            public double duration => S.Duration;

            // The helper reports whole seconds as they change; between reports the position runs on.
            public double position
            {
                get
                {
                    var s = S;
                    if (!s.Playing || s.Duration <= 0) return s.Position;
                    return Math.Min(s.Duration, s.Position + (Stopwatch.GetTimestamp() - s.At) / (double)Stopwatch.Frequency);
                }
            }

            public void toggle() => Send("toggle");
            public void play() => Send("play");
            public void pause() => Send("pause");
            public void next() => Send("next");
            public void previous() => Send("previous");
        }
    }
}
