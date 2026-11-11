using System.Linq;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // The Hardware tab of the Computer screen: the attached computer's board and the chips in its sockets on the left,
    // the computer parts you carry (inventory, and this ship's cargo) on the right. Put in moves a part from your
    // storage into the computer; Take out moves it back. Parts.cs has the parts and what they do.
    internal static class HardwareEditor
    {
        static Vector2 scroll;
        static GUIStyle head, name, dim, good;
        static string message = "", lastMessage = "";
        static System.Collections.Generic.List<Parts.Held> cached = new System.Collections.Generic.List<Parts.Held>();
        static float nextRead;
        static readonly System.Collections.Generic.Dictionary<int, string> costs = new System.Collections.Generic.Dictionary<int, string>();
        static string repairCost = "";

        static void Styles()
        {
            if (head != null) return;
            head = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            head.normal.textColor = new Color(0.95f, 0.75f, 0.35f);
            name = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            name.normal.textColor = new Color(0.55f, 0.85f, 1f);
            dim = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            dim.normal.textColor = new Color(0.6f, 0.63f, 0.67f);
            good = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            good.normal.textColor = new Color(0.45f, 1f, 0.55f);
        }

        // A Salvage Processor (SalvageProcessor.cs): every kind and rarity of salvage in the backpack, with what one
        // piece breaks down into, Break down 1 / all and Refine (4 into 1 of the next rarity).
        static System.Collections.Generic.List<(Salvage.Kind Kind, Game.Rarity.RarityLevels Rarity, float Count)> salvage =
            new System.Collections.Generic.List<(Salvage.Kind, Game.Rarity.RarityLevels, float)>();

        static void DrawProcessor(float height)
        {
            GUILayout.Label("SALVAGE PROCESSOR", head);
            GUILayout.Label($"The salvage in your backpack. Break down turns scrap into materials (rare and epic scrap may hide a chip); Refine turns {Processing.RefineCount} into 1 of the next rarity.", dim);
            if (Time.unscaledTime >= nextRead || message != lastMessage)
            {
                salvage = Processing.Held();
                nextRead = Time.unscaledTime + 0.5f;
                lastMessage = message;
            }
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(height));
            if (salvage.Count == 0) GUILayout.Label("No salvage on you. Break up wrecks with the multitool to find some.", dim);
            foreach (var s in salvage)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{s.Count:0} x {Salvage.Name(s.Kind, s.Rarity)}", name, GUILayout.Width(300));
                GUILayout.Label("1 gives " + Processing.Yields(s.Kind, s.Rarity), dim, GUILayout.Width(420));
                if (GUILayout.Button("Break down 1", GUILayout.Width(110))) message = Processing.BreakDown(s.Kind, s.Rarity, 1);
                if (GUILayout.Button("All", GUILayout.Width(50))) message = Processing.BreakDown(s.Kind, s.Rarity, (int)s.Count);
                GUI.enabled = s.Rarity < Game.Rarity.RarityLevels.epic && s.Count >= Processing.RefineCount;
                if (GUILayout.Button($"Refine {Processing.RefineCount}", GUILayout.Width(90))) message = Processing.Refine(s.Kind, s.Rarity, 1);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        // A PC Workstation: the computers and Expansion Slots linked to it, each with Open.
        static void DrawWorkstation(Workstation w, float height)
        {
            // The bench: a loose board, programmed here, then taken off and put in any computer.
            GUILayout.Label("BENCH", head);
            var bench = Parts.Of(w).Board;
            if (bench == null)
            {
                GUILayout.Label("Put a board on the bench to program it here; its program goes with it into any computer.", dim);
                if (Time.unscaledTime >= nextRead || message != lastMessage)
                {
                    cached = Parts.Reachable(w.brickCoord.entity.Parent);
                    nextRead = Time.unscaledTime + 0.5f;
                    lastMessage = message;
                }
                foreach (var h in cached.Where(x => x.Def.Kind == "board"))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Parts.NameOf(h.Def, h.Flags) + (BoardStore.Peek(h.Seed) != null ? " (programmed)" : ""), name, GUILayout.Width(300));
                    GUILayout.Label(h.Where, dim, GUILayout.Width(60));
                    if (GUILayout.Button("Put on bench", GUILayout.Width(120))) message = Parts.Install(w, h, w.brickCoord.entity.Parent);
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(bench.Name + (w.HasProgram ? $" ({(w.ProgramKind == "graph" ? "circuit" : "Lua")} program)" : " (blank)"), name, GUILayout.Width(300));
                if (GUILayout.Button("Program this board", GUILayout.Width(160))) { Lua.OpenComputer(w); message = ""; }
                if (GUILayout.Button("Take off", GUILayout.Width(90))) message = Parts.Remove(w, -1, w.brickCoord.entity.Parent);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(14);

            GUILayout.Label("PC WORKSTATION", head);
            var targets = Lua.WorkstationTargets(w);
            GUILayout.Label(targets.Count == 0 ? "Nothing linked yet. Link Lua Computers and Expansion Slots to this workstation (either way), then open them from here." :
                            "Open a computer to program it, or an Expansion Slot to fit its board.", dim);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(height));
            foreach (var t in targets)
            {
                GUILayout.BeginHorizontal();
                var kind = t is ExpansionSlot ? "Expansion Slot" : t is Mainframe ? "Mainframe" : "Lua Computer";
                GUILayout.Label($"{kind}  {t.ScriptName}", name, GUILayout.Width(320));
                var hw = Parts.Of(t);
                GUILayout.Label(hw.Board == null ? "no board" : hw.Board.Name, dim, GUILayout.Width(200));
                if (GUILayout.Button("Open", GUILayout.Width(90))) { Lua.OpenFromWorkstation(t); message = ""; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        internal static void Draw(float height)
        {
            Styles();
            var c = Lua.Attached != null && Lua.Attached.brickCoord.entity != null ? Lua.Attached : null;
            if (c == null) { GUILayout.Label("Open a Lua Computer (the activate key on it) to see its hardware."); return; }
            var ship = c.brickCoord.entity.Parent;
            var hw = Parts.Of(c);
            if (message != "") GUILayout.Label(message, good);
            if (c is Workstation w) { DrawWorkstation(w, height); return; }
            if (c is SalvageProcessor) { DrawProcessor(height); return; }
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(height));
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(460));
            var slot = c as ExpansionSlot;
            GUILayout.Label(slot != null ? "THIS EXPANSION SLOT" : "THIS COMPUTER", head);
            if (slot != null)
            {
                var main = Parts.MainOf(slot);
                GUILayout.Label(main == null ? "Not linked to a Lua Computer yet: link it (either way) and its chips count for that computer." :
                                $"Serves the computer {main.ScriptName}: its chips count there.", dim);
            }
            if (hw.Board == null)
            {
                GUILayout.Label(slot != null ? "No board. Put a Daughter Board in." :
                                "No board. The computer runs as it is; a board adds sockets for chips that make it do more.", dim);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(hw.Board.Name, name, GUILayout.Width(300));
                if (GUILayout.Button("Take out", GUILayout.Width(90))) message = Parts.Remove(c, -1, ship);
                GUILayout.EndHorizontal();
                GUILayout.Label($"{hw.Sockets.Length} sockets, {hw.Board.Def.Pins}-pin. Taking the board out takes its chips with it.", dim);
                if ((hw.Board.Flags & Parts.Damaged) != 0)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Damaged: one socket doesn't work. Repair: " + (PlayerState.IsSurvival ? repairCost : "free in creative"), dim, GUILayout.Width(360));
                    if (GUILayout.Button("Repair", GUILayout.Width(90))) message = Parts.Repair(c);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(6);
                for (int i = 0; i < hw.Sockets.Length; i++)
                {
                    var chip = hw.Sockets[i];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Socket {i + 1}", dim, GUILayout.Width(70));
                    GUILayout.Label(chip == null ? "empty" : chip.Name, chip == null ? dim : name, GUILayout.Width(230));
                    if (chip != null && GUILayout.Button("Take out", GUILayout.Width(90))) message = Parts.Remove(c, i, ship);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Space(10);
            if (slot == null)
            {
                var extra = Parts.AppSlots(c);
                var slots = c.Inputs.Concat(c.Outputs).OfType<ExpansionSlot>().Distinct().Count();
                GUILayout.Label($"App slots: {Apps.Slots(c)}" + (extra > 0 ? $" ({extra} from memory chips)" : "") +
                                $"   Tick speed: x{Parts.Speed(c):0.##}" + (slots > 0 ? $"   Expansion Slots linked: {slots}" : ""), dim);
            }
            GUILayout.EndVertical();

            GUILayout.Space(30);

            GUILayout.BeginVertical(GUILayout.Width(520));
            GUILayout.Label("YOUR PARTS", head);
            // Storage is walked twice a second at most (OnGUI runs several times a frame), and after every action.
            if (Time.unscaledTime >= nextRead || message != lastMessage)
            {
                cached = Parts.Reachable(ship);
                if (PlayerState.IsSurvival)
                {
                    foreach (var d in Parts.All) costs[d.Id] = PartsSurvival.CostText(d.Cost);
                    repairCost = PartsSurvival.CostText(Parts.RepairCost);
                }
                nextRead = Time.unscaledTime + 0.5f;
                lastMessage = message;
            }
            var held = cached;
            if (held.Count == 0) GUILayout.Label("No computer parts on you or in this ship's cargo.", dim);
            foreach (var h in held)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Parts.NameOf(h.Def, h.Flags), name, GUILayout.Width(220));
                GUILayout.Label(h.Where, dim, GUILayout.Width(60));
                if (GUILayout.Button("Put in", GUILayout.Width(90))) message = Parts.Install(c, h, ship);
                GUILayout.EndHorizontal();
                GUILayout.Label("   " + h.Def.About, dim);
            }
            if (PlayerState.gameMode == PlayerState.GameMode.creative)
            {
                GUILayout.Space(10);
                if (GUILayout.Button("Creative: get one of each part", GUILayout.Width(260))) message = Parts.SpawnAll();
            }

            GUILayout.Space(16);
            GUILayout.Label("BUILD PARTS", head);
            GUILayout.Label(PlayerState.IsSurvival ? "From the materials in your backpack, once their research is done at a Research Station." :
                            "Free in creative. In survival, from your backpack's materials once researched.", dim);
            foreach (var d in Parts.All)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(d.Name, name, GUILayout.Width(220));
                if (d.Research == null) GUILayout.Label("found in wrecked ship computers", dim, GUILayout.Width(280));
                else
                {
                    var locked = PlayerState.IsSurvival && !d.Research.Unlocked;
                    GUILayout.Label(locked ? $"research \"{d.Research.Name}\" (tier {d.Research.Tier})" : PlayerState.IsSurvival ? costs[d.Id] : "free", dim, GUILayout.Width(280));
                    GUI.enabled = !locked;
                    if (GUILayout.Button("Build", GUILayout.Width(70))) message = Parts.Build(d);
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
        }
    }
}
