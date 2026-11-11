-- unbundle.lua: a stock node (EvoCore; delete it to get the stock one back).
-- The other end of bundle: name the values you want in `fields` and the node grows an output for each. Names the
-- other computer doesn't send read 0; names it sends that aren't listed here are ignored.
return {
  help = "gets named values from another linked computer's bundle node",
  category = "Bus",
  ins = {},
  outs = { "linked", "$fields" },
  settings = { channel = "bus", fields = "a, b, c" },
  kinds = { linked = "onoff" },
  declares = { channels = "channel" },
  docs = {
    channel = "the channel's name; both computers use the same one",
    fields = "the values to take, by name (commas between); each becomes an output",
    linked = "1 while the channel is connected to the other computer",
  },
  run = function(i, s, state)
    local msg = ports.last(s.channel)
    local r = type(msg) == "table" and msg or {}
    r.linked = ports.linked(s.channel) ~= nil
    return r
  end,
}
