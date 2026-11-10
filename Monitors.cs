using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Rendering;
using Manager;
using VoxelEngine;

namespace EvoCore
{
    // Monitors linked from a Lua Computer become its displays: the script draws on them (display / displays, see
    // DisplayApi) and the picture replaces the monitor's own status icon. The game's monitors (flat, wedged, large)
    // are a body, a glowing sloped rim and a flat picture square on top; the picture goes on that square, found in the
    // block's own mesh (its holo slot), so it follows resizing and turning.
    // The script draws on a canvas Height units tall (width by the screen's shape), in drawing order, y down. The
    // picture is drawn into a texture only when the drawing changed: shapes and text first (Draw), then the "CRT"
    // pass (Shown) bends the picture slightly at the edges and adds scanlines and a darker rim, and the glass in the
    // world bulges a little and glows.
    public static class Monitors
    {
        public static float Height = 240f;        // canvas units, top to bottom
        public static int Supersample = 3;         // texture pixels per canvas unit
        public static bool Crt = false;            // off by default: the game's own holo screens and helmet view already have their look
        public static float Glow = 0.85f;          // brightness of the glass (above 1 feeds the game's bloom)
        public static float Bulge = 0.03f;         // how far the glass domes out, as a share of its shorter side
        public static bool FillFace = true;       // the picture covers the whole lit face (square and rim), not just the square
        public static float Lift = 0.0005f;        // how far the glass sits off the monitor's face: enough not to flicker, little enough not to show an edge
        public static float Backdrop = 1f;         // how solid the glass's own background is (0 clear, 1 solid; below 1 the monitor's icon shows through)
        public static float BackdropLift = 0f;     // the background: the monitor's paint, this far towards white

        public class Display
        {
            public Command Block;                  // a monitor, or a screen another mod shows the canvas for
            public bool External;                  // drawn by that mod (SurfaceAspect): no glass, see-through, no CRT
            public float Aspect;
            public LuaComputer Owner;
            public readonly List<Cmd> Cmds = new List<Cmd>();
            public bool Dirty = true, Crt = true;
            public float Glow = -1f;               // this display's brightness; below 0 means Monitors.Glow
            public float Width, Height;            // canvas units
            public RenderTexture Draw, Shown;
            public GameObject Go;
            public Mesh Glass;
            public Material Material, Picture, Sides;   // Sides: the face's tapered edges, plain background
            public long Shape;
            public float Born;
            public readonly List<Mesh> Pool = new List<Mesh>();
            public Vector3 C00, C10, C01, Normal;  // the picture square in its ship's space (canvas left/bottom corner, right, top)
            public readonly List<(string name, Rect rect)> Buttons = new List<(string, Rect)>();
            public string Hovered;                 // the button under the player's cursor, drawn lit
            public Color Tint = Color.white;       // the monitor's paint
            public bool Domed;                     // the glass is the domed square, not the monitor's own face
            public float Backdrop = -1f;           // this display's background opacity; below 0 means Monitors.Backdrop
        }

        public struct Cmd
        {
            public int Kind;                       // 0 solid quad list, 1 text
            public Vector2[] Points;               // solid: 4 corners per quad
            public Color Color;
            public string Text;
            public float X, Y, Size;
            public int Align;                      // -1 left, 0 centre, 1 right
            public Font Font;
        }

        static readonly Dictionary<Command, Display> displays = new Dictionary<Command, Display>();
        static Material solid, text, glassBase, scanMat, vignetteMat;
        static Texture2D scan, vignette;
        static readonly Dictionary<Font, Material> fontMats = new Dictionary<Font, Material>();
        static bool hooked;
        static float nextPrune;

        internal static void Init()
        {
            Lua.Expose("display", c =>
            {
                if (!(c.Host is LuaComputer comp)) return null;
                // A stopped or restarted script's drawings go; its monitors are plain until it draws again.
                c.Stopped += () => Release(comp);
                return new DisplayApi(comp, 1);
            });
            Lua.Expose("displays", c => c.Host is LuaComputer comp ? new DisplaysApi(comp) : null);
            Font.textureRebuilt += _ => { foreach (var d in displays.Values) d.Dirty = true; };
        }

        // ---- Which monitors are whose ----

        // The i-th monitor (from 1) among a computer's outputs, made a display on first use.
        internal static Display Get(LuaComputer computer, int index) => Get(computer, DynValue.NewNumber(index));

        // A display by number or display port name (Ports.cs), made a display on first use.
        internal static Display Get(LuaComputer computer, DynValue which)
        {
            var block = Lua.DisplayBlock(computer, which, Linked(computer));
            if (block == null) return null;
            var external = !(block is CommandDisplay);
            if (displays.TryGetValue(block, out var d) && d.Owner == computer && d.External == external) return d;
            if (d != null) Drop(block, d);
            d = new Display { Block = block, Owner = computer, Born = Time.time, Crt = Crt && !external, External = external };
            displays[block] = d;
            return d;
        }

        internal static int Count(LuaComputer computer)
        {
            var names = Lua.DeclaredOf(computer)?.Displays;
            return names != null && names.Count > 0 ? names.Count : Linked(computer).Count;
        }

        // Whether a block can be a Lua display: a monitor, or a block another mod shows a canvas on.
        internal static bool IsDisplay(ICommand c) => c is CommandDisplay || c is Command command && Aspect(command) > 0f;

        // A computer's displays in link order: monitors, and blocks another mod shows a canvas on (SurfaceAspect). A
        // mainframe's go on with its terminals' displays (Mainframe.cs).
        static List<Command> Linked(LuaComputer computer)
        {
            var list = computer.Outputs.OfType<Command>().Where(c => c is CommandDisplay || Aspect(c) > 0f).ToList();
            if (computer is Mainframe m)
                foreach (var terminal in Mainframes.Terminals(m))
                    foreach (var c in terminal.Outputs.OfType<Command>())
                        if ((c is CommandDisplay || Aspect(c) > 0f) && !list.Contains(c)) list.Add(c);
            return list;
        }

        // ---- For other mods: a canvas they show themselves ----

        // Set by a mod that can show a canvas on a block (Cockpit Displays: a screen's decal surfaces): the canvas's
        // width over height for that block, or 0 when it can't. Linking a Lua Computer to such a block makes it a
        // display the script draws on; the mod reads the picture with CanvasTexture and puts it on its surfaces.
        public static Func<Command, float> SurfaceAspect;

        static float Aspect(Command c)
        {
            try { return SurfaceAspect != null ? SurfaceAspect(c) : 0f; }
            catch { return 0f; }
        }

        // The block's current canvas picture (see-through where nothing is drawn), or null when no script draws on it.
        public static Texture CanvasTexture(Command block) =>
            displays.TryGetValue(block, out var d) && d.External && d.Draw != null && d.Cmds.Count > 0 ? d.Draw : null;

        // A display goes back to being a plain monitor when its computer stops, is unlinked, or either block is gone.
        static void Prune()
        {
            foreach (var pair in displays.ToList())
            {
                var d = pair.Value;
                bool keep;
                try { keep = d.Block.brickCoord.entity != null && Lua.Runs(d.Owner) && Linked(d.Owner).Contains(d.Block) && (!d.External || Aspect(d.Block) > 0f); }
                catch { keep = false; }
                if (!keep) Drop(pair.Key, d);
            }
        }

