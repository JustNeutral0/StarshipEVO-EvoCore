-- plot.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "a line graph of the value over the last few seconds",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "value", "hide" },
  outs = {},
  settings = { label = "none", x = 10, y = 10, w = 200, h = 80, samples = 60, min = "auto", max = "auto", color = "theme", screen = 1 },
  optional = { "hide" },
  kinds = { hide = "onoff" },
  docs = {
    value = "the number to plot", hide = "on: not drawn (it keeps recording)",
    label = "the name in its corner", x = "left edge", y = "top edge", w = "width", h = "height",
    samples = "how many ticks it remembers (60 at the usual rate of 0.1 s is 6 seconds)",
    min = "the bottom line's value (auto: from the data)", max = "the top line's value (auto: from the data)",
    color = "the line's colour (theme: the usual green)",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    local h = state.history or {}
    state.history = h
    h[#h + 1] = i.value
    while #h > math.max(2, s.samples) do table.remove(h, 1) end
    nodeui.draw(state, s.screen, 1, i.hide <= 0.5 and function(d)
      ui.graph(d, s.x, s.y, s.w, s.h, h, { label = nodeui.opt(s.label), min = nodeui.opt(s.min), max = nodeui.opt(s.max),
        color = nodeui.opt(s.color) })
    end or nil)
  end,
}
