using System.Linq;
using Game;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Test rig for the animation core, used through DevBridge only: a construct of its own, a few metres ahead, with
    // three hulls in a row, a rotator, a piston and a hinge standing on them, and a Lua Computer labelled "animtest"
    // running scripts\animtest.lua (written by Build). Wire() tags the computer once the game has made its command.
    public static class AnimRig
    {
        const string Name = "EvoCore Anim Rig", FloorName = "EvoCore Anim Floor", Label = "animtest";
        static ParentEntity rig;

        const string Script =
@"-- EvoCore's animation test rig: two poses from the joints' ends, a looping clip between them.
local all = anim.joints()
print(#all .. ' joint(s): ' .. (all[1] and all[1].name or '-'))
for _, j in ipairs(all) do anim.set(j.name, 0) end
anim.capture('low')
for _, j in ipairs(all) do anim.set(j.name, 1) end
anim.capture('high')
anim.clip('sway', { {0, 'low'}, {1.5, 'high', 'smooth'}, {3, 'low', 'smooth'} }, true)
anim.play('sway')
";

        public static string Build(float ahead)
        {
            if (rig != null) return "a rig already exists; Remove() first";
            var player = Dependency.PlayerManager;
            var view = Dependency.RenderPipeline.playerCamera.transform;
            var flatForward = Vector3.ProjectOnPlane(view.forward, view.up).normalized;
            var origin = view.position + flatForward * ahead - view.up * 1.5f;
            rig = player.NetworkSetup.ServerCreateEntity(Hull(0), new WorldTransform(origin, Quaternion.LookRotation(-flatForward, view.up)), EntityType.spaceship);
            if (rig == null) return "the game didn't create an entity (not the server?)";
            rig.EntityName = rig.name = Name;
            Put(Hull(-1)); Put(Hull(1)); Put(Hull(-2)); Put(Hull(2));
            Put(Part(TabMechanism.ROTATOR, -64));
            Put(Part(TabMechanism.PISTON, -32));
            Put(Part(TabMechanism.HINGE, 32));
            Put(Part(Brick.BRICK_INDEX[LuaComputerBlock.BrickId], 64));
            System.IO.File.WriteAllText(System.IO.Path.Combine(Lua.ScriptsDir, Label + ".lua"), Script);
            return $"built {Name} {ahead} m ahead; call Wire() in a few seconds";

            void Put(BrickInstanceData brick) =>
                player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = brick, entityID = rig.ID });
        }

        public static string Wire()
        {
            if (rig == null) return "no rig";
            var computer = CommandManager.commands.Datas.OfType<LuaComputer>().FirstOrDefault(c => c.brickCoord.entity?.Parent == rig);
            if (computer == null) return "no computer yet";
            rig.BrickCount.SetTag(computer.brickCoord, Label);
            Lua.RescanSoon();
            return "tagged; joints: " + string.Join(", ", Anim.Joints(rig).Select(j => $"{j.Name} {j.Value:0.00}").ToArray());
        }

        // Opens the rig's computer on the Computer screen's Anim tab (finds the rig by name after a restart).
        public static string Open()
        {
            var computer = CommandManager.commands.Datas.OfType<LuaComputer>()
                .FirstOrDefault(c => c.brickCoord.entity?.Parent != null && c.brickCoord.entity.Parent.EntityName == Name);
            if (computer == null) return "no rig computer";
            rig = computer.brickCoord.entity.Parent;
            Lua.OpenComputer(computer);
            Graphs.Canvas.ShowAnim();
            return "opened";
        }

        // Puts a gait test program on the rig's computer: "sway" walked by distance, "mid" standing, "high" in the air,
        // "low" for landing.
        const string GaitScript =