        static void Release(LuaComputer computer)
        {
            foreach (var pair in displays.Where(p => p.Value.Owner == computer).ToList()) Drop(pair.Key, pair.Value);
        }

        static void Drop(Command block, Display d)
        {
            displays.Remove(block);
            if (d.Go != null) UnityEngine.Object.Destroy(d.Go);
            if (d.Glass != null) UnityEngine.Object.Destroy(d.Glass);
            if (d.Material != null) UnityEngine.Object.Destroy(d.Material);
            if (d.Sides != null) UnityEngine.Object.Destroy(d.Sides);
            if (d.Picture != null) UnityEngine.Object.Destroy(d.Picture);
            if (d.Draw != null) d.Draw.Release();
            if (d.Shown != null) d.Shown.Release();
            foreach (var m in d.Pool) UnityEngine.Object.Destroy(m);
        }

        // ---- Every frame ----

        internal static void LateTick()
        {
            if (displays.Count == 0) return;
            if (!hooked) { Camera.onPreCull += Place; hooked = true; }
            if (Time.unscaledTime >= nextPrune) { nextPrune = Time.unscaledTime + 0.5f; Prune(); }
            foreach (var d in displays.Values)
            {
                try
                {
                    Build(d);
                    if (d.External)
                    {
                        if (d.Dirty && d.Draw != null) { Render(d); d.Dirty = false; }
                        continue;
                    }
                    if (d.Go == null) continue;
                    var boot = Mathf.Clamp01((Time.time - d.Born) / 0.5f);
                    // Power-on: a short flicker up to full brightness.
                    var flicker = boot < 1f ? boot * (0.75f + 0.25f * Mathf.Sin(Time.time * 90f)) : 1f;
                    var glow = (d.Glow >= 0f ? d.Glow : Glow) * flicker;
                    d.Material.color = new Color(glow, glow, glow, 1f);
                    var side = Color.Lerp(d.Tint, Color.white, BackdropLift) * glow;
                    side.a = 1f;
                    d.Sides.color = side;
                    if (d.Dirty) { Render(d); d.Dirty = false; }
                }
                catch (Exception e) { Core.Log.Error("Lua display: " + e); Drop(d.Block, d); break; }
            }
        }

        // The glass follows its ship; placed just before the player's camera culls, after everything has moved.
        static void Place(Camera camera)
        {
            var player = Dependency.RenderPipeline != null ? Dependency.RenderPipeline.playerCamera : null;
            if (camera != player) return;
            foreach (var d in displays.Values)
            {
                if (d.Go == null) continue;
                try
                {
                    var m = d.Block.brickCoord.TransformData.localToWorldMatrix;
                    d.Go.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                    d.Go.transform.localScale = m.lossyScale;
                }
                catch { }
            }
            PlaceCursor();
        }

        // ---- Touch: where a ray meets a display's picture (Touch.cs) ----

        // The nearest monitor picture the ray meets within reach, among those wanted, and where on its canvas (units,
        // y down). The glass's slight dome and the CRT bend are ignored: a few canvas units off at the very corners.
        internal static bool Hit(Ray ray, float reach, Func<Display, bool> wanted, out Display hit, out Vector2 at)
        {
            hit = null; at = default;
            var best = reach;
            foreach (var d in displays.Values)
            {
                if (d.External || d.Go == null || !wanted(d)) continue;
                Matrix4x4 m;
                try { m = d.Block.brickCoord.TransformData.localToWorldMatrix; } catch { continue; }
                var inv = m.inverse;
                var o = inv.MultiplyPoint3x4(ray.origin);
                var dir = inv.MultiplyVector(ray.direction);
                var facing = Vector3.Dot(dir, d.Normal);
                if (facing >= -1e-6f) continue;                       // from behind or edge on
                var t = Vector3.Dot(d.C00 - o, d.Normal) / facing;
                if (t < 0f) continue;
                var p = o + dir * t;
                var dist = (m.MultiplyPoint3x4(p) - ray.origin).magnitude;
                if (dist > best) continue;
                // p - C00 = a * U + b * V, solved in the plane.
                Vector3 u = d.C10 - d.C00, v = d.C01 - d.C00, w = p - d.C00;
                float uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v), wu = Vector3.Dot(w, u), wv = Vector3.Dot(w, v);
                var det = uu * vv - uv * uv;
                if (Mathf.Abs(det) < 1e-12f) continue;
                float a = (vv * wu - uv * wv) / det, b = (uu * wv - uv * wu) / det;
                if (a < 0f || a > 1f || b < 0f || b > 1f) continue;
                best = dist; hit = d;
                at = new Vector2(a * d.Width, (1f - b) * d.Height);
            }
            return hit != null;
        }

        // The cursor: a small bright dot on the glass where the player aims, or hidden.
        static GameObject cursor;
        static Display cursorOn;
        static Vector3 cursorAt;

        internal static void ShowCursor(Display d, Vector2 at)
        {
            if (d == null || d.Go == null) { if (cursor != null) cursor.SetActive(false); cursorOn = null; return; }
            if (cursor == null)
            {
                Materials();
                var mesh = new Mesh { name = "Lua display cursor" };
                var pts = new List<Vector3> { Vector3.zero };
                for (int i = 0; i <= 16; i++) pts.Add(new Vector3(Mathf.Cos(i * Mathf.PI / 8f), Mathf.Sin(i * Mathf.PI / 8f), 0f));
                var tris = new List<int>();
                for (int i = 1; i <= 16; i++) tris.AddRange(new[] { 0, i + 1, i, 0, i, i + 1 });   // both faces
                mesh.SetVertices(pts);
                mesh.SetColors(pts.Select(_ => Color.white).ToList());
                mesh.SetTriangles(tris, 0);
                cursor = new GameObject("Lua display cursor");
                cursor.AddComponent<MeshFilter>().sharedMesh = mesh;
                cursor.AddComponent<MeshRenderer>().sharedMaterial = new Material(glassBase) { name = "Lua display cursor", color = new Color(1.6f, 1.6f, 1.6f, 0.9f), renderQueue = 3001 };
            }
            float a = at.x / Mathf.Max(1f, d.Width), b = 1f - at.y / Mathf.Max(1f, d.Height);
            var dome = d.Domed ? Bulge * Mathf.Min((d.C10 - d.C00).magnitude, (d.C01 - d.C00).magnitude) : 0f;
            float ex = 2f * a - 1f, ey = 2f * b - 1f;
            // On the domed glass, a hair above it.
            cursorAt = d.C00 + (d.C10 - d.C00) * a + (d.C01 - d.C00) * b + d.Normal * (0.006f + dome * (1f - ex * ex) * (1f - ey * ey));
            cursorOn = d;
            cursor.SetActive(true);
        }

