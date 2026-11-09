using System;
using System.Collections.Generic;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using Game;
using VoxelEngine;

namespace EvoCore
{
    // The `pilot` global: a script's flight input, added to the ship's own each frame. It only acts while you are
    // flying the ship from its pilot seat on this machine, and never while the game's autopilot is engaged; the game
    // itself stops updating a ship's flight soon after its pilot leaves, so a script can assist but not fly an empty
    // ship. A stopped or reloaded script lets go at once.
    //   pilot.move(right, up, forward)   held until changed; each -1 to 1, added to your keys and clamped
    //   pilot.turn(pitch, yaw, roll)     the same for turning
    //   pilot.mode                        "standard", "boost", "cruise", "warp" or "dropoff"; set "boost" or "standard"
    //   pilot.release()                   let go of everything
    //   pilot.flying, pilot.autopilot     whether you're flying it, and whether the autopilot has it
    // ship.velocity() gives the ship's speed along its own right, up and forward axes, for scripts that hold a speed.
    public static partial class Lua
    {
        class PilotInput
        {
            public Vector3 Move, Turn;
            public string Mode;
        }

        static readonly Dictionary<ParentEntity, List<PilotInput>> pilotInputs = new Dictionary<ParentEntity, List<PilotInput>>();
        static readonly Dictionary<ParentEntity, Spaceship> spaceships = new Dictionary<ParentEntity, Spaceship>();

        static void ExposePilot() => Expose("pilot", c => new PilotApi(c));

        internal static Spaceship SpaceshipOf(ParentEntity ship)
        {
            if (ship == null) return null;
            if (spaceships.TryGetValue(ship, out var found) && found != null) return found;
            found = ship.GetComponentInChildren<Spaceship>();
            if (found != null) spaceships[ship] = found;
            return found;
        }

        static string ModeName(SpaceshipDrive.DriveMode mode)
        {
            switch (mode)
            {
                case SpaceshipDrive.DriveMode.boost: return "boost";
                case SpaceshipDrive.DriveMode.lightcruise: return "cruise";
                case SpaceshipDrive.DriveMode.warp: return "warp";
                case SpaceshipDrive.DriveMode.lightcruiseDropoff: return "dropoff";
                default: return "standard";
            }
        }

        static Vector3 Clamp(Vector3 v) =>
            new Vector3(Mathf.Clamp(v.x, -1f, 1f), Mathf.Clamp(v.y, -1f, 1f), Mathf.Clamp(v.z, -1f, 1f));

        // Speed along the pilot's right, up and forward axes, in m/s.
        static DynValue LocalVelocity(ParentEntity ship)
        {
            var craft = SpaceshipOf(ship);
            if (craft == null || ship.Rigidbody == null) return DynValue.NewTuple(DynValue.NewNumber(0), DynValue.NewNumber(0), DynValue.NewNumber(0));
            var v = ship.Rigidbody.LinearVelocity;
            return DynValue.NewTuple(
                DynValue.NewNumber(Vector3.Dot(v, craft.WorldRight)),
                DynValue.NewNumber(Vector3.Dot(v, craft.WorldUp)),
                DynValue.NewNumber(Vector3.Dot(v, craft.WorldForward)));
        }

        public class PilotApi
        {
            readonly Context c;
            readonly PilotInput input = new PilotInput();

            public PilotApi(Context context)
            {
                c = context;
                c.Stopped += release;
            }

            void Hold()
            {
                if (c.Ship == null) return;
                if (!pilotInputs.TryGetValue(c.Ship, out var list)) pilotInputs[c.Ship] = list = new List<PilotInput>();
                if (!list.Contains(input)) list.Add(input);
            }

            public void move(double right, double up, double forward)
            {
                input.Move = Clamp(new Vector3((float)right, (float)up, (float)forward));
                Hold();
            }

            public void turn(double pitch, double yaw, double roll)
            {
                input.Turn = Clamp(new Vector3((float)pitch, (float)yaw, (float)roll));
                Hold();
            }

            public string mode
            {
                get { var craft = SpaceshipOf(c.Ship); return craft != null && craft.Drive != null ? ModeName(craft.Drive.Mode) : ""; }
                set
                {
                    var wanted = (value ?? "").Trim().ToLowerInvariant();
                    input.Mode = wanted == "boost" || wanted == "standard" ? wanted : null;
                    Hold();
                }
            }

            public void release()
            {
                input.Move = input.Turn = Vector3.zero;
                input.Mode = null;
                if (c.Ship != null && pilotInputs.TryGetValue(c.Ship, out var list))
                {
                    list.Remove(input);
                    if (list.Count == 0) pilotInputs.Remove(c.Ship);
                }
            }

            public bool flying { get { var craft = SpaceshipOf(c.Ship); return craft != null && craft.IsControlledByLocalPlayer; } }
            public bool autopilot { get { var craft = SpaceshipOf(c.Ship); return craft != null && craft.autoPilot != null && craft.autoPilot.autopilotEngaged; } }
        }

        // Adds the scripts' input to what the ship is about to fly with this frame.
        [HarmonyPatch(typeof(SpaceshipDrive), nameof(SpaceshipDrive.UpdateShipTransform))]
        static class PilotPatch
        {
            static readonly AccessTools.FieldRef<SpaceshipDrive, Spaceship> craftOf = AccessTools.FieldRefAccess<SpaceshipDrive, Spaceship>("spaceship");

            static void Prefix(SpaceshipDrive __instance, ref VehicleControlInput input)
            {
                try
                {
                    var craft = craftOf(__instance);
                    if (craft == null || craft.entity == null) return;
                    spaceships[craft.entity] = craft;
                    if (!craft.IsControlledByLocalPlayer || (craft.autoPilot != null && craft.autoPilot.autopilotEngaged)) return;
                    if (!pilotInputs.TryGetValue(craft.entity, out var list) || list.Count == 0) return;
                    Vector3 move = Vector3.zero, turn = Vector3.zero;
                    string wanted = null;
                    foreach (var script in list)
                    {
                        move += script.Move;
                        turn += script.Turn;
                        if (script.Mode != null) wanted = script.Mode;
                    }
                    input.translation = Clamp(input.translation + move);
                    input.orient = Clamp(input.orient + turn);
                    // Boost is a toggle in the game: press it once when the drive isn't in the wanted mode.
                    var current = __instance.Mode;
                    if ((wanted == "boost" && current == SpaceshipDrive.DriveMode.standard) ||
                        (wanted == "standard" && current == SpaceshipDrive.DriveMode.boost))
                        input.boostKeyDown = true;
                }
                catch (Exception e) { Core.Log.Error("Lua pilot input failed: " + e); }
            }
        }
    }
}
