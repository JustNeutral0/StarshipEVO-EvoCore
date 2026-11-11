-- rate.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "how fast x is changing, per second (fuel burn, closing speed, heating up)",
  category = "Signals",
  ins = { "x" },
  outs = { "out" },
  settings = {},
  docs = { x = "the value to watch", out = "change per second: + rising, - falling" },
  run = function(i, s, state)
    local dt = nodeui.dt(state)
    if state.last ~= nil and dt > 0 then state.rate = (i.x - state.last) / dt end
    state.last = i.x
    return state.rate or 0
  end,
}