        static void PlaceCursor()
        {
            if (cursor == null || cursorOn == null || !cursor.activeSelf) return;
            try
            {
                var m = cursorOn.Block.brickCoord.TransformData.localToWorldMatrix;
                var size = 0.012f * Mathf.Min((cursorOn.C10 - cursorOn.C00).magnitude, (cursorOn.C01 - cursorOn.C00).magnitude) / 0.5f;
                cursor.transform.SetPositionAndRotation(m.MultiplyPoint3x4(cursorAt), Quaternion.LookRotation(m.MultiplyVector(cursorOn.Normal), m.MultiplyVector(cursorOn.C01 - cursorOn.C00)));
                cursor.transform.localScale = Vector3.one * Mathf.Clamp(size * m.lossyScale.x, 0.004f, 0.03f);
            }
            catch { cursor.SetActive(false); }
        }

        // Which display number (or display port name) a block is for its computer, as scripts name it.
        internal static DynValue NameOf(Display d)
        {
            var names = Lua.DeclaredOf(d.Owner)?.Displays;
            var linked = Linked(d.Owner);
            if (names != null)
                foreach (var n in names)
                    if (Lua.DisplayBlock(d.Owner, DynValue.NewString(n), linked) == d.Block) return DynValue.NewString(n);
            var i = linked.IndexOf(d.Block);
            return DynValue.NewNumber(i >= 0 ? i + 1 : 1);
        }

        internal static Display Of(Command block) => block != null && displays.TryGetValue(block, out var d) ? d : null;

        // ---- The glass: the monitor's picture square, in its ship's space ----

        static int ScreenSlot(Brick brick)
        {
            var materials = brick.VoxelMaterials;
            for (int i = materials.Length - 1; i >= 0; i--)
                if (materials[i].AlphaMode == AlphaMode.holo || materials[i].AlphaMode == AlphaMode.holoSqrd) return i;
            return materials.Length - 1;
        }

