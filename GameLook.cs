using System.Linq;
using System.Text;
using TMPro;
using UI;
using UnityEngine;

namespace EvoCore
{
    // The game's own UI look, for drawing mod screens (the Lua Computer's) to match its menus. Every game window is
    // skinned from a UITemplateData: plain Unity fonts (usable by IMGUI), sprites for panels, buttons and inputs, and
    // colours. Probe() (DevBridge: get EvoCore.GameLook.Probe()) lists what's there; ShowWindow(name) opens one of the
    // game's windows by its object name, for comparing screenshots.
    public static class GameLook
    {
        // ---- The skin: the game's template as IMGUI styles ----
        //
        // Prepare() (outside OnGUI: it copies textures on the GPU) makes tinted copies of the template's sprites, as the
        // game tints them with Image.color; Skin() (inside OnGUI, where GUI.skin can be read) builds a GUISkin from them:
        // buttons and tabs from its button and Codex-tab sprites, inputs from its input sprite, labels in Inter, and a
        // dark code area in the code font. Screens draw with `GUI.skin = GameLook.Skin()` and put the old skin back.

        static UITemplateData data;
        static bool looked, prepared;
        static GUISkin skin;
        public static Texture2D TitleBar, Body, Button, ButtonHover, ButtonDown, Tab, TabOn, Input, InputFocus, Box, Thumb, Track, CodeBack;
        static RectOffset titleBorder, bodyBorder, buttonBorder, inputBorder, tabBorder;

        public static UITemplateData Data
        {
            get
            {
                if (!looked || data == null) { looked = true; data = Resources.FindObjectsOfTypeAll<UITemplateData>().FirstOrDefault(); }
                return data;
            }
        }

        static Font display, code;
        public static Font TitleFont => Data != null && Data.titleFont != null ? Data.titleFont : null;
        public static Font TextFont => Data != null && Data.buttonFont != null ? Data.buttonFont : null;
        // The game's wide sci-fi font (holograms): for the computer's own readouts.
        public static Font DisplayFont => display != null ? display : (display = Loaded("Microgramma D Extended Bold") ?? TitleFont);
        // Readable code: the game's own Consolas. Unity 6's IMGUI draws labels only in font assets the game ships;
        // a font made from a Windows font (Cascadia Mono, JetBrains Mono) works in a TextArea but labels in it draw
        // stale text or nothing, so installed fonts are only a last resort.
        public static Font CodeFont => code != null ? code : (code = PickCode());

        static Font Loaded(string name) => Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == name);

