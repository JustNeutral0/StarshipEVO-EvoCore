-- smooth.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "follows x gently instead of jumping: for steadier needles, softer lights, eased controls",
  category = "Signals",
  ins = { "x" },
  outs = { "out" },
  settings = { seconds = 0.5 },
  docs = {
    x = "the value to follow", out = "x, smoothed",
    seconds = "about how long it takes to catch up (0: no smoothing)",
  },
  run = function(i, s, state)
    local dt = nodeui.dt(state)
    if state.v == nil or s.seconds <= 0 then state.v = i.x
    else state.v = state.v + (i.x - state.v) * (1 - math.exp(-dt * 3 / s.seconds)) end
    return state.v
  end,
}
