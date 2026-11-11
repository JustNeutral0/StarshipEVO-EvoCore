-- watch.lua: a stock node (EvoCore; delete it to get the stock one back).
return {
  help = "shows its input, under a name, in the game's info panel (top right) while the computer is picked with the link tool",
  category = "Signals",
  ins = { "value" },
  outs = {},
  settings = { name = "value" },
  docs = {
    value = "the value to show",
    name = "what to call it in the panel; two watch nodes with the same name share a line",
  },
  run = function(i, s, state)
    if i.value ~= state.last then
      state.last = i.value
      if watch then watch(tostring(s.name), i.value) end
    end
  end,
}
