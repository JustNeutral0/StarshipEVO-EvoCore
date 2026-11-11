-- ship os.lua: a stock node (EvoCore; delete it to get the stock one back).
-- The ship OS (lib\shipos.lua) as one node: the desktop, apps and crew assistant on the computer's monitors, with the
-- circuit able to raise an alert on it and to read whether one is showing. One per circuit: a second does nothing.
-- Touches reach it the way they reach the button node, through nodeui.touch.
function on_touch(x, y, screen, down, button) nodeui.touch(x, y, screen, down, button) end

return {
  help = "the ship OS on the monitors: desktop, apps and the crew assistant. Wire raise to put your own warning on it",
  category = "Ship OS",
  declares = { displays = "screen" },
  ins = { "raise" },
  outs = { "alert", "talking" },
  settings = { screen = "none", name = "HELM", crew = "IRIS", call = "Captain", style = "calm", alert_text = "CIRCUIT ALERT" },
  optional = { "raise" },
  kinds = { raise = "onoff", alert = "onoff", talking = "onoff" },
  choices = { style = { "calm", "casual" } },
  docs = {
    raise = "on: your alert (alert_text) goes on the status bar and the assistant says it",
    alert = "1 while any alert is showing, yours or the OS's own (low fuel, hull...)",
    talking = "1 while the assistant is saying something",
    screen = "none: every linked monitor. A number or a display port's name: only that one, leaving the rest for Display nodes",
    name = "the OS's name, shown on its boot screen",
    crew = "the assistant's name", call = "what it calls you", style = "how it talks: calm or casual",
    alert_text = "what your alert says",
  },
  run = function(i, s, state)
    if not shipos.node then
      shipos.node = state
      -- Touches go to the OS as well as to any button nodes.
      local buttons = nodeui.touch
      function nodeui.touch(x, y, screen, down, button)
        buttons(x, y, screen, down, button)
        shipos.touch(x, y, screen, down, button)
      end
      shipos.rule{ id = "circuit", level = "alert", label = tostring(s.alert_text), issue = tostring(s.alert_text),
        when = function() return state.raise end, say = function() return tostring(s.alert_text) end, again = 60 }
      -- shipos.run takes over the script's tick and on_touch; here the circuit keeps them and runs the OS itself.
      local circuit_tick, circuit_touch = tick, on_touch
      local only = nodeui.opt(s.screen)
      shipos.run{ name = tostring(s.name), displays = only and { only } or nil,
        crew = { name = tostring(s.crew), call = tostring(s.call), style = s.style } }
      tick, on_touch = circuit_tick, circuit_touch
    end
    if shipos.node ~= state then return { alert = false, talking = false } end
    state.raise = i.raise > 0.5
    shipos.tick(nodeui.dt(state))
    return { alert = shipos.alert ~= nil and shipos.alert() or state.raise, talking = shipos.talk.now ~= nil }
  end,
}
