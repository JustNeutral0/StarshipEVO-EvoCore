using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace EvoCore
{
    // The Computer screen's Anim tab: poses and clips (Animation.cs) for the computer it was opened from.
    //  - Joints: every rotator, hinge, piston and rail on its ship, each with a slider that moves the real part, and a
    //    tick that says whether captures include it (none ticked means all).
    //  - Poses: capture where the joints are now under a name, or update the picked pose; click a pose to glide to it.
    //  - Clips: keys (time, pose, ease) on a timeline; scrub it to see any moment on the ship, play or stop it, loop.
    // Every change is saved into the computer block at once, the same as anim.capture / anim.clip from a script.
    internal static class AnimEditor
    {
        static readonly HashSet<string> included = new HashSet<string>();
        static string pickedPose, pickedClip, poseName = "", clipName = "", message = "";
        static Vector2 jointScroll, poseScroll, keyScroll;
        static float scrub;
        static readonly Dictionary<Anim.Key, string> timeText = new Dictionary<Anim.Key, string>();
        static readonly string[] eases = { "smooth", "linear", "in", "out", "step" };

        static LuaComputer Computer => Lua.Attached != null && Lua.Attached.brickCoord.entity != null ? Lua.Attached : null;

        internal static void Draw(float height)
        {
            var c = Computer;
            if (c == null)
            {
                GUILayout.Label("Open a Lua Computer (its activate key) to animate the ship it's on. Its poses and clips are saved in it.");
                return;
            }
            var lib = Anim.Of(c);
            var joints = Anim.Joints(c.brickCoord.entity.Parent);
            GUILayout.BeginHorizontal();
            DrawJoints(joints, height);
            GUILayout.Space(12);
            DrawPoses(c, lib, joints, height);
            GUILayout.Space(12);
            DrawClips(c, lib, joints, height);
            GUILayout.EndHorizontal();
            if (message != "") GUILayout.Label(message);
        }

        // ---- Joints ----

        static void DrawJoints(List<Anim.Joint> joints, float height)
        {
            GUILayout.BeginVertical(GUILayout.Width(380));
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Joints ({joints.Count})", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("All", GUILayout.Width(50))) foreach (var j in joints) included.Add(j.Id);
            if (GUILayout.Button("None", GUILayout.Width(60))) included.Clear();
            GUILayout.EndHorizontal();
            jointScroll = GUILayout.BeginScrollView(jointScroll, GUILayout.Height(height));
            if (joints.Count == 0) GUILayout.Label("This ship has no rotators, hinges, pistons or sliding rails.");
            foreach (var j in joints)
            {
                GUILayout.BeginHorizontal();
                var on = included.Contains(j.Id);
                if (GUILayout.Toggle(on, "", GUILayout.Width(22)) != on) { if (on) included.Remove(j.Id); else included.Add(j.Id); }
                GUILayout.Label(j.Name, GUILayout.Width(130));
                var v = GUILayout.HorizontalSlider(j.Value, 0f, 1f, GUILayout.Width(140));
                if (Mathf.Abs(v - j.Value) > 1e-4f) Anim.Move(j, v);
                GUILayout.Label(j.System is VoxelEngine.RotatorSystem r ? $"{r.CurrentAngle:0}°" : $"{j.Value * 100f:0}%", GUILayout.Width(50));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Label(included.Count == 0 ? "Captures take every joint." : $"Captures take the {included.Count} ticked.");
            GUILayout.EndVertical();
        }

        // ---- Poses ----

        static void DrawPoses(LuaComputer c, Anim.Library lib, List<Anim.Joint> joints, float height)
        {
            GUILayout.BeginVertical(GUILayout.Width(250));
            GUILayout.Label($"Poses ({lib.Poses.Count})");
            poseScroll = GUILayout.BeginScrollView(poseScroll, GUILayout.Height(height - 90));
            foreach (var name in lib.Poses.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList())
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button((name == pickedPose ? "> " : "") + name + $"  ({lib.Poses[name].Count})", GUI.skin.label))
                {
                    pickedPose = name;
                    new Anim.AnimApi(c).pose(name, 0.6);
                    message = $"Gliding to {name}.";
                }
                if (GUILayout.Button("x", GUILayout.Width(26)))
                {
                    lib.Poses.Remove(name);
                    if (pickedPose == name) pickedPose = null;
                    Anim.Store(c, lib);
                    message = $"Deleted pose {name}" + (lib.Clips.Values.Any(cl => cl.Keys.Any(k => k.Pose == name)) ? " (clips still name it; those keys do nothing now)." : ".");
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Label("New pose:");
            poseName = GUILayout.TextField(poseName);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Capture"))
            {
                var name = poseName.Trim();
                if (name == "") message = "Name the pose first.";
                else { Capture(c, lib, joints, name); pickedPose = name; poseName = ""; }
            }
            GUI.enabled = pickedPose != null;
            if (GUILayout.Button("Update picked")) Capture(c, lib, joints, pickedPose);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        static void Capture(LuaComputer c, Anim.Library lib, List<Anim.Joint> joints, string name)
        {
            var pick = included.Count == 0 ? joints : joints.Where(j => included.Contains(j.Id)).ToList();
            lib.Poses[name] = pick.ToDictionary(j => j.Id, j => j.Value);
            Anim.Store(c, lib);
            message = $"Pose {name}: {pick.Count} joint(s) as they are now.";
        }

        // ---- Clips ----

        static void DrawClips(LuaComputer c, Anim.Library lib, List<Anim.Joint> joints, float height)
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            var previewHeight = Mathf.Round(height * 0.5f);
            DrawPreview(c, joints, GUILayoutUtility.GetRect(200, 10000, previewHeight, previewHeight, GUILayout.ExpandWidth(true)));
            height -= previewHeight + 8;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Clips:", GUILayout.Width(50));
            foreach (var name in lib.Clips.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList())
                if (GUILayout.Toggle(name == pickedClip, name, GameLook.TabStyle(), GUILayout.Width(110)) && name != pickedClip) { pickedClip = name; scrub = 0f; }
            GUILayout.FlexibleSpace();
            clipName = GUILayout.TextField(clipName, GUILayout.Width(140));
            if (GUILayout.Button("New clip", GUILayout.Width(90)))
            {
                var name = clipName.Trim();
                var first = pickedPose ?? lib.Poses.Keys.FirstOrDefault();
                if (name == "") message = "Name the clip first.";
                else if (first == null) message = "Capture a pose first: a clip is keys of poses.";
                else
                {
                    var clip = new Anim.Clip { Name = name };
                    clip.Keys.Add(new Anim.Key { Time = 0f, Pose = first });
                    lib.Clips[name] = clip;
                    Anim.Store(c, lib);
                    pickedClip = name;
                    clipName = "";
                    message = $"Clip {name} starts at {first}. Add keys for the poses it moves through.";
                }
            }
            GUILayout.EndHorizontal();

            if (pickedClip == null || !lib.Clips.TryGetValue(pickedClip, out var picked))
            {
                GUILayout.Label(lib.Clips.Count == 0 ? "No clips yet. Capture a few poses, then make a clip from them." : "Pick a clip above.");
                GUILayout.EndVertical();
                return;
            }

            // Play controls.
            GUILayout.BeginHorizontal();
            var api = new Anim.AnimApi(c);
            var playing = api.playing(picked.Name);
            if (GUILayout.Button(playing ? "Stop" : "Play", GUILayout.Width(80)))
            {
                if (playing) api.stop(picked.Name);
                else api.play(picked.Name, null);
            }
            var loop = GUILayout.Toggle(picked.Loop, " Loop", GUILayout.Width(70));
            if (loop != picked.Loop) { picked.Loop = loop; Anim.Store(c, lib); }
            GUILayout.Label($"{picked.Keys.Count} key(s), {picked.Length:0.##} s", GUILayout.Width(150));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Delete clip", GUILayout.Width(110)))
            {
                api.stop(picked.Name, 0);
                lib.Clips.Remove(picked.Name);
                Anim.Store(c, lib);
                pickedClip = null;
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                return;
            }
            GUILayout.EndHorizontal();

            // The timeline: keys as marks, the scrub (or play) position as a line; dragging the slider shows that
            // moment on the ship.
            var length = Mathf.Max(0.01f, picked.Length);
            var bar = GUILayoutUtility.GetRect(200, 10000, 34, 34, GUILayout.ExpandWidth(true));
            var now = playing ? api.time(picked.Name) : scrub;
            if (Event.current.type == EventType.Repaint)
            {
                Fill(bar, new Color(0.1f, 0.11f, 0.13f));
                foreach (var k in picked.Keys)
                {
                    var x = bar.x + 6 + (bar.width - 12) * k.Time / length;
                    Fill(new Rect(x - 2, bar.y + 4, 4, bar.height - 8), k.Pose == pickedPose ? new Color(1f, 0.8f, 0.3f) : new Color(0.45f, 0.85f, 1f));
                }
                var nx = bar.x + 6 + (bar.width - 12) * Mathf.Clamp01((float)now / length);
                Fill(new Rect(nx - 1, bar.y, 2, bar.height), new Color(0.4f, 1f, 0.55f));
            }
            var s = GUILayout.HorizontalSlider(scrub, 0f, length);
            if (Mathf.Abs(s - scrub) > 1e-4f)
            {
                scrub = s;
                if (playing) api.stop(picked.Name, 0);
                Preview(lib, picked, joints, scrub);
            }
            GUILayout.Label($"{scrub:0.00} s");

            // The keys.
            keyScroll = GUILayout.BeginScrollView(keyScroll, GUILayout.Height(Mathf.Max(60, height - 130)));
            var poses = lib.Poses.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Anim.Key remove = null;
            var changed = false;
            foreach (var k in picked.Keys)
            {
                GUILayout.BeginHorizontal();
                if (!timeText.TryGetValue(k, out var t)) t = timeText[k] = k.Time.ToString("0.##", CultureInfo.InvariantCulture);
                GUILayout.Label("at", GUILayout.Width(22));
                var nt = GUILayout.TextField(t, GUILayout.Width(60));
                if (nt != t)
                {
                    timeText[k] = nt;
                    if (float.TryParse(nt, NumberStyles.Float, CultureInfo.InvariantCulture, out var time) && time >= 0f) { k.Time = time; changed = true; }
                }
                GUILayout.Label("s  pose", GUILayout.Width(52));
                // Click to step through the poses; right-click steps back.
                if (GUILayout.Button(k.Pose, GUILayout.Width(150)) && poses.Count > 0)
                {
                    var i = poses.IndexOf(k.Pose);
                    i = Event.current.button == 1 ? (i - 1 + poses.Count) % poses.Count : (i + 1) % poses.Count;
                    k.Pose = poses[i];
                    changed = true;
                }
                if (GUILayout.Button(k.Ease, GUILayout.Width(80)))
                {
                    k.Ease = eases[(Array.IndexOf(eases, k.Ease) + 1) % eases.Length];
                    changed = true;
                }
                if (GUILayout.Button("go", GUILayout.Width(40))) { scrub = k.Time; Preview(lib, picked, joints, scrub); }
                if (GUILayout.Button("x", GUILayout.Width(26))) remove = k;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (remove != null) { picked.Keys.Remove(remove); changed = true; }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add key", GUILayout.Width(110)))
            {
                var pose = pickedPose ?? picked.Keys.LastOrDefault()?.Pose ?? poses.FirstOrDefault();
                if (pose == null) message = "Capture a pose first.";
                else { picked.Keys.Add(new Anim.Key { Time = picked.Length + 1f, Pose = pose }); changed = true; }
            }
            GUILayout.Label("A new key is the picked pose, a second after the last key. Click a key's pose to change it.");
            GUILayout.EndHorizontal();
            if (changed)
            {
                // Keep keys in time order once nobody is typing a time.
                if (GUIUtility.keyboardControl == 0) picked.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                Anim.Store(c, lib);
            }
            GUILayout.EndVertical();
        }

        // ---- Preview: the ship's joints as a skeleton ----
        // Seen from an orbit around the joints, in the ship's own space (so it holds still while the ship moves):
        // the ship's main body as a grey box with a line out of its front, each joint a dot at its pivot coloured by
        // kind (rotator cyan, hinge amber, piston green, rail violet) with a bone to the middle of the part it moves and
        // a thin line from the joint it rides on; rotators and hinges show their axis. Drag to orbit, scroll to zoom,
        // click a dot to tick or untick that joint for captures. It follows the real parts live.

        static float yaw = 35f, pitch = 25f, zoom = 1f;
        static Vector2 lastMouse, downAt;
        static bool orbiting;
        static GUIStyle previewLabel;

        static Color KindColour(Anim.Joint j) =>
            j.Kind == "hinge" ? new Color(1f, 0.72f, 0.3f) : j.Kind == "piston" ? new Color(0.45f, 1f, 0.55f) :
            j.Kind == "slider" ? new Color(0.75f, 0.55f, 1f) : new Color(0.4f, 0.85f, 1f);

        static GUIStyle Label()
        {
            if (previewLabel != null) return previewLabel;
            previewLabel = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            previewLabel.normal.textColor = new Color(0.85f, 0.88f, 0.92f);
            return previewLabel;
        }

        static void DrawPreview(LuaComputer c, List<Anim.Joint> joints, Rect r)
        {
            var e = Event.current;
            var ship = c.brickCoord.entity.Parent;
            var rootPos = ship.TransformData.WorldPosition;
            var rootInv = Quaternion.Inverse(ship.TransformData.rotation);
            Vector3 L(Vector3 world) => rootInv * (world - rootPos);
            Vector3 D(Vector3 worldDir) => rootInv * worldDir;

            // The pieces, in ship space.
            var body = ship.Collider.OwnBounds;
            var pivots = new Dictionary<Anim.Joint, Vector3>();
            var tips = new Dictionary<Anim.Joint, Vector3>();
            var parents = new Dictionary<Anim.Joint, Anim.Joint>();
            foreach (var j in joints)
            {
                try
                {
                    pivots[j] = L(j.System.brickCoord.WorldPosition);
                    var child = j.System.ChildrenEntity;
                    tips[j] = child != null ? L(child.TransformData.LocalToWorld(child.Collider.OwnBounds.center)) : pivots[j];
                    var on = (object)j.System.brickCoord.entity;
                    var parent = joints.FirstOrDefault(p => p.System.ChildrenEntity != null && (object)p.System.ChildrenEntity == on);
                    if (parent != null) parents[j] = parent;
                }
                catch { }
            }

            // Framing: around the body and every joint.
            var frame = body;
            foreach (var p in pivots.Values) frame.Encapsulate(p);
            foreach (var p in tips.Values) frame.Encapsulate(p);
            var centre = frame.center;
            var extent = Mathf.Max(1f, frame.extents.magnitude);
            var scale = zoom * Mathf.Min(r.width, r.height) * 0.45f / extent;
            var view = Quaternion.Euler(pitch, 0f, 0f) * Quaternion.Euler(0f, yaw, 0f);
            Vector2 P(Vector3 local)
            {
                var v = view * (local - centre);
                return r.center + new Vector2(v.x, -v.y) * scale;
            }

            // The joint under the mouse.
            Anim.Joint near = null;
            var nearest = 14f;
            if (r.Contains(e.mousePosition))
                foreach (var kv in pivots)
                {
                    var dist = (P(kv.Value) - e.mousePosition).magnitude;
                    if (dist < nearest) { nearest = dist; near = kv.Key; }
                }

            // Orbit, zoom, and a click (no drag) on a dot ticks or unticks that joint.
            if (r.Contains(e.mousePosition))
            {
                if (e.type == EventType.ScrollWheel) { zoom = Mathf.Clamp(zoom * (e.delta.y > 0 ? 0.9f : 1.1f), 0.2f, 8f); e.Use(); }
                if (e.type == EventType.MouseDown) { orbiting = true; lastMouse = downAt = e.mousePosition; e.Use(); }
            }
            if (orbiting && e.type == EventType.MouseDrag)
            {
                var d = e.mousePosition - lastMouse;
                lastMouse = e.mousePosition;
                yaw -= d.x * 0.4f;
                pitch = Mathf.Clamp(pitch + d.y * 0.4f, -89f, 89f);
                e.Use();
            }
            if (orbiting && e.type == EventType.MouseUp)
            {
                orbiting = false;
                if (near != null && (e.mousePosition - downAt).magnitude < 4f && !included.Remove(near.Id)) included.Add(near.Id);
                e.Use();
            }

            if (e.type != EventType.Repaint) return;
            GUI.BeginClip(r);
            var offset = r.position;
            Vector2 C(Vector3 local) => P(local) - offset;
            Graphs.Canvas.Fill(new Rect(0, 0, r.width, r.height), new Color(0.07f, 0.08f, 0.1f));

            // The body box, and its front.
            var grey = new Color(0.45f, 0.48f, 0.52f, 0.8f);
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = new Vector3((i & 1) == 0 ? body.min.x : body.max.x, (i & 2) == 0 ? body.min.y : body.max.y, (i & 4) == 0 ? body.min.z : body.max.z);
            foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                Graphs.Canvas.Line(C(corners[a]), C(corners[b]), 1.5f, grey);
            var front = body.center + Vector3.forward * (body.extents.z + 0.5f);
            Graphs.Canvas.Line(C(body.center), C(front), 2f, new Color(0.9f, 0.9f, 0.95f, 0.6f));
            Graphs.Canvas.Dot(C(front), 3f, new Color(0.9f, 0.9f, 0.95f, 0.8f));

            // Bones, then joints on top.
            foreach (var j in joints)
            {
                if (!pivots.TryGetValue(j, out var pivot)) continue;
                if (parents.TryGetValue(j, out var parent) && pivots.TryGetValue(parent, out var pp))
                    Graphs.Canvas.Line(C(pp), C(pivot), 1.5f, new Color(0.7f, 0.72f, 0.78f, 0.55f));
                var kind = KindColour(j);
                Graphs.Canvas.Line(C(pivot), C(tips[j]), 4f, new Color(kind.r, kind.g, kind.b, 0.85f));
                if (j.System is VoxelEngine.RotatorSystem rot)
                {
                    var axis = D(rot.WorldRotationAxis).normalized * 0.4f;
                    Graphs.Canvas.Line(C(pivot - axis), C(pivot + axis), 1f, new Color(1f, 1f, 1f, 0.35f));
                }
            }
            foreach (var j in joints)
            {
                if (!pivots.TryGetValue(j, out var pivot)) continue;
                var at = C(pivot);
                Graphs.Canvas.Dot(at, j == near ? 7f : 5.5f, KindColour(j));
                if (included.Contains(j.Id)) Graphs.Canvas.Ring(at, 10f, new Color(1f, 0.82f, 0.3f));
                if (joints.Count <= 24 || j == near)
                    GUI.Label(new Rect(at.x + 9, at.y - 10, 220, 20), j == near ? $"{j.Name}  {j.Value * 100f:0}%" : j.Name, Label());
            }
            GUI.Label(new Rect(8, r.height - 22, r.width - 16, 20), "Drag to orbit, scroll to zoom, click a joint to tick it for captures.", Label());
            GUI.EndClip();
        }

        static void Fill(Rect r, Color colour)
        {
            var old = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        // Shows the clip at a time on the ship (without playing it).
        static void Preview(Anim.Library lib, Anim.Clip clip, List<Anim.Joint> joints, float time)
        {
            var sorted = new Anim.Clip { Name = clip.Name, Loop = clip.Loop };
            sorted.Keys.AddRange(clip.Keys.OrderBy(k => k.Time));
            var byId = joints.ToDictionary(j => j.Id);
            foreach (var (id, v) in Anim.Sample(lib, new Anim.Playing { Clip = sorted, Time = time, Weight = 1f }))
                if (byId.TryGetValue(id, out var j)) Anim.Move(j, v);
        }
    }
}
