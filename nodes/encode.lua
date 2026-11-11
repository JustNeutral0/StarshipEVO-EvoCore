-- encode.lua: a stock node (EvoCore; delete it to get the stock one back).
-- Eight on/off signals packed into one number, so one wire, port or channel carries all of them. decode unpacks it.
return {
  help = "packs eight on/off signals into one number (0 to 255); decode gets them back",
  category = "Encode",
  ins = { "b1", "b2", "b3", "b4", "b5", "b6", "b7", "b8" },
  outs = { "out" },
  settings = {},
  kinds = { b1 = "onoff", b2 = "onoff", b3 = "onoff", b4 = "onoff", b5 = "onoff", b6 = "onoff", b7 = "onoff", b8 = "onoff" },
  docs = {
    b1 = "on/off (adds 1)", b2 = "on/off (adds 2)", b3 = "on/off (adds 4)", b4 = "on/off (adds 8)",
    b5 = "on/off (adds 16)", b6 = "on/off (adds 32)", b7 = "on/off (adds 64)", b8 = "on/off (adds 128)",
    out = "the packed number: send it, then decode it on the other side",
  },
  run = function(i, s, state)
    local n, bit = 0, 1
    for k = 1, 8 do
      if i["b" .. k] > 0.5 then n = n + bit end
      bit = bit * 2
    end
    return n
  end,
}
