-- bus read.lua: a stock node (EvoCore; delete it to get the stock one back).
-- Reads named values off one of the ship's buses. Names nobody has put on it read 0.
return {
  help = "reads named values from a ship bus that any computer on this ship puts on with bus send",
  category = "Bus",
  ins = {},
  outs = { "ok", "$fields" },
  settings = { bus = "ship", fields = "a, b, c" },
  kinds = { ok = "onoff" },
  docs = {
    bus = "the bus's name, like engines or nav",
    fields = "the values to take, by name (commas between); each becomes an output",
    ok = "1 while the ship's buses work (a Mainframe is on and powered)",
  },
  run = function(i, s, state)
    local r = bus.read(tostring(s.bus))
    r.ok = bus.ok
    return r
  end,
}
