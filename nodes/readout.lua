-- readout.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "a line of text with the value in it: text=\"SPEED {v} m/s\"",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "value", "hide" },
  outs = {},
  settings = { text = "{v}", decimals = 0, percent = 0, x = 10, y = 10, size = 14, color = "theme", align = "left", font = "display", screen = 1 },
  optional = { "hide" },
  kinds = { hide = "onoff" },
  choices = { percent = { 0, 1 }, align = { "left", "center", "right" }, font = { "display", "text", "code" } },
  docs = {
    value = "the number shown where {v} is",
    hide = "on: not drawn",
    text = "what it says; {v} is the value",
    decimals = "places after the point",
    percent = "1: show 0 to 1 as 0% to 100%",
    x = "where it starts (or its middle or right end, by align)", y = "its top", size = "text height",
    color = "a colour name or #rrggbb (theme: the usual text colour)",
    align = "left, center or right", font = "display, text or code",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    local shown = (tostring(s.text):gsub("{v}", nodeui.format(i.value, s.decimals, s.percent)))
    nodeui.draw(state, s.screen, 1, i.hide <= 0.5 and function(d)
      d.text(s.x, s.y, shown, nodeui.opt(s.color) or ui.theme.text, s.size, s.align, s.font)
    end or nil)
  end,
}
