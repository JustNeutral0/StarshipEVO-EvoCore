-- average.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "the average, lowest and highest of x over the last few seconds",
  category = "Signals",
  ins = { "x" },
  outs = { "avg", "low", "high" },
  settings = { seconds = 5 },
  docs = {
    x = "the value to watch", seconds = "how far back it looks",
    avg = "the average", low = "the lowest", high = "the highest",
  },
  run = function(i, s, state)
    local now = time()
    local q = state.q or {}
    state.q = q
    q[#q + 1] = { now, i.x }
    while #q > 1 and q[1][1] < now - s.seconds do table.remove(q, 1) end
    local sum, lo, hi = 0, math.huge, -math.huge
    for _, p in ipairs(q) do sum = sum + p[2]; lo = math.min(lo, p[2]); hi = math.max(hi, p[2]) end
    return { avg = sum / #q, low = lo, high = hi }
  end,
}
