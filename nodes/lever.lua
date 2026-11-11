-- lever.lua: a stock node (EvoCore; delete it to get the stock one back).
-- What would take keys, counters, adds and a clamp: a value two keys (or buttons) push up and down.
return {
  help = "a value that `up` and `down` move while held: a throttle, a slider, a turret angle",
  category = "Shortcuts",
  ins = { "up", "down", "reset" },
  outs = { "out" },
  settings = { speed = 0.5, min = 0, max = 1, start = 0, spring = 0 },
  kinds = { up = "onoff", down = "onoff", reset = "onoff" },
  choices = { spring = { 0, 1 } },
  docs = {
    up = "raises it while on", down = "lowers it while on", reset = "puts it back to start",
    speed = "how much it moves per second",
    min = "the lowest it goes", max = "the highest it goes", start = "where it starts and resets to",
    spring = "1: goes back to start when nothing is held (a spring-loaded stick)",
    out = "the value",
  },
  run = function(i, s, state)
    local dt = nodeui.dt(state)
    local v = state.v or s.start
    local dir = (i.up > 0.5 and 1 or 0) - (i.down > 0.5 and 1 or 0)
    if i.reset > 0.5 then v = s.start
    elseif dir ~= 0 then v = v + dir * s.speed * dt
    elseif s.spring == 1 then
      local step = s.speed * dt
      if math.abs(v - s.start) <= step then v = s.start elseif v > s.start then v = v - step else v = v + step end
    end
    state.v = math.max(s.min, math.min(s.max, v))
    return state.v
  end,
}
