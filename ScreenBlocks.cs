using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace EvoCore
{
    // Lua Screens: EvoCore's own monitors, made for Lua displays from the stock models (Monitor, Monitor Wedged): the
    // whole front plate is the screen. The black collar and glowing slab the game stands on it are gone, and the plate
    // and its opening become one flat glowing face; the icon square is replaced by a hidden copy of that face's outline
    // just behind it (Monitors finds the picture's frame and up from it). The screen is dark by default.
    // The wedge keeps the stock wedge resize (BrickFullAnchor, scaled evenly; Monitors.Placed): the plain stepped
    // resize (BrickFull) moves each point by which side of the middle it's on, which bends a wedge out of shape.
    // They are CommandDisplays, so everything that works with a monitor works with them.
    static class ScreenBlocks
    {
        public static readonly int FlatId = Ids.Claim(IdSpace.Brick, "EvoCore.LuaScreen", 517);
        public static readonly int WedgeId = Ids.Claim(IdSpace.Brick, "EvoCore.LuaScreenWedge", 518);
        const string FlatLook = "EvoCore.LuaScreen", WedgeLook = "EvoCore.LuaScreenWedge";
        // A Lua Screen's background until a script sets display.background: a dark screen, not the frame's paint.
        public static readonly Color Dark = new Color32(0x0b, 0x13, 0x10, 255);

        public static bool Is(int brickId) => brickId == FlatId || brickId == WedgeId;

        static Brick StockOf(int id) => id == FlatId ? TabLogic.SCREEN_FLAT : id == WedgeId ? TabLogic.SCREEN_WEDGE : null;

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class Register
        {
            // An exception here would stop every block from loading, so failures are logged and contained.
            static void Prefix()
            {
                try
                {
                    // The stock arguments (TabLogic.SCREEN_FLAT, SCREEN_WEDGE).
                    var flat = new CommandBrick<CommandDisplay>(FlatId, "Lua Screen", "screens/screen_flat", ResizeMode.BrickFlat,
                        "Hull/HullA", UV_Proj.on, BorderMode.full_tile, "Construction Small/brick_base_nopaint", "Hull/HullA", UV_Proj.on,
                        BorderMode.full_tile, "Construction Small/brick_base_nopaint", "Screens/black_bits", UV_Proj.on, PaintMode.noPaint,
                        "Screens/screen_base", 4f, UV_Proj.on, AlphaMode.glowPaint, BorderMode.full_tile, "Screens/screen_border",
                        "Gates/gate_void", AlphaMode.holoSqrd, DynData_Vert.vertexUVOffset, BorderMode.noStretch);
                    var wedge = new CommandBrick<CommandDisplay>(WedgeId, "Lua Screen Wedge", "screens/screen_wedge", ResizeMode.BrickFullAnchor,
                        "Hull/HullA", 4f, UV_Proj.on, new Vector2(0.49f, 0.51f), BorderMode.full_tile, "Screens/wedge_side", "Hull/HullA",
                        UV_Proj.on, BorderMode.full_tile, "Construction Small/brick_base_nopaint", "Screens/black_bits", UV_Proj.on,
                        PaintMode.noPaint, "Screens/screen_base", 4f, UV_Proj.on, AlphaMode.glowPaint, BorderMode.full_tile,
                        "Screens/screen_border", "Gates/gate_void", AlphaMode.holo, DynData_Vert.vertexUVOffset, BorderMode.noStretch);
                    foreach (var (brick, look) in new[] { (flat, FlatLook), (wedge, WedgeLook) })
                    {
                        var stock = StockOf(brick.brickID);
                        brick.PreviewPaint = stock.PreviewPaint;
                        brick.defaultPaint = stock.defaultPaint;
                        brick.functionType = stock.functionType;
                        brick.cost = stock.cost;
                        brick.massForUnitShape = stock.massForUnitShape;
                        brick.boundsOverride = stock.boundsOverride;
                        brick.gridLevel = stock.gridLevel;
                        brick.gridLockOffset = stock.gridLockOffset;
                        brick.hasGlowPaint = stock.hasGlowPaint;
                        brick.Desc("A monitor made for Lua Computers: link one to it and its script's picture fills the whole lit screen, at any size. " +
                                   "Set display.background to colour the screen and its edge.")
                             .Tab(UI.Codex.CodexTab.COMMANDS);
                        Looks.Register(brick.brickID, look); // Looks\<look>.obj, made from the stock model if missing
                        Looks.Generators[look] = Look;
                        Core.Log.Msg($"Registered {brick.name} as brick {brick.brickID}");
                    }
                }
                catch (Exception e) { Core.Log.Error("Lua Screen registration failed: " + e); }
            }
        }

        // Materials are consumed from a baked table in ID order and spare entries are empty, so copy the stock
        // monitor's rows into ours.
        [HarmonyPatch(typeof(Brick), "LoadResourcesRuntime")]
        static class CopyMaterials
        {
            static void Prefix(Brick __instance)
            {
                var stock = StockOf(__instance.brickID);
                if (stock == null || __instance != Brick.BRICK_INDEX[__instance.brickID]) return;
                try
                {
                    var table = VoxelMaterialResources.materialParamDatas;
                    var start = VoxelMaterialResources.MaterialCount;
                    var source = stock.VoxelMaterials;
                    if (start + source.Length > table.Length) { Core.Log.Error($"No room for {__instance.name} materials: table {table.Length}, start {start}"); return; }
                    for (int i = 0; i < source.Length; i++)
                    {
                        var material = source[i];
                        material.Id = start + i;
                        table[start + i] = material;
                    }
                }
                catch (Exception e) { Core.Log.Error($"{__instance.name} materials failed: " + e); }
            }
        }

        // The stock model with its front plate made the screen (see the top).
        static string Look(Mesh stock, Brick brick)
        {
            var verts = stock.vertices.ToList();
            var normals = stock.normals.ToList();
            var uvs = stock.uv.ToList();
            var tris = stock.triangles;
            var materials = brick.VoxelMaterials;
            int SlotOf(int t) => Mathf.FloorToInt((uvs[tris[t]].x + uvs[tris[t + 1]].x + uvs[tris[t + 2]].x) / 3f);
            bool Is(int slot, params AlphaMode[] modes) => slot >= 0 && slot < materials.Length && modes.Contains(materials[slot].AlphaMode);
            var icon = Enumerable.Range(0, tris.Length / 3).Select(i => i * 3).Where(t => Is(SlotOf(t), AlphaMode.holo, AlphaMode.holoSqrd)).ToList();
            if (icon.Count == 0) return Looks.ObjText(stock, brick, brick.brickID, out _);
            var slot = SlotOf(icon[0]);

            // The icon square's frame, by its texture corners (as Monitors finds it).
            var points = icon.SelectMany(t => new[] { tris[t], tris[t + 1], tris[t + 2] }).Distinct().ToList();
            float u0 = points.Min(i => uvs[i].x), u1 = points.Max(i => uvs[i].x), v0 = points.Min(i => uvs[i].y), v1 = points.Max(i => uvs[i].y);
            int Corner(float u, float v) => points.OrderBy(i => (uvs[i] - new Vector2(u, v)).sqrMagnitude).First();
            int i00 = Corner(u0, v0), i10 = Corner(u1, v0), i01 = Corner(u0, v1), i11 = Corner(u1, v1);
            Vector3 c00 = verts[i00], U = verts[i10] - c00, V = verts[i01] - c00;
            var n = Vector3.Cross(U, V).normalized;
            // Out of the block: away from the middle of its surface, weighted by area (the middle of its bounds lies on
            // a wedge's slope, so it can't tell which side is out).
            Vector3 sum = Vector3.zero; float area = 0f;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                Vector3 p0 = verts[tris[t]], p1 = verts[tris[t + 1]], p2 = verts[tris[t + 2]];
                var a = Vector3.Cross(p1 - p0, p2 - p0).magnitude;
                sum += (p0 + p1 + p2) / 3f * a;
                area += a;
            }
            var middle = area > 0f ? sum / area : stock.bounds.center;
            var iconMiddle = icon.SelectMany(t => new[] { verts[tris[t]], verts[tris[t + 1]], verts[tris[t + 2]] }).Aggregate(Vector3.zero, (s, p) => s + p) / (icon.Count * 3);
            if (Vector3.Dot(n, iconMiddle - middle) < 0f) n = -n;

            // The front plate: the flattest painted face turned the screen's way, behind the screen (the game's model
            // stands a black collar and a glowing slab on it, 3.5 to 5 cm out). Its outline is the new screen.
            var frame = float.MinValue;
            var frameSlot = -1;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                var s = SlotOf(t);
                if (s == slot || Is(s, AlphaMode.glowPaint, AlphaMode.glowDiffuse, AlphaMode.glowTransparent)) continue;
                Vector3 p0 = verts[tris[t]], p1 = verts[tris[t + 1]], p2 = verts[tris[t + 2]];
                if (Mathf.Abs(Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0).normalized, n)) < 0.99f) continue;
                var o = Vector3.Dot(p0 - c00, n);
                if (o < -0.003f && o > frame && Vector3.Cross(p1 - p0, p2 - p0).sqrMagnitude > 1e-8f) { frame = o; frameSlot = s; }
            }
            var lit = Enumerable.Range(0, materials.Length).Where(s => s != slot && Is(s, AlphaMode.glowPaint)).DefaultIfEmpty(-1).First();
            if (frameSlot < 0 || lit < 0) return Looks.ObjText(stock, brick, brick.brickID, out _);
            bool OnPlate(int t) => SlotOf(t) == frameSlot && new[] { 0, 1, 2 }.All(k => Mathf.Abs(Vector3.Dot(verts[tris[t + k]] - c00, n) - frame) < 0.002f);   // a Monitor's plate isn't quite flat (1 mm)
            float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
            var plateOut = false;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (!OnPlate(t)) continue;
                plateOut = Vector3.Dot(Vector3.Cross(verts[tris[t + 1]] - verts[tris[t]], verts[tris[t + 2]] - verts[tris[t]]), n) > 0f;
                for (int k = 0; k < 3; k++)
                {
                    var w = verts[tris[t + k]] - c00;
                    float a = Vector3.Dot(w, U) / U.sqrMagnitude, b = Vector3.Dot(w, V) / V.sqrMagnitude;
                    a0 = Mathf.Min(a0, a); a1 = Mathf.Max(a1, a); b0 = Mathf.Min(b0, b); b1 = Mathf.Max(b1, b);
                }
            }

            // Keep the body: everything but the plate and what stood in front of it (collar, slab, icon).
            var kept = new List<int>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (SlotOf(t) == slot || OnPlate(t)) continue;
                if (new[] { 0, 1, 2 }.Any(k => Vector3.Dot(verts[tris[t + k]] - c00, n) > frame + 0.002f)) continue;
                kept.AddRange(new[] { tris[t], tris[t + 1], tris[t + 2] });
            }
            var inside = stock.bounds;
            var plateAt = c00 + n * frame;
            int Add(Vector3 p, Vector2 uv)
            {
                verts.Add(Vector3.Max(inside.min, Vector3.Min(inside.max, p)));
                normals.Add(n);
                uvs.Add(uv);
                return verts.Count - 1;
            }
            // Quads wound as the face they replace was: (00, 10, 01) turns the way Cross(U, V) does.
            var uvOut = Vector3.Dot(Vector3.Cross(U, V), n) > 0f;
            void Quad(int q00, int q10, int q01, int q11, bool facesOut) =>
                kept.AddRange(facesOut == uvOut ? new[] { q00, q10, q01, q10, q11, q01 } : new[] { q00, q01, q10, q10, q01, q11 });

            // The screen: one glowing face over the whole plate, its opening filled.
            Vector2 Tex(float a, float b) => new Vector2(lit + 0.01f + 0.98f * Mathf.InverseLerp(a0, a1, a), Mathf.InverseLerp(b0, b1, b));
            Quad(Add(plateAt + U * a0 + V * b0, Tex(a0, b0)), Add(plateAt + U * a1 + V * b0, Tex(a1, b0)),
                 Add(plateAt + U * a0 + V * b1, Tex(a0, b1)), Add(plateAt + U * a1 + V * b1, Tex(a1, b1)), plateOut);

            // The hidden copy of its outline behind it, with the icon's texture corners, so Monitors finds the picture's
            // frame (and its up) as on a stock monitor. In from the edges, so a wedge's corners stay inside the block.
            var back = 0.03f * Mathf.Min(U.magnitude * (a1 - a0), V.magnitude * (b1 - b0));
            float da = 2f * back / U.magnitude, db = 2f * back / V.magnitude;
            var hidden = plateAt - n * back;
            var iconOut = Vector3.Dot(Vector3.Cross(verts[tris[icon[0] + 1]] - verts[tris[icon[0]]], verts[tris[icon[0] + 2]] - verts[tris[icon[0]]]), n) > 0f;
            Quad(Add(hidden + U * (a0 + da) + V * (b0 + db), uvs[i00]), Add(hidden + U * (a1 - da) + V * (b0 + db), uvs[i10]),
                 Add(hidden + U * (a0 + da) + V * (b1 - db), uvs[i01]), Add(hidden + U * (a1 - da) + V * (b1 - db), uvs[i11]), iconOut);

            // The stock model's exact bounds, through a flat (zero-area, so never drawn) triangle from corner to corner:
            // the game stretches a wedge between the stock bounds, so ours must match or what Monitors works out drifts
            // from what's drawn.
            int Pin(Vector3 p) { verts.Add(p); normals.Add(n); uvs.Add(uvs[i00]); return verts.Count - 1; }
            kept.AddRange(new[] { Pin(stock.bounds.min), Pin(stock.bounds.max), Pin(stock.bounds.max) });
            var mesh = new Mesh { name = brick.name };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(kept, 0);
            mesh.RecalculateBounds();
            return Looks.ObjText(mesh, brick, brick.brickID, out _);
        }
    }
}
