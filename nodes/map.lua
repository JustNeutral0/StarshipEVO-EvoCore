-- map.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "rescales x from one range to another: a 0-1 level into 0-100, a -1 to 1 stick into 0-1...",
  category = "Maths",
  ins = { "x" },
  outs = { "out" },
  settings = { from_lo = 0, from_hi = 1, to_lo = 0, to_hi = 100, clamp = 1 },
  choices = { clamp = { 0, 1 } },
  docs = {
    x = "the number to rescale", out = "x moved into the new range",
    from_lo = "the old range's bottom", from_hi = "the old range's top",
    to_lo = "what from_lo becomes", to_hi = "what from_hi becomes",
    clamp = "1: never goes past to_lo or to_hi",
  },
  run = function(i, s, state)
    local span = s.from_hi - s.from_lo
    local f = span ~= 0 and (i.x - s.from_lo) / span or 0
    if s.clamp == 1 then f = math.max(0, math.min(1, f)) end
    return s.to_lo + f * (s.to_hi - s.to_lo)
  end,
}
