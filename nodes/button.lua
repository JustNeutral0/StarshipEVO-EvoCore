-- button.lua: a stock Display node (EvoCore; delete it to get the stock one back).
-- Touch: aim at the monitor and press the use key. It sets the script's on_touch, which only this node uses.
function on_touch(x, y, screen, down, button) nodeui.touch(x, y, screen, down, button) end

return {
  help = "a touch button on the monitor: aim at it and press use",
  category = "Display",
  declares = { displays = "screen" },
  ins = { "hide" },
  outs = { "on" },
  settings = { label = "BUTTON", mode = "hold", x = 10, y = 10, w = 80, h = 28, color = "theme", screen = 1 },
  optional = { "hide" },
  kinds = { hide = "onoff", on = "onoff" },
  choices = { mode = { "hold", "toggle", "pulse" } },
  docs = {
    hide = "on: not drawn, and can't be pressed",
    on = "hold: 1 while pressed; toggle: flips each press; pulse: 1 for one tick per press",
    label = "its text", mode = "hold, toggle or pulse",
    x = "left edge", y = "top edge", w = "width", h = "height",
    color = "a colour name or #rrggbb (theme: green)",
    screen = "which monitor: 1, 2... or a display port's name",
  },
  run = function(i, s, state)
    if not state.name then
      nodeui.buttons = (nodeui.buttons or 0) + 1
      state.name = "node button " .. nodeui.buttons
    end
    local presses = nodeui.presses[state.name] or 0
    local new = presses - (state.presses or 0)
    state.presses = presses
    local held = nodeui.held[state.name] == true
    local out, lit
    if s.mode == "toggle" then
      if new % 2 == 1 then state.on = not state.on end
      out, lit = state.on, state.on
    elseif s.mode == "pulse" then
      out, lit = new > 0, held
    else
      out, lit = held or new > 0, held
    end
    local hidden = i.hide > 0.5
    nodeui.draw(state, s.screen, 1, not hidden and function(d)
      d.button(state.name, s.x, s.y, s.w, s.h, tostring(s.label), nodeui.opt(s.color))
      if lit then ui.button(d, s.x, s.y, s.w, s.h, tostring(s.label), true, { color = nodeui.opt(s.color) }) end
    end or nil)
    return out and not hidden
  end,
}
