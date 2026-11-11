-- decode.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "unpacks a number from encode back into eight on/off signals",
  category = "Encode",
  ins = { "in" },
  outs = { "b1", "b2", "b3", "b4", "b5", "b6", "b7", "b8" },
  settings = {},
  kinds = { b1 = "onoff", b2 = "onoff", b3 = "onoff", b4 = "onoff", b5 = "onoff", b6 = "onoff", b7 = "onoff", b8 = "onoff" },
  docs = {
    ["in"] = "a number made by encode (0 to 255)",
    b1 = "on when 1 is in it", b2 = "on when 2 is in it", b3 = "on when 4 is in it", b4 = "on when 8 is in it",
    b5 = "on when 16 is in it", b6 = "on when 32 is in it", b7 = "on when 64 is in it", b8 = "on when 128 is in it",
  },
  run = function(i, s, state)
    local n = math.floor(i["in"] + 0.5)
    local r = {}
    for k = 1, 8 do
      r["b" .. k] = n % 2 == 1
      n = math.floor(n / 2)
    end
    return r
  end,
}
