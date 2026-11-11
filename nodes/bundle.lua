-- bundle.lua: a stock node (EvoCore; delete it to get the stock one back).
-- Many named values down one link to another computer, whose unbundle node gets them back by name. Name the values
-- in `fields` and the node grows an input for each. Link the two computers, then pick the channel in the window
-- that comes up (or in the Links tab).
return {
  help = "sends named values to another linked computer; its unbundle node gets them back by name",
  category = "Bus",
  ins = { "$fields" },
  outs = { "linked" },
  settings = { channel = "bus", fields = "a, b, c" },
  kinds = { linked = "onoff" },
  declares = { channels = "channel" },
  docs = {
    channel = "the channel's name; both computers use the same one",
    fields = "the values to send, by name (commas between); each becomes an input",
    linked = "1 while the channel reaches the other computer",
  },
  run = function(i, s, state)
    -- sent when something changes, and every second anyway (the other computer may have just started)
    local changed = state.last == nil
    if not changed then for k, v in pairs(i) do if state.last[k] ~= v then changed = true; break end end end
    local now = time()
    if changed or now - (state.at or 0) >= 1 then
      state.linked = ports.send(s.channel, i)
      state.last, state.at = i, now
    end
    return { linked = state.linked }
  end,
}
