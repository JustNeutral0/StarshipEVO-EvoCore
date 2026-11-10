using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Game;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // The Lua Computer: a logic block that runs a Lua script (Lua.cs) and is wired up with the game's own link tool.
    // Everything linked into it is an input, everything it's linked to an output, in the order they were linked.
    // Its script is UserData\EvoCore\scripts\<label>.lua, where the label is the one the multitool's Tag mode gives
    // the block (computer.lua when it has none). It has the Spaceship Computer's 2x2 gate base, whose model, materials
    // and size it borrows; its own look (Looks\EvoCore.LuaComputer.obj) puts a Lua logo where that block's symbol is.
    // It never passes its own state on by itself: the script decides what each output gets.
    // Using it (the activate key) opens its program in the editor instead of changing its state; the first time, the
    // program it runs is stored in the block (ComputerPrograms.cs).
    public class LuaComputer : Command
    {
        public override bool AllowMultipleInputs => true;
        protected override bool CanPropagate => false;
        public override bool CanActivateManually(PlayerCommandManager commandManager) => true;

        // Called on the local player's key release only, so this never goes over the network. A terminal opens its
        // mainframe's program (Mainframe.cs).
        public override void OnKeyUp(PlayerCommandManager playerManager) => Lua.OpenComputer(Mainframes.TerminalOf(this) ?? this);

        // Its own program (ComputerPrograms.cs), saved in the block: "graph" or "lua", and an id naming its working
        // file. Null until the computer is first opened; until then it runs scripts\<label>.
        public string Program, ProgramKind = "lua", ProgramId;
        public bool HasProgram => Program != null;
        // Its poses and clips (Animation.cs), as saved text; null when it has none.
        public string Anims;
        // Which linked block sits in which named port (Ports.cs), as saved text; null when none are set.
        public string PortMap;

        public void SetProgram(string kind, string text)
        {
            ProgramKind = kind == "graph" ? "graph" : "lua";
            Program = text ?? "";
            if (ProgramId == null) NewProgramId();
            OnEdited(); // saved with the ship, and sent to the server like a text screen's text
        }

        public void SetAnims(string text)
        {
            Anims = string.IsNullOrEmpty(text) ? null : text;
            if (ProgramId == null) NewProgramId();
            OnEdited();
        }

        public void SetPorts(string text)
        {
            PortMap = string.IsNullOrEmpty(text) ? null : text;
            if (ProgramId == null) NewProgramId();
            OnEdited();
        }

        public void NewProgramId() => ProgramId = Guid.NewGuid().ToString("N").Substring(0, 6);

        // evoprog3\n<id>\n<kind>\n<program length, -1 for none>\n<anims length>\n<program><anims><ports>.
        // (evoprog2 had no ports and no anims length; evoprog1 no anims and no length.)
        // Once a computer has had a program it always writes its data, even "none" (length -1): with nothing written,
        // the game would keep the last data it had and load it back (an undone refit came back that way).
        public override byte[] GetCustomData(bool isBlueprintExport) =>
            Program == null && Anims == null && PortMap == null && ProgramId == null ? null :
            Encoding.UTF8.GetBytes($"evoprog3\n{ProgramId}\n{ProgramKind}\n{(Program == null ? -1 : Program.Length)}\n{(Anims ?? "").Length}\n{Program}{Anims}{PortMap}");

        public override void OnSaveLoaded(byte[] customData, bool onInit)
        {
            base.OnSaveLoaded(customData, onInit);
            if (customData == null || customData.Length == 0) return;
            try
            {
                var text = Encoding.UTF8.GetString(customData);
                if (text.StartsWith("evoprog1\n"))
                {
                    var parts = text.Split(new[] { '\n' }, 4);
                    if (parts.Length < 4) return;
                    ProgramId = parts[1];
                    ProgramKind = parts[2] == "graph" ? "graph" : "lua";
                    Program = parts[3];
                    Anims = null;
                }
                else if (text.StartsWith("evoprog3\n"))
                {
                    var parts = text.Split(new[] { '\n' }, 6);
                    if (parts.Length < 6) return;
                    ProgramId = parts[1];
                    ProgramKind = parts[2] == "graph" ? "graph" : "lua";
                    var length = int.Parse(parts[3]);
                    var animsLength = int.Parse(parts[4]);
                    var rest = parts[5];
                    var at = Math.Max(0, length);
                    Program = length < 0 ? null : rest.Substring(0, length);
                    var anims = rest.Substring(at, animsLength);
                    Anims = anims.Length == 0 ? null : anims;
                    var ports = rest.Substring(at + animsLength);
                    PortMap = ports.Length == 0 ? null : ports;
                }
                else if (text.StartsWith("evoprog2\n"))
                {
                    var parts = text.Split(new[] { '\n' }, 5);
                    if (parts.Length < 5) return;
                    ProgramId = parts[1];
                    ProgramKind = parts[2] == "graph" ? "graph" : "lua";
                    var length = int.Parse(parts[3]);
                    var rest = parts[4];
                    Program = length < 0 ? null : rest.Substring(0, length);
                    var anims = length < 0 ? rest : rest.Substring(length);
                    Anims = anims.Length == 0 ? null : anims;
                }
                Anim.Reload(this);
            }
            catch (Exception e) { Core.Log.Error("Lua Computer: couldn't read its stored program: " + e.Message); }
        }
        public override string OverText => Mainframes.TerminalOf(this) is Mainframe m ? Mainframes.TerminalStatus(m) : Lua.ComputerStatus(this);
        public override string OverTextIndirect => "Lua Computer";

        // The script's name: the block's Tag-tool label, or DefaultScript ("computer"; a Mainframe's is "mainframe").
        public string ScriptName
        {
            get
            {
                try
                {
                    var label = brickCoord.TagData.label;
                    return string.IsNullOrEmpty(label) ? DefaultScript : label.Trim();
                }
                catch { return DefaultScript; }
            }
        }

        protected virtual string DefaultScript => "computer";

        // Linked blocks, oldest link first. (The game keeps a computer's links newest first, and that order survives
        // saving; named ports (Ports.cs) are the way not to depend on it at all.)
        public List<ICommand> Inputs => Linked(predecessors);
        public List<ICommand> Outputs => Linked(targets);

        static List<ICommand> Linked(CommandLinkData links)
        {
            var list = new List<ICommand>();
            foreach (var item in links.Items)
                if (item.Command != null) list.Add(item.Command);
            list.Reverse();
            return list;
        }

        // Sends a value down one link, the way a block passes its state on: the target runs as if signalled, and
        // passes it on through its own links.
        public static void Send(ICommand target, float value)
        {
            var player = Dependency.RenderPipeline != null ? Dependency.RenderPipeline.commandManager : null;
            target.Activate(manualActivation: false, player, Signal(value));
        }

        // A link carries 0 to 1, as vanilla links do. Systems multiply their output by their state unchecked (a
        // thruster sent 3 pushed 3x, a reactor would make 3x power). Tiered parts raise the rated stats instead, so
        // they are untouched by this.
        internal static float Signal(float value) => float.IsNaN(value) ? 0f : Mathf.Clamp01(value);
    }

    static class LuaComputerBlock
    {
        public static readonly int BrickId = Ids.Claim(IdSpace.Brick, "EvoCore.LuaComputer", 500); // 500 since it first shipped; 507 is TieredParts' thruster
        const string LookKey = "EvoCore.LuaComputer";

        // The Lua Computer's look, made on the player's machine from the game's own Spaceship Computer mesh (so no game
        // geometry ships with the mod): the stock frame (slot0) kept, its hologram symbol (slot1) replaced by one square
        // showing the lua_holo picture from EvoCore.LuaComputer.mtl. (lua_gate.py's "holo" mode, which made the file
        // this replaces.)
        internal static string Gate(string stockObj)
        {
            var v = new List<string>(); var vt = new List<string>(); var vn = new List<string>();
            var faces = new List<(string mat, string face)>();
            var holo = new List<int>();
            string mat = null;
            foreach (var raw in stockObj.Split('\n'))
            {
                var line = raw.Trim();
                var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                switch (p[0])
                {
                    case "v": v.Add(line); break;
                    case "vt": vt.Add(line); break;
                    case "vn": vn.Add(line); break;
                    case "usemtl": mat = p.Length > 1 ? p[1] : null; break;
                    case "f":
                        if (mat == "slot0") faces.Add(("slot0", line));
                        else if (mat == "slot1") { holo.Clear(); for (int i = 1; i < p.Length; i++) holo.Add(int.Parse(p[i].Split('/')[0])); }
                        break;
                }
            }
            float F(string x) => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture);
            string G(float x) => x.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
            // The square covers the stock symbol, just under where the symbol sat.
            var h = 0f;
            foreach (var i in holo) { var q = v[i - 1].Split(' '); h = Math.Max(h, Math.Max(Math.Abs(F(q[1])), Math.Abs(F(q[3])))); }
            const float y = 0.1407f - 0.0003f;
            vn.Add("vn 0 1 0");
            var up = vn.Count;
            vt.Add("vt 0.5 0.5");
            var first = v.Count + 1;
            foreach (var (x, z) in new[] { (-h, -h), (-h, h), (h, h), (h, -h) }) v.Add($"v {G(x)} {G(y)} {G(z)}");
            // The picture's u runs along -x and v along +z: Looks mirrors the OBJ's x into Unity.
            var corner = vt.Count + 1;
            vt.AddRange(new[] { "vt 1 0", "vt 1 1", "vt 0 1", "vt 0 0" });
            faces.Add(("lua_holo", "f " + string.Join(" ", new[] { 0, 1, 2, 3 }.Select(k => $"{first + k}/{corner + k}/{up}").ToArray())));

            var sb = new StringBuilder();
            sb.Append("# Lua Computer look: the game's Spaceship Computer frame (slot0) with a Lua logo in place of its holo\n");
            sb.Append("# symbol. Made by EvoCore from the game's own mesh on this machine; delete it to make it again.\n");
            sb.Append("mtllib EvoCore.LuaComputer.mtl\n");
            foreach (var line in v.Concat(vt).Concat(vn)) sb.Append(line).Append('\n');
            string last = null;
            foreach (var (m, face) in faces)
            {
                if (m != last) { sb.Append("usemtl ").Append(m).Append('\n'); last = m; }
                sb.Append(face).Append('\n');
            }
            return sb.ToString();
        }

        // For DevBridge: makes the look from the Spaceship Computer's mesh as a load would, and compares it with the
        // file in Looks (all but the comment lines).
        public static string CheckGate()
        {
            var stock = Brick.BRICK_INDEX[TabLogic.SPACESHIP_COMPUTER.brickID];
            var made = Gate(Looks.ObjText(stock.VoxelMesh.Mesh, stock, stock.brickID, out _));
            var path = System.IO.Path.Combine(Looks.Folder, LookKey + ".obj");
            System.IO.File.WriteAllText(System.IO.Path.Combine(Looks.Folder, "export", "generated_" + LookKey + ".obj"), made);
            if (!System.IO.File.Exists(path)) return "no file to compare with; generated one written to export";
            string[] Lines(string t) => t.Replace("\r", "").Split('\n').Where(l => l.Length > 0 && !l.StartsWith("#")).ToArray();
            var a = Lines(System.IO.File.ReadAllText(path));
            var b = Lines(made);
            var diff = Enumerable.Range(0, Math.Max(a.Length, b.Length)).Count(i => i >= a.Length || i >= b.Length || a[i] != b[i]);
            return diff == 0 ? $"identical ({b.Length} lines)" : $"{diff} line(s) differ of {a.Length} vs {b.Length}";
        }

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class Register
        {
            // An exception here would stop every block from loading, so failures are logged and contained.
            static void Prefix()
            {
                try
                {
                    var stock = TabLogic.SPACESHIP_COMPUTER;
                    var brick = new CommandBrick<LuaComputer>(BrickId, "Lua Computer", "gates/gate_large", ResizeMode.NoneBrick,
                        "Gates/gate_large_frame", AlphaMode.glowPaint, "Gates/gate_spaceship_screen", AlphaMode.holo)
                    {
                        PreviewPaint = stock.PreviewPaint,
                        defaultPaint = stock.defaultPaint,
                        functionType = stock.functionType,
                        cost = stock.cost,
                        massForUnitShape = stock.massForUnitShape,
                        boundsOverride = stock.boundsOverride,
                        gridLevel = stock.gridLevel,
                        gridLockOffset = stock.gridLockOffset,
                        hasGlowPaint = stock.hasGlowPaint,
                    };
                    brick.Desc("Runs a Lua script. Link buttons, switches and sensors into it, and link it to lights, doors and thrusters: " +
                               "the script reads its inputs and sets its outputs. Name it with the multitool's Tag mode; the label picks the script.")
                         .Tab(UI.Codex.CodexTab.COMMANDS);
                    Looks.Register(BrickId, LookKey); // its own mesh: Looks\EvoCore.LuaComputer.obj, made from the stock one if missing
                    Looks.Generators[LookKey] = (mesh, brick) => Gate(Looks.ObjText(mesh, brick, brick.brickID, out _));
                    Core.Log.Msg("Registered Lua Computer as brick " + BrickId);
                }
                catch (Exception e) { Core.Log.Error("Lua Computer registration failed: " + e); }
            }
        }

        // Materials are consumed from a baked table in ID order and spare entries are empty, so copy the Spaceship
        // Computer's rows into ours.
        [HarmonyPatch(typeof(Brick), "LoadResourcesRuntime")]
        static class CopyMaterials
        {
            static void Prefix(Brick __instance)
            {
                if (__instance.brickID != BrickId || __instance != Brick.BRICK_INDEX[BrickId]) return;
                try
                {
                    var table = VoxelMaterialResources.materialParamDatas;
                    var start = VoxelMaterialResources.MaterialCount;
                    var source = TabLogic.SPACESHIP_COMPUTER.VoxelMaterials;
                    if (start + source.Length > table.Length)
                    {
                        Core.Log.Error($"No room for Lua Computer materials: table {table.Length}, start {start}");
                        return;
                    }
                    for (int i = 0; i < source.Length; i++)
                    {
                        var material = source[i];
                        material.Id = start + i;
                        table[start + i] = material;
                    }
                }
                catch (Exception e) { Core.Log.Error("Lua Computer materials failed: " + e); }
            }
        }
    }
}
