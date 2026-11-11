-- nodeui: what the Display nodes (UserData\EvoCore\nodes: panel, readout, meter, gauge, button...) share, so a circuit
-- can build a monitor screen without any typed Lua.
-- EvoCore's stock library (UserData\EvoCore\lib\nodeui.lua). Delete this file to get the stock one back.
--
-- How a frame works: each Display node hands nodeui.draw its drawing instead of drawing straight away. Once every
-- Display node in the circuit has had its turn this tick, the screens they use are cleared and the drawings run, lowest
-- layer first (panels are layer 0, so they sit behind what's on them), then in node order. So nodes can be added in
-- any order, and one node clearing the screen never wipes another's drawing.
-- A circuit's lua node that draws on the same monitor gets wiped each tick: give it a monitor of its own.
nodeui = { queue = {}, seen = {}, expected = 0, looks = {}, held = {}, presses = {} }

-- A setting of none, auto or theme (or blank) means "the default". (A blank default would make the circuit compiler
-- ask for the setting, so the node files use these words.)
local defaults = { [""] = true, none = true, auto = true, theme = true }
function nodeui.opt(v) if defaults[v] then return nil end return v end

-- The display a node's `screen` setting names: a number (link order, or declared display ports) or a port name.
function nodeui.screen(which)
  which = nodeui.opt(which) or 1
  if which == 1 and display then return display end
  if displays then return displays.get(which) end
  return display
end

-- Queue one node's drawing. state: the node's memory (one per node), screen: its `screen` setting, layer: 0 behind,
-- 1 normal, 2 on top; fn(d): the drawing, or nil when the node is hidden this tick (it still counts towards the frame).
function nodeui.draw(state, screen, layer, fn)
  if nodeui.seen[state] then nodeui.flush() end   -- this node already had its turn: a new tick has started
  nodeui.seen[state] = true
  local q = nodeui.queue
  q[#q + 1] = { layer = layer or 1, n = #q, screen = nodeui.opt(screen) or 1, fn = fn }
  if #q == nodeui.expected then nodeui.flush() end
end

function nodeui.flush()
  local q = nodeui.queue
  nodeui.expected = #q
  nodeui.queue, nodeui.seen = {}, {}
  local cleared = {}
  for _, item in ipairs(q) do
    if not cleared[item.screen] then
      cleared[item.screen] = true
      local d = nodeui.screen(item.screen)
      if d and d.present then
        local look = nodeui.looks[item.screen]
        if look and look.background then d.clear(look.background) else d.clear() end
      end
    end
  end
  table.sort(q, function(a, b) if a.layer ~= b.layer then return a.layer < b.layer end return a.n < b.n end)
  for _, item in ipairs(q) do
    if item.fn then
      local d = nodeui.screen(item.screen)
      if d and d.present then item.fn(d) end
    end
  end
end

-- Touch buttons (the button node): which are held, and how many presses each has had.
function nodeui.touch(x, y, screen, down, button)
  if not down then
    for name in pairs(nodeui.held) do nodeui.held[name] = false end   -- let go anywhere: nothing is held
  elseif button then
    nodeui.held[button] = true
    nodeui.presses[button] = (nodeui.presses[button] or 0) + 1
  end
end

-- A number as text: decimals places, or a percent when percent is 1.
function nodeui.format(v, decimals, percent)
  if percent == 1 then return string.format("%." .. (decimals or 0) .. "f%%", v * 100) end
  return string.format("%." .. (decimals or 0) .. "f", v)
end

-- Seconds since the node last ran (the first time: 0). Nodes don't get the circuit's dt, so they time themselves.
function nodeui.dt(state)
  local now = time()
  local dt = state.last_time and (now - state.last_time) or 0
  state.last_time = now
  return dt
end
