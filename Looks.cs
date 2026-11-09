using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine;

namespace EvoCore
{
    // Custom looks for mod blocks: a mesh made by hand (Blender) replaces the stock mesh a brick borrows, while the
    // stock block keeps supplying the collider, bounds and placement rules. Plan and game notes: BLOCK-LOOKS.md.
    //
    //   Looks.Register(BrickId, "MyMod.Widget");  // while registering the brick; uses Looks\MyMod.Widget.obj if it's there
    //   get EvoCore.Looks.Export(196)              // DevBridge: writes a stock block's mesh as a starting point
    //
    // The game picks a triangle's material from the whole-number part of its first UV's x: slot n of the brick's
    // materials for x in [n, n+1). An OBJ reuses the stock block's materials with `usemtl slot0`, `usemtl slot1`, ...
    // Any other material name is a new surface of our own (step 2), coloured from the .mtl Blender writes beside the
    // OBJ: Kd is the colour, Ke the glow. Coordinates are Blender's OBJ default (Y up, -Z forward); x is mirrored
    // into Unity's left-handed space here, and back again on export, the same way Unity's own importer does it.
    public static class Looks
    {
        public static string Folder => Path.Combine(Paths.UserData, "EvoCore", "Looks");

        static readonly Dictionary<int, string> registered = new Dictionary<int, string>();
        public static readonly List<string> Status = new List<string>();
        public static bool Hooked;

        // Looks made on the player's machine from the game's own mesh instead of shipped as a file: given the stock
        // mesh and brick, the OBJ text. Used when Folder has no <key>.obj; the result is saved there.
        public static readonly Dictionary<string, Func<Mesh, Brick, string>> Generators = new Dictionary<string, Func<Mesh, Brick, string>>();

        public static void Register(int brickId, string key)
        {
            WriteStock();
            registered[brickId] = key;
        }

        // EvoCore's own looks ship inside the DLL (looks\ in the source) and are written to Folder when missing, so a
        // fresh install has them; files already there are left alone, so edited ones stay.
        static bool stockWritten;

