-- unpack.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "unpacks a number from pack back into its four values (use the same lo, hi and steps)",
  category = "Encode",
  ins = { "in" },
  outs = { "a", "b", "c", "d" },
  settings = { lo = 0, hi = 1, steps = 1000 },
  docs = {
    ["in"] = "a number made by pack",
    lo = "pack's lo", hi = "pack's hi", steps = "pack's steps",
    a = "the first value", b = "the second", c = "the third", d = "the fourth",
  },
  run = function(i, s, state)
    local steps = math.max(1, math.min(5000, math.floor(s.steps)))
    local base = steps + 1
    local n = math.max(0, math.floor(i["in"] + 0.5))
    local r = {}
    for _, k in ipairs({ "a", "b", "c", "d" }) do
      r[k] = s.lo + (n % base) / steps * (s.hi - s.lo)
      n = math.floor(n / base)
    end
    return r
  end,
}
