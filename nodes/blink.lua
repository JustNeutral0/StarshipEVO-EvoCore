-- blink.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "blinks on and off while `in` is on: warning lights, beacons",
  category = "Shortcuts",
  ins = { "in" },
  outs = { "out" },
  settings = { on = 0.5, off = 0.5 },
  kinds = { ["in"] = "onoff", out = "onoff" },
  docs = { ["in"] = "blinks while above 0.5", on = "seconds lit", off = "seconds dark", out = "1 or 0" },
  run = function(i, s, state)
    if i["in"] <= 0.5 then state.start = nil; return 0 end
    local now = time()
    state.start = state.start or now
    local period = math.max(0.05, s.on + s.off)
    return (now - state.start) % period < s.on
  end,
}
