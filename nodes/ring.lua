-- ring.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "a ring that fills clockwise from the top, 0 to 1, the value in the middle",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "value", "hide" },
  outs = {},
  settings = { label = "none", x = 60, y = 60, r = 40, thickness = 8, warn = "none", alert = "none", low = 0, screen = 1 },
  optional = { "hide" },
  kinds = { value = "level", hide = "onoff" },
  choices = { low = { 0, 1 } },
  docs = {
    value = "how full, 0 to 1", hide = "on: not drawn",
    label = "the name under the number", x = "centre", y = "centre", r = "radius", thickness = "ring width",
    warn = "amber past this (none: never)", alert = "red past this (none: never)",
    low = "1: low is bad, so warn and alert are below those values",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    local v = i.value
    nodeui.draw(state, s.screen, 1, i.hide <= 0.5 and function(d)
      ui.ring(d, s.x, s.y, s.r, v, { label = nodeui.opt(s.label), thickness = s.thickness,
        warn = nodeui.opt(s.warn), alert = nodeui.opt(s.alert), low = s.low == 1 })
    end or nil)
  end,
}
