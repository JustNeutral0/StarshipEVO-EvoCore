-- lamp.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "an indicator light with a label: lit while the value is on",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "value", "hide" },
  outs = {},
  settings = { label = "none", x = 20, y = 20, r = 7, color = "theme", screen = 1 },
  optional = { "hide" },
  kinds = { value = "onoff", hide = "onoff" },
  docs = {
    value = "lit above 0.5", hide = "on: not drawn",
    label = "the text to its right", x = "centre", y = "centre", r = "radius",
    color = "the lit colour (theme: the usual green)",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    local on = i.value > 0.5
    nodeui.draw(state, s.screen, 1, i.hide <= 0.5 and function(d)
      local t = ui.theme
      local c = nodeui.opt(s.color) or t.fg
      d.circle(s.x, s.y, s.r, on and c or t.dim)
      d.ring(s.x, s.y, s.r, on and c or t.muted, 1.5)
      local label = nodeui.opt(s.label)
      if label then d.text(s.x + s.r + 6, s.y - 6, label, on and t.text or t.muted, 12) end
    end or nil)
  end,
}
