using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MoonSharp.Interpreter;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Animation for any ship's moving parts: rotators, hinges, ball joints, pistons and sliding rails (the game's
    // ChildrenEntitySystem blocks). Positions are each joint's state, 0 to 1 across its range (a rotator's angle range,
    // a piston's length), so every kind animates the same way.
    //  - A pose is a set of joint states with a name, captured from where the joints are now.
    //  - A clip is a timeline of poses: keys at times, each with an ease into it; it plays once (and the joints stay
    //    where it ends) or loops.
    //  - Playing clips blend: each frame they're applied in layer order (then start order), each pulling the joints it
    //    has keys for toward its value by its weight, which fades in and out. A higher layer wins on the joints it
    //    keys and leaves the rest to the layers under it (an arm wave over a walk).
    //  - anim.pose(name, seconds) is a one-off clip from wherever the joints are to that pose.
    // Poses and clips belong to a Lua Computer and are saved in its block with its program (LuaComputer.Anims), so they
    // travel with the ship; the joints are the computer's whole ship's. Joints are moved with the game's own
    // SetStateFromShipyard (sets the state and puts the part there at once), which also leaves the state saved.
    public static class Anim
    {
        public class Joint
        {
            public ChildrenEntitySystem System;
            public string Id, Name, Kind;
            public float Value => System.tmpState;
        }

        public class Key
        {
            public float Time;
            public string Pose;
            public string Ease = "smooth";
        }

        public class Clip
        {
            public string Name;
            public bool Loop;
            public readonly List<Key> Keys = new List<Key>();
            public float Length => Keys.Count == 0 ? 0f : Keys[Keys.Count - 1].Time;
        }

        // A computer's poses and clips (saved), and what it's playing (not saved).
        public class Library
        {
            public readonly Dictionary<string, Dictionary<string, float>> Poses = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Clip> Clips = new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Playing> Active = new List<Playing>();
            internal Gait Gait;                       // walking driven by the ship's movement (Locomotion.cs)
        }

        public class Playing
        {
            public Clip Clip;
            public Dictionary<string, float> Start;   // a pose transition's starting states (key pose "@start")
            public float Time, Speed = 1f, Weight, Fade = 0.2f;
            public int Layer;
            public bool Loop, Stopping;
            public bool Managed;                      // a gait sets its time and weight itself
            public long Order;
        }

        static long order;
        internal static long NextOrder() => ++order;

        // ---- Joints ----

        // Every joint on a ship, in a stable order (by child entity, then block). Named by their Tag label, else
        // "rotator 3" / "piston 1"... numbered per kind in that order.
        public static List<Joint> Joints(ParentEntity ship)
        {
            var list = new List<Joint>();
            if (ship == null) return list;
            foreach (var c in CommandManager.commands.Datas)
            {
                if (!(c is ChildrenEntitySystem s) || s.brickCoord.entity == null || s.brickCoord.entity.Parent != ship) continue;
                var kind = s is PistonSystem ? "piston" : s is SliderSystem ? "slider" : s.brickData.brickId == TabMechanism.HINGE.brickID ? "hinge" : "rotator";
                list.Add(new Joint { System = s, Kind = kind, Id = $"j{s.brickCoord.entity.ChildrenID}.{s.brickCoord.brickData.instanceId}" });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            var counts = new Dictionary<string, int>();
            foreach (var j in list)
            {
                counts.TryGetValue(j.Kind, out var n);
                counts[j.Kind] = ++n;
                string label = null;
                try { label = j.System.brickCoord.TagData.label; } catch { }
                j.Name = string.IsNullOrWhiteSpace(label) ? $"{j.Kind} {n}" : label.Trim();
            }
            // Labels can repeat (the game numbers copies the same): later ones get " (2)", " (3)"...
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in list)
            {
                seen.TryGetValue(j.Name, out var times);
                seen[j.Name] = ++times;
                if (times > 1) j.Name += $" ({times})";
            }
            return list;
        }

        public static Joint Find(List<Joint> joints, string nameOrId) =>
            joints.FirstOrDefault(j => string.Equals(j.Name, nameOrId, StringComparison.OrdinalIgnoreCase)) ??
            joints.FirstOrDefault(j => j.Id == nameOrId);

        internal static void Move(Joint j, float state)
        {
            state = Mathf.Clamp01(state);
            if (Mathf.Abs(state - j.System.tmpState) < 1e-5f) return;
            j.System.SetStateFromShipyard(state);
        }

        // ---- Playing ----

        static readonly Dictionary<LuaComputer, Library> libraries = new Dictionary<LuaComputer, Library>();

        internal static Library Of(LuaComputer c)
        {
            if (!libraries.TryGetValue(c, out var lib))
            {
                lib = new Library();
                Read(lib, c.Anims);
                libraries[c] = lib;
            }
            return lib;
        }

        // The computer's saved text changed (loaded, or another player's edit): read it again next time.
        internal static void Reload(LuaComputer c)
        {
            if (libraries.TryGetValue(c, out var lib)) { lib.Poses.Clear(); lib.Clips.Clear(); Read(lib, c.Anims); }
        }

        // Saves the library into the computer block when it changed.
        internal static void Store(LuaComputer c, Library lib)
        {
            foreach (var clip in lib.Clips.Values) clip.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            var text = Write(lib);
            if (text != (c.Anims ?? "")) c.SetAnims(text);
        }

        internal static void StopAll(LuaComputer c)
        {
            if (libraries.TryGetValue(c, out var lib)) { lib.Active.Clear(); lib.Gait = null; }
        }

        // Every frame: advance and apply every computer's playing clips.
        internal static void Update()
        {
            AnimRig.Tick();
            if (libraries.Count == 0) return;
            foreach (var pair in libraries.ToList())
            {
                var computer = pair.Key;
                var lib = pair.Value;
                if (computer.brickCoord.entity == null) { libraries.Remove(computer); continue; }
                var dt = Time.deltaTime;
                if (lib.Gait != null) Locomotion.Step(computer.brickCoord.entity.Parent, lib, lib.Gait, dt);
                if (lib.Active.Count == 0) continue;
                var joints = Joints(computer.brickCoord.entity.Parent);
                var byId = joints.ToDictionary(j => j.Id);
                var values = new Dictionary<string, float>();
                foreach (var p in lib.Active.OrderBy(p => p.Layer).ThenBy(p => p.Order).ToList())
                {
                    if (p.Managed)
                    {
                        if (p.Weight > 1e-4f) Blend(p);
                        continue;
                    }
                    // Weight fades toward 1 (or toward 0 when stopping, then the clip is dropped).
                    var rate = p.Fade > 0f ? dt / p.Fade : 1f;
                    p.Weight = p.Stopping ? Mathf.Max(0f, p.Weight - rate) : Mathf.Min(1f, p.Weight + rate);
                    if (p.Stopping && p.Weight <= 0f) { lib.Active.Remove(p); continue; }
                    var length = p.Clip.Length;
                    p.Time += dt * p.Speed;
                    var ended = false;
                    if (length <= 0f) ended = true;
                    else if (p.Loop) p.Time = Mathf.Repeat(p.Time, length);
                    else if (p.Time >= length) { p.Time = length; ended = true; }
                    Blend(p);
                    if (ended && !p.Stopping) lib.Active.Remove(p); // a one-shot leaves the joints where it ended
                }
                foreach (var kv in values) Move(byId[kv.Key], kv.Value);

                void Blend(Playing p)
                {
                    foreach (var (id, target) in Sample(lib, p))
                    {
                        if (!byId.TryGetValue(id, out var j)) continue;
                        var under = values.TryGetValue(id, out var v) ? v : j.Value;
                        values[id] = Mathf.Lerp(under, target, p.Weight);
                    }
                }
            }
        }

        // The clip's value for each joint it keys, at the playing's time.
        internal static IEnumerable<(string, float)> Sample(Library lib, Playing p)
        {
            var keys = p.Clip.Keys;
            if (keys.Count == 0) yield break;
            int i = 0;
            while (i + 1 < keys.Count && keys[i + 1].Time <= p.Time) i++;
            var a = keys[i];
            var b = i + 1 < keys.Count ? keys[i + 1] : a;
            var span = b.Time - a.Time;
            var u = span > 1e-5f ? Mathf.Clamp01((p.Time - a.Time) / span) : 1f;
            u = Ease(b.Ease, u);
            var pa = PoseOf(lib, p, a.Pose);
            var pb = PoseOf(lib, p, b.Pose);
            if (pa == null && pb == null) yield break;
            foreach (var id in (pa?.Keys ?? Enumerable.Empty<string>()).Union(pb?.Keys ?? Enumerable.Empty<string>()))
            {
                float va = 0f, vb = 0f;
                var hasA = pa != null && pa.TryGetValue(id, out va);
                var hasB = pb != null && pb.TryGetValue(id, out vb);
                // A joint only one of the two poses has holds that pose's value.
                yield return (id, hasA && hasB ? Mathf.Lerp(va, vb, u) : hasA ? va : vb);
            }
        }

        static Dictionary<string, float> PoseOf(Library lib, Playing p, string name) =>
            name == "@start" ? p.Start : lib.Poses.TryGetValue(name, out var pose) ? pose : null;

        public static float Ease(string ease, float u)
        {
            switch ((ease ?? "smooth").ToLowerInvariant())
            {
                case "linear": return u;
                case "in": return u * u;
                case "out": return 1f - (1f - u) * (1f - u);
                case "step": return u < 1f ? 0f : 1f;
                default: return u * u * (3f - 2f * u); // smooth
            }
        }

        // ---- Saved text: one line per pose joint and clip key ----
        //   pose <name>
        //   j <joint id> <state>
        //   clip <name> loop|once
        //   k <time> <pose> <ease>
        // Names are quoted when they hold spaces.

        static string Q(string s) => s.Contains(' ') || s.Contains('"') ? "\"" + s.Replace("\"", "'") + "\"" : s;
        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

        static string Write(Library lib)
        {
            var sb = new StringBuilder();
            foreach (var pose in lib.Poses.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("pose ").Append(Q(pose.Key)).Append('\n');
                foreach (var j in pose.Value.OrderBy(j => j.Key, StringComparer.Ordinal)) sb.Append("j ").Append(j.Key).Append(' ').Append(F(j.Value)).Append('\n');
            }
            foreach (var clip in lib.Clips.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("clip ").Append(Q(clip.Name)).Append(clip.Loop ? " loop" : " once").Append('\n');
                foreach (var k in clip.Keys) sb.Append("k ").Append(F(k.Time)).Append(' ').Append(Q(k.Pose)).Append(' ').Append(k.Ease).Append('\n');
            }
            return sb.ToString();
        }

        static List<string> Words(string line)
        {
            var words = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == ' ') { i++; continue; }
                if (line[i] == '"')
                {
                    var end = line.IndexOf('"', i + 1);
                    if (end < 0) end = line.Length;
                    words.Add(line.Substring(i + 1, end - i - 1));
                    i = end + 1;
                }
                else
                {
                    var end = line.IndexOf(' ', i);
                    if (end < 0) end = line.Length;
                    words.Add(line.Substring(i, end - i));
                    i = end;
                }
            }
            return words;
        }

        static void Read(Library lib, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Dictionary<string, float> pose = null;
            Clip clip = null;
            foreach (var raw in text.Split('\n'))
            {
                var w = Words(raw.Trim());
                if (w.Count == 0) continue;
                try
                {
                    switch (w[0])
                    {
                        case "pose": pose = lib.Poses[w[1]] = new Dictionary<string, float>(); clip = null; break;
                        case "j": if (pose != null) pose[w[1]] = float.Parse(w[2], CultureInfo.InvariantCulture); break;
                        case "clip": clip = lib.Clips[w[1]] = new Clip { Name = w[1], Loop = w.Count > 2 && w[2] == "loop" }; pose = null; break;
                        case "k": clip?.Keys.Add(new Key { Time = float.Parse(w[1], CultureInfo.InvariantCulture), Pose = w[2], Ease = w.Count > 3 ? w[3] : "smooth" }); break;
                    }
                }
                catch (Exception e) { Core.Log.Warning("Animation line skipped: " + raw + " (" + e.Message + ")"); }
            }
        }

        // ---- Lua: the `anim` global on Lua Computers ----

        internal static void Init()
        {
            Lua.Expose("anim", c =>
            {
                if (!(c.Host is LuaComputer comp)) return null;
                c.Stopped += () => StopAll(comp);
                return new AnimApi(comp);
            });
        }

        // anim.joints()                          list of { id, name, kind, value (0-1), angle (rotators, degrees) }
        // anim.get(joint) / anim.set(joint, v)  a joint's state, 0-1 (joint: its Tag label, "rotator 2", or its id)
        // anim.capture(name [, joints])         saves the joints' current states as a pose (all, or a list of joints)
        // anim.poses() / anim.clips()           names; anim.forget(name) deletes a pose or clip
        // anim.clip(name, keys [, loop])        keys: { {0, "rest"}, {0.5, "up", "smooth"}, {1, "rest"} } (time, pose, ease)
        // anim.play(name [, opts])              opts: loop, speed, layer, fade (seconds), restart
        // anim.stop([name [, fade]])            fades a clip out (all clips with no name)
        // anim.pose(name [, seconds [, ease]])  moves to a pose from wherever the joints are
        // anim.playing(name) / anim.speed(name, s) / anim.time(name [, t])
        // anim.gait{ walk=, stride=, idle=, run=, run_stride=, run_at=, turn=, turn_step=, fall=, land=, land_speed=,
        //            land_time=, reach=, weight=, layer= }   walks the clips from the ship's own movement (Locomotion.cs);
        //   names are clips or poses. anim.gait() is its state { state, speed, forward, side, turn, vertical,
        //   grounded, clearance, phase, impact, land } (nil when off); anim.gait(false) stops it.
        // Eases: smooth (default), linear, in, out, step.
        public class AnimApi
        {
            readonly LuaComputer computer;
            public AnimApi(LuaComputer host) { computer = host; }

            Library Lib => Of(computer);
            List<Joint> All => Joints(computer.brickCoord.entity?.Parent);

            Joint J(string name)
            {
                var j = Find(All, name);
                if (j == null) throw new ScriptRuntimeException($"no joint \"{name}\" on this ship (anim.joints() lists them)");
                return j;
            }

            public Table joints(ScriptExecutionContext context)
            {
                var script = context.GetScript();
                var table = new Table(script);
                foreach (var j in All)
                {
                    var row = new Table(script);
                    row["id"] = j.Id; row["name"] = j.Name; row["kind"] = j.Kind; row["value"] = (double)j.Value;
                    if (j.System is RotatorSystem r) row["angle"] = (double)r.CurrentAngle;
                    table.Append(DynValue.NewTable(row));
                }
                return table;
            }

            public double get(string joint) => J(joint).Value;
            public void set(string joint, double value) => Move(J(joint), (float)value);

            public int capture(string name, Table only = null)
            {
                var pick = only?.Values.Where(v => v.Type == DataType.String).Select(v => J(v.String)).ToList() ?? All;
                Lib.Poses[name] = pick.ToDictionary(j => j.Id, j => j.Value);
                Store(computer, Lib);
                return pick.Count;
            }

            public Table poses(ScriptExecutionContext context) => Names(context, Lib.Poses.Keys);
            public Table clips(ScriptExecutionContext context) => Names(context, Lib.Clips.Keys);

            static Table Names(ScriptExecutionContext context, IEnumerable<string> names)
            {
                var t = new Table(context.GetScript());
                foreach (var n in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) t.Append(DynValue.NewString(n));
                return t;
            }

            public bool forget(string name)
            {
                var gone = Lib.Poses.Remove(name) | Lib.Clips.Remove(name);
                if (gone) Store(computer, Lib);
                return gone;
            }

            public void clip(string name, Table keys, bool loop = false)
            {
                var c = new Clip { Name = name, Loop = loop };
                foreach (var v in keys.Values)
                {
                    if (v.Type != DataType.Table) throw new ScriptRuntimeException("each key is { time, pose [, ease] }");
                    var k = v.Table;
                    var time = k.Get(1).CastToNumber() ?? throw new ScriptRuntimeException("a key's first value is its time in seconds");
                    var pose = k.Get(2).CastToString() ?? throw new ScriptRuntimeException("a key's second value is a pose name");
                    if (!Lib.Poses.ContainsKey(pose)) throw new ScriptRuntimeException($"no pose \"{pose}\" (anim.capture makes one)");
                    c.Keys.Add(new Key { Time = (float)time, Pose = pose, Ease = k.Get(3).CastToString() ?? "smooth" });
                }
                c.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                Lib.Clips[name] = c;
                Store(computer, Lib);
            }

            public void play(string name, Table opts = null)
            {
                if (!Lib.Clips.TryGetValue(name, out var c)) throw new ScriptRuntimeException($"no clip \"{name}\"");
                double Num(string k, double d) => opts?.Get(k).CastToNumber() ?? d;
                bool Flag(string k, bool d) { var v = opts?.Get(k); return v == null || v.IsNil() ? d : v.CastToBool(); }
                var existing = Lib.Active.FirstOrDefault(p => p.Clip == c && !p.Stopping);
                if (existing != null && !Flag("restart", false))
                {
                    existing.Speed = (float)Num("speed", existing.Speed);
                    return;
                }
                Lib.Active.Add(new Playing
                {
                    Clip = c, Loop = Flag("loop", c.Loop), Speed = (float)Num("speed", 1), Layer = (int)Num("layer", 0),
                    Fade = (float)Num("fade", 0.2), Order = ++order,
                });
            }

            public void stop(string name = null, double fade = 0.2)
            {
                if (name == null && Lib.Gait != null) { Locomotion.Release(Lib, Lib.Gait, (float)fade); Lib.Gait = null; }
                foreach (var p in Lib.Active.Where(p => !p.Managed && (name == null || string.Equals(p.Clip.Name, name, StringComparison.OrdinalIgnoreCase))))
                {
                    p.Stopping = true;
                    p.Fade = (float)fade;
                }
            }

            public void pose(string name, double seconds = 1, string ease = "smooth")
            {
                if (!Lib.Poses.TryGetValue(name, out var target)) throw new ScriptRuntimeException($"no pose \"{name}\"");
                var joints = All.ToDictionary(j => j.Id);
                var start = target.Keys.Where(joints.ContainsKey).ToDictionary(id => id, id => joints[id].Value);
                var clip = new Clip { Name = "@pose " + name };
                clip.Keys.Add(new Key { Time = 0f, Pose = "@start" });
                clip.Keys.Add(new Key { Time = Mathf.Max(0.01f, (float)seconds), Pose = name, Ease = ease });
                // A new move to a pose replaces any move to a pose still under way.
                Lib.Active.RemoveAll(p => p.Clip.Name.StartsWith("@pose "));
                Lib.Active.Add(new Playing { Clip = clip, Start = start, Weight = 1f, Fade = 0f, Layer = 100, Order = ++order });
            }

            public DynValue gait(ScriptExecutionContext context, DynValue opts = null)
            {
                if (opts == null || opts.IsNil())
                    return Lib.Gait == null ? DynValue.Nil : DynValue.NewTable(Locomotion.State(context.GetScript(), Lib.Gait));
                if (opts.Type == DataType.Boolean && !opts.Boolean)
                {
                    if (Lib.Gait != null) { Locomotion.Release(Lib, Lib.Gait); Lib.Gait = null; }
                    return DynValue.Nil;
                }
                if (opts.Type != DataType.Table) throw new ScriptRuntimeException("anim.gait takes a table of options, or false to stop");
                Lib.Gait = Locomotion.Configure(Lib, Lib.Gait, opts.Table);
                return DynValue.NewTable(Locomotion.State(context.GetScript(), Lib.Gait));
            }

            public bool playing(string name) => Lib.Active.Any(p => string.Equals(p.Clip.Name, name, StringComparison.OrdinalIgnoreCase) && !p.Stopping);

            public void speed(string name, double s)
            {
                foreach (var p in Lib.Active.Where(p => string.Equals(p.Clip.Name, name, StringComparison.OrdinalIgnoreCase))) p.Speed = (float)s;
            }

            public double time(string name, double? t = null)
            {
                var p = Lib.Active.FirstOrDefault(x => string.Equals(x.Clip.Name, name, StringComparison.OrdinalIgnoreCase));
                if (p == null) return -1;
                if (t.HasValue) p.Time = (float)t.Value;
                return p.Time;
            }
        }

        // ---- For DevBridge ----

        // Every walking computer's gait state.
        public static string GaitReport()
        {
            var lines = libraries.Where(kv => kv.Value.Gait != null).Select(kv =>
            {
                var g = kv.Value.Gait;
                var weights = string.Join(" ", g.Parts.Select(p => $"{p.Key}={p.Value.Weight:0.00}").ToArray());
                return $"{kv.Key.brickCoord.entity?.Parent?.EntityName}: {g.State} speed {g.Speed:0.00} (fwd {g.Forward:0.00} side {g.Side:0.00}) " +
                       $"turn {g.Yaw:0} deg/s vert {g.Vertical:0.00} grounded {g.Grounded} clearance {g.Clearance:0.00} phase {g.Phase:0.00} " +
                       $"impact {g.Impact:0.0} land {g.Depth:0.00} | {weights}";
            }).ToArray();
            return lines.Length == 0 ? "no gaits running" : string.Join("\n", lines);
        }

        // The joints on the player's ship (or the ship of the Lua Computer nearest the player).
        public static string Report()
        {
            var eye = Manager.Dependency.RenderPipeline.playerCamera.transform.position;
            var ships = CommandManager.commands.Datas.OfType<ChildrenEntitySystem>().Where(s => s.brickCoord.entity?.Parent != null)
                .Select(s => s.brickCoord.entity.Parent).Distinct()
                .OrderBy(p => (p.TransformData.WorldPosition - eye).sqrMagnitude).Take(4).ToList();
            if (ships.Count == 0) return "no ships with joints nearby";
            return string.Join("\n", ships.Select(ship => ship.EntityName + ": " +
                string.Join(", ", Joints(ship).Select(j => $"{j.Name} [{j.Id}] {j.Value:0.00}").ToArray())).ToArray());
        }
    }
}