@"-- EvoCore's gait test: the sway clip walked from the rig's own movement.
anim.stop()
local all = anim.joints()
for _, j in ipairs(all) do anim.set(j.name, 0) end
anim.capture('low')
for _, j in ipairs(all) do anim.set(j.name, 1) end
anim.capture('high')
for _, j in ipairs(all) do anim.set(j.name, 0.5) end
anim.capture('mid')
anim.clip('sway', { {0, 'low'}, {1.5, 'high', 'smooth'}, {3, 'low', 'smooth'} }, true)
anim.gait{ idle = 'mid', walk = 'sway', stride = 2, fall = 'high', land = 'low', land_speed = 4, reach = REACH }
print('gait on')
";

        public static string Gait(float reach)
        {
            var computer = Computer();
            if (computer == null) return "no rig computer";
            computer.SetProgram("lua", GaitScript.Replace("REACH", reach.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Lua.RescanSoon();
            return "gait program stored";
        }

        // A deck to walk on (the test save is in orbit): 12 by 32 hulls just under the rig, reaching away behind it.
        public static string Floor()
        {
            if (Computer() == null) return "no rig";
            var player = Dependency.PlayerManager;
            var t = rig.TransformData;
            var origin = t.LocalToWorld(new Vector3(0f, -1.3f, 0f));
            var floor = player.NetworkSetup.ServerCreateEntity(Hull(0), new WorldTransform(origin, t.rotation), EntityType.spaceship);
            if (floor == null) return "the game didn't create an entity";
            floor.EntityName = floor.name = FloorName;
            for (int x = -6; x <= 6; x++)
                for (int z = -30; z <= 2; z++)
                    if (x != 0 || z != 0)
                    {
                        var brick = Hull(x);
                        brick.gridPosition = new Vector3i(32 * x, 0, 32 * z);
                        player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = brick, entityID = floor.ID });
                    }
            return "floor built";
        }

        // Pushes the rig along its front at a speed for some seconds, or throws it up (DevBridge can't drive it).
        static float pushSpeed, pushUntil, spinRate, spinUntil;
        static Vector3 lastFront;
        public static string Spin(float degreesPerSecond, float seconds) { spinRate = degreesPerSecond; spinUntil = Time.time + seconds; return "spinning"; }
        public static string Push(float speed, float seconds) { pushSpeed = speed; pushUntil = Time.time + seconds; return "pushing"; }

        public static string Hop(float upSpeed)
        {
            if (rig == null || rig.Rigidbody == null) return "no rig";
            var up = -rig.Rigidbody.rigidbody.Data.previousGravityVector.normalized;
            rig.Rigidbody.rigidbody.AddForce(rig.Rigidbody.Mass * upSpeed / Mathf.Max(Time.deltaTime, 0.005f) * up);
            return "hopped";
        }

        internal static void Tick()
        {
            if (rig == null || rig.Rigidbody == null) return;
            var body = rig.Rigidbody.rigidbody;
            var t = rig.TransformData;
            if (Time.time < spinUntil)
            {
                // A couple: equal and opposite pushes ahead of and behind the middle, steering the yaw rate.
                var facing = t.rotation * Vector3.forward;
                var rate = lastFront == Vector3.zero ? 0f : Vector3.SignedAngle(lastFront, facing, t.rotation * Vector3.up) / Mathf.Max(Time.deltaTime, 0.001f);
                lastFront = facing;
                var push = rig.Rigidbody.Mass * 2f * (spinRate - rate) * Mathf.Deg2Rad;
                var middle = rig.ParentBounds.center;
                body.AddForceAtSystemPosition(push * (t.rotation * Vector3.right), t.LocalToSystem(middle + Vector3.forward));
                body.AddForceAtSystemPosition(-push * (t.rotation * Vector3.right), t.LocalToSystem(middle - Vector3.forward));
            }
            if (Time.time > pushUntil) return;
            var up = -rig.Rigidbody.rigidbody.Data.previousGravityVector.normalized;
            var front = Vector3.ProjectOnPlane(rig.TransformData.rotation * Vector3.forward, up).normalized;
            var v = Vector3.ProjectOnPlane(rig.Rigidbody.LinearVelocity, up);
            rig.Rigidbody.rigidbody.AddForce(rig.Rigidbody.Mass * 4f * (front * pushSpeed - v));
        }

        static LuaComputer Computer()
        {
            var computer = CommandManager.commands.Datas.OfType<LuaComputer>()
                .Where(c => c.brickCoord.entity?.Parent != null && c.brickCoord.entity.Parent.EntityName == Name)
                .OrderBy(c => c.ScriptName == Label ? 0 : 1).FirstOrDefault();
            if (computer != null) rig = computer.brickCoord.entity.Parent;
            return computer;
        }

        // Ports test (Ports.cs): a second computer, "talker", in the middle of the rig. PortsPlace() puts it there;
        // PortsWire() links animtest -> talker and a joint -> talker, gives both programs that talk over a two-way
        // "nav" channel (and use input and output ports), and sets the ports; PortsReport() reads it all back.
        const string TalkerLabel = "talker";

        const string PortsA =