        static void Build(Display d)
        {
            if (d.External) { BuildExternal(d); return; }
            var b = d.Block.brickCoord.brickData;
            long shape = b.brickId * 7919L + b.gridPosition.GetHashCode() * 31L + b.rotation * 131L + b.scale.GetHashCode();
            if (d.Go != null && shape == d.Shape) return;
            d.Shape = shape;
            var mesh = b.Brick.VoxelMesh;
            var buffer = mesh.meshBuffer;
            var slot = ScreenSlot(b.Brick);
            var points = new List<Vector3>();
            var uvs = new List<Vector2>();
            for (int t = 0; t + 2 < buffer.triangles.Count; t += 3)
            {
                var i0 = buffer.triangles[t]; var i1 = buffer.triangles[t + 1]; var i2 = buffer.triangles[t + 2];
                if (Mathf.FloorToInt((buffer.uv[i0].x + buffer.uv[i1].x + buffer.uv[i2].x) / 3f) != slot) continue;
                foreach (var i in new[] { i0, i1, i2 })
                {
                    points.Add(b.Position + b.Quaternion * (b.GridSize * mesh.ResizePoint(b.Scale, buffer.vertices[i])));
                    uvs.Add(buffer.uv[i] - new Vector2(slot, 0f));
                }
            }
            if (points.Count < 3) throw new Exception($"monitor {b.Brick.name} has no picture square");
            // The square's corners, by its own texture coordinates (they cover one quarter of the icon sheet).
            float u0 = uvs.Min(p => p.x), u1 = uvs.Max(p => p.x), v0 = uvs.Min(p => p.y), v1 = uvs.Max(p => p.y);
            Vector3 Corner(float u, float v)
            {
                int best = 0; float bestD = float.MaxValue;
                for (int i = 0; i < uvs.Count; i++)
                {
                    var dd = (uvs[i] - new Vector2(u, v)).sqrMagnitude;
                    if (dd < bestD) { bestD = dd; best = i; }
                }
                return points[best];
            }
            Vector3 c00 = Corner(u0, v0), c10 = Corner(u1, v0), c01 = Corner(u0, v1), c11 = Corner(u1, v1);
            var normal = Vector3.Cross(c10 - c00, c01 - c00).normalized;
            // Facing out of the block: away from its centre.
            if (Vector3.Dot(normal, c00 - b.Position) < 0f) normal = -normal;
            var face = FillFace ? Grow(b, mesh, normal, ref c00, ref c10, ref c01, ref c11) : null;
            float w = (c10 - c00).magnitude, h = (c01 - c00).magnitude;
            d.C00 = c00; d.C10 = c10; d.C01 = c01; d.Normal = normal;
            d.Tint = b.color;
            d.Height = Height;
            d.Width = Mathf.Round(Height * w / Mathf.Max(1e-4f, h));

            if (d.Glass == null) d.Glass = new Mesh { name = "Lua display glass" };
            d.Glass.Clear();
            if (face != null && face.Count >= 3) FaceGlass(d, face);
            else SquareGlass(d, c00, c10, c01, c11, normal, w, h);

            int pw = Mathf.Clamp((int)(d.Width * Supersample), 16, 2048), ph = Mathf.Clamp((int)(d.Height * Supersample), 16, 2048);
            if (d.Draw == null || d.Draw.width != pw || d.Draw.height != ph)
            {
                if (d.Draw != null) d.Draw.Release();
                if (d.Shown != null) d.Shown.Release();
                d.Draw = new RenderTexture(pw, ph, 0, RenderTextureFormat.ARGB32) { name = "Lua display draw", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                d.Shown = new RenderTexture(pw, ph, 0, RenderTextureFormat.ARGB32) { name = "Lua display", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            }
            if (d.Go == null)
            {
                Materials();
                d.Go = new GameObject("Lua display");
                d.Go.AddComponent<MeshFilter>().sharedMesh = d.Glass;
                d.Material = new Material(glassBase) { name = "Lua display glass", mainTexture = d.Shown };
                d.Sides = new Material(glassBase) { name = "Lua display sides", mainTexture = Texture2D.whiteTexture };
                d.Go.AddComponent<MeshRenderer>().sharedMaterials = new[] { d.Material, d.Sides };
            }
            d.Dirty = true;
        }

        // No face found: a domed grid over the square, flat at the edges, out by Bulge in the middle; 2 mm off the
        // square so it never fights it.
        static void SquareGlass(Display d, Vector3 c00, Vector3 c10, Vector3 c01, Vector3 c11, Vector3 normal, float w, float h)
        {
            const int n = 12;
            var verts = new Vector3[(n + 1) * (n + 1)];
            var tex = new Vector2[verts.Length];
            var dome = Bulge * Mathf.Min(w, h);
            for (int y = 0; y <= n; y++)
                for (int x = 0; x <= n; x++)
                {
                    float fx = (float)x / n, fy = (float)y / n;
                    var p = Vector3.Lerp(Vector3.Lerp(c00, c10, fx), Vector3.Lerp(c01, c11, fx), fy);
                    var ex = 2f * fx - 1f; var ey = 2f * fy - 1f;
                    p += normal * (0.002f + dome * (1f - ex * ex) * (1f - ey * ey));
                    verts[y * (n + 1) + x] = p;
                    tex[y * (n + 1) + x] = new Vector2(fx, fy);
                }
            var tris = new List<int>();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int a = y * (n + 1) + x, bb = a + 1, c = a + n + 1, dd = c + 1;
                    tris.AddRange(new[] { a, c, bb, bb, c, dd });
                }
            d.Glass.vertices = verts;
            d.Glass.uv = tex;
            d.Glass.subMeshCount = 2;
            d.Glass.SetTriangles(tris, 0);
            d.Glass.SetTriangles(new int[0], 1);
            d.Glass.RecalculateBounds();
            d.Domed = true;
        }

        // The glass on the monitor's own face: its triangles (square, rim, cut corners and all), a hair in front of
        // each, with the picture laid on flat across the grown square.
        static void FaceGlass(Display d, List<Vector3> face)
        {
            Vector3 u = d.C10 - d.C00, v = d.C01 - d.C00;
            var verts = new List<Vector3>(face.Count);
            var tex = new List<Vector2>(face.Count);
            var tris = new List<int>(face.Count);
            var sides = new List<int>();
            for (int t = 0; t + 2 < face.Count; t += 3)
            {
                // The monitor mesh's own winding points each face out (front and tapered sides alike).
                var n = Vector3.Cross(face[t + 1] - face[t], face[t + 2] - face[t]).normalized;
                for (int k = 0; k < 3; k++)
                {
                    var p = face[t + k];
                    var w = p - d.C00;
                    tex.Add(new Vector2(Vector3.Dot(w, u) / u.sqrMagnitude, Vector3.Dot(w, v) / v.sqrMagnitude));
                    verts.Add(p + n * Lift);
                }
                int s = verts.Count - 3;
                // Reversed, as the grid's are, to show from outside. The steep tapered sides get the plain background
                // (as the game's own sides are), not the picture smeared along them.
                (Vector3.Dot(n, d.Normal) < 0.3f ? sides : tris).AddRange(new[] { s, s + 2, s + 1 });
            }
            d.Glass.SetVertices(verts);
            d.Glass.SetUVs(0, tex);
            d.Glass.subMeshCount = 2;
            d.Glass.SetTriangles(tris, 0);
            d.Glass.SetTriangles(sides, 1);
            d.Glass.RecalculateBounds();
            d.Domed = false;
        }

        // The picture square sits inside a glowing rim that slopes back from it (a Monitor's: 2.5% wider, 1.8 cm deep),
        // so the lit face is bigger than the square. Grows the square, in its own plane, over every face turned the
        // screen's way that lies close behind it (the rim, not the body further back).
        static List<Vector3> Grow(BrickInstanceData b, VoxelMesh mesh, Vector3 normal, ref Vector3 c00, ref Vector3 c10, ref Vector3 c01, ref Vector3 c11)
        {
            var face = new List<Vector3>();
            var buffer = mesh.meshBuffer;
            Vector3 u = c10 - c00, v = c01 - c00;
            var depth = 0.1f * Mathf.Min(u.magnitude, v.magnitude);
            float a0 = 0f, a1 = 1f, b0 = 0f, b1 = 1f;
            for (int t = 0; t + 2 < buffer.triangles.Count; t += 3)
            {
                var p0 = b.Position + b.Quaternion * (b.GridSize * mesh.ResizePoint(b.Scale, buffer.vertices[buffer.triangles[t]]));
                var p1 = b.Position + b.Quaternion * (b.GridSize * mesh.ResizePoint(b.Scale, buffer.vertices[buffer.triangles[t + 1]]));
                var p2 = b.Position + b.Quaternion * (b.GridSize * mesh.ResizePoint(b.Scale, buffer.vertices[buffer.triangles[t + 2]]));
                // Faces turned the screen's way set the picture's size; the tapered sides around them (steep, but not
                // turned away) are lit too, as the game's holo face is, so the screen has its edges and not a flat cut.
                var facing = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0).normalized, normal);
                if (facing < -0.2f) continue;
                var tri = new[] { p0, p1, p2 };
                var part = true;
                foreach (var p in tri)
                {
                    var w = p - c00;
                    var out_ = Vector3.Dot(w, normal);
                    float a = Vector3.Dot(w, u) / u.sqrMagnitude, bb = Vector3.Dot(w, v) / v.sqrMagnitude;
                    // Close behind the square and just around it: a stray face far off to the side isn't part of it.
                    if (out_ < -depth || out_ > depth || a < -0.25f || a > 1.25f || bb < -0.25f || bb > 1.25f) { part = false; break; }
                }
                if (!part) continue;
                face.AddRange(tri);
                if (facing < 0.3f) continue;
                foreach (var p in tri)
                {
                    var w = p - c00;
                    float a = Vector3.Dot(w, u) / u.sqrMagnitude, bb = Vector3.Dot(w, v) / v.sqrMagnitude;
                    a0 = Mathf.Min(a0, a); a1 = Mathf.Max(a1, a); b0 = Mathf.Min(b0, bb); b1 = Mathf.Max(b1, bb);
                }
            }
            var o = c00;
            c00 = o + u * a0 + v * b0;
            c10 = o + u * a1 + v * b0;
            c01 = o + u * a0 + v * b1;
            c11 = o + u * a1 + v * b1;
            return face;
        }

        // Rebuilds every display's glass (after changing FillFace or Bulge from DevBridge).
        public static string Rebuild()
        {
            foreach (var d in displays.Values) d.Shape = 0;
            return $"rebuilding {displays.Count} display(s), fill face {FillFace}";
        }

        static void BuildExternal(Display d)
        {
            var aspect = Aspect(d.Block);
            if (aspect <= 0f || Mathf.Abs(aspect - d.Aspect) < 1e-4f && d.Draw != null) return;
            d.Aspect = aspect;
            d.Height = Height;
            d.Width = Mathf.Max(8f, Mathf.Round(Height * aspect));
            int pw = Mathf.Clamp((int)(d.Width * Supersample), 16, 2048), ph = Mathf.Clamp((int)(d.Height * Supersample), 16, 2048);
            if (d.Draw != null) d.Draw.Release();
            d.Draw = new RenderTexture(pw, ph, 0, RenderTextureFormat.ARGB32) { name = "Lua canvas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Materials();
            d.Dirty = true;
        }

        static void Materials()
        {
            if (solid != null) return;
            solid = new Material(Shader.Find("UI/Default")) { name = "Lua display shapes", mainTexture = Texture2D.whiteTexture };
            text = new Material(Shader.Find("GUI/Text Shader")) { name = "Lua display text" };
            glassBase = new Material(Shader.Find("UI/Default")) { name = "Lua display glass" };
            glassBase.SetFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
            glassBase.renderQueue = 3000;
            // Scanlines: every other texture row a little darker.
            scan = new Texture2D(1, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, name = "Lua display scanlines" };
            scan.SetPixels(new[] { new Color(0, 0, 0, 0f), new Color(0, 0, 0, 0.32f) });
            scan.Apply();
            // Darker towards the edges and corners.
            const int s = 64;
            vignette = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Lua display vignette" };
            var px = new Color[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float ex = (x + 0.5f) / s * 2f - 1f, ey = (y + 0.5f) / s * 2f - 1f;
                    var r = Mathf.Pow(ex * ex * ex * ex + ey * ey * ey * ey, 0.25f);
                    px[y * s + x] = new Color(0, 0, 0, Mathf.Clamp01((r - 0.55f) / 0.45f) * 0.7f);
                }
            vignette.SetPixels(px);
            vignette.Apply();
            scanMat = new Material(solid) { name = "Lua display scanlines", mainTexture = scan };
            vignetteMat = new Material(solid) { name = "Lua display vignette", mainTexture = vignette };
        }

        // ---- Drawing the picture ----

        static Mesh Pooled(Display d, int i)
        {
            while (d.Pool.Count <= i) d.Pool.Add(new Mesh { name = "Lua display batch" });
            var m = d.Pool[i];
            m.Clear();
            return m;
        }

        static void Render(Display d)
        {
            float ss = d.Draw.width / Mathf.Max(1f, d.Width);
            var cb = new CommandBuffer { name = "Lua display" };
            cb.SetRenderTarget(d.Draw);
            // A monitor's own background: its paint, lit like the game's holo screens (Backdrop below 1 lets the
            // monitor's own icon show through). Anything the script draws over it is solid.
            var backdrop = d.External ? Color.clear : BackdropOf(d);
            cb.ClearRenderTarget(false, true, backdrop);
            cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, d.Draw.width, d.Draw.height, 0f, -1f, 1f));
            int pooled = 0;
            // Runs of shapes share one mesh; each text is its own (its font's texture).
            var verts = new List<Vector3>(); var cols = new List<Color>(); var uvs = new List<Vector2>(); var tris = new List<int>();
            void Flush()
            {
                if (verts.Count == 0) return;
                var m = Pooled(d, pooled++);
                m.SetVertices(verts); m.SetColors(cols); m.SetUVs(0, uvs); m.SetTriangles(tris, 0);
                cb.DrawMesh(m, Matrix4x4.identity, solid);
                verts.Clear(); cols.Clear(); uvs.Clear(); tris.Clear();
            }
            foreach (var c in d.Cmds)
            {
                if (c.Kind == 0)
                {
                    for (int q = 0; q + 3 < c.Points.Length; q += 4)
                    {
                        var s = verts.Count;
                        for (int k = 0; k < 4; k++) { verts.Add(c.Points[q + k] * ss); cols.Add(Lin(c.Color)); uvs.Add(new Vector2(0.5f, 0.5f)); }
                        tris.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
                    }
                }
                else
                {
                    Flush();
                    var m = Pooled(d, pooled++);
                    if (Glyphs(c, ss, m))
                    {
                        if (!fontMats.TryGetValue(c.Font, out var mat)) fontMats[c.Font] = mat = new Material(text) { name = "Lua display " + c.Font.name };
                        mat.mainTexture = c.Font.material.mainTexture;
                        cb.DrawMesh(m, Matrix4x4.identity, mat);
                    }
                }
            }
            // The button under the player's cursor, lit.
            foreach (var (name, r) in d.Buttons)
            {
                if (name != d.Hovered) continue;
                var s = verts.Count;
                foreach (var p in new[] { new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax) })
                { verts.Add(p * ss); cols.Add(new Color(1f, 1f, 1f, 0.18f)); uvs.Add(new Vector2(0.5f, 0.5f)); }
                tris.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
            }
            Flush();

            if (d.External)
            {
                Graphics.ExecuteCommandBuffer(cb);
                cb.Release();
                return;
            }

            // CRT: the picture on a pinched grid (corners pulled in most, so the edges bow out), scanlines, vignette.
            float W = d.Shown.width, H = d.Shown.height;
            cb.SetRenderTarget(d.Shown);
            cb.ClearRenderTarget(false, true, Color.clear);
            cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, W, H, 0f, -1f, 1f));
            if (d.Picture == null) d.Picture = new Material(solid) { name = "Lua display picture" };
            d.Picture.mainTexture = d.Draw;
            cb.DrawMesh(Grid(d, pooled++, W, H, d.Crt ? 0.1f : 0f), Matrix4x4.identity, d.Picture);
            if (d.Crt)
            {
                var lines = Pooled(d, pooled++);
                Quad(lines, W, H, new Vector2(1f, H / (2f * Supersample)));
                cb.DrawMesh(lines, Matrix4x4.identity, scanMat);
                var edge = Pooled(d, pooled++);
                Quad(edge, W, H, Vector2.one);
                cb.DrawMesh(edge, Matrix4x4.identity, vignetteMat);
            }
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        // The game renders in linear colour, and mesh colours aren't converted, so script colours (as seen on screen)
        // are turned linear first; without this they came out washed out.
        static Color BackdropOf(Display d)
        {
            var shade = Lin(Color.Lerp(d.Tint, Color.white, BackdropLift));
            shade.a = d.Backdrop >= 0f ? d.Backdrop : Backdrop;
            return shade;
        }

        static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

        static void Quad(Mesh m, float w, float h, Vector2 tiles)
        {
            m.vertices = new[] { new Vector3(0, 0), new Vector3(w, 0), new Vector3(w, h), new Vector3(0, h) };
            m.uv = new[] { new Vector2(0, tiles.y), new Vector2(tiles.x, tiles.y), new Vector2(tiles.x, 0), new Vector2(0, 0) };
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        }

        static Mesh Grid(Display d, int index, float w, float h, float k)
        {
            const int n = 16;
            var m = Pooled(d, index);
            var verts = new Vector3[(n + 1) * (n + 1)];
            var uv = new Vector2[verts.Length];
            var col = new Color[verts.Length];
            for (int y = 0; y <= n; y++)
                for (int x = 0; x <= n; x++)
                {
                    float fx = (float)x / n, fy = (float)y / n;
                    float dx = fx - 0.5f, dy = fy - 0.5f;
                    var f = 1f - k * (dx * dx + dy * dy);
                    verts[y * (n + 1) + x] = new Vector3((0.5f + dx * f) * w, (0.5f + dy * f) * h);
                    uv[y * (n + 1) + x] = new Vector2(fx, 1f - fy);
                    col[y * (n + 1) + x] = Color.white;
                }
            var tris = new List<int>();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, e = c + 1;
                    tris.AddRange(new[] { a, b, c, b, e, c });
                }
            m.vertices = verts; m.uv = uv; m.colors = col; m.SetTriangles(tris, 0);
            return m;
        }

        // One text's letters as quads in texture pixels.
        static bool Glyphs(Cmd c, float ss, Mesh m)
        {
            var font = c.Font;
            if (font == null || string.IsNullOrEmpty(c.Text)) return false;
            int px = Mathf.Clamp(Mathf.RoundToInt(c.Size * ss), 4, 256);
            font.RequestCharactersInTexture(c.Text, px, FontStyle.Normal);
            float width = 0f;
            foreach (var ch in c.Text) if (font.GetCharacterInfo(ch, out var info, px, FontStyle.Normal)) width += info.advance;
            float pen = c.X * ss - (c.Align == 0 ? width / 2f : c.Align > 0 ? width : 0f);
            float ascent = font.fontSize > 0 ? font.ascent * px / font.fontSize : px * 0.8f;
            float baseline = c.Y * ss + ascent;
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var cols = new List<Color>(); var tris = new List<int>();
            foreach (var ch in c.Text)
            {
                if (!font.GetCharacterInfo(ch, out var info, px, FontStyle.Normal)) continue;
                var s = verts.Count;
                float l = pen + info.minX, r = pen + info.maxX, top = baseline - info.maxY, bottom = baseline - info.minY;
                verts.Add(new Vector3(l, top)); verts.Add(new Vector3(r, top)); verts.Add(new Vector3(r, bottom)); verts.Add(new Vector3(l, bottom));
                uvs.Add(info.uvTopLeft); uvs.Add(info.uvTopRight); uvs.Add(info.uvBottomRight); uvs.Add(info.uvBottomLeft);
                for (int k = 0; k < 4; k++) cols.Add(Lin(c.Color));
                tris.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
                pen += info.advance;
            }
            if (verts.Count == 0) return false;
            m.SetVertices(verts); m.SetUVs(0, uvs); m.SetColors(cols); m.SetTriangles(tris, 0);
            return true;
        }

        internal static float Measure(Font font, string s, float size)
        {
            if (font == null || string.IsNullOrEmpty(s)) return 0f;
            int px = Mathf.Clamp(Mathf.RoundToInt(size * Supersample), 4, 256);
            font.RequestCharactersInTexture(s, px, FontStyle.Normal);
            float width = 0f;
            foreach (var ch in s) if (font.GetCharacterInfo(ch, out var info, px, FontStyle.Normal)) width += info.advance;
            return width / Supersample;
        }

        // ---- For DevBridge ----

        public static string Report() => displays.Count == 0 ? "No Lua displays." : string.Join("\n", displays.Values.Select(d =>
            $"{d.Block.brickData.Brick.name}{(d.External ? " (surface)" : "")} on {d.Block.brickCoord.entity?.Parent?.EntityName}: {d.Width}x{d.Height}, {d.Cmds.Count} drawings, crt {d.Crt}, texture {(d.External ? d.Draw : d.Shown)?.width}x{(d.External ? d.Draw : d.Shown)?.height}").ToArray());

        // The first display's monitor mesh, slot by slot, in the picture square's own frame: across (u) and up (v) from
        // its C00 corner as shares of its sides, and out along its normal (block units), to see where the face is.
        public static string MeshReport() => MeshReport(null);

        // ...or the one on that ship (by name).
        public static string MeshReport(string ship)
        {
            var d = displays.Values.FirstOrDefault(x => !x.External && x.Go != null && (ship == null || x.Block.brickCoord.entity?.Parent?.EntityName == ship));
            if (d == null) return "no monitor display";
            var b = d.Block.brickCoord.brickData;
            var mesh = b.Brick.VoxelMesh;
            var buffer = mesh.meshBuffer;
            Vector3 u = d.C10 - d.C00, v = d.C01 - d.C00;
            var sb = new System.Text.StringBuilder($"{b.Brick.name}: holo slot {ScreenSlot(b.Brick)}, square {u.magnitude:0.000} x {v.magnitude:0.000}\n");
            // The glass itself, in the same frame.
            var gl = d.Glass.vertices.Select(q => { var w = q - d.C00; return new Vector3(Vector3.Dot(w, u) / u.sqrMagnitude, Vector3.Dot(w, v) / v.sqrMagnitude, Vector3.Dot(w, d.Normal)); }).ToList();
            if (gl.Count > 0)
                sb.AppendLine($"glass ({(d.Domed ? "domed square" : "face")}): {gl.Count} verts, u {gl.Min(p => p.x):0.000}..{gl.Max(p => p.x):0.000}, v {gl.Min(p => p.y):0.000}..{gl.Max(p => p.y):0.000}, out {gl.Min(p => p.z):0.0000}..{gl.Max(p => p.z):0.0000}");
            var bySlot = new SortedDictionary<int, List<Vector3>>();
            for (int t = 0; t + 2 < buffer.triangles.Count; t += 3)
            {
                var i0 = buffer.triangles[t]; var i1 = buffer.triangles[t + 1]; var i2 = buffer.triangles[t + 2];
                var slot = Mathf.FloorToInt((buffer.uv[i0].x + buffer.uv[i1].x + buffer.uv[i2].x) / 3f);
                var p = new[] { i0, i1, i2 }.Select(i => b.Position + b.Quaternion * (b.GridSize * mesh.ResizePoint(b.Scale, buffer.vertices[i]))).ToArray();
                // Only faces turned the screen's way.
                if (Vector3.Dot(Vector3.Cross(p[1] - p[0], p[2] - p[0]).normalized, d.Normal) < 0.3f) continue;
                if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = new List<Vector3>();
                foreach (var q in p) { var w = q - d.C00; list.Add(new Vector3(Vector3.Dot(w, u) / u.sqrMagnitude, Vector3.Dot(w, v) / v.sqrMagnitude, Vector3.Dot(w, d.Normal))); }
            }
            foreach (var pair in bySlot)
            {
                var l = pair.Value;
                sb.AppendLine($"slot {pair.Key}: {l.Count / 3} tris, u {l.Min(p => p.x):0.000}..{l.Max(p => p.x):0.000}, v {l.Min(p => p.y):0.000}..{l.Max(p => p.y):0.000}, out {l.Min(p => p.z):0.0000}..{l.Max(p => p.z):0.0000}");
            }
            return sb.ToString();
        }

        // Testing without the link tool: links the Lua Computer nearest the player to the nearest monitor.
        public static string LinkNearest()
        {
            var eye = Dependency.RenderPipeline.playerCamera.transform.position;
            Command computer = null, monitor = null;
            float dc = float.MaxValue, dm = float.MaxValue;
            foreach (var c in CommandManager.commands.Datas)
            {
                if (c == null || c.brickCoord.entity == null) continue;
                float dist;
                try { dist = (c.brickCoord.WorldPosition - eye).magnitude; } catch { continue; }
                if (c is LuaComputer && dist < dc) { dc = dist; computer = c; }
                if (c is CommandDisplay && dist < dm) { dm = dist; monitor = c; }
            }
            if (computer == null || monitor == null) return $"computer {computer != null}, monitor {monitor != null}";
            if (((LuaComputer)computer).Outputs.Contains(monitor)) return $"already linked ({dc:0.0} m, {dm:0.0} m)";
            if (!CommandCommon.CanLinkToCommand((ICommandLinkSource)computer, monitor)) return "refused";
            CommandCommon.LinkToCommand((ICommandLinkSource)computer, monitor, onLoad: false, onInit: false);
            return $"linked computer ({dc:0.0} m) to {monitor.brickData.Brick.name} ({dm:0.0} m)";
        }

        // Same, to the nearest block another mod shows a canvas on (a display surface's controller screen), from the
        // nearest Lua Computer on that block's own ship (links stay within one ship).
        public static string LinkNearestSurface()
        {
            var eye = Dependency.RenderPipeline.playerCamera.transform.position;
            Command target = null;
            float dt = float.MaxValue;
            foreach (var c in CommandManager.commands.Datas)
            {
                if (c == null || c.brickCoord.entity == null || c is CommandDisplay || c is LuaComputer) continue;
                float dist;
                try { dist = (c.brickCoord.WorldPosition - eye).magnitude; } catch { continue; }
                if (dist < dt && Aspect(c) > 0f) { dt = dist; target = c; }
            }
            if (target == null) return "no surface";
            var ship = target.brickCoord.entity.Parent;
            var computer = CommandManager.commands.Datas.OfType<LuaComputer>()
                .Where(c => c.brickCoord.entity != null && c.brickCoord.entity.Parent == ship)
                .OrderBy(c => (c.brickCoord.WorldPosition - target.brickCoord.WorldPosition).sqrMagnitude).FirstOrDefault();
            if (computer == null) return "no Lua Computer on " + ship.EntityName;
            if (computer.Outputs.Contains(target)) return "already linked";
            if (!CommandCommon.CanLinkToCommand(computer, target)) return "refused";
            CommandCommon.LinkToCommand(computer, target, onLoad: false, onInit: false);
            return $"linked the computer on {ship.EntityName} to {target.brickData.Brick.name} ({dt:0.0} m away)";
        }

        // Testing: puts a Lua Computer on Cockpit Displays' display rig (on top of its right wall).
        public static string PutComputerOnDisplayRig()
        {
            var ship = CommandManager.commands.Datas
                .Where(c => c != null && c.brickCoord.entity != null && c.brickCoord.entity.Parent != null && c.brickCoord.entity.Parent.EntityName == "CockpitDisplays Display Rig")
                .Select(c => c.brickCoord.entity.Parent).FirstOrDefault();
            if (ship == null) return "no display rig";
            var brick = Brick.BRICK_INDEX[LuaComputerBlock.BrickId];
            var b = new BrickInstanceData
            {
                brickId = (ushort)brick.ItemID,
                gridPosition = new Vector3i(64, 80, -32),
                color = brick.defaultPaint,
                rotation = Quaternion.identity.ToByte(),
                healthScore = byte.MaxValue,
                gridLevel = (byte)brick.gridLevel,
            };
            Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = b, entityID = ship.ID });
            return "placed; link it in a moment with LinkNearestSurface()";
        }

        // ---- Linking: a Lua Computer may be linked to a monitor (the game only lets status sources link in) ----

        [HarmonyPatch(typeof(CommandDisplay), nameof(CommandDisplay.CanBeLinkTargetFromCommand))]
        static class LinkFromComputer
        {
            static void Postfix(ICommand predecessor, ref bool __result)
            {
                if (predecessor is LuaComputer) __result = true;
            }
        }
    }

    // What a script draws with: `display` is the computer's first linked monitor, displays.get(i) the i-th.
    // Coordinates are canvas units from the top left (display.width x display.height, 240 tall); colours are names
    // (green, amber, red, blue, cyan, white, grey, dim, black), "#rrggbb" / "#rrggbbaa", or 0xrrggbb.
    // What's drawn stays until clear(); draw in tick() and clear first to animate.
    public class DisplayApi
    {
        readonly LuaComputer computer;
        readonly DynValue which;   // a number or a display port's name
        public DisplayApi(LuaComputer host, int i) { computer = host; which = DynValue.NewNumber(i); }
        public DisplayApi(LuaComputer host, DynValue i) { computer = host; which = i; }

        Monitors.Display D => Monitors.Get(computer, which);

        public bool present => D != null;
        // A display surface (decals) another mod shows: see-through, so drawing nothing there leaves the view clear.
        public bool surface => D?.External ?? false;
        public double width => D?.Width ?? 0;
        public double height => D?.Height ?? 0;
        public bool crt { get => D?.Crt ?? false; set { var d = D; if (d != null && d.Crt != value) { d.Crt = value; d.Dirty = true; } } }
        // Brightness of the glass: about 1 plain, higher glows (bloom). Monitors.Glow until set.
        public double glow { get => D == null ? 0 : D.Glow >= 0f ? D.Glow : Monitors.Glow; set { var d = D; if (d != null) d.Glow = Mathf.Clamp((float)value, 0f, 4f); } }

        // The monitor's paint as "#rrggbb", to draw in its colour.
        public string tint => D == null ? "#ffffff" : "#" + ColorUtility.ToHtmlStringRGB(D.Tint);
        // How solid the background is where nothing is drawn: 0 see-through to the monitor, 1 solid.
        public double backdrop { get => D == null ? 0 : D.Backdrop >= 0f ? D.Backdrop : Monitors.Backdrop; set { var d = D; if (d != null) { d.Backdrop = Mathf.Clamp01((float)value); d.Dirty = true; } } }

        public void clear(object color = null)
        {
            var d = D; if (d == null) return;
            d.Cmds.Clear();
            d.Buttons.Clear();
            if (color != null) Add(d, new Monitors.Cmd { Kind = 0, Color = Col(color, Color.black), Points = Rect(0, 0, d.Width, d.Height) });
            d.Dirty = true;
        }

        // A touch button: drawn as a framed box with a centred label, lit while the player aims at it; touching it
        // passes its name to on_touch (the fifth value). Buttons last until clear().
        public void button(string name, double x, double y, double w, double h, object label = null, object color = null)
        {
            var d = D; if (d == null || string.IsNullOrEmpty(name)) return;
            var c = Col(color, Col("green"));
            rect(x, y, w, h, new Color(c.r * 0.18f, c.g * 0.18f, c.b * 0.18f, 1f));
            box(x, y, w, h, c, 1.5);
            var size = Math.Min(18.0, h * 0.55);
            text(x + w / 2, y + (h - size) / 2 - 1, label ?? name, c, size, "center");
            d.Buttons.RemoveAll(b => b.name == name);
            d.Buttons.Add((name, new Rect((float)x, (float)y, (float)w, (float)h)));
        }

        // Where the player aims on this display (canvas units), or nil when they aren't.
        public DynValue hover()
        {
            var at = Lua.HoverOn(D);
            return at == null ? DynValue.Nil : DynValue.NewTuple(DynValue.NewNumber(at.Value.x), DynValue.NewNumber(at.Value.y));
        }

        public void rect(double x, double y, double w, double h, object color = null)
        {
            var d = D; if (d == null) return;
            Add(d, new Monitors.Cmd { Kind = 0, Color = Col(color), Points = Rect((float)x, (float)y, (float)w, (float)h) });
        }

        // An outline.
        public void box(double x, double y, double w, double h, object color = null, double thickness = 1)
        {
            var d = D; if (d == null) return;
            float X = (float)x, Y = (float)y, Wd = (float)w, Ht = (float)h, t = (float)thickness;
            var pts = new List<Vector2>();
            pts.AddRange(Rect(X, Y, Wd, t)); pts.AddRange(Rect(X, Y + Ht - t, Wd, t));
            pts.AddRange(Rect(X, Y + t, t, Ht - 2 * t)); pts.AddRange(Rect(X + Wd - t, Y + t, t, Ht - 2 * t));
            Add(d, new Monitors.Cmd { Kind = 0, Color = Col(color), Points = pts.ToArray() });
        }

        public void line(double x1, double y1, double x2, double y2, object color = null, double thickness = 1)
        {
            var d = D; if (d == null) return;
            var a = new Vector2((float)x1, (float)y1); var b = new Vector2((float)x2, (float)y2);
            var dir = b - a; if (dir.sqrMagnitude < 1e-6f) dir = Vector2.right;
            var side = new Vector2(-dir.y, dir.x).normalized * (float)thickness * 0.5f;
            Add(d, new Monitors.Cmd { Kind = 0, Color = Col(color), Points = new[] { a + side, b + side, b - side, a - side } });
        }

        // Angles are degrees clockwise from straight up. An arc is a band thickness wide centred on radius r.
        public void arc(double x, double y, double r, double from, double to, object color = null, double thickness = 2)
        {
            var d = D; if (d == null) return;
            Add(d, new Monitors.Cmd { Kind = 0, Color = Col(color), Points = Arc((float)x, (float)y, (float)r, (float)from, (float)to, (float)thickness) });
        }

        public void ring(double x, double y, double r, object color = null, double thickness = 2) => arc(x, y, r, 0, 360, color, thickness);

        // A filled circle.
        public void circle(double x, double y, double r, object color = null)
        {
            var d = D; if (d == null) return;
            Add(d, new Monitors.Cmd { Kind = 0, Color = Col(color), Points = Arc((float)x, (float)y, (float)r / 2f, 0f, 360f, (float)r) });
        }

        // A filled triangle.
        public void tri(double x1, double y1, double x2, double y2, double x3, double y3, object color = null)
        {
            var d = D; if (d == null) return;
            var c = new Vector2((float)x3, (float)y3);
            Add(d, new Monitors.Cmd { Kind = 0, Color = Col(color), Points = new[] { new Vector2((float)x1, (float)y1), new Vector2((float)x2, (float)y2), c, c } });
        }

        static Vector2[] Arc(float cx, float cy, float r, float from, float to, float thickness)
        {
            var span = to - from;
            int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(span) * Mathf.Max(r, 4f) / 240f), 3, 128);
            float outer = r + thickness / 2f, inner = Mathf.Max(0f, r - thickness / 2f);
            Vector2 P(float radius, float a) => new Vector2(cx + radius * Mathf.Sin(a * Mathf.Deg2Rad), cy - radius * Mathf.Cos(a * Mathf.Deg2Rad));
            var pts = new Vector2[n * 4];
            for (int i = 0; i < n; i++)
            {
                float a0 = from + span * i / n, a1 = from + span * (i + 1) / n;
                pts[i * 4] = P(outer, a0); pts[i * 4 + 1] = P(outer, a1); pts[i * 4 + 2] = P(inner, a1); pts[i * 4 + 3] = P(inner, a0);
            }
            return pts;
        }

        // A level bar: fill (0-1) of the box in color, the rest in back (dim by default).
        public void bar(double x, double y, double w, double h, double fill, object color = null, object back = null)
        {
            var d = D; if (d == null) return;
            var f = Mathf.Clamp01((float)fill);
            Add(d, new Monitors.Cmd { Kind = 0, Color = Col(back, Col("dim")), Points = Rect((float)x, (float)y, (float)w, (float)h) });
            if (f > 0f) Add(d, new Monitors.Cmd { Kind = 0, Color = Col(color), Points = Rect((float)x, (float)y, (float)w * f, (float)h) });
        }

        // Text with its top at y. align: "left" (default), "center" or "right" of x. font: "code" (default),
        // "display" (the game's wide title face) or "text".
        public void text(double x, double y, object s, object color = null, double size = 16, string align = null, string font = null)
        {
            var d = D; if (d == null) return;
            var str = s is double n ? n.ToString("0.###") : s is bool bo ? (bo ? "true" : "false") : s?.ToString() ?? "";
            Add(d, new Monitors.Cmd { Kind = 1, Color = Col(color), Text = str, X = (float)x, Y = (float)y, Size = (float)size, Align = Align(align), Font = Face(font) });
        }

        // How wide a text would be, in canvas units.
        public double measure(object s, double size = 16, string font = null) =>
            Monitors.Measure(Face(font), s?.ToString() ?? "", (float)size);

        static void Add(Monitors.Display d, Monitors.Cmd c)
        {
            if (d.Cmds.Count >= 4000) throw new MoonSharp.Interpreter.ScriptRuntimeException("too many drawings on one display (4000); clear() it first");
            d.Cmds.Add(c);
            d.Dirty = true;
        }

        static Vector2[] Rect(float x, float y, float w, float h) =>
            new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h) };

        static int Align(string a) => a == null ? -1 : a.StartsWith("c", StringComparison.OrdinalIgnoreCase) || a.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? 0 : a.StartsWith("r", StringComparison.OrdinalIgnoreCase) ? 1 : -1;

        static Font Face(string name)
        {
            Font f = null;
            if (name != null && name.StartsWith("d", StringComparison.OrdinalIgnoreCase)) f = GameLook.DisplayFont;
            else if (name != null && name.StartsWith("t", StringComparison.OrdinalIgnoreCase)) f = GameLook.TextFont;
            else f = GameLook.CodeFont;
            return f ?? GameLook.TextFont ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        static readonly Dictionary<string, Color> named = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["green"] = new Color32(0x42, 0xff, 0x8c, 255),
            ["amber"] = new Color32(0xff, 0xb0, 0x3a, 255),
            ["red"] = new Color32(0xff, 0x4d, 0x5a, 255),
            ["blue"] = new Color32(0x57, 0xb3, 0xff, 255),
            ["cyan"] = new Color32(0x42, 0xff, 0xe8, 255),
            ["white"] = new Color32(0xee, 0xf4, 0xf0, 255),
            ["grey"] = new Color32(0x8a, 0x96, 0x92, 255),
            ["gray"] = new Color32(0x8a, 0x96, 0x92, 255),
            ["dim"] = new Color32(0x1c, 0x30, 0x28, 255),
            ["black"] = new Color32(0x03, 0x05, 0x04, 255),
        };

        internal static Color Col(object c, Color? fallback = null)
        {
            var def = fallback ?? named["green"];
            switch (c)
            {
                case null: return def;
                case Color given: return given;   // from C# helpers like button()
                case double n:
                    var v = (int)n;
                    return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
                case string s:
                    if (named.TryGetValue(s.Trim(), out var nc)) return nc;
                    if (ColorUtility.TryParseHtmlString(s.Trim().StartsWith("#") ? s.Trim() : "#" + s.Trim(), out var hc)) return hc;
                    return def;
                default: return def;
            }
        }
    }

    public class DisplaysApi
    {
        readonly LuaComputer computer;
        public DisplaysApi(LuaComputer host) { computer = host; }
        public int count => Monitors.Count(computer);
        // i: a display port's name, or a number (the declared display ports once there are any, else link order).
        public DisplayApi get(DynValue i) => new DisplayApi(computer, i);
    }
}
