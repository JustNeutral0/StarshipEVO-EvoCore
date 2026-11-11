-- screen.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "sets how a monitor looks for the Display nodes: background colour, CRT lines and glow",
  category = "Display",
  declares = { displays = "screen" },
  ins = {},
  outs = {},
  settings = { screen = 1, background = "none", crt = 0, glow = 1 },
  choices = { crt = { 0, 1 } },
  docs = {
    screen = "which monitor: 1 for the first linked, 2 the second..., or a display port's name",
    background = "a colour name or #rrggbb filled behind everything; none: the monitor's own see-through tint",
    crt = "1 for the old-screen look (scanlines, bent edges)",
    glow = "how bright the glass is: about 1 plain, up to 4",
  },
  run = function(i, s, state)
    local key = nodeui.opt(s.screen) or 1
    nodeui.looks[key] = { background = nodeui.opt(s.background) }
    local d = nodeui.screen(key)
    if d and d.present and not state.set then
      d.crt = s.crt == 1
      d.glow = s.glow
      state.set = true
    end
  end,
}
