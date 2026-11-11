-- bus send.lua: a stock node (EvoCore; delete it to get the stock one back).
-- Puts named values on one of the ship's buses, where any computer on the ship can read them with bus read: no links.
-- The ship's Mainframe hosts the buses, so it needs one that's running and powered.
return {
  help = "puts named values on a ship bus; any computer on this ship reads them with bus read",
  category = "Bus",
  ins = { "$fields" },
  outs = { "ok" },
  settings = { bus = "ship", fields = "a, b, c" },
  kinds = { ok = "onoff" },
  docs = {
    bus = "the bus's name, like engines or nav",
    fields = "the values to put on it, by name (commas between); each becomes an input",
    ok = "1 while the ship's buses work (a Mainframe is on and powered)",
  },
  run = function(i, s, state)
    local changed = state.last == nil
    if not changed then for k, v in pairs(i) do if state.last[k] ~= v then changed = true; break end end end
    local now = time()
    if changed or now - (state.at or 0) >= 1 then
      if bus.publish(tostring(s.bus), i) then state.last, state.at = i, now end
    end
    return { ok = bus.ok }
  end,
}
