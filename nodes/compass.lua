-- compass.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "a heading tape with N E S W",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "heading", "hide" },
  outs = {},
  settings = { x = 10, y = 10, w = 220, h = 30, span = 90, screen = 1 },
  optional = { "hide" },
  kinds = { hide = "onoff" },
  docs = {
    heading = "degrees, 0 is north, clockwise", hide = "on: not drawn",
    x = "left edge", y = "top edge", w = "width", h = "height",
    span = "how many degrees fit across it",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    local heading = i.heading
    nodeui.draw(state, s.screen, 1, i.hide <= 0.5 and function(d)
      ui.compass(d, s.x, s.y, s.w, s.h, heading, { span = s.span })
    end or nil)
  end,
}
