-- hold.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "remembers x when `take` turns on, and keeps giving it until the next take",
  category = "Signals",
  ins = { "x", "take" },
  outs = { "out" },
  settings = {},
  kinds = { take = "onoff" },
  docs = { x = "the value to remember", take = "turning on (above 0.5) stores x", out = "the stored value (0 before any)" },
  run = function(i, s, state)
    local on = i.take > 0.5
    if on and not state.was then state.v = i.x end
    state.was = on
    return state.v or 0
  end,
}
