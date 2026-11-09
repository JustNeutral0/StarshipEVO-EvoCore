using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EvoCore
{
    public static partial class Lua
    {
        // The code area of the script editor, Godot style: line numbers, the current line lit, syntax colours, and an
        // error marked on its line. IMGUI's TextArea can't colour its own text, so the real TextArea does the typing,
        // caret, selection and clipboard with its text drawn invisible, and the coloured lines are drawn over it in the
        // same monospace font and size, so every character lands in the same place.
        internal static class CodeView
        {
            // Godot 4's default script colours.
            static readonly string Keyword = "ff7085", Str = "ffeca1", Num = "a1ffe0", Comment = "9aa1ad", Call = "57b3ff",
                Api = "42ffc2", Member = "bce0ff", Symbol = "abc9ff", Plain = "cdcfd2";
            static readonly HashSet<string> keywords = new HashSet<string>
            {
                "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil",
                "not", "or", "repeat", "return", "then", "true", "until", "while",
            };
            // What scripts get from EvoCore and the sandbox, and the names the runner calls.
            static readonly HashSet<string> api = new HashSet<string>
            {
                "ship", "inputs", "outputs", "screen", "pilot", "time", "print", "rate", "tick", "on_input", "displays",
                "math", "string", "table", "pairs", "ipairs", "tostring", "tonumber", "type", "select", "next", "error",
                "pcall", "assert", "unpack", "V", "S",
            };

            const int Gutter = 54;
            static GUIStyle area, overlay, numbers;
            static Font styledFont;
            static float lineHeight, charWidth;

            // Cached colouring, redone only when the text changes.
            static string coloured;
            static string[] lines = new string[0], rich = new string[0];

            static void Styles()
            {
                var font = GameLook.CodeFont;
                if (area != null && styledFont == font) return;
                styledFont = font;
                area = new GUIStyle(GUIStyle.none) { font = font, fontSize = 15, wordWrap = false, richText = false, alignment = TextAnchor.UpperLeft, padding = new RectOffset(Gutter + 10, 12, 8, 8) };
                area.normal.textColor = area.hover.textColor = area.focused.textColor = area.active.textColor = new Color(0, 0, 0, 0);
                overlay = new GUIStyle(area) { richText = true, padding = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Overflow };
                overlay.normal.textColor = Hex(Plain);
                numbers = new GUIStyle(overlay) { richText = false, alignment = TextAnchor.UpperRight };
                numbers.normal.textColor = new Color(1, 1, 1, 0.28f);
                lineHeight = area.lineHeight > 0 ? area.lineHeight : 18;
                charWidth = Mathf.Max(1, area.CalcSize(new GUIContent("MMMMMMMMMM")).x / 10f);
            }

            // Draws the code area at the layout's next place; returns the (possibly edited) text. errorLine is 1-based,
            // 0 for none; cursorLine comes back 1-based.
            internal static string Draw(string text, ref Vector2 scroll, float height, string control, int errorLine, out int cursorLine)
            {
                Styles();
                if (!ReferenceEquals(text, coloured)) Colour(text);
                var e = Event.current;
                cursorLine = 1;

                var widest = 0;
                foreach (var l in lines) widest = Math.Max(widest, l.Length);
                var contentW = Gutter + 10 + widest * charWidth + 40;
                var contentH = 16 + lines.Length * lineHeight + height * 0.5f;   // room to scroll the last line up

                var view = GUILayoutUtility.GetRect(100, 10000, height, height, GUILayout.ExpandWidth(true));
                if (e.type == EventType.Repaint) Fill(view, new Color(0.125f, 0.145f, 0.18f, 1f));
                var content = new Rect(0, 0, Mathf.Max(contentW, view.width - 14), Mathf.Max(contentH, view.height));
                scroll = GUI.BeginScrollView(view, scroll, content);

                // The current line (from the TextArea's caret) and the error line, behind the text.
                var editor = GUIUtility.keyboardControl != 0 && GUI.GetNameOfFocusedControl() == control
                    ? GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor : null;
                if (editor != null) cursorLine = 1 + CountLines(text, Math.Min(editor.cursorIndex, text.Length));
                if (e.type == EventType.Repaint)
                {
                    Fill(new Rect(scroll.x, 0, Gutter, content.height), new Color(0.105f, 0.12f, 0.15f, 1f));
                    if (editor != null) Fill(new Rect(scroll.x, LineY(cursorLine), content.width, lineHeight), new Color(1, 1, 1, 0.045f));
                    if (errorLine > 0) Fill(new Rect(scroll.x, LineY(errorLine), content.width, lineHeight), new Color(1f, 0.25f, 0.2f, 0.16f));
                }

                GUI.SetNextControlName(control);
                var edited = GUI.TextArea(content, text, area);

                if (e.type == EventType.Repaint)
                {
                    // Only the lines in view.
                    int first = Mathf.Max(0, Mathf.FloorToInt((scroll.y - 8) / lineHeight)), last = Mathf.Min(lines.Length - 1, first + Mathf.CeilToInt(view.height / lineHeight) + 1);
                    for (int i = first; i <= last; i++)
                    {
                        var y = LineY(i + 1);
                        var n = i + 1;
                        numbers.normal.textColor = n == errorLine ? new Color(1f, 0.45f, 0.4f) : n == cursorLine && editor != null ? new Color(1, 1, 1, 0.75f) : new Color(1, 1, 1, 0.28f);
                        GUI.Label(new Rect(scroll.x, y, Gutter - 12, lineHeight), n.ToString(), numbers);
                        GUI.Label(new Rect(Gutter + 10, y, contentW, lineHeight), rich[i], overlay);
                    }
                }

                // Keep the caret in view while typing or moving it with the keys.
                if (editor != null && e.type == EventType.Repaint && editor.cursorIndex != lastCursor)
                {
                    lastCursor = editor.cursorIndex;
                    var y = LineY(cursorLine);
                    if (y < scroll.y) scroll.y = y - 4;
                    else if (y + lineHeight > scroll.y + view.height - 16) scroll.y = y + lineHeight - view.height + 20;
                    var column = editor.cursorIndex - (text.LastIndexOf('\n', Math.Max(0, Math.Min(editor.cursorIndex, text.Length) - 1)) + 1);
                    var x = Gutter + 10 + column * charWidth;
                    if (x < scroll.x + Gutter + 10) scroll.x = Mathf.Max(0, x - Gutter - 30);
                    else if (x > scroll.x + view.width - 30) scroll.x = x - view.width + 60;
                }
                GUI.EndScrollView();
                return edited;
            }


            static int lastCursor = -1;
            static float LineY(int line) => 8 + (line - 1) * lineHeight;

            static int CountLines(string s, int upTo)
            {
                int n = 0;
                for (int i = 0; i < upTo; i++) if (s[i] == '\n') n++;
                return n;
            }

            // ---- Colouring ----

            static void Colour(string text)
            {
                coloured = text;
                lines = text.Split('\n');
                rich = new string[lines.Length];
                string closing = null;   // inside a long comment or string: what ends it ("]]", "]=]"...)
                bool comment = false;
                for (int i = 0; i < lines.Length; i++) rich[i] = Line(lines[i].TrimEnd('\r'), ref closing, ref comment);
            }

            // IMGUI's rich text reads these as tags, so a line holding one is shown plain.
            static readonly Regex tagLike = new Regex(@"</?(b|i|size|color|material|quad)\b", RegexOptions.IgnoreCase);

            static string Line(string line, ref string closing, ref bool comment)
            {
                var sb = new StringBuilder();
                int i = 0;
                if (tagLike.IsMatch(line))
                {
                    // Still track long comments and strings, so the lines after it colour right.
                    Scan(line, ref closing, ref comment);
                    return line;
                }
                while (i < line.Length)
                {
                    // Inside a long comment or string from an earlier line.
                    if (closing != null)
                    {
                        var end = line.IndexOf(closing, i, StringComparison.Ordinal);
                        var stop = end < 0 ? line.Length : end + closing.Length;
                        Span(sb, line.Substring(i, stop - i), comment ? Comment : Str);
                        i = stop;
                        if (end >= 0) closing = null;
                        continue;
                    }
                    var c = line[i];
                    if (c == '-' && At(line, i + 1) == '-')
                    {
                        var level = LongOpen(line, i + 2);
                        if (level >= 0) { closing = "]" + new string('=', level) + "]"; comment = true; Span(sb, line.Substring(i, level + 4), Comment); i += level + 4; continue; }
                        Span(sb, line.Substring(i), Comment);
                        break;
                    }
                    if (c == '[')
                    {
                        var level = LongOpen(line, i);
                        if (level >= 0) { closing = "]" + new string('=', level) + "]"; comment = false; Span(sb, line.Substring(i, level + 2), Str); i += level + 2; continue; }
                    }
                    if (c == '"' || c == '\'')
                    {
                        int j = i + 1;
                        while (j < line.Length && line[j] != c) j += line[j] == '\\' ? 2 : 1;
                        j = Math.Min(j + 1, line.Length);
                        Span(sb, line.Substring(i, j - i), Str);
                        i = j;
                        continue;
                    }
                    if (char.IsDigit(c) || (c == '.' && char.IsDigit(At(line, i + 1))))
                    {
                        int j = i;
                        if (c == '0' && (At(line, i + 1) == 'x' || At(line, i + 1) == 'X')) { j += 2; while (j < line.Length && Uri.IsHexDigit(line[j])) j++; }
                        else
                        {
                            while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.')) j++;
                            if (j < line.Length && (line[j] == 'e' || line[j] == 'E')) { j++; if (At(line, j) == '-' || At(line, j) == '+') j++; while (j < line.Length && char.IsDigit(line[j])) j++; }
                        }
                        Span(sb, line.Substring(i, j - i), Num);
                        i = j;
                        continue;
                    }
                    if (char.IsLetter(c) || c == '_')
                    {
                        int j = i;
                        while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                        var word = line.Substring(i, j - i);
                        var before = PrevNonSpace(line, i);
                        var after = NextNonSpace(line, j);
                        string colour;
                        if (keywords.Contains(word)) colour = Keyword;
                        else if (before == '.' || before == ':') colour = after == '(' ? Call : Member;
                        else if (api.Contains(word)) colour = Api;
                        else if (after == '(' || after == '"' || after == '{') colour = Call;
                        else colour = Plain;
                        Span(sb, word, colour);
                        i = j;
                        continue;
                    }
                    if (char.IsWhiteSpace(c))
                    {
                        int j = i;
                        while (j < line.Length && char.IsWhiteSpace(line[j])) j++;
                        sb.Append(line, i, j - i);
                        i = j;
                        continue;
                    }
                    Span(sb, c.ToString(), Symbol);
                    i++;
                }
                return sb.ToString();
            }

            // Follows long comments and strings through a line without colouring it.
            static void Scan(string line, ref string closing, ref bool comment)
            {
                int i = 0;
                while (i < line.Length)
                {
                    if (closing != null)
                    {
                        var end = line.IndexOf(closing, i, StringComparison.Ordinal);
                        if (end < 0) return;
                        i = end + closing.Length;
                        closing = null;
                        continue;
                    }
                    if (line[i] == '-' && At(line, i + 1) == '-')
                    {
                        var level = LongOpen(line, i + 2);
                        if (level < 0) return;
                        closing = "]" + new string('=', level) + "]"; comment = true; i += level + 4;
                        continue;
                    }
                    if (line[i] == '[')
                    {
                        var level = LongOpen(line, i);
                        if (level >= 0) { closing = "]" + new string('=', level) + "]"; comment = false; i += level + 2; continue; }
                    }
                    if (line[i] == '"' || line[i] == '\'')
                    {
                        var q = line[i]; i++;
                        while (i < line.Length && line[i] != q) i += line[i] == '\\' ? 2 : 1;
                    }
                    i++;
                }
            }

            // [[ or [==[ at i: the number of '=', or -1.
            static int LongOpen(string line, int i)
            {
                if (At(line, i) != '[') return -1;
                int j = i + 1, level = 0;
                while (At(line, j) == '=') { j++; level++; }
                return At(line, j) == '[' ? level : -1;
            }

            static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

            static char PrevNonSpace(string s, int i)
            {
                for (int k = i - 1; k >= 0; k--) if (!char.IsWhiteSpace(s[k])) return s[k];
                return '\0';
            }

            static char NextNonSpace(string s, int i)
            {
                for (int k = i; k < s.Length; k++) if (!char.IsWhiteSpace(s[k])) return s[k];
                return '\0';
            }

            static void Span(StringBuilder sb, string s, string colour)
            {
                if (s.Length == 0) return;
                sb.Append("<color=#").Append(colour).Append('>').Append(s).Append("</color>");
            }

            static Color Hex(string h) => ColorUtility.TryParseHtmlString("#" + h, out var c) ? c : Color.white;

            static void Fill(Rect r, Color c)
            {
                var old = GUI.color;
                GUI.color = c;
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = old;
            }

            // The line a Lua error points at: MoonSharp writes "name:(line,col-col): message".
            static readonly Regex errorAt = new Regex(@":\((\d+),\d+");
            internal static int ErrorLine(string message)
            {
                if (string.IsNullOrEmpty(message)) return 0;
                var m = errorAt.Match(message);
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            }
        }
    }
}