        static void WriteStock()
        {
            if (stockWritten) return;
            stockWritten = true;
            try
            {
                Directory.CreateDirectory(Folder);
                var asm = typeof(Looks).Assembly;
                foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("EvoCore.looks.")))
                {
                    var path = Path.Combine(Folder, res.Substring("EvoCore.looks.".Length));
                    if (File.Exists(path)) continue;
                    using (var from = asm.GetManifestResourceStream(res))
                    using (var to = File.Create(path))
                        from.CopyTo(to);
                }
            }
            catch (Exception e) { Core.Log.Warning("Looks: couldn't write the stock looks: " + e.Message); }
        }

        public static string Report() => (Hooked ? "Looks hooked" : "Looks NOT hooked (game changed?)") +
                                         (Status.Count > 0 ? ": " + string.Join("; ", Status) : ", none applied") +
                                         (Surfaces.Count > 0 ? $". Surfaces: {string.Join("; ", Surfaces.Select(Describe))}" : "");

        // Swaps the mesh inside VoxelMesh's constructor, before it pushes the triangles to the renderer. The preview
        // load is made to miss, so the constructor makes its preview from our mesh.
        [HarmonyPatch(typeof(VoxelMesh), MethodType.Constructor,
            new[] { typeof(string), typeof(Brick), typeof(float), typeof(ResizeMode), typeof(bool), typeof(bool) })]
        static class SwapInConstructor
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var body = instructions.ToList();
                var main = AccessTools.Field(typeof(VoxelMesh), nameof(VoxelMesh.Mesh));
                var preview = AccessTools.Field(typeof(VoxelMesh), nameof(VoxelMesh.MeshPreview));
                var mainAt = body.FindIndex(i => i.opcode == OpCodes.Stfld && Equals(i.operand, main));
                var previewAt = body.FindIndex(i => i.opcode == OpCodes.Stfld && Equals(i.operand, preview));
                bool once(FieldInfo f) => body.Count(i => i.opcode == OpCodes.Stfld && Equals(i.operand, f)) == (f == main ? 1 : 2);
                if (mainAt < 0 || previewAt < mainAt || !once(main) || !once(preview) || body[mainAt].labels.Count > 0 || body[previewAt].labels.Count > 0)
                {
                    Core.Log.Warning("Looks: VoxelMesh's constructor has changed; custom block looks are off");
                    return body;
                }
                // Later index first, so the earlier one doesn't move.
                body.InsertRange(previewAt, new[] { new CodeInstruction(OpCodes.Ldarg_2), CodeInstruction.Call(typeof(Looks), nameof(PickPreview)) });
                body.InsertRange(mainAt, new[] { new CodeInstruction(OpCodes.Ldarg_2), CodeInstruction.Call(typeof(Looks), nameof(PickMesh)) });
                Hooked = true;
                return body;
            }
        }

        static readonly Dictionary<Brick, Mesh> applied = new Dictionary<Brick, Mesh>();

        static Mesh PickMesh(Mesh stock, Brick brick)
        {
            try
            {
                if (brick == null || stock == null || !registered.TryGetValue(brick.brickID, out var key)) return stock;
                if (brick.brickID >= Brick.BRICK_INDEX.Length || Brick.BRICK_INDEX[brick.brickID] != brick) return stock;
                var path = Path.Combine(Folder, key + ".obj");
                if (!File.Exists(path) && Generators.TryGetValue(key, out var make))
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(path, make(stock, brick));
                    Core.Log.Msg($"Looks: made {key}.obj from the game's own mesh");
                }
                if (!File.Exists(path)) { Note($"{key}: no {key}.obj, stock look"); return stock; }
                var mesh = Load(path, stock.bounds, brick.VoxelMaterials?.Length ?? 0, out var info, out var names, out var mtllib);
                if (names.Count > 0)
                {
                    AddSurfaces(brick, key, MtlPath(path, mtllib), names);
                    info += $", {names.Count} new surface(s)";
                }
                mesh.name = "EvoCore look " + key;
                applied[brick] = mesh;
                Note($"{key}: {info}");
                Core.Log.Msg($"Looks: brick {brick.brickID} uses {key}.obj ({info})");
                return mesh;
            }
            catch (Exception e)
            {
                Note($"{registered[brick.brickID]}: {e.Message}, stock look");
                Core.Log.Warning("Looks: " + e.Message + "; keeping the stock look");
                return stock;
            }
        }

        static Mesh PickPreview(Mesh stockPreview, Brick brick) => brick != null && applied.ContainsKey(brick) ? null : stockPreview;

        static void Note(string line)
        {
            Status.RemoveAll(s => s.StartsWith(line.Substring(0, line.IndexOf(':') + 1)));
            Status.Add(line);
        }

        static readonly Regex slotName = new Regex(@"^slot(\d+)$", RegexOptions.IgnoreCase);
        const float UvInset = 0.0001f;

        // Reads an OBJ into a mesh the game can use. The mesh has to fit the stock block's bounds (a little slack for
        // rounding), since the collider, culling and placement all still come from the stock block.
        // `surfaces` gets the new surfaces' names in the order they first appear; they take slots after the stock ones.
        public static Mesh Load(string path, Bounds fit, int slots, out string info, out List<string> surfaces, out string mtllib)
        {
            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var names = surfaces = new List<string>();
            mtllib = null;
            var slot = 0;
            var corners = new Dictionary<(int, int, int, int), int>();
            var vertices = new List<Vector3>();
            var vertexNormals = new List<Vector3>();
            var vertexUvs = new List<Vector2>();
            var triangles = new List<int>();
            bool missingNormals = false;
            int lineNo = 0;
            float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            int Index(string s, int count) { var i = int.Parse(s, CultureInfo.InvariantCulture); return i < 0 ? count + i : i - 1; }

            foreach (var raw in File.ReadLines(path))
            {
                lineNo++;
                var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts[0].StartsWith("#")) continue;
                switch (parts[0])
                {
                    case "v": positions.Add(new Vector3(-F(parts[1]), F(parts[2]), F(parts[3]))); break;
                    case "vt": uvs.Add(new Vector2(F(parts[1]), parts.Length > 2 ? F(parts[2]) : 0f)); break;
                    case "vn": normals.Add(new Vector3(-F(parts[1]), F(parts[2]), F(parts[3]))); break;
                    case "mtllib": mtllib = raw.Substring(raw.IndexOf("mtllib", StringComparison.Ordinal) + 6).Trim(); break;
                    case "usemtl":
                        var name = parts.Length > 1 ? parts[1] : "";
                        var m = slotName.Match(name);
                        if (m.Success)
                        {
                            slot = int.Parse(m.Groups[1].Value);
                            if (slot >= slots) throw new InvalidDataException($"line {lineNo}: {name}, but the block has {slots} material(s)");
                        }
                        else { if (!names.Contains(name)) names.Add(name); slot = slots + names.IndexOf(name); }
                        break;
                    case "f":
                        if (parts.Length < 4) throw new InvalidDataException($"line {lineNo}: a face needs 3 corners");
                        var face = new List<int>();
                        for (int c = 1; c < parts.Length; c++)
                        {
                            var ids = parts[c].Split('/');
                            var v = Index(ids[0], positions.Count);
                            var t = ids.Length > 1 && ids[1] != "" ? Index(ids[1], uvs.Count) : -1;
                            var n = ids.Length > 2 && ids[2] != "" ? Index(ids[2], normals.Count) : -1;
                            if (v < 0 || v >= positions.Count || t >= uvs.Count || n >= normals.Count) throw new InvalidDataException($"line {lineNo}: index out of range");
                            if (n < 0) missingNormals = true;
                            var cornerKey = (v, t, n, slot);
                            if (!corners.TryGetValue(cornerKey, out var at))
                            {
                                at = vertices.Count;
                                corners[cornerKey] = at;
                                vertices.Add(positions[v]);
                                vertexNormals.Add(n >= 0 ? normals[n] : Vector3.up);
                                var uv = t >= 0 ? uvs[t] : Vector2.zero;
                                vertexUvs.Add(new Vector2(slot + Mathf.Lerp(UvInset, 1f - UvInset, Mathf.Clamp01(uv.x)), uv.y));
                            }
                            face.Add(at);
                        }
                        // Fan into triangles; mirroring x flips the winding, so each is reversed.
                        for (int c = 1; c + 1 < face.Count; c++) { triangles.Add(face[0]); triangles.Add(face[c + 1]); triangles.Add(face[c]); }
                        break;
                }
            }
            if (triangles.Count == 0) throw new InvalidDataException("no faces");

            var slack = fit;
            slack.Expand(Vector3.Max(fit.size * 0.02f, Vector3.one * 0.001f));
            var outside = vertices.Count(p => !slack.Contains(p));
            if (outside > 0)
            {
                var b = new Bounds(vertices[0], Vector3.zero);
                foreach (var p in vertices) b.Encapsulate(p);
                throw new InvalidDataException($"{outside} vertices outside the stock block (mesh {Show(b)}, block {Show(fit)})");
            }

            var mesh = new Mesh();
            if (vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetNormals(vertexNormals);
            mesh.SetUVs(0, vertexUvs);
            mesh.SetTriangles(triangles, 0, calculateBounds: false);
            if (missingNormals) mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            // The game takes the block's bounds (collider, mass, culling) from the mesh.
            mesh.bounds = fit;
            var used = triangles.Select((v, i) => i).Where(i => i % 3 == 0).Select(i => Mathf.FloorToInt(vertexUvs[triangles[i]].x)).Distinct().OrderBy(s => s);
            info = $"{triangles.Count / 3} triangles, {vertices.Count} vertices, slots {string.Join(",", used)}";
            return mesh;
        }

        // ---- Step 2: new surfaces (solid colours and glow) ----
        //
        // Each new surface is a material row of our own plus one diffuse and one specular slice appended to the game's
        // texture arrays. The row is a copy of the plain hull's (opaque, no normal map), pointed at our slices:
        // - diffuse: the colour (sRGB, like the game's own textures);
        // - specular: the hull's shine in red and green, the paint mask in blue, the glow strength in alpha.
        // A glowing surface (Ke above black) uses AlphaMode.glowDiffuse, which glows in its own colour, so it takes
        // Ke's colour. A name containing "paint" takes the paint tool's colour, shaded by Kd's brightness.
        // Rows must be written before the game uploads the table to the GPU (just after the bricks load), and slices
        // are added once all bricks have loaded. Looks.Reload() re-reads the .mtl files while playing, for colour tweaks.
        // Step 3: a surface whose .mtl entry has `map_Kd picture.png` (Blender writes this for an image texture) draws
        // that picture, read from beside the .mtl, instead of Kd's colour. Any size: it's scaled to the arrays' 256x256
        // and compressed like the game's own. UVs 0-1 cover the picture; PNG's top is the top of UV space. Glow (Ke)
        // still works, in the picture's own colours; a "paint" surface takes the picture's brightness as its shading.
        public class Surface
        {
            public string Key, Name, Mtl, Texture;
            public Color Colour = new Color(0.5f, 0.5f, 0.5f);
            public float Glow;
            public bool Paint, Holo, Defined;
            public int Row, Diffuse, Specular;
            public Brick Brick;   // the block whose materials hold this row
        }

        public static readonly List<Surface> Surfaces = new List<Surface>();
        public static bool Gamma = true;                              // Kd is linear in Blender; the game's textures are sRGB
        public static Color32 Shine = new Color32(255, 102, 0, 0);    // the hull's specular red and green, re-read at load
        public static Texture2DArray Diffuse, Specular;
        static int diffuseAdded, specularAdded;

        static string MtlPath(string obj, string mtllib)
        {
            var dir = Path.GetDirectoryName(obj);
            if (!string.IsNullOrEmpty(mtllib) && File.Exists(Path.Combine(dir, mtllib))) return Path.Combine(dir, mtllib);
            return Path.ChangeExtension(obj, ".mtl");
        }

        class MtlEntry
        {
            public Color kd = new Color(0.5f, 0.5f, 0.5f), ke = Color.black;
            public string map;   // map_Kd, as a full path
        }

        static Dictionary<string, MtlEntry> ReadMtl(string path)
        {
            var result = new Dictionary<string, MtlEntry>();
            if (!File.Exists(path)) return result;
            MtlEntry entry = null;
            float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            Color C(string[] p) => p.Length >= 4 ? new Color(F(p[1]), F(p[2]), F(p[3])) : Color.black;
            foreach (var raw in File.ReadLines(path))
            {
                var p = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                if (p[0] == "newmtl") result[p.Length > 1 ? p[1] : ""] = entry = new MtlEntry();
                else if (entry != null && p[0] == "Kd") entry.kd = C(p);
                else if (entry != null && p[0] == "Ke") entry.ke = C(p);
                // The file name is the last word (options like -bm 1 come first); Blender may write an absolute path.
                else if (entry != null && p[0] == "map_Kd" && p.Length > 1)
                    entry.map = Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(p[p.Length - 1]));
            }
            return result;
        }

        static void Define(Surface s, Dictionary<string, MtlEntry> mtl)
        {
            s.Paint = s.Name.IndexOf("paint", StringComparison.OrdinalIgnoreCase) >= 0;
            s.Holo = s.Name.IndexOf("holo", StringComparison.OrdinalIgnoreCase) >= 0;
            s.Defined = mtl.TryGetValue(s.Name, out var def);
            s.Texture = s.Defined ? def.map : null;
            if (!s.Defined) { s.Colour = new Color(0.5f, 0.5f, 0.5f); s.Glow = 0; return; }
            var peak = Mathf.Max(def.ke.r, def.ke.g, def.ke.b);
            s.Glow = Mathf.Clamp01(peak);
            s.Colour = peak > 0.001f ? def.ke / peak : def.kd;
            s.Colour.a = 1;
        }

        static void AddSurfaces(Brick brick, string key, string mtlPath, List<string> names)
        {
            var table = VoxelMaterialResources.materialParamDatas;
            var start = VoxelMaterialResources.MaterialCount;
            if (start + names.Count > table.Length) throw new InvalidDataException($"no room for {names.Count} material rows at {start}");
            var diffuse = ResourcesLoader.VOXEL_DIFFUSE;
            var specular = Shader.GetGlobalTexture("SPECULAR_TEX") as Texture2DArray;
            if (diffuse == null || specular == null) throw new InvalidDataException("the game's texture arrays aren't loaded");
            var template = TabHull.HULL_A?.VoxelMaterials?.Length > 0 ? TabHull.HULL_A.VoxelMaterials[0] : brick.VoxelMaterials[0];
            var mtl = ReadMtl(mtlPath);
            var rows = brick.VoxelMaterials.ToList();
            var made = new List<Surface>();
            foreach (var name in names)
            {
                // The layers are a first guess: AddSlices sets the ones the slices really get (other mods may append first).
                var s = new Surface { Key = key, Name = name, Mtl = mtlPath, Brick = brick, Row = start + made.Count, Diffuse = diffuse.depth + diffuseAdded + made.Count, Specular = specular.depth + specularAdded + made.Count };
                Define(s, mtl);
                if (!s.Defined) Core.Log.Warning($"Looks: {key} surface \"{name}\" isn't in {Path.GetFileName(mtlPath)}; it'll be grey");
                table[s.Row] = s.Holo ? HoloRow(brick, template, s) : Row(template, s);
                rows.Add(table[s.Row]);
                made.Add(s);
            }
            VoxelMaterialResources.MaterialCount = start + made.Count;
            diffuseAdded += made.Count;
            specularAdded += made.Count;
            Surfaces.AddRange(made);
            AccessTools.PropertySetter(typeof(Brick), nameof(Brick.VoxelMaterials)).Invoke(brick, new object[] { rows.ToArray() });
        }

        static VoxelMaterial Row(VoxelMaterial template, Surface s)
        {
            var m = template;
            m.Id = s.Row;
            m.AlphaMode = s.Glow > 0 ? AlphaMode.glowDiffuse : AlphaMode.noAlpha;
            m.paintMode = s.Paint ? PaintMode.on : PaintMode.noPaint;
            m.TextureID = s.Diffuse;
            m.specularID = s.Specular;
            m.normalID = -1;
            m.BorderMode = BorderMode.noBorder;
            m.BorderId = m.borderNormalID = m.borderSpecularID = -1;
            m.dynDataVert = DynData_Vert.none;
            m.dynDataPixel = DynData_Pixel.none;
            m.fuelOverlay = FuelOverlay.off;
            m.uvProj = UV_Proj.off;
            m.Tiling = 1;
            return m;
        }

        // A hologram symbol like the stock gates': the stock block's own holo row (or the hull's, set to holo) pointed
        // at our picture. The picture is white line-art on black: black is see-through, white glows, tinted by the
        // block's paint colour. Holo rows have no specular slice.
        static VoxelMaterial HoloRow(Brick brick, VoxelMaterial hull, Surface s)
        {
            var stock = brick.VoxelMaterials.Where(m => m.AlphaMode == AlphaMode.holo).ToList();
            var m = stock.Count > 0 ? stock[0] : hull;
            m.Id = s.Row;
            m.AlphaMode = AlphaMode.holo;
            m.paintMode = PaintMode.on;
            m.TextureID = s.Diffuse;
            m.specularID = m.normalID = -1;
            return m;
        }

        static Color32 DiffusePixel(Surface s)
        {
            var c = s.Paint ? Color.white * s.Colour.grayscale : s.Colour;
            if (Gamma) c = new Color(Mathf.LinearToGammaSpace(c.r), Mathf.LinearToGammaSpace(c.g), Mathf.LinearToGammaSpace(c.b));
            c.a = 1;
            return c;
        }

        static Color32 SpecularPixel(Surface s) =>
            new Color32(Shine.r, Shine.g, (byte)(s.Paint ? 255 : 0), (byte)(s.Glow > 0 ? Mathf.RoundToInt(s.Glow * 255) : 255));

        // After every brick has loaded: copy the arrays into bigger ones with our slices on the end, and point the shaders
        // (and the decal code, which reads VOXEL_DIFFUSE on the CPU) at them. Every existing layer and all its mips are
        // kept where they are (Keel's legacy-extension rules, docs/KEEL-MATERIAL-COMPATIBILITY.md in Starship-Keel), and
        // our rows are pointed at the layers the slices actually got: a mod that appends layers after our rows were made
        // (Keel attachment palettes, Tractor Beam) moves them, which once showed the Lua Computer someone else's texture.
        // Test for the layer fix (StarshipEVO-EvoCore issue #1): with UserData\EvoCore\test-extra-layers present, a
        // stand-in for another mod appends 3 magenta layers to both arrays after EvoCore's rows are made and before its
        // slices go in (Harmony runs this postfix before AddSlices'). CheckLayers() then reads what the rows point at.
        static readonly Color32 TestMagenta = new Color32(255, 0, 255, 255);
        static int testLayersAdded;

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class TestExtraLayers
        {
            [HarmonyPriority(Priority.First)]
            static void Postfix()
            {
                try
                {
                    if (!File.Exists(Path.Combine(Paths.UserData, "EvoCore", "test-extra-layers"))) return;
                    var add = Enumerable.Range(0, 3).Select(_ => (Action<Texture2DArray, int>)((a, e) => Fill(a, e, TestMagenta))).ToList();
                    var diffuse = Grow(ResourcesLoader.VOXEL_DIFFUSE, add);
                    var specular = Grow(Shader.GetGlobalTexture("SPECULAR_TEX") as Texture2DArray, add);
                    ResourcesLoader.VOXEL_DIFFUSE = diffuse;
                    Shader.SetGlobalTexture("MAIN_TEX", diffuse);
                    Shader.SetGlobalTexture("SPECULAR_TEX", specular);
                    testLayersAdded = 3;
                    Core.Log.Warning("Looks: TEST added 3 magenta layers before EvoCore's slices (test-extra-layers is present)");
                }
                catch (Exception e) { Core.Log.Error("Looks: test layers failed: " + e); }
            }
        }

        // For DevBridge: each surface's row, the layer it points at, and whether that layer is the stand-in's magenta.
        public static string CheckLayers()
        {
            if (Diffuse == null) return "no surfaces";
            var table = VoxelMaterialResources.materialParamDatas;
            return $"test layers added: {testLayersAdded}; diffuse array {Diffuse.depth}\n" + string.Join("\n", Surfaces.Select(s =>
            {
                var row = table[s.Row];
                var brickRow = s.Brick?.VoxelMaterials?.FirstOrDefault(m => m.Id == s.Row);
                var mip = Diffuse.mipmapCount - 1;
                var pixel = Diffuse.GetPixels32(row.TextureID, mip)[0];
                var before = row.TextureID > 0 ? Diffuse.GetPixels32(row.TextureID - 1, mip)[0] : default;
                bool Magenta(Color32 c) => c.r > 240 && c.g < 16 && c.b > 240;
                return $"{s.Key}/{s.Name}: row {s.Row} -> layer {row.TextureID} (block's copy {brickRow?.TextureID}), " +
                       $"that layer is {(Magenta(pixel) ? "MAGENTA (wrong)" : "ours")}, the one before is {(Magenta(before) ? "magenta" : "not magenta")}";
            }).ToArray());
        }

        [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
        static class AddSlices
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix()
            {
                if (Surfaces.Count == 0) return;
                try
                {
                    var stockSpecular = Shader.GetGlobalTexture("SPECULAR_TEX") as Texture2DArray;
                    var hullSpec = TabHull.HULL_A?.VoxelMaterials?[0].specularID ?? -1;
                    if (stockSpecular != null && hullSpec >= 0 && hullSpec < stockSpecular.depth)
                    {
                        var p = stockSpecular.GetPixels32(hullSpec, stockSpecular.mipmapCount - 1)[0];
                        Shine = new Color32(p.r, p.g, 0, 0);
                    }
                    Diffuse = Grow(ResourcesLoader.VOXEL_DIFFUSE, Surfaces.Select(s => (Action<Texture2DArray, int>)((a, e) => FillDiffuse(a, e, s))).ToList());
                    Specular = Grow(stockSpecular, Surfaces.Select(s => (Action<Texture2DArray, int>)((a, e) => Fill(a, e, SpecularPixel(s)))).ToList());
                    ResourcesLoader.VOXEL_DIFFUSE = Diffuse;
                    Shader.SetGlobalTexture("MAIN_TEX", Diffuse);
                    Shader.SetGlobalTexture("SPECULAR_TEX", Specular);
                    var diffuseBase = Diffuse.depth - Surfaces.Count;
                    var specularBase = Specular.depth - Surfaces.Count;
                    var moved = 0;
                    for (int k = 0; k < Surfaces.Count; k++)
                    {
                        var s = Surfaces[k];
                        if (s.Diffuse != diffuseBase + k || s.Specular != specularBase + k) moved++;
                        s.Diffuse = diffuseBase + k;
                        s.Specular = specularBase + k;
                        Point(s);
                    }
                    if (moved > 0)
                    {
                        Core.Log.Msg($"Looks: {moved} surface(s) moved to the layers they really got (another mod added layers first)");
                        // Already on the GPU? Send the corrected table again.
                        if (AccessTools.Field(typeof(VoxelMaterialResources), "materialParamsBuffer").GetValue(null) != null)
                            AccessTools.Method(typeof(VoxelMaterialResources), "LoadBuffers").Invoke(null, null);
                    }
                    Core.Log.Msg($"Looks: {Surfaces.Count} new surface(s); texture arrays now {Diffuse.depth} diffuse, {Specular.depth} specular");
                }
                catch (Exception e)
                {
                    // The rows point past the stock arrays now, so those surfaces may draw wrongly; the blocks still work.
                    Note($"surfaces: {e.Message}");
                    Core.Log.Error("Looks: couldn't add texture slices: " + e);
                }
            }
        }

        // Points a surface's material row (in the game's table and in its block's own list) at its layers.
        static void Point(Surface s)
        {
            void Set(ref VoxelMaterial m)
            {
                m.TextureID = s.Diffuse;
                if (!s.Holo) m.specularID = s.Specular;
            }
            var table = VoxelMaterialResources.materialParamDatas;
            if (s.Row >= 0 && s.Row < table.Length) { var m = table[s.Row]; Set(ref m); table[s.Row] = m; }
            var rows = s.Brick?.VoxelMaterials;
            if (rows == null) return;
            for (int i = 0; i < rows.Length; i++)
                if (rows[i].Id == s.Row) { var m = rows[i]; Set(ref m); rows[i] = m; }
        }

        static Texture2DArray Grow(Texture2DArray stock, List<Action<Texture2DArray, int>> add)
        {
            if (stock == null) throw new InvalidDataException("a texture array is missing");
            if (stock.graphicsFormat != UnityEngine.Experimental.Rendering.GraphicsFormat.RGBA_DXT5_UNorm)
                throw new InvalidDataException($"{stock.name} is {stock.graphicsFormat}, expected DXT5");
            var grown = new Texture2DArray(stock.width, stock.height, stock.depth + add.Count, stock.graphicsFormat,
                UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain, stock.mipmapCount)
            {
                name = stock.name + " (EvoCore)",
                wrapMode = stock.wrapMode,
                filterMode = stock.filterMode,
                anisoLevel = stock.anisoLevel,
                mipMapBias = stock.mipMapBias,
            };
            for (int e = 0; e < stock.depth; e++)
                for (int m = 0; m < stock.mipmapCount; m++)
                    grown.SetPixelData(stock.GetPixelData<byte>(m, e), m, e);
            for (int k = 0; k < add.Count; k++) add[k](grown, stock.depth + k);
            grown.Apply(false, false); // stays readable: decals read the diffuse array on the CPU
            return grown;
        }

        // A solid-colour DXT5 slice, written block by block: every 4x4 block is the same 16 bytes (alpha block with
        // both end points the alpha and all indices 0, colour block with both end points the RGB565 colour).
        static void Fill(Texture2DArray array, int element, Color32 c)
        {
            ushort rgb = (ushort)(((c.r * 31 + 127) / 255) << 11 | ((c.g * 63 + 127) / 255) << 5 | ((c.b * 31 + 127) / 255));
            var block = new byte[] { c.a, c.a, 0, 0, 0, 0, 0, 0, (byte)rgb, (byte)(rgb >> 8), (byte)rgb, (byte)(rgb >> 8), 0, 0, 0, 0 };
            for (int m = 0; m < array.mipmapCount; m++)
            {
                var side = Mathf.Max(1, (array.width >> m) / 4);
                var data = new byte[side * side * 16];
                for (int b = 0; b < data.Length; b += 16) Buffer.BlockCopy(block, 0, data, b, 16);
                array.SetPixelData(data, m, element);
            }
        }

        // A surface's diffuse slice: its picture if it has one that loads, else its solid colour.
        static void FillDiffuse(Texture2DArray array, int element, Surface s)
        {
            if (s.Texture != null)
            {
                try { FillPicture(array, element, s); return; }
                catch (Exception e)
                {
                    Note($"{s.Key}/{s.Name}: {Path.GetFileName(s.Texture)}: {e.Message}, solid colour");
                    Core.Log.Warning($"Looks: {s.Key} surface \"{s.Name}\": {e.Message}; using its solid colour");
                }
            }
            Fill(array, element, DiffusePixel(s));
        }

        // Loads the PNG, scales it to the slice size, makes mips, compresses to DXT5 like the game's textures and
        // copies each mip in.
        static void FillPicture(Texture2DArray array, int element, Surface s)
        {
            if (!File.Exists(s.Texture)) throw new FileNotFoundException("not found");
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var slice = new Texture2D(array.width, array.height, TextureFormat.RGBA32, array.mipmapCount, false);
            try
            {
                if (!source.LoadImage(File.ReadAllBytes(s.Texture))) throw new InvalidDataException("not a PNG or JPG Unity can read");
                var pixels = new Color32[array.width * array.height];
                for (int y = 0; y < array.height; y++)
                    for (int x = 0; x < array.width; x++)
                    {
                        Color c = source.width == array.width && source.height == array.height
                            ? source.GetPixel(x, y)
                            : source.GetPixelBilinear((x + 0.5f) / array.width, (y + 0.5f) / array.height);
                        if (s.Paint) c = Color.white * c.grayscale;
                        c.a = s.Holo ? 0 : 1; // the stock holo pictures have alpha 0 throughout
                        pixels[y * array.width + x] = c;
                    }
                slice.SetPixels32(pixels);
                slice.Apply(true, false);
                slice.Compress(true);
                if (slice.format != TextureFormat.DXT5) throw new InvalidDataException($"compressed to {slice.format}, not DXT5");
                for (int m = 0; m < array.mipmapCount; m++) array.SetPixelData(slice.GetPixelData<byte>(m), m, element);
            }
            finally
            {
                UnityEngine.Object.Destroy(source);
                UnityEngine.Object.Destroy(slice);
            }
        }

        // DevBridge: `get EvoCore.Looks.Reload()` re-reads every look's .mtl and pictures and updates colours, pictures
        // and glow in place. (The mesh and the list of surfaces only change on a restart.)
        public static string Reload()
        {
            if (Diffuse == null || Specular == null) return "no new surfaces loaded";
            var cache = new Dictionary<string, Dictionary<string, MtlEntry>>();
            foreach (var s in Surfaces)
            {
                if (!cache.TryGetValue(s.Mtl, out var mtl)) cache[s.Mtl] = mtl = ReadMtl(s.Mtl);
                Define(s, mtl);
                FillDiffuse(Diffuse, s.Diffuse, s);
                Fill(Specular, s.Specular, SpecularPixel(s));
                var row = VoxelMaterialResources.materialParamDatas[s.Row];
                if (!s.Holo)
                {
                    row.AlphaMode = s.Glow > 0 ? AlphaMode.glowDiffuse : AlphaMode.noAlpha;
                    row.paintMode = s.Paint ? PaintMode.on : PaintMode.noPaint;
                }
                VoxelMaterialResources.materialParamDatas[s.Row] = row;
            }
            Diffuse.Apply(false, false);
            Specular.Apply(false, false);
            AccessTools.Method(typeof(VoxelMaterialResources), "LoadBuffers").Invoke(null, null);
            return string.Join("; ", Surfaces.Select(Describe));
        }

        static string Describe(Surface s) =>
            $"{s.Key}/{s.Name}: row {s.Row}, " + (s.Texture != null ? "picture " + Path.GetFileName(s.Texture) : $"colour {ColorUtility.ToHtmlStringRGB(s.Colour)}") +
            (s.Glow > 0 ? $", glow {s.Glow:0.##}" : "") +
            (s.Paint ? ", paintable" : "") + (s.Holo ? ", hologram" : "") + (s.Defined ? "" : " (not in the .mtl)");

        // DevBridge: a brick's material rows (alpha mode, paint, slices, dynamic data), to see how stock looks are set up.
        public static string Rows(int brickId)
        {
            var brick = brickId >= 0 && brickId < Brick.BRICK_INDEX.Length ? Brick.BRICK_INDEX[brickId] : null;
            if (brick?.VoxelMaterials == null) return $"brick {brickId} has no materials";
            return string.Join("\n", brick.VoxelMaterials.Select((m, i) =>
                $"slot{i}: row {m.Id}, {m.AlphaMode}, paint {m.paintMode}, blend {m.blendMode}, diffuse {m.TextureID}, specular {m.specularID}, " +
                $"normal {m.normalID}, vert {m.dynDataVert}, pixel {m.dynDataPixel}, tiling {m.Tiling}, uvProj {m.uvProj}").ToArray());
        }

        // DevBridge: writes one slice of the diffuse ("main") or specular array to Looks\export as a PNG, colour and
        // alpha separately (<name>_rgb.png, <name>_a.png).
        public static string DumpSlice(string array, int element)
        {
            var source = array == "specular" ? Shader.GetGlobalTexture("SPECULAR_TEX") as Texture2DArray : ResourcesLoader.VOXEL_DIFFUSE;
            if (source == null || element < 0 || element >= source.depth) return "no such slice";
            var pixels = source.GetPixels32(element, 0);
            var rgb = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            var a = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            rgb.SetPixels32(pixels.Select(p => new Color32(p.r, p.g, p.b, 255)).ToArray());
            a.SetPixels32(pixels.Select(p => new Color32(p.a, p.a, p.a, 255)).ToArray());
            var dir = Path.Combine(Folder, "export");
            Directory.CreateDirectory(dir);
            var name = Path.Combine(dir, $"{array}_{element}");
            File.WriteAllBytes(name + "_rgb.png", rgb.EncodeToPNG());
            File.WriteAllBytes(name + "_a.png", a.EncodeToPNG());
            UnityEngine.Object.Destroy(rgb); UnityEngine.Object.Destroy(a);
            return name + "_rgb.png / _a.png";
        }

        static string Show(Bounds b) => $"{b.min.x:0.###},{b.min.y:0.###},{b.min.z:0.###} to {b.max.x:0.###},{b.max.y:0.###},{b.max.z:0.###}";

        // Writes a brick's mesh as an OBJ to Looks\export, with its material slots as `usemtl slotN` and the block's
        // bounds in the header, as a starting point for a new look.
        public static string Export(int brickId)
        {
            var brick = brickId >= 0 && brickId < Brick.BRICK_INDEX.Length ? Brick.BRICK_INDEX[brickId] : null;
            var mesh = brick?.VoxelMesh?.Mesh;
            if (mesh == null) return $"brick {brickId} has no mesh";
            var text = ObjText(mesh, brick, brickId, out var info);
            var dir = Path.Combine(Folder, "export");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{brickId}_{Regex.Replace(brick.name ?? "brick", @"[^\w\-]+", "_")}.obj");
            File.WriteAllText(file, text);
            return $"{file}: {info}";
        }

        // A mesh as OBJ text: x mirrored into OBJ's handedness, one `usemtl slotN` group per material slot.
        internal static string ObjText(Mesh mesh, Brick brick, int brickId, out string info)
        {
            var v = mesh.vertices; var n = mesh.normals; var uv = mesh.uv; var tris = mesh.triangles;
            var sb = new StringBuilder();
            sb.AppendLine($"# Starship EVO brick {brickId} \"{brick.name}\", mesh {brick.nameMesh}, written by EvoCore.Looks");
            sb.AppendLine($"# block bounds {Show(mesh.bounds)} (Unity, x mirrored below); keep the look inside them");
            var materials = brick.VoxelMaterials ?? new VoxelMaterial[0];
            for (int i = 0; i < materials.Length; i++)
                sb.AppendLine($"# slot{i}: material {materials[i].Id}, {materials[i].AlphaMode}, paint {materials[i].paintMode}");
            string f(float x) => x.ToString("0.######", CultureInfo.InvariantCulture);
            foreach (var p in v) sb.AppendLine($"v {f(-p.x)} {f(p.y)} {f(p.z)}");
            foreach (var p in n) sb.AppendLine($"vn {f(-p.x)} {f(p.y)} {f(p.z)}");
            // One vt per triangle corner, with the slot taken out, since a vertex may sit on two slots' edge.
            var bySlot = new SortedDictionary<int, List<int>>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                var s = Mathf.FloorToInt((uv[tris[t]].x + uv[tris[t + 1]].x + uv[tris[t + 2]].x) / 3f);
                if (!bySlot.TryGetValue(s, out var list)) bySlot[s] = list = new List<int>();
                list.Add(t);
            }
            int vt = 0;
            foreach (var kv in bySlot)
            {
                sb.AppendLine($"usemtl slot{kv.Key}");
                foreach (var t in kv.Value)
                {
                    var corner = new int[3];
                    for (int c = 0; c < 3; c++)
                    {
                        var p = uv[tris[t + c]];
                        sb.AppendLine($"vt {f(p.x - kv.Key)} {f(p.y)}");
                        corner[c] = ++vt;
                    }
                    // Mirrored x, so the winding is reversed back.
                    int a = tris[t] + 1, b = tris[t + 1] + 1, c2 = tris[t + 2] + 1;
                    sb.AppendLine($"f {a}/{corner[0]}/{a} {c2}/{corner[2]}/{c2} {b}/{corner[1]}/{b}");
                }
            }
            info = $"{tris.Length / 3} triangles, {v.Length} vertices, slots {string.Join(",", bySlot.Keys)}, bounds {Show(mesh.bounds)}";
            return sb.ToString();
        }
    }
}
