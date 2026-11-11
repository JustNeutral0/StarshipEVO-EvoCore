-- pid.lua: a stock node (EvoCore; delete it to get the stock one back).
-- A controller: hold a hover height, a speed, a turret on target. Start with p only, add d to stop overshoot, add a
-- little i to remove a steady error.
return {
  help = "pushes `value` towards `target`: the out goes to a thruster, hinge or rotor",
  category = "Shortcuts",
  ins = { "target", "value", "reset" },
  outs = { "out", "error" },
  settings = { p = 1, i = 0, d = 0, min = -1, max = 1 },
  kinds = { reset = "onoff" },
  docs = {
    target = "where you want it", value = "where it is now", reset = "forgets the built-up i part",
    p = "push in proportion to the error", i = "push to remove an error that stays", d = "brake as it closes in",
    min = "the lowest out", max = "the highest out",
    out = "the push", error = "target minus value",
  },
  run = function(i, s, state)
    local dt = nodeui.dt(state)
    local err = i.target - i.value
    if i.reset > 0.5 then state.sum = 0 end
    local sum = (state.sum or 0) + err * dt
    local deriv = (state.err ~= nil and dt > 0) and (err - state.err) / dt or 0
    state.err = err
    local out = s.p * err + s.i * sum + s.d * deriv
    -- stop the i part building up while the output is pinned at a limit
    if (out <= s.max and out >= s.min) or s.i == 0 then state.sum = sum end
    return { out = math.max(s.min, math.min(s.max, out)), error = err }
  end,
}
