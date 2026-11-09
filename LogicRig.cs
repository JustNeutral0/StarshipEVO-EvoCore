using System.Linq;
using Game;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Test rig for the logic converter and refit, used through DevBridge only: a construct a way ahead with five hulls
    // and on them a Switch -> Logic Gate (OR) -> Timer Gate (0.1 s) -> Indicator Light chain, plus a Lua Computer.
    // Build(ahead), then Wire() once the blocks exist; Press() flips the switch, State() reads the chain.
    public static class LogicRig
    {
        const string Name = "EvoCore Logic Rig";

        public static string Build(float ahead)
        {
            if (Ship() != null) return "a logic rig already exists";
            var player = Dependency.PlayerManager;
            var view = Dependency.RenderPipeline.playerCamera.transform;
            var flat = Vector3.ProjectOnPlane(view.forward, view.up).normalized;
            var origin = view.position + flat * ahead - view.up * 1.5f;
            var rig = player.NetworkSetup.ServerCreateEntity(Hull(0), new WorldTransform(origin, Quaternion.LookRotation(-flat, view.up)), EntityType.spaceship);
            if (rig == null) return "the game didn't create an entity";
            rig.EntityName = rig.name = Name;
            void Put(BrickInstanceData b) => player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = b, entityID = rig.ID });
            Put(Hull(-1)); Put(Hull(1)); Put(Hull(-2)); Put(Hull(2));
            Put(Part(TabLogic.SWITCH_BUTTON, -64));
            Put(Part(TabLogic.LOGIC_GATE, -32));
            Put(Part(TabLogic.TIMER_GATE, 0));
            Put(Part(TabLogic.COMMAND_INDICATOR, 32));
            Put(Part(Brick.BRICK_INDEX[LuaComputerBlock.BrickId], 64));
            return $"built {Name} {ahead} m ahead; Wire() in a few seconds";
        }

        static ParentEntity Ship() => Object.FindObjectsByType<ParentEntity>().FirstOrDefault(e => e.EntityName == Name);

        static T On<T>() where T : class, ICommand =>
            CommandManager.commands.Datas.OfType<T>().FirstOrDefault(c => c.brickCoord.entity?.Parent?.EntityName == Name);

        public static string Wire()
        {
            var sw = On<CommandSwitch>(); var gate = On<CommandLogic>(); var timer = On<CommandDelay>(); var light = On<CommandIndicator>();
            if (sw == null || gate == null || timer == null || light == null) return "blocks not all there yet";
            void L(ICommand a, ICommand b) { if (a is ICommandLinkSource s && !s.targets.Contains(b) && CommandCommon.CanLinkToCommand(s, b)) CommandCommon.LinkToCommand(s, b, false, false); }
            L(sw, gate); L(gate, timer); L(timer, light);
            return "wired: switch -> gate -> timer -> light";
        }

        public static string Press()
        {
            var sw = On<CommandSwitch>();
            if (sw == null) return "no switch";
            sw.Activate(false, Dependency.PlayerManager, 1f);
            return "switch now " + sw.State;
        }

        public static string State()
        {
            var sw = On<CommandSwitch>(); var gate = On<CommandLogic>(); var timer = On<CommandDelay>(); var light = On<CommandIndicator>(); var pc = On<LuaComputer>();
            return $"switch {sw?.State} gate {gate?.State} timer {timer?.State} light {light?.State} | computer {(pc == null ? "none" : pc.HasProgram ? pc.ProgramKind : "no program")}";
        }

        public static string Remove()
        {
            var rig = Ship();
            if (rig == null) return "no rig";
            BlockEntityManager.instance.RemoveAndDestroy(rig, removeFromLocationMapping: true);
            return "removed";
        }

        static BrickInstanceData Hull(int x) => new BrickInstanceData
        {
            brickId = (ushort)TabHull.HULL_A.ItemID, gridPosition = new Vector3i(32 * x, 0, 0), color = TabHull.HULL_A.defaultPaint,
            rotation = Quaternion.identity.ToByte(), healthScore = byte.MaxValue, gridLevel = 3,
        };

        static BrickInstanceData Part(Brick brick, int x) => new BrickInstanceData
        {
            brickId = (ushort)brick.ItemID, gridPosition = new Vector3i(x, 16, 0), color = brick.defaultPaint,
            rotation = Quaternion.identity.ToByte(), healthScore = byte.MaxValue, gridLevel = (byte)brick.gridLevel,
        };
    }
}
