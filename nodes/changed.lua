-- changed.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "1 for one tick whenever x changes (by more than `by`)",
  category = "Signals",
  ins = { "x" },
  outs = { "out", "up", "down" },
  settings = { by = 0 },
  kinds = { out = "onoff", up = "onoff", down = "onoff" },
  docs = {
    x = "the value to watch", by = "how much it must move to count (0: any change)",
    out = "1 on the tick it changed", up = "1 on the tick it went up", down = "1 on the tick it went down",
  },
  run = function(i, s, state)
    if state.last == nil then state.last = i.x end
    local diff = i.x - state.last
    local moved = math.abs(diff) > s.by
    if moved then state.last = i.x end
    return { out = moved, up = moved and diff > 0, down = moved and diff < 0 }
  end,
}
