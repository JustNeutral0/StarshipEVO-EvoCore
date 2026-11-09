using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Locomotion: a gait drives a computer's walk clips from how its ship is really moving, so the feet keep pace
    // with the ground instead of playing at a fixed rate (the game's own mech walk slides).
    //  - Ground: a ray straight down (along gravity, or the ship's own down in space) from the middle of the whole
    //    construct finds the ground under it; within `reach` of the bottom of the construct counts as standing.
    //    Speeds are measured against what it stands on, so walking on a moving carrier works.
    //  - Walk and run: one stride phase, advanced by the distance covered (one loop of the walk clip per `stride`
    //    metres, of the run clip per `run_stride`), so both stay in step and blend into each other around `run_at`.
    //    Walking backwards plays the cycle backwards.
    //  - Turning on the spot steps too: the turn clip (or the walk clip) cycles once per `turn_step` degrees.
    //  - Off the ground the fall pose or clip fades in; on touchdown the land pose dips in deeper the harder the
    //    landing (full at `land_speed` m/s) and recovers over `land_time` seconds.
    //  - `weight` (seconds) is how slowly the gait takes up speed changes: a heavy mech settles into a stride.
    // Every part is optional except walk. The gait's clips play on their layer (0 unless `layer` says otherwise), so
    // other clips on higher layers (aiming arms, a wave) play over it; land goes one layer above.
    internal class Gait
    {
        public string Idle, Walk, Run, Turn, Fall, Land;
        public float Stride = 1.5f, RunStride = 3f, RunAt = 5f, TurnStep = 90f, Reach = 0.6f, Weight = 0.15f, LandSpeed = 8f, LandTime = 0.45f;
        public int Layer;

        // Live state.
        public float Phase, TurnPhase, Speed, Forward, Side, Yaw, Vertical, Clearance = float.PositiveInfinity, Air, Impact, FallSpeed, Depth;
        public float LandAt = -100f, LastContact = -100f;
        public bool Grounded = true, HasFacing;
        public Vector3 Facing, GroundVelocity;
        public string State = "idle";
        public readonly Dictionary<string, Anim.Playing> Parts = new Dictionary<string, Anim.Playing>();
    }

    public static class Locomotion
    {
        static readonly string[] roles = { "idle", "walk", "run", "turn", "fall", "land" };
        static readonly VoxelRaycast probe = new VoxelRaycast();

        static string RoleName(Gait g, string role)
        {
            switch (role)
            {
                case "idle": return g.Idle;
                case "walk": return g.Walk;
                case "run": return g.Run;
                case "turn": return g.Turn;
                case "fall": return g.Fall;
                default: return g.Land;
            }
        }

        // A clip by that name, else a pose by that name as a one-key clip, else null.
        static Anim.Clip Resolve(Anim.Library lib, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (lib.Clips.TryGetValue(name, out var clip)) return clip;
            if (!lib.Poses.ContainsKey(name)) return null;
            var held = new Anim.Clip { Name = "@gait " + name, Loop = true };
            held.Keys.Add(new Anim.Key { Time = 0f, Pose = name });
            return held;
        }

        // The gait's playing clips, put in the library's active list the first time (and again if a clip was edited
        // away from under them).
        static Anim.Playing Part(Anim.Library lib, Gait g, string role)
        {
            if (g.Parts.TryGetValue(role, out var p) && lib.Active.Contains(p)) return p;
            var clip = Resolve(lib, RoleName(g, role));
            if (clip == null) { g.Parts.Remove(role); return null; }
            p = new Anim.Playing
            {
                Clip = clip, Loop = true, Speed = 0f, Weight = 0f, Fade = 0f, Managed = true,
                Layer = role == "land" ? g.Layer + 1 : g.Layer, Order = Anim.NextOrder(),
            };
            g.Parts[role] = p;
            lib.Active.Add(p);
            return p;
        }

        // Lets go of a gait's clips: they fade out like stopped clips.
        internal static void Release(Anim.Library lib, Gait g, float fade = 0.3f)
        {
            foreach (var p in g.Parts.Values)
            {
                p.Managed = false;
                p.Stopping = true;
                p.Fade = fade;
                p.Speed = 0f;
            }
            g.Parts.Clear();
        }

        static float Smooth(float edge0, float edge1, float x)
        {
            var t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        // Every frame, before the clips are blended: measure the ship and set the gait's clips' times and weights.
        internal static void Step(ParentEntity ship, Anim.Library lib, Gait g, float dt)
        {
            if (ship == null || ship.Rigidbody == null || dt <= 0f) return;
            var now = Time.time;
            var t = ship.TransformData;

            // Up: against gravity, or the ship's own up where there is none.
            var gravity = ship.Rigidbody.rigidbody.Data.previousGravityVector;
            var craft = Lua.SpaceshipOf(ship);
            var up = gravity.magnitude > 0.01f ? -gravity.normalized : craft != null ? craft.WorldUp : t.rotation * Vector3.up;
            var facing = craft != null ? craft.WorldForward : t.rotation * Vector3.forward;
            var flatFacing = Vector3.ProjectOnPlane(facing, up);
            if (flatFacing.sqrMagnitude < 1e-4f) flatFacing = Vector3.ProjectOnPlane(t.rotation * Vector3.up, up);
            flatFacing.Normalize();
            var flatRight = Vector3.Cross(up, flatFacing);

            // Ground: from the middle of the construct, down past its bottom.
            var bounds = ship.ParentBounds;
            var centre = t.LocalToWorld(bounds.center);
            var upLocal = Quaternion.Inverse(t.rotation) * up;
            var half = Mathf.Abs(upLocal.x) * bounds.extents.x + Mathf.Abs(upLocal.y) * bounds.extents.y + Mathf.Abs(upLocal.z) * bounds.extents.z;
            var reachDown = half + Mathf.Max(g.Reach, 0.05f) + 20f;
            g.Clearance = float.PositiveInfinity;
            if (probe.Raycast(centre, -up, out var hit, reachDown, ship))
            {
                g.Clearance = hit.distance - half;
                var under = hit.HasEntityReference && hit.Entity != null ? hit.Entity.Parent : null;
                g.GroundVelocity = under != null && under != ship && under.Rigidbody != null ? under.Rigidbody.LinearVelocity : Vector3.zero;
            }
            var touching = g.Clearance < g.Reach;
            if (touching) g.LastContact = now;

            var v = ship.Rigidbody.LinearVelocity - g.GroundVelocity;
            g.Vertical = Vector3.Dot(v, up);
            var wasGrounded = g.Grounded;
            // A short gap (a bump, a step) doesn't count as leaving the ground.
            g.Grounded = touching || now - g.LastContact < 0.12f;
            if (!g.Grounded) g.FallSpeed = Mathf.Max(g.FallSpeed, -g.Vertical);
            if (g.Grounded && !wasGrounded)
            {
                g.Impact = g.FallSpeed;
                g.LandAt = now;
                g.FallSpeed = 0f;
            }

            // Speeds along the ground, eased by the gait's weight.
            var ease = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, g.Weight));
            g.Forward += (Vector3.Dot(v, flatFacing) - g.Forward) * ease;
            g.Side += (Vector3.Dot(v, flatRight) - g.Side) * ease;
            g.Speed = Mathf.Sqrt(g.Forward * g.Forward + g.Side * g.Side);
            if (g.HasFacing)
            {
                var yaw = Vector3.SignedAngle(g.Facing, flatFacing, up) / dt;
                g.Yaw += (yaw - g.Yaw) * (1f - Mathf.Exp(-dt / Mathf.Max(0.1f, g.Weight)));
            }
            g.Facing = flatFacing;
            g.HasFacing = true;

            // Weights: in the air the fall takes over; on the ground walk/run by speed, turning when standing.
            g.Air = Mathf.MoveTowards(g.Air, g.Grounded ? 0f : 1f, dt / 0.2f);
            var moving = Smooth(0.15f, 0.6f, g.Speed);
            var run = Part(lib, g, "run");
            var running = run != null ? Smooth(g.RunAt - 1f, g.RunAt + 1f, g.Speed) : 0f;
            var turning = (1f - moving) * Smooth(8f, 30f, Mathf.Abs(g.Yaw));
            var turnPart = Part(lib, g, "turn");
            var ground = 1f - g.Air;

            // One stride phase for walk and run: distance over the stride length in use.
            var stride = Mathf.Max(0.05f, Mathf.Lerp(g.Stride, g.RunStride, running));
            var backwards = g.Forward < 0f && Mathf.Abs(g.Forward) >= Mathf.Abs(g.Side);
            g.Phase += (backwards ? -g.Speed : g.Speed) * dt / stride;
            if (turnPart == null) g.Phase += Mathf.Abs(g.Yaw) * turning * dt / Mathf.Max(1f, g.TurnStep);
            g.Phase = Mathf.Repeat(g.Phase, 1f);
            g.TurnPhase = Mathf.Repeat(g.TurnPhase + g.Yaw * dt / Mathf.Max(1f, g.TurnStep), 1f);

            var walk = Part(lib, g, "walk");
            var idle = Part(lib, g, "idle");
            var fall = Part(lib, g, "fall");
            var parts = new List<(Anim.Playing, float)>();
            void Add(Anim.Playing p, float w) { if (p != null) parts.Add((p, w)); }
            float walkW = ground * moving * (1f - running) + (turnPart == null ? ground * turning : 0f);
            Add(idle, ground * Mathf.Max(0f, 1f - moving - turning));
            Add(walk, walkW);
            Add(run, ground * moving * running);
            Add(turnPart, ground * turning);
            Add(fall, g.Air);
            if (walk != null) walk.Time = g.Phase * walk.Clip.Length;
            if (run != null) run.Time = g.Phase * run.Clip.Length;
            if (turnPart != null) turnPart.Time = g.TurnPhase * turnPart.Clip.Length;
            if (idle != null && idle.Clip.Length > 0f) idle.Time = Mathf.Repeat(idle.Time + dt, idle.Clip.Length);
            if (fall != null && fall.Clip.Length > 0f) fall.Time = Mathf.Repeat(fall.Time + dt, fall.Clip.Length);

            // The blend applies clips one over another, so each one's pull is its share of everything so far; what's
            // left of the whole holds the joints where they are.
            var hold = Mathf.Max(0f, 1f - parts.Sum(x => x.Item2));
            var sum = hold;
            foreach (var (p, w) in parts)
            {
                sum += w;
                p.Weight = sum > 1e-5f ? w / sum : 0f;
            }

            // Landing: a dip in on touchdown, as deep as the landing was hard, then back out.
            var land = Part(lib, g, "land");
            var since = now - g.LandAt;
            g.Depth = 0f;
            if (since < g.LandTime)
            {
                var dip = since < 0.08f ? since / 0.08f : 1f - Smooth(0.08f, g.LandTime, since);
                g.Depth = dip * Mathf.Clamp01(g.Impact / Mathf.Max(0.1f, g.LandSpeed));
            }
            if (land != null)
            {
                land.Weight = g.Depth;
                land.Time = Mathf.Min(since, land.Clip.Length);
            }

            g.State = g.Air > 0.5f ? "fall" : g.Depth > 0.05f ? "land" : moving > 0.5f ? (running > 0.5f ? "run" : "walk") : turning > 0.5f ? "turn" : "idle";
        }

        // ---- Lua (anim.gait) ----

        internal static Gait Configure(Anim.Library lib, Gait old, Table opts)
        {
            var g = old ?? new Gait();
            string S(string k, string d) { var v = opts.Get(k); return v.IsNil() ? d : v.CastToString(); }
            float N(string k, float d) { var v = opts.Get(k); return v.IsNil() ? d : (float)(v.CastToNumber() ?? d); }
            g.Idle = S("idle", g.Idle); g.Walk = S("walk", g.Walk); g.Run = S("run", g.Run);
            g.Turn = S("turn", g.Turn); g.Fall = S("fall", g.Fall); g.Land = S("land", g.Land);
            g.Stride = N("stride", g.Stride); g.RunStride = N("run_stride", g.RunStride); g.RunAt = N("run_at", g.RunAt);
            g.TurnStep = N("turn_step", g.TurnStep); g.Reach = N("reach", g.Reach); g.Weight = N("weight", g.Weight);
            g.LandSpeed = N("land_speed", g.LandSpeed); g.LandTime = N("land_time", g.LandTime);
            g.Layer = (int)N("layer", g.Layer);
            if (string.IsNullOrEmpty(g.Walk)) throw new ScriptRuntimeException("a gait needs at least a walk clip: anim.gait{ walk = 'walk', stride = 1.5 }");
            foreach (var role in roles)
            {
                var name = RoleName(g, role);
                if (!string.IsNullOrEmpty(name) && Resolve(lib, name) == null)
                    throw new ScriptRuntimeException($"gait {role}: no clip or pose \"{name}\"");
            }
            // Changed clips are picked up fresh.
            if (old != null) Release(lib, old, 0.2f);
            return g;
        }

        internal static Table State(Script script, Gait g)
        {
            var t = new Table(script);
            t["state"] = g.State;
            t["speed"] = (double)g.Speed;
            t["forward"] = (double)g.Forward;
            t["side"] = (double)g.Side;
            t["turn"] = (double)g.Yaw;
            t["vertical"] = (double)g.Vertical;
            t["grounded"] = g.Grounded;
            if (!float.IsInfinity(g.Clearance)) t["clearance"] = (double)g.Clearance;
            t["phase"] = (double)g.Phase;
            t["impact"] = (double)g.Impact;
            t["land"] = (double)g.Depth;
            return t;
        }

        // ---- For DevBridge ----

        public static string Report() => Anim.GaitReport();
    }
}
