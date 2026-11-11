-- deadzone.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "ignores small wobbles around 0 (a joystick at rest), then rescales so the full range still reaches 1",
  category = "Maths",
  ins = { "x" },
  outs = { "out" },
  settings = { size = 0.1 },
  docs = { x = "-1 to 1", size = "how much around 0 counts as 0", out = "0 inside the dead zone, else -1 to 1" },
  run = function(i, s, state)
    local a = math.abs(i.x)
    if a <= s.size or s.size >= 1 then return 0 end
    local f = math.min(1, (a - s.size) / (1 - s.size))
    return i.x < 0 and -f or f
  end,
}