        static Font PickCode()
        {
            var game = Loaded("CONSOLA") ?? Loaded("Consolas");
            if (game != null) return game;
            try { return Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Cascadia Mono" }, 14); }
            catch { return null; }
        }

        public static bool Ready => prepared;

        public static void Prepare()
        {
            if (prepared) return;
            prepared = true;
            var d = Data;
            var grey = new Color(0.72f, 0.72f, 0.72f, 0.81f);
            TitleBar = Tinted(d?.titleBarSprite, d != null ? d.mainColor : new Color(0.87f, 0.87f, 0.87f, 0.78f), out titleBorder);
            Body = Tinted(d?.backgroundSprite, new Color(0.25f, 0.25f, 0.26f, 0.97f), out bodyBorder);
            Button = Tinted(d?.buttonSprite, d != null ? d.buttonColor : grey, out buttonBorder);
            ButtonHover = Tinted(d?.buttonSprite, Color.white, out _);
            ButtonDown = Tinted(d?.buttonPressedSprite ?? d?.buttonSprite, Color.white, out _);
            Tab = Tinted(d?.inputSprite, new Color(0.34f, 0.34f, 0.36f, 0.95f), out tabBorder);
            TabOn = Tinted(d?.inputSprite, d != null ? d.mainColor : Color.white, out _);
            Input = Tinted(d?.inputSprite, d != null ? d.inputColor : new Color(0.9f, 0.9f, 0.9f, 0.86f), out inputBorder);
            InputFocus = Tinted(d?.inputSprite, Color.white, out _);
            Box = Tinted(d?.descBackground, d != null ? d.descColor : new Color(0.2f, 0.2f, 0.2f, 0.86f), out _);
            Thumb = Tinted(d?.scrollHandle, d != null ? d.scrollHandleColor * new Color(1, 1, 1, 0.6f) : grey, out _);
            Track = Tinted(d?.inputSprite, new Color(1, 1, 1, 0.06f), out _);
            CodeBack = Solid(new Color(0.125f, 0.145f, 0.18f, 1f));   // Godot's slate code background
        }

        // A sprite drawn into a readable texture through the GPU, then multiplied by tint, as Image.color does.
        static Texture2D Tinted(Sprite s, Color tint, out RectOffset border)
        {
            border = new RectOffset(3, 3, 3, 3);
            if (s == null || s.texture == null)
            {
                var t0 = Solid(tint);
                return t0;
            }
            var b = s.border;   // left, bottom, right, top
            border = new RectOffset((int)b.x, (int)b.z, (int)b.w, (int)b.y);
            var src = s.texture;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var r = s.textureRect;
            var t = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            t.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var px = t.GetPixels();
            for (int i = 0; i < px.Length; i++) px[i] *= tint;
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        public static RectOffset TitleBorder => titleBorder;
        public static RectOffset BodyBorder => bodyBorder;

        public static GUISkin Skin()
        {
            if (skin != null) return skin;
            Prepare();
            var s = Object.Instantiate(GUI.skin);
            Color text = new Color(0.82f, 0.82f, 0.82f), bright = Color.white;
            var font = TextFont;

            s.label = new GUIStyle(GUI.skin.label) { font = font, fontSize = 15, wordWrap = false };
            s.label.normal.textColor = text;

            s.button = new GUIStyle(GUI.skin.button) { font = font, fontSize = 14, border = buttonBorder, padding = new RectOffset(8, 8, 6, 7), alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            s.button.normal.background = Button; s.button.normal.textColor = Data != null ? Data.buttonTextColor : text;
            s.button.hover.background = ButtonHover; s.button.hover.textColor = bright;
            s.button.active.background = ButtonDown; s.button.active.textColor = bright;
            s.button.onNormal.background = ButtonDown; s.button.onNormal.textColor = bright;
            s.button.onHover.background = ButtonDown; s.button.onHover.textColor = bright;
            s.button.onActive.background = ButtonDown; s.button.onActive.textColor = bright;

            s.textField = new GUIStyle(GUI.skin.textField) { font = font, fontSize = 14, border = inputBorder, padding = new RectOffset(7, 7, 4, 4) };
            var dark = Data != null ? Data.inputFontColor : new Color(0.2f, 0.2f, 0.2f);
            s.textField.normal.background = s.textField.hover.background = Input;
            s.textField.focused.background = s.textField.onFocused.background = InputFocus;
            s.textField.normal.textColor = s.textField.hover.textColor = s.textField.focused.textColor = dark;

            // Code: Godot-like, a slate area in the code font.
            s.textArea = new GUIStyle(GUI.skin.textArea) { font = CodeFont, fontSize = 15, border = new RectOffset(0, 0, 0, 0), padding = new RectOffset(10, 10, 8, 8), wordWrap = false };
            s.textArea.normal.background = s.textArea.hover.background = s.textArea.focused.background = s.textArea.onFocused.background = CodeBack;
            s.textArea.normal.textColor = s.textArea.hover.textColor = s.textArea.focused.textColor = new Color(0.8f, 0.83f, 0.87f);

            s.box = new GUIStyle(GUI.skin.box) { font = font, fontSize = 14, border = inputBorder, padding = new RectOffset(8, 8, 6, 6) };
            s.box.normal.background = Box;
            s.box.normal.textColor = Data != null ? Data.descTextColor : text;

            s.toggle = new GUIStyle(GUI.skin.toggle) { font = font, fontSize = 15 };
            s.toggle.normal.textColor = s.toggle.onNormal.textColor = text;

            s.scrollView = new GUIStyle(GUIStyle.none);
            s.verticalScrollbar = new GUIStyle(GUI.skin.verticalScrollbar) { fixedWidth = 10, border = inputBorder, margin = new RectOffset(2, 2, 2, 2) };
            s.verticalScrollbar.normal.background = Track;
            s.verticalScrollbarThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb) { fixedWidth = 10, border = inputBorder };
            s.verticalScrollbarThumb.normal.background = Thumb;
            s.horizontalScrollbar = new GUIStyle(GUI.skin.horizontalScrollbar) { fixedHeight = 10, border = inputBorder, margin = new RectOffset(2, 2, 2, 2) };
            s.horizontalScrollbar.normal.background = Track;
            s.horizontalScrollbarThumb = new GUIStyle(GUI.skin.horizontalScrollbarThumb) { fixedHeight = 10, border = inputBorder };
            s.horizontalScrollbarThumb.normal.background = Thumb;
            s.settings.cursorColor = new Color(0.9f, 0.92f, 0.95f);
            s.settings.selectionColor = new Color(0.3f, 0.45f, 0.7f, 0.55f);   // Godot's soft blue selection
            skin = s;
            return skin;
        }

        // A tab: the input sprite, light when picked (with dark text) like the title bar, grey otherwise.
        static GUIStyle tab;
        public static GUIStyle TabStyle()
        {
            if (tab != null) return tab;
            Prepare();
            tab = new GUIStyle(Skin().button) { border = tabBorder, padding = new RectOffset(18, 18, 7, 8), fontSize = 15 };
            tab.normal.background = tab.active.background = Tab; tab.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            tab.hover.background = Tab; tab.hover.textColor = Color.white;
            tab.onNormal.background = tab.onHover.background = tab.onActive.background = TabOn;
            tab.onNormal.textColor = tab.onHover.textColor = tab.onActive.textColor = new Color(0.17f, 0.17f, 0.18f);
            return tab;
        }

        public static string Probe()
        {
            var sb = new StringBuilder();
            foreach (var d in Resources.FindObjectsOfTypeAll<UITemplateData>())
            {
                sb.AppendLine($"UITemplateData {d.name}");
                void F(string n, Font f) => sb.AppendLine($"  font {n}: " + (f != null ? $"{f.name}, dynamic {f.dynamic}, size {f.fontSize}, os names [{string.Join(", ", f.fontNames ?? new string[0])}]" : "none"));
                void S(string n, Sprite s) => sb.AppendLine($"  sprite {n}: " + (s != null ? $"{s.name}, texture {s.texture?.name} {s.texture?.width}x{s.texture?.height}, rect {s.rect}, border {s.border}, readable {s.texture?.isReadable}" : "none"));
                void C(string n, Color c) => sb.AppendLine($"  colour {n}: #{ColorUtility.ToHtmlStringRGBA(c)}");
                F("title", d.titleFont); F("button", d.buttonFont); F("desc", d.descFont); F("mediumTitle", d.mediumTitleFont);
                sb.AppendLine($"  tmp buttonFont: {(d.buttonFontTMP != null ? d.buttonFontTMP.name + ", family " + d.buttonFontTMP.faceInfo.familyName : "none")}");
                S("titleBar", d.titleBarSprite); S("background", d.backgroundSprite); S("backgroundWithTitle", d.backgroundWithTitleSprite);
                S("button", d.buttonSprite); S("buttonPressed", d.buttonPressedSprite); S("input", d.inputSprite); S("scrollHandle", d.scrollHandle);
                S("descBackground", d.descBackground); S("overlay", d.overlaySprite); S("toggleChecked", d.checkedToggleSprite);
                S("toggleBackground", d.backgroundToggleSprite); S("dropdownArrow", d.dropdownArrow); S("codexTab", d.codexTabSprite); S("itemBar", d.itemBarSprite);
                C("main", d.mainColor); C("mainFlat", d.mainColorFlat); C("desc", d.descColor); C("descText", d.descTextColor);
                C("mediumTitle", d.mediumTitleColor); C("overlay", d.overlayColor); C("button", d.buttonColor); C("buttonText", d.buttonTextColor);
                C("scrollHandle", d.scrollHandleColor); C("dropdown", d.dropdownColor); C("dropdownFont", d.dropdownFontColor);
                C("input", d.inputColor); C("inputFont", d.inputFontColor); C("codexTabHighlighted", d.codexTabHighlightedColor);
                sb.AppendLine($"  sizes: button text {d.buttonTextSize}, button height {d.buttonHeight}, desc {d.descTextSize}, medium title {d.mediumTitleSize}, upper case {d.forceUpperCase}");
            }
            sb.AppendLine("Fonts loaded: " + string.Join(", ", Resources.FindObjectsOfTypeAll<Font>().Select(f => f.name).Distinct().OrderBy(n => n).ToArray()));
            sb.AppendLine("TMP fonts: " + string.Join(", ", Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                .Select(f => $"{f.name} ({f.faceInfo.familyName}{(f.sourceFontFile != null ? ", source " + f.sourceFontFile.name : "")})").Distinct().OrderBy(n => n).ToArray()));
            sb.AppendLine("Windows: " + string.Join(", ", Resources.FindObjectsOfTypeAll<UITemplateWindow>()
                .Where(w => w.gameObject.scene.IsValid()).Select(w => $"{w.name}{(w.Active ? " (open)" : "")}").OrderBy(n => n).ToArray()));
            return sb.ToString();
        }

        // Opens (or closes) one of the game's windows by its object name, as listed by Probe().
        public static string ShowWindow(string name, bool open)
        {
            var w = Resources.FindObjectsOfTypeAll<UITemplateWindow>().FirstOrDefault(x => x.name == name && x.gameObject.scene.IsValid());
            if (w == null) return "no window " + name;
            w.SetActive(open);
            return $"{name} {(open ? "opened" : "closed")}";
        }
    }
}
