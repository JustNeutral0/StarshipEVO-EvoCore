-- alert.lua: a stock Display node (EvoCore; delete it to get the stock one back).
return {
  help = "a flashing banner across the monitor while the value is on",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "value" },
  outs = {},
  settings = { text = "ALERT", y = 0, height = 22, color = "theme", screen = 1 },
  kinds = { value = "onoff" },
  docs = {
    value = "shown while above 0.5",
    text = "what it says", y = "its top", height = "how tall",
    color = "a colour name or #rrggbb (theme: red)",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    nodeui.draw(state, s.screen, 2, i.value > 0.5 and function(d)
      ui.alert(d, tostring(s.text), { y = s.y, height = s.height, color = nodeui.opt(s.color) })
    end or nil)
  end,
}
