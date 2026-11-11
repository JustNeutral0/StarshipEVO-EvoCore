-- overcharge.lua: a stock Upgrades node (EvoCore; delete it to get the stock one back).
-- Needs an Overcharge Chip on the computer's board (Hardware tab): each chip runs one overcharge node.
return {
  help = "pushes the thrusters in an output port past full power: wire your throttle in, name the port",
  category = "Upgrades",
  declares = { outputs = "port" },
  ins = { "throttle" },
  outs = { "boosting" },
  settings = { port = "thrusters", boost = 1.2 },
  kinds = { throttle = "level", boosting = "onoff" },
  docs = {
    throttle = "0 to 1, as you'd send the thrusters: full throttle becomes full times boost",
    boosting = "1 while it's pushing past full power (needs its chip, a thruster in the port, throttle above 0)",
    port = "the output port the thrusters are linked into (Links tab)",
    boost = "how far past full: 1.2 is 120%. One Overcharge Chip allows up to 1.2; each extra chip on the board adds 0.2",
  },
  run = function(i, s, state)
    local ok = upgrades and upgrades.overcharge(s.port, i.throttle, s.boost) or false
    return { boosting = (ok == true and i.throttle > 0) and 1 or 0 }
  end,
}
