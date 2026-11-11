-- pack.lua: a stock node (EvoCore; delete it to get the stock one back).
-- Four levels in one number, each rounded to `steps` steps between lo and hi. unpack with the same settings gets them
-- back. With steps 1000 each value keeps three decimals of its range.
return {
  help = "packs four values into one number to send down one wire or channel; unpack gets them back",
  category = "Encode",
  ins = { "a", "b", "c", "d" },
  outs = { "out" },
  settings = { lo = 0, hi = 1, steps = 1000 },
  docs = {
    a = "a value between lo and hi", b = "a value between lo and hi", c = "a value between lo and hi", d = "a value between lo and hi",
    lo = "the lowest value (lower is sent as lo)", hi = "the highest value (higher is sent as hi)",
    steps = "how finely each is kept (unpack needs the same lo, hi and steps; at most 5000, so four fit exactly)",
    out = "the packed number",
  },
  run = function(i, s, state)
    local steps = math.max(1, math.min(5000, math.floor(s.steps)))
    local base = steps + 1
    local span = s.hi - s.lo
    local n, mul = 0, 1
    for _, k in ipairs({ "a", "b", "c", "d" }) do
      local f = span ~= 0 and (i[k] - s.lo) / span or 0
      n = n + math.floor(math.max(0, math.min(1, f)) * steps + 0.5) * mul
      mul = mul * base
    end
    return n
  end,
}
