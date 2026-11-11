-- gauge.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "a dial with ticks and a needle, the value in the middle",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "value", "hide" },
  outs = {},
  settings = { label = "none", unit = "none", x = 60, y = 60, r = 50, min = 0, max = 1, warn = "none", alert = "none", low = 0, screen = 1 },
  optional = { "hide" },
  kinds = { hide = "onoff" },
  choices = { low = { 0, 1 } },
  docs = {
    value = "the needle's value", hide = "on: not drawn",
    label = "the name under it", unit = "after the number (with min 0 and max 1 it shows a percent instead)",
    x = "centre", y = "centre", r = "radius",
    min = "the value at the left end", max = "the value at the right end",
    warn = "amber past this (none: never)", alert = "red past this (none: never)",
    low = "1: low is bad, so warn and alert are below those values",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    local v = i.value
    nodeui.draw(state, s.screen, 1, i.hide <= 0.5 and function(d)
      ui.gauge(d, s.x, s.y, s.r, v, { label = nodeui.opt(s.label), unit = nodeui.opt(s.unit), min = s.min, max = s.max,
        warn = nodeui.opt(s.warn), alert = nodeui.opt(s.alert), low = s.low == 1 })
    end or nil)
  end,
}