@"ports.declare{ channels = {'nav'}, outputs = {'poke'} }
local n = 0
rate = 1
function tick(dt)
  n = n + 1
  ports.send('nav', { n = n, text = 'hello' })
  outputs.set('poke', n % 2)
end
function on_message(port, msg) print('A got ' .. port .. ' ack ' .. tostring(msg.ack)) end
";

        const string PortsB =
@"ports.declare{ channels = {'nav'}, inputs = {'angle', 'pokein'} }
function on_message(port, msg)
  print(string.format('B got %s n=%d %s angle=%.2f poke=%d', port, msg.n, msg.text, inputs.get('angle'), inputs.get('pokein')))
  ports.send(port, { ack = msg.n })
end
";

        public static string PortsPlace()
        {
            if (Computer() == null) return "no rig";
            if (Talker() != null) return "talker already there";
            Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(new VoxelActionData
            {
                type = VoxelActionType.build, brickData = Part(Brick.BRICK_INDEX[LuaComputerBlock.BrickId], 0), entityID = rig.ID,
            });
            return "placed; PortsWire() in a few seconds";
        }

        static LuaComputer Talker() => CommandManager.commands.Datas.OfType<LuaComputer>()
            .FirstOrDefault(c => rig != null && c.brickCoord.entity?.Parent == rig && c != Computer());

        public static string PortsWire()
        {
            var a = Computer();
            var b = Talker();
            if (a == null || b == null) return "need both computers";
            rig.BrickCount.SetTag(b.brickCoord, TalkerLabel);
            var joint = Anim.Joints(rig).Select(j => (ICommand)j.System).FirstOrDefault(j => j is ICommandLinkSource s && CommandCommon.CanLinkToCommand(s, b));
            if (!a.targets.Contains(b)) CommandCommon.LinkToCommand(a, b, onLoad: false, onInit: false);
            if (joint != null && !b.predecessors.Contains(joint)) CommandCommon.LinkToCommand((ICommandLinkSource)joint, b, onLoad: false, onInit: false);
            a.SetProgram("lua", PortsA);
            b.SetProgram("lua", PortsB);
            Lua.Pair(a, "nav", b, "nav");
            Lua.AssignOut(a, "poke", b);
            Lua.AssignIn(b, "pokein", a, "state");
            if (joint != null) Lua.AssignIn(b, "angle", joint, "state");
            Lua.RescanSoon();
            return $"wired; joint into talker: {(joint != null ? Lua.Describe(joint) : "none could link")}";
        }

        public static string PortsReport()
        {
            var a = Computer();
            var b = Talker();
            if (a == null || b == null) return "need both computers";
            string Map(LuaComputer c) => string.Join("; ", Lua.LinksOf(c).Select(l => $"{l.Kind} {l.Port} -> {Lua.Describe(Lua.TargetOf(c, l))} {l.Extra}").ToArray());
            return $"A ports: {Map(a)}\nB ports: {Map(b)}\nA declared: {Lua.DeclaredOf(a)?.Channels.Count} ch; B declared: {Lua.DeclaredOf(b)?.Inputs.Count} in";
        }

        // Puts the rig back as it was: the animtest program again, the talker removed.
        public static string PortsUndo()
        {
            var a = Computer();
            var b = Talker();
            if (a == null) return "no rig";
            a.SetPorts(null);
            a.SetProgram("lua", Script);
            // The link order test's links to the joints (linking again removes a link).
            foreach (var j in Anim.Joints(rig).Select(x => (ICommand)x.System).Where(x => a.targets.Contains(x)).ToList())
                CommandCommon.LinkToCommand(a, j, onLoad: false, onInit: false);
            if (b != null)
                Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.delete, brickData = b.brickCoord.brickData, entityID = b.brickCoord.entity.ID });
            Lua.RescanSoon();
            return "animtest restored" + (b != null ? ", talker removed" : "");
        }

        // Opens the link tool's picker as if animtest had just been linked to the talker (for a screenshot).
        public static string PickerDemo()
        {
            var a = Computer();
            var b = Talker();
            return a == null || b == null ? "need both computers" : LinkPicker.Demo(a, b);
        }

        // Link order test: links the rig's computer to its joints in a known order (rotator, piston, hinge) and
        // reports what the computer then calls output 1, 2, 3; LinkOrder() reads it again (after a reload).
        public static string LinkTest()
        {
            var computer = Computer();
            if (computer == null) return "no rig computer";
            var joints = Anim.Joints(rig);
            foreach (var j in new[] { "rotator", "piston", "hinge" }.Select(k => joints.FirstOrDefault(x => x.Kind == k)).Where(x => x != null))
                if (!computer.targets.Contains(j.System) && CommandCommon.CanLinkToCommand(computer, j.System))
                    CommandCommon.LinkToCommand(computer, j.System, onLoad: false, onInit: false);
            return "linked rotator, piston, hinge in that order; " + LinkOrder();
        }

        public static string LinkOrder()
        {
            var computer = Computer();
            if (computer == null) return "no rig computer";
            var kinds = Anim.Joints(rig).ToDictionary(j => (ICommand)j.System, j => j.Kind);
            return "outputs: " + string.Join(", ", computer.Outputs.Select((o, i) => $"{i + 1}={(kinds.TryGetValue(o, out var k) ? k : o.OverTextIndirect)}").ToArray());
        }

        public static string Joints() => rig == null ? "no rig" : string.Join(", ", Anim.Joints(rig).Select(j => $"{j.Name} {j.Value:0.00}").ToArray());

        public static string Remove()
        {
            var found = Object.FindObjectsByType<ParentEntity>().Where(e => e.EntityName == Name || e.name == Name || e.EntityName == FloorName || e.name == FloorName).ToList();
            foreach (var e in found) BlockEntityManager.instance.RemoveAndDestroy(e, removeFromLocationMapping: true);
            rig = null;
            return $"removed {found.Count}";
        }

        static BrickInstanceData Hull(int x) => new BrickInstanceData
        {
            brickId = (ushort)TabHull.HULL_A.ItemID,
            gridPosition = new Vector3i(32 * x, 0, 0),
            color = TabHull.HULL_A.defaultPaint,
            rotation = Quaternion.identity.ToByte(),
            healthScore = byte.MaxValue,
            gridLevel = 3,
        };

        // Parts stand on the hulls' top face, 16 units (half a metre) up, as in LuaRig.
        static BrickInstanceData Part(Brick brick, int x) => new BrickInstanceData
        {
            brickId = (ushort)brick.ItemID,
            gridPosition = new Vector3i(x, 16, 0),
            color = brick.defaultPaint,
            rotation = Quaternion.identity.ToByte(),
            healthScore = byte.MaxValue,
            gridLevel = (byte)brick.gridLevel,
        };
    }
}
