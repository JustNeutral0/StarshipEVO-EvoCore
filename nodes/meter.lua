-- meter.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "a labelled level bar in segments, turning amber and red past warn and alert",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "value", "hide" },
  outs = {},
  settings = { label = "none", x = 10, y = 10, w = 200, min = 0, max = 1, warn = "none", alert = "none", low = 0, screen = 1 },
  optional = { "hide" },
  kinds = { hide = "onoff" },
  choices = { low = { 0, 1 } },
  docs = {
    value = "the level", hide = "on: not drawn",
    label = "the name on its left",
    x = "left edge", y = "top edge (it is 24 tall)", w = "width",
    min = "the value for an empty bar", max = "the value for a full bar",
    warn = "amber past this (none: never)", alert = "red past this (none: never)",
    low = "1: low is bad (fuel), so warn and alert are below those values",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    local v = i.value
    nodeui.draw(state, s.screen, 1, i.hide <= 0.5 and function(d)
      ui.meter(d, s.x, s.y, s.w, v, nodeui.opt(s.label),
        { min = s.min, max = s.max, warn = nodeui.opt(s.warn), alert = nodeui.opt(s.alert), low = s.low == 1 })
    end or nil)
  end,
}
