using System;
using Game;
using Manager;
using MoonSharp.Interpreter;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // The `player` global: you (the local player) as the script's ship sees you.
    //   player.seated          sitting in any seat of this ship
    //   player.aboard          seated in it, or standing on it
    //   player.third_person    the camera is in third person (it orbits the ship, so look() is the orbit, not your head)
    //   player.look()          yaw, pitch: degrees from the ship's forward; yaw + to the right, pitch + up
    //   player.aim()           the look direction along the ship's right, up and forward axes (three numbers)
    // The ship's axes are its pilot seat's (as for ship.velocity) when it has one, else the ship's own. Single-player
    // first: it's always this machine's camera.
    public static partial class Lua
    {
        static void ExposePlayer() => Expose("player", c => new PlayerApi(c));

        // The camera's direction along a ship's right, up and forward.
        static Vector3 LookInShip(ParentEntity ship)
        {
            var camera = Dependency.RenderPipeline?.playerCamera;
            if (camera == null || ship == null) return Vector3.forward;
            var f = camera.transform.forward;
            var craft = SpaceshipOf(ship);
            Vector3 right, up, forward;
            if (craft != null) { right = craft.WorldRight; up = craft.WorldUp; forward = craft.WorldForward; }
            else
            {
                var r = ship.TransformData.rotation;
                right = r * Vector3.right; up = r * Vector3.up; forward = r * Vector3.forward;
            }
            return new Vector3(Vector3.Dot(f, right), Vector3.Dot(f, up), Vector3.Dot(f, forward));
        }

        public class PlayerApi
        {
            readonly Context c;
            public PlayerApi(Context context) { c = context; }

            static PlayerCommandManager Me => Dependency.PlayerManager;

            public bool seated
            {
                get
                {
                    var me = Me;
                    return me != null && me.IsSeated && me.LockedEntity != null && me.LockedEntity.Parent == c.Ship;
                }
            }

            public bool aboard
            {
                get
                {
                    var me = Me;
                    // LockedEntity is what the game moves you with: your seat's ship, or the ship you stand on.
                    return me != null && c.Ship != null && me.LockedEntity != null && me.LockedEntity.Parent == c.Ship;
                }
            }

            public bool third_person => Me != null && Me.ThirdPersonOn;

            public DynValue look()
            {
                var d = LookInShip(c.Ship);
                var yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                var pitch = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;
                return DynValue.NewTuple(DynValue.NewNumber(yaw), DynValue.NewNumber(pitch));
            }

            public DynValue aim()
            {
                var d = LookInShip(c.Ship);
                return DynValue.NewTuple(DynValue.NewNumber(d.x), DynValue.NewNumber(d.y), DynValue.NewNumber(d.z));
            }
        }

        // ---- For DevBridge ----

        // Where the player is and looks, against the ship of the nearest Lua Computer's.
        public static string PlayerReport()
        {
            var me = Dependency.PlayerManager;
            if (me == null) return "no player";
            var ship = me.LockedEntity?.Parent;
            var d = ship != null ? LookInShip(ship) : Vector3.zero;
            return $"seated {me.IsSeated}, locked to {(ship != null ? ship.EntityName : "nothing")}, third person {me.ThirdPersonOn}" +
                   (ship != null ? $", look yaw {Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg:0.0} pitch {Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg:0.0}" : "");
        }
    }
}
