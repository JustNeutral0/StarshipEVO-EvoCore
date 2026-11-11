using System.Linq;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Test rig for the Lua Computer, used through DevBridge only; nothing in play calls it. A construct of its own
    // ahead of the player: a slab of five 1 m hull cubes with, on top, a switch, a Lua Computer labelled "rigtest",
    // a light linked out of it, a light labelled "rig lamp" (not linked; found by label), and a text screen running
    // [lua: status]. Build() makes the bricks; the game makes their commands a moment later, so Wire() then links
    // switch -> computer -> light and sets the label and screen. Press(v) flips the switch, State() reports.
    public static class LuaRig
    {
        const string Name = "EvoCore Lua Rig", Label = "rigtest", LampLabel = "rig lamp";
        static ParentEntity rig;

        const string Script =
@"-- EvoCore's Lua test rig. Light 1 follows the switch; the 'rig lamp', found by label, does the opposite.
rate = 0.5
print(string.format('ship %s fuel %.2f power %.2f hull %.2f blocks %d planet %s', ship.name, ship.fuel, ship.power, ship.hull, #ship.blocks(), ship.planet))
function on_input(i, value)
  print('input ' .. i .. ' = ' .. value)
  outputs.set(1, value)
end
function tick(dt)
  local lamp = ship.block('rig lamp')
  if lamp then lamp.on = not inputs.on(1) end
  watch('switch', inputs.on(1))
  watch('panel', inputs.get(2))
end
local sw, light = inputs.block(1), outputs.block(1)
print('linked: ' .. (sw and sw.type or 'nil') .. ' -> ' .. (light and light.type or 'nil'))
for _, b in ipairs(ship.blocks()) do
  local n = 0
  for name, v in pairs(b.values()) do n = n + 1; print(b.name .. ' ' .. name .. ' = ' .. v) end
  print(b.name .. ' (' .. b.type .. '): ' .. n .. ' readouts, state ' .. tostring(b.read('state')))
end
";

        public static string Build(float ahead, float right, float up) => Build(ahead, right, up, false);

        // facing: the parts' fronts (the computer's screen) turn toward the camera; the rig's x then runs right to
        // left, so the computer (x -1) sits 1 m to the camera's right of the origin.
        public static string Build(float ahead, float right, float up, bool facing)
        {
            if (rig != null) return "a rig already exists; Remove() first";
            var player = Dependency.PlayerManager;
            var view = Dependency.RenderPipeline.playerCamera.transform;
            var origin = view.position + view.forward * ahead + view.right * right + view.up * (up - 1.5f);
            var rotation = Quaternion.LookRotation(facing ? -view.forward : view.forward, view.up);
            rig = player.NetworkSetup.ServerCreateEntity(Hull(0), new WorldTransform(origin, rotation), EntityType.spaceship);
            if (rig == null) return "the game didn't create an entity (not the server?)";
            rig.EntityName = rig.name = Name;
            for (int x = -2; x <= 3; x++) if (x != 0) Put(Hull(x));
            // Parts stand on the slab's top face (16 = half a metre in 1/32 m units), along their up.
            Put(Part(TabLogic.SWITCH_BUTTON, -64));
            Put(Part(Brick.BRICK_INDEX[LuaComputerBlock.BrickId], -32));
            Put(Part(TabInterior.LIGHT_FLAT, 0));
            Put(Part(TabInterior.LIGHT_FLAT, 32));
            Put(Part(TabLogic.SCREEN_TEXT_INPUT, 64));
            Put(Part(TabSystem.SOLAR_PANEL_FLAT, 96));   // links in only through SourceLinks
            System.IO.File.WriteAllText(System.IO.Path.Combine(Lua.ScriptsDir, Label + ".lua"), Script);
            return $"built {Name} {ahead} m ahead; call Wire() in a few seconds";

            void Put(BrickInstanceData brick) =>
                player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = brick, entityID = rig.ID });
        }

        static ICommand At(int x) => Lua.ShipCommands(rig)
            .FirstOrDefault(c => c.Snapshot.brickCoord.brickData.gridPosition.x == x && c.Snapshot.brickCoord.brickData.gridPosition.y == 16)?.Now;

        public static string Wire()
        {
            if (rig == null) return "no rig";
            ICommand sw = At(-64), computer = At(-32), light = At(0), lamp = At(32), screen = At(64);
            var found = $"switch {Kind(sw)}, computer {Kind(computer)}, light {Kind(light)}, lamp {Kind(lamp)}, screen {Kind(screen)}";
            if (sw == null || computer == null || light == null || lamp == null || screen == null) return "not all commands yet: " + found;
            var report = found;
            report += "; switch->computer " + Link(sw, computer);
            report += "; computer->light " + Link(computer, light);
            report += "; panel->computer " + (At(96) is ICommand panel ? Link(panel, computer) : "no panel");
            rig.BrickCount.SetTag(computer.brickCoord, Label);
            rig.BrickCount.SetTag(lamp.brickCoord, LampLabel);
            if (screen is CommandInputText text) text.SetData(new CommandInputText.InputTextData { text = "[lua: status]", fontSize = 12f });
            return report;
        }

        static string Kind(ICommand c) => c == null ? "missing" : c.GetType().Name;

        static string Link(ICommand from, ICommand to)
        {
            if (!(from is ICommandLinkSource source)) return "no (not a link source)";
            if (!CommandCommon.CanLinkToCommand(source, to)) return "refused";
            CommandCommon.LinkToCommand(source, to, onLoad: false, onInit: false);
            return "ok";
        }

        // Puts the computer in the game's info panel (top right), as picking it with the link tool does.
        public static string ShowInfo()
        {
            if (!(At(-32) is UI.IBrickGizmoDisplayable computer)) return "no computer";
            UI.UI_BrickGizmoDisplay.SetNewGizmoDisplayable(computer);
            return "shown";
        }

        public static string Press(float value)
        {
            var sw = At(-64);
            if (sw == null) return "no switch";
            sw.Activate(manualActivation: false, Dependency.RenderPipeline.commandManager, value);
            return "switch set to " + value;
        }

        public static string State()
        {
            if (rig == null) return "no rig";
            ICommand sw = At(-64), computer = At(-32), light = At(0), lamp = At(32);
            string s(ICommand c) => c == null ? "missing" : c.State.ToString("0.##");
            var status = computer is LuaComputer lc ? Lua.ComputerStatus(lc).Replace("\n", " | ") : "no computer";
            var watched = computer is LuaComputer w ? (Lua.WatchedText(w) ?? "").Replace("\n", " | ") : "";
            return $"switch {s(sw)}, light {s(light)}, lamp {s(lamp)}; computer: {status}; watched:{watched}";
        }

        // Readouts (Readouts.cs) of every logic block on ships within `range` metres of the camera: per ship, each
        // block type once with its readout names and values, to see what scripts can read.
        public static string Readouts(float range)
        {
            var eye = Dependency.RenderPipeline.playerCamera.transform.position;
            var lines = new System.Collections.Generic.List<string>();
            foreach (var ship in Object.FindObjectsByType<ParentEntity>())
            {
                if (ship == null || Vector3.Distance(ship.transform.position, eye) > range) continue;
                var found = Lua.ShipCommands(ship);
                if (found.Count == 0) continue;
                lines.Add($"{ship.EntityName}: {found.Count} logic blocks");
                foreach (var group in found.GroupBy(f => f.Snapshot.GetType()))
                {
                    var now = group.First().Now;
                    var values = Lua.ReadoutsOf(group.Key).Select(r => $"{r.Name}={Lua.Read(now, r.Name)?.ToString("0.##") ?? "nil"}");
                    lines.Add($"  {group.Key.Name} x{group.Count()}: {string.Join(", ", values.ToArray())}");
                }
            }
            return lines.Count > 0 ? string.Join("\n", lines.ToArray()) : "no ships with logic blocks in range";
        }

        public static string Remove()
        {
            var found = Object.FindObjectsByType<ParentEntity>().Where(e => e.EntityName == Name || e.name == Name).ToList();
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
