-- select.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "passes on one of four inputs, picked by number: a page switch, a mode dial",
  category = "Signals",
  ins = { "pick", "in1", "in2", "in3", "in4" },
  outs = { "out" },
  settings = {},
  docs = {
    pick = "which input: 1 to 4 (rounded; anything else gives 0)",
    in1 = "given when pick is 1", in2 = "given when pick is 2", in3 = "given when pick is 3", in4 = "given when pick is 4",
    out = "the picked input",
  },
  run = function(i, s, state)
    local n = math.floor(i.pick + 0.5)
    return i["in" .. n] or 0
  end,
}
