-- panel.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "a framed box with a title strip, drawn behind the other Display nodes",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "hide" },
  outs = {},
  settings = { title = "none", x = 10, y = 10, w = 200, h = 100, color = "theme", screen = 1 },
  optional = { "hide" },
  kinds = { hide = "onoff" },
  docs = {
    hide = "on: not drawn",
    title = "the text in its top strip (none: no strip)",
    x = "left edge, canvas units (a monitor is 240 tall)", y = "top edge", w = "width", h = "height",
    color = "the corners and title colour (theme: the usual green)",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    nodeui.draw(state, s.screen, 0, i.hide <= 0.5 and function(d)
      ui.panel(d, s.x, s.y, s.w, s.h, nodeui.opt(s.title), { color = nodeui.opt(s.color) })
    end or nil)
  end,
}
