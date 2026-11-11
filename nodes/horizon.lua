-- horizon.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "an artificial horizon: pitch and roll in a circle",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "pitch", "roll", "hide" },
  outs = {},
  settings = { x = 60, y = 60, r = 50, screen = 1 },
  optional = { "hide" },
  kinds = { hide = "onoff" },
  docs = {
    pitch = "degrees, nose up is +", roll = "degrees, right wing down is +", hide = "on: not drawn",
    x = "centre", y = "centre", r = "radius",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    local pitch, roll = i.pitch, i.roll
    nodeui.draw(state, s.screen, 1, i.hide <= 0.5 and function(d)
      ui.horizon(d, s.x, s.y, s.r, pitch, roll)
    end or nil)
  end,
}
