-- shipos: a small ship operating system for a Lua Computer and its monitors. It boots, puts a status bar and a row
-- of app tabs on every linked monitor, raises alerts, and has a crew assistant: a scripted NPC (no real AI) who
-- watches the ship and speaks up. Start it from a script with
--   shipos.run{ crew = { name = "IRIS", style = "calm" } }
-- Add your own apps with shipos.app{...} and your own assistant rules with shipos.rule{...} before shipos.run.
-- Needs ui.lua (the stock lib). EvoCore's stock library (UserData\EvoCore\lib\shipos.lua): edit away; delete this
-- file to get the stock one back.
shipos = { name = "HELM", version = "0.1" }

local apps, order = {}, {}      -- id -> app, ids in tab order
local rules = {}                -- assistant rules, checked every tick after boot
local screens = {}              -- per display: { key, app, page }
local logs = {}                 -- { at, text, level }
local voices = {}
local cfg = {}
local boot_at, booted = 0, false
local s = {}                    -- this tick's readings (shipos.state)
shipos.state = s

local BOOT = 3.5                -- seconds of boot screen
local TOP, TABS = 18, 28        -- status bar and tab row heights

local function th() return ui.theme end
local function opt(o, k, v) if o ~= nil and o[k] ~= nil then return o[k] end return v end
local function pct(v) return string.format("%.0f%%", (v or 0) * 100) end

local function clock(seconds)
  seconds = math.max(0, math.floor(seconds))
  local h, m, sec = math.floor(seconds / 3600), math.floor(seconds / 60) % 60, seconds % 60
  if h > 0 then return string.format("%d:%02d:%02d", h, m, sec) end
  return string.format("%02d:%02d", m, sec)
end

local function distance(m)
  if not m then return "-" end
  if m >= 100000 then return string.format("%.0f km", m / 1000) end
  if m >= 1000 then return string.format("%.1f km", m / 1000) end
  return string.format("%.0f m", m)
end

-- s cut to fit w canvas units, with ".." when cut.
local function fit(d, str, size, w, font)
  str = tostring(str)
  if d.measure(str, size, font) <= w then return str end
  while #str > 1 and d.measure(str .. "..", size, font) > w do str = str:sub(1, -2) end
  return str .. ".."
end

-- Lines of text that fit w, broken at spaces.
local function wrap(d, str, size, w, font)
  local lines, line = {}, ""
  for word in tostring(str):gmatch("%S+") do
    local try = line == "" and word or line .. " " .. word
    if line ~= "" and d.measure(try, size, font) > w then lines[#lines + 1] = line; line = word else line = try end
  end
  if line ~= "" then lines[#lines + 1] = line end
  return lines
end

shipos.fit, shipos.wrap, shipos.distance, shipos.clock = fit, wrap, distance, clock

-- Log ----------------------------------------------------------------------------------------------------------------

function shipos.log(text, level)
  logs[#logs + 1] = { at = time() - boot_at, text = tostring(text), level = level or "info" }
  while #logs > 60 do table.remove(logs, 1) end
end

local function level_color(level)
  local t = th()
  if level == "alert" then return t.alert elseif level == "warn" then return t.warn elseif level == "good" then return t.fg end
  return t.text
end

-- Crew assistant -----------------------------------------------------------------------------------------------------
-- A scripted NPC: it reads the ship every tick, and rules decide when it speaks. Lines come from its voice (a style),
-- with {fuel}, {hull}, {planet}... filled in. One line at a time, typed out, then held a few seconds.

local talk = { queue = {}, now = nil, shown = 0, done_at = 0, history = {} }
shipos.talk = talk

-- Voices: each key is a list of lines, one picked at random. {call} is how it addresses you (cfg.crew.call).
voices.calm = {
  boot = { "{name} online. {status}", "Systems up. {status}", "Good to see you, {call}. {status}" },
  board = { "Welcome aboard, {call}.", "{call} on board.", "Good to have you back, {call}." },
  nominal = { "All systems within limits.", "Everything looks good from here.", "No problems to report." },
  report = { "Fuel {fuel}, power {power}, hull {hull}, shields {shield}. {status}" },
  where_space = { "We're in open space, moving at {speed}.", "Deep space, {speed}. {target_line}" },
  where_planet = { "We're near {planet}, {altitude} up, moving at {speed}.", "{planet} below us at {altitude}. Speed {speed}." },
  fuel_low = { "Fuel is down to {fuel}. We should refuel soon.", "Fuel at {fuel}, {call}. Worth planning a stop." },
  fuel_critical = { "Fuel critical: {fuel}. We won't get far.", "{call}, fuel is almost gone: {fuel}." },
  power_low = { "Power reserves at {power}.", "Batteries are getting low: {power}." },
  stalled = { "We've lost power! Systems are stalling.", "Power's out, {call}. Everything's on standby." },
  power_back = { "Power is back.", "We have power again." },
  hull_damaged = { "Hull integrity at {hull}.", "We've taken damage: hull at {hull}." },
  hull_critical = { "Hull critical at {hull}! Get us out of here.", "{call}, the hull won't take much more: {hull}." },
  repaired = { "Hull is back above {hull}.", "Repairs are holding: hull at {hull}." },
  shield_down = { "Shields are down.", "We've lost our shields." },
  shield_up = { "Shields are up.", "Shields back online." },
  heat_high = { "We're running hot: heat at {heat}.", "Heat is at {heat}. Ease off a little." },
  contact = { "New contact: {target}, {target_dist} out.", "Target locked: {target} at {target_dist}." },
  arrive = { "We're coming up on {planet}.", "{planet} ahead." },
  warp = { "Warp engaged.", "Going to warp." },
  idle = { "Quiet out here.", "All quiet, {call}.", "Holding steady." },
  overload = { "The mainframe is over capacity. Programs are slowing down; another server rack would help.", "I'm running at {load}. Things will be slower until the load drops." },
  load_ok = { "Mainframe load is back to normal.", "Load's down again: {load}." },
}
voices.casual = {
  boot = { "Hey, {name} here. {status}", "Morning! {status}", "I'm up, I'm up. {status}" },
  board = { "Oh hey, you're back!", "Welcome home, {call}.", "There you are." },
  nominal = { "Everything's fine. Really.", "All good on my end.", "Nothing's on fire." },
  report = { "Fuel {fuel}, power {power}, hull {hull}, shields {shield}. {status}" },
  where_space = { "Middle of nowhere, doing {speed}.", "Out in the black, {speed}. {target_line}" },
  where_planet = { "Over {planet}, {altitude} up, doing {speed}.", "{planet}'s right there. {altitude} up." },
  fuel_low = { "Uh, fuel's at {fuel}. Just saying.", "We're getting thirsty: fuel {fuel}." },
  fuel_critical = { "Fuel {fuel}! We're running on fumes!", "{call}. Fuel. {fuel}. Please." },
  power_low = { "Power's dipping: {power}.", "Batteries at {power}, might want to look at that." },
  stalled = { "Lights out! We've got no power!", "Aaand we're dark. No power." },
  power_back = { "Power's back, phew.", "And we're back!" },
  hull_damaged = { "Ow. Hull at {hull}.", "That hurt: hull at {hull}." },
  hull_critical = { "Hull {hull}! Get us out of here!", "We're falling apart, {call}! Hull {hull}!" },
  repaired = { "Patched up: hull at {hull}.", "Much better. Hull {hull}." },
  shield_down = { "Shields are gone!", "No more shields, careful." },
  shield_up = { "Shields up.", "Shields are back, nice." },
  heat_high = { "It's getting toasty: heat {heat}.", "Heat {heat}, I'm sweating here." },
  contact = { "Ooh, contact: {target}, {target_dist} out.", "Got something: {target} at {target_dist}." },
  arrive = { "Hey look, {planet}.", "{planet}! Pretty." },
  warp = { "Hold on to something!", "Warp! Wheee." },
  idle = { "So... nice weather in space.", "Bored. Just letting you know.", "Still here." },
  overload = { "I'm thinking too hard: load {load}. Get me another rack?", "Brain's full. Load {load}, things will lag." },
  load_ok = { "Phew, load's back down.", "Thinking clearly again: load {load}." },
}
shipos.voices = voices

-- The text a voice line becomes, from the current readings.
local function status_line()
  local issues = {}
  for _, r in ipairs(rules) do
    if r.active and r.issue then issues[#issues + 1] = r.issue end
  end
  if #issues == 0 then
    local v = voices[opt(cfg.crew, "style", "calm")] or voices.calm
    return v.nominal[math.random(#v.nominal)]
  end
  return "Watch: " .. table.concat(issues, ", ") .. "."
end

local function fill(text)
  local crew = cfg.crew or {}
  local vals = {
    name = crew.name or "IRIS", call = crew.call or "Captain", ship = s.name ~= "" and s.name or "the ship",
    fuel = pct(s.fuel), power = pct(s.power), hull = pct(s.hull), shield = s.shield_on and pct(s.shield) or "down",
    heat = pct(s.heat), speed = string.format("%.0f m/s", s.speed or 0),
    planet = s.planet ~= "" and s.planet or "open space", altitude = distance(s.altitude),
    target = s.target and s.target.name or "nothing", target_dist = s.target and distance(s.target.distance) or "-",
    load = mainframe and pct(mainframe.load) or "-",
  }
  vals.target_line = s.target and ("Tracking " .. vals.target .. " at " .. vals.target_dist .. ".") or ""
  text = text:gsub("{status}", function() return status_line() end)
  text = text:gsub("{([%w_]+)}", function(k) return vals[k] or "{" .. k .. "}" end)
  return (text:gsub("%s+$", ""))
end

-- Say a line now (after what's queued). text is said as is; shipos.speak(key) picks one from the voice.
function shipos.say(text, level)
  for _, q in ipairs(talk.queue) do if q.text == text then return end end
  if #talk.queue >= 4 then table.remove(talk.queue, 1) end
  talk.queue[#talk.queue + 1] = { text = text, level = level or "info" }
end

function shipos.speak(key, level)
  local v = voices[opt(cfg.crew, "style", "calm")] or voices.calm
  local lines = v[key] or voices.calm[key]
  if not lines then return end
  -- An urgent line jumps the queue.
  local line = { text = fill(lines[math.random(#lines)]), level = level or "info" }
  if level == "alert" then table.insert(talk.queue, 1, line) else shipos.say(line.text, line.level) end
end

local function talk_tick(dt)
  local now = time()
  if talk.now and talk.shown < #talk.now.text then
    talk.shown = math.min(#talk.now.text, talk.shown + dt * 45)
    if talk.shown >= #talk.now.text then talk.done_at = now end
  elseif talk.now and now - talk.done_at > (#talk.queue > 0 and 2 or opt(cfg.crew, "hold", 6)) then
    talk.now = nil
  end
  if not talk.now and #talk.queue > 0 then
    talk.now = table.remove(talk.queue, 1)
    talk.shown = 0
    talk.history[#talk.history + 1] = { at = now - boot_at, text = talk.now.text, level = talk.now.level }
    while #talk.history > 20 do table.remove(talk.history, 1) end
    shipos.log(opt(cfg.crew, "name", "IRIS") .. ": " .. talk.now.text, talk.now.level)
  end
end

-- A rule: when(s) true starts it (said once), clear(s) true ends it (default: when it stops being true). again =
-- seconds before it's said again while still on. issue = a few words for status reports while it's on. label = the
-- status bar text for alert-level rules. say = a voice key, or a function(s) returning text; level info/warn/alert.
function shipos.rule(r)
  for i, old in ipairs(rules) do if old.id == r.id then rules[i] = r; return r end end
  rules[#rules + 1] = r
  return r
end

local function check_rules()
  local now = time()
  for _, r in ipairs(rules) do
    local ok, on = pcall(r.when, s)
    if ok then
      if not r.active and on then
        r.active, r.said = true, now
        if r.say then
          if type(r.say) == "function" then shipos.say(r.say(s), r.level) else shipos.speak(r.say, r.level) end
        end
      elseif r.active then
        local cleared = not on
        if r.clear then
          local ok2, c = pcall(r.clear, s)
          cleared = ok2 and c
        end
        if cleared then
          r.active = false
          if r.after then shipos.speak(r.after, "good") end
        elseif r.again and now - r.said > r.again then
          r.said = now
          if type(r.say) == "function" then shipos.say(r.say(s), r.level) else shipos.speak(r.say, r.level) end
        end
      end
    end
  end
end

-- The stock rules. Thresholds have a gap between on and off so a value sitting on the line doesn't chatter.
local function stock_rules()
  shipos.rule{ id = "fuel_critical", level = "alert", say = "fuel_critical", again = 90, issue = "fuel critical", label = "FUEL CRITICAL",
    when = function(s) return s.fuel_capacity > 0 and s.fuel < 0.08 end, clear = function(s) return s.fuel > 0.12 end }
  shipos.rule{ id = "fuel_low", level = "warn", say = "fuel_low", issue = "fuel low",
    when = function(s) return s.fuel_capacity > 0 and s.fuel < 0.25 and s.fuel >= 0.08 end, clear = function(s) return s.fuel > 0.3 or s.fuel < 0.08 end }
  shipos.rule{ id = "stalled", level = "alert", say = "stalled", after = "power_back", issue = "no power", label = "NO POWER",
    when = function(s) return s.stalled end }
  shipos.rule{ id = "power_low", level = "warn", say = "power_low", issue = "power low",
    when = function(s) return s.power_capacity > 0 and s.power < 0.2 and not s.stalled end, clear = function(s) return s.power > 0.3 or s.stalled end }
  shipos.rule{ id = "hull_critical", level = "alert", say = "hull_critical", again = 45, issue = "hull critical", label = "HULL CRITICAL",
    when = function(s) return s.hull_max > 0 and s.hull < 0.35 end, clear = function(s) return s.hull > 0.45 end }
  shipos.rule{ id = "hull_damaged", level = "warn", say = "hull_damaged", after = "repaired", issue = "hull damaged",
    when = function(s) return s.hull_max > 0 and s.hull < 0.75 end, clear = function(s) return s.hull > 0.85 end }
  shipos.rule{ id = "shield_down", level = "warn", say = "shield_down", after = "shield_up", issue = "shields down",
    when = function(s) return s.had_shield and not s.shield_on end }
  shipos.rule{ id = "heat_high", level = "alert", say = "heat_high", again = 60, issue = "running hot", label = "OVERHEATING",
    when = function(s) return s.heat > 0.85 end, clear = function(s) return s.heat < 0.7 end }
  shipos.rule{ id = "contact", level = "info", say = "contact",
    when = function(s) return s.target ~= nil end, clear = function(s) return s.target == nil or s.target.name ~= s.contact_name end }
  shipos.rule{ id = "arrive", level = "info", say = "arrive",
    when = function(s) return s.planet ~= "" end, clear = function(s) return s.planet ~= s.arrived_at end }
  shipos.rule{ id = "warp", level = "info", say = "warp", when = function(s) return s.warping end }
  shipos.rule{ id = "board", level = "info", say = "board", when = function(s) return s.aboard end }
  if mainframe then
    shipos.rule{ id = "overload", level = "warn", say = "overload", after = "load_ok", issue = "mainframe overloaded",
      when = function(s) return mainframe.load > 1.05 end, clear = function(s) return mainframe.load < 0.9 end }
  end
end

-- Readings ------------------------------------------------------------------------------------------------------------

local function rule_active(id)
  for _, r in ipairs(rules) do if r.id == id then return r.active end end
  return false
end
shipos.active = rule_active

local function read_ship()
  s.name = ship.name or ""
  s.speed = ship.speed or 0
  s.fuel, s.fuel_capacity = ship.fuel or 0, ship.fuel_capacity or 0
  s.power, s.power_capacity = ship.power or 0, ship.power_capacity or 0
  s.power_in, s.power_use = ship.power_in or 0, ship.power_use or 0
  s.stalled = ship.stalled
  s.heat = ship.heat or 0
  s.hull, s.hull_max = ship.hull or 0, ship.hull_max or 0
  s.shield, s.shield_on = ship.shield or 0, ship.shield_on
  if s.shield_on then s.had_shield = true end
  s.warping, s.cruising = ship.warping, ship.cruising
  s.planet, s.altitude = ship.planet or "", ship.altitude
  if s.planet == "" then s.altitude = nil end
  local t = ship.target
  s.target = t and { name = t.name, distance = t.distance, speed = t.speed } or nil
  -- The contact and arrival rules remember what they last spoke about, so a new one counts as new.
  if s.target and not rule_active("contact") then s.contact_name = s.target.name end
  if s.planet ~= "" and not rule_active("arrive") then s.arrived_at = s.planet end
  s.aboard = player ~= nil and player.aboard or false
end

-- The highest alert-level rule that's on (for the status bar), or nil.
local function top_alert()
  for _, r in ipairs(rules) do
    if r.active and r.level == "alert" and r.label then return r end
  end
  return nil
end

-- Apps ----------------------------------------------------------------------------------------------------------------
-- app = { id, title, short (tab label on narrow screens), draw(d, x, y, w, h, scr), touch(button, x, y, scr), tick(dt) }
-- draw gets the content area; buttons it makes should have names unique to the app (they come back to touch).

function shipos.app(app)
  if not apps[app.id] then order[#order + 1] = app.id end
  apps[app.id] = app
  return app
end

-- Status: the four levels as rings, heat and power flow below.
shipos.app{ id = "status", title = "STATUS", short = "STA",
  draw = function(d, x, y, w, h)
    local t = th()
    local n = 4
    local r = math.min((w / n) * 0.36, h * 0.24)
    local cy = y + r + 6
    local rings = {
      { s.fuel, "FUEL", { warn = 0.25, alert = 0.08, low = true } },
      { s.power, "POWER", { warn = 0.2, alert = 0.05, low = true } },
      { s.hull, "HULL", { warn = 0.75, alert = 0.35, low = true } },
      { s.shield_on and s.shield or 0, s.shield_on and "SHIELD" or "SHLD OFF", { warn = 0.3, low = true } },
    }
    for i, ring in ipairs(rings) do
      ui.ring(d, x + w * (i - 0.5) / n, cy, r, ring[1], { label = ring[2], warn = ring[3].warn, alert = ring[3].alert, low = true })
    end
    local my = cy + r + 12
    ui.meter(d, x + 4, my, w - 8, s.heat, "HEAT", { warn = 0.7, alert = 0.85 })
    local flow = s.power_in - s.power_use
    ui.rows(d, x + 4, my + 32, w - 8, {
      { "POWER IN / USE", string.format("%.0f / %.0f", s.power_in, s.power_use) },
      { "NET", string.format("%+.0f", flow) },
    }, { size = 11 })
  end }

-- Nav: speed, where we are, the target, a speed history.
local speed_hist, hist_t = {}, 0
shipos.app{ id = "nav", title = "NAVIGATION", short = "NAV",
  tick = function(dt)
    hist_t = hist_t + dt
    if hist_t >= 0.5 then hist_t = 0; speed_hist = ui.history("shipos_speed", s.speed, 60) end
  end,
  draw = function(d, x, y, w, h)
    local t = th()
    local r = math.min(h * 0.3, w * 0.2)
    ui.gauge(d, x + r + 6, y + r + 4, r, s.speed, { max = math.max(100, math.ceil(s.speed / 100) * 100), format = "%.0f", label = "M/S" })
    local rx, rw = x + 2 * r + 20, w - 2 * r - 24
    local mode = s.warping and "WARP" or s.cruising and "CRUISE" or "NORMAL"
    ui.rows(d, rx, y + 2, rw, {
      { "LOCATION", s.planet ~= "" and fit(d, s.planet, 11, rw * 0.6) or "open space" },
      { "ALTITUDE", s.altitude and distance(s.altitude) or "-" },
      { "DRIVE", mode },
      { "TARGET", s.target and fit(d, s.target.name, 11, rw * 0.6) or "none" },
      { "RANGE", s.target and distance(s.target.distance) or "-" },
    }, { size = 11 })
    local gy = y + 2 * r + 14
    if h - (gy - y) > 24 then ui.graph(d, x + 2, gy, w - 4, h - (gy - y) - 2, speed_hist, { min = 0, label = "SPEED" }) end
  end }

-- Systems: the ship's labelled logic blocks as switches.
local sys_list, sys_t = {}, -10
local function refresh_systems()
  sys_list = {}
  local found = ship.blocks()
  if found then for _, b in ipairs(found) do sys_list[#sys_list + 1] = b end end
end
shipos.app{ id = "systems", title = "SYSTEMS", short = "SYS",
  tick = function()
    if time() - sys_t > 3 then sys_t = time(); refresh_systems() end
  end,
  draw = function(d, x, y, w, h, scr)
    local t = th()
    if #sys_list == 0 then
      local lines = wrap(d, "No labelled blocks. Name doors, lights and switches with the multitool's Tag mode and they show up here.", 11, w - 16)
      for i, l in ipairs(lines) do d.text(x + w / 2, y + 10 + (i - 1) * 15, l, t.muted, 11, "center") end
      return
    end
    local cols = w >= 280 and 2 or 1
    local bh, gap = 24, 4
    local rows = math.max(1, math.floor((h - 26) / (bh + gap)))
    local per = rows * cols
    local pages = math.max(1, math.ceil(#sys_list / per))
    scr.page = math.min(scr.page or 1, pages)
    local bw = (w - (cols - 1) * gap) / cols
    for k = 1, per do
      local i = (scr.page - 1) * per + k
      local b = sys_list[i]
      if not b then break end
      local col, row = (k - 1) % cols, math.floor((k - 1) / cols)
      local on = b.on
      local label = fit(d, b.name, 11, bw - 40)
      d.button("sys:" .. i, x + col * (bw + gap), y + row * (bh + gap), bw, bh, label, on and t.fg or t.muted)
      d.text(x + col * (bw + gap) + bw - 6, y + row * (bh + gap) + 6, on and "ON" or "OFF", on and t.fg or t.muted, 10, "right")
    end
    local py = y + h - 22
    if pages > 1 then
      d.button("sys_prev", x, py, 40, 20, "<", t.accent)
      d.button("sys_next", x + w - 40, py, 40, 20, ">", t.accent)
    end
    d.text(x + w / 2, py + 4, string.format("%d BLOCKS  %d/%d", #sys_list, scr.page, pages), t.muted, 10, "center")
  end,
  touch = function(button, x, y, scr)
    if button == "sys_prev" then scr.page = math.max(1, (scr.page or 1) - 1)
    elseif button == "sys_next" then scr.page = (scr.page or 1) + 1
    else
      local i = tonumber(button:match("^sys:(%d+)$"))
      local b = i and sys_list[i]
      if b then b.toggle(); shipos.log((b.on and "ON  " or "OFF ") .. b.name) end
    end
  end }

-- Server: the mainframe's racks, load and power draw (only on a mainframe; `mainframe` is nil elsewhere).
local load_hist, load_t = {}, 0
shipos.app{ id = "server", title = "SERVER", short = "SRV",
  tick = function(dt)
    load_t = load_t + dt
    if mainframe and load_t >= 0.5 then load_t = 0; load_hist = ui.history("shipos_load", mainframe.load, 60) end
  end,
  draw = function(d, x, y, w, h)
    local t = th()
    if not mainframe then
      d.text(x + w / 2, y + 20, "Not a mainframe", t.muted, 12, "center")
      return
    end
    local r = math.min(h * 0.28, w * 0.18)
    ui.gauge(d, x + r + 6, y + r + 4, r, mainframe.load * 100, { max = 150, format = "%.0f", unit = "%", label = "LOAD", warn = 80, alert = 100 })
    local rx, rw = x + 2 * r + 20, w - 2 * r - 24
    ui.rows(d, rx, y + 2, rw, {
      { "RACKS", tostring(mainframe.racks) },
      { "CAPACITY", string.format("%.0fk/s", mainframe.capacity / 1000) },
      { "IN USE", string.format("%.0fk/s", mainframe.steps / 1000) },
      { "POWER", string.format("%.1f e/s", mainframe.draw) },
      { "TERMINALS", tostring(mainframe.terminals) },
    }, { size = 11 })
    local gy = y + 2 * r + 14
    if h - (gy - y) > 24 then ui.graph(d, x + 2, gy, w - 4, h - (gy - y) - 2, load_hist, { min = 0, max = 1.5, label = "LOAD" }) end
  end }

-- The assistant's face: a ring head, eyes that blink, a mouth that moves while it talks.
function shipos.portrait(d, x, y, r, color)
  local t = th()
  color = color or t.accent
  d.circle(x, y, r, t.panel)
  d.ring(x, y, r, color, math.max(1.5, r * 0.08))
  d.arc(x, y, r * 1.18, -50, 50, t.dim, math.max(1, r * 0.06))
  local blink = (time() % 4.2) < 0.15
  local ew, eh = r * 0.22, blink and r * 0.04 or r * 0.2
  d.rect(x - r * 0.42, y - r * 0.25 - eh / 2, ew, eh, color)
  d.rect(x + r * 0.2, y - r * 0.25 - eh / 2, ew, eh, color)
  local speaking = talk.now and talk.shown < #talk.now.text
  local mh = speaking and r * (0.06 + 0.14 * math.abs(math.sin(time() * 14))) or r * 0.05
  d.rect(x - r * 0.35, y + r * 0.35 - mh / 2, r * 0.7, mh, color)
end

-- Crew: the assistant, what it said, and things to ask it.
shipos.app{ id = "crew", title = "CREW", short = "CRW",
  draw = function(d, x, y, w, h)
    local t = th()
    local crew = cfg.crew or {}
    local r = math.min(h * 0.2, 28)
    local col = talk.now and level_color(talk.now.level) or t.accent
    shipos.portrait(d, x + r + 4, y + r + 4, r, col)
    d.text(x + 2 * r + 14, y + 4, crew.name or "IRIS", t.accent, 16, "left", "display")
    d.text(x + 2 * r + 14, y + 24, crew.role or "Ship assistant", t.muted, 11)
    local by = y + h - 24
    local bw = (w - 8) / 3
    d.button("ask_report", x, by, bw, 22, "REPORT", t.accent)
    d.button("ask_where", x + bw + 4, by, bw, 22, "WHERE", t.accent)
    d.button("ask_idle", x + 2 * (bw + 4), by, bw, 22, "CHAT", t.accent)
    -- What it said, newest at the bottom, as many as fit.
    local ly, size = y + 2 * r + 14, 11
    local room = math.floor((by - 6 - ly) / (size + 4))
    local lines = {}
    for i = #talk.history, 1, -1 do
      local h_ = talk.history[i]
      local text = h_.text
      if talk.now and i == #talk.history then text = text:sub(1, math.floor(talk.shown)) end
      local wrapped = wrap(d, text, size, w - 8)
      for j = #wrapped, 1, -1 do table.insert(lines, 1, { wrapped[j], level_color(h_.level), i == #talk.history }) end
      if #lines >= room then break end
    end
    while #lines > room do table.remove(lines, 1) end
    for i, l in ipairs(lines) do d.text(x + 4, ly + (i - 1) * (size + 4), l[1], l[3] and l[2] or t.muted, size) end
  end,
  touch = function(button)
    if button == "ask_report" then shipos.speak("report")
    elseif button == "ask_where" then shipos.speak(s.planet ~= "" and "where_planet" or "where_space")
    elseif button == "ask_idle" then shipos.speak("idle") end
  end }

-- Log: everything shipos.log got, newest at the bottom.
shipos.app{ id = "log", title = "LOG", short = "LOG",
  draw = function(d, x, y, w, h)
    local t = th()
    local size = 10
    local room = math.floor(h / (size + 4))
    local first = math.max(1, #logs - room + 1)
    for i = first, #logs do
      local l = logs[i]
      local ly = y + (i - first) * (size + 4)
      d.text(x + 2, ly, clock(l.at), t.muted, size)
      d.text(x + 40, ly, fit(d, l.text, size, w - 42), level_color(l.level), size)
    end
  end }

-- Drawing ------------------------------------------------------------------------------------------------------------

local function draw_boot(d, scr)
  local t = th()
  local w, h = d.width, d.height
  local e = time() - boot_at
  d.clear()
  d.text(w / 2, h * 0.18, shipos.name, t.accent, 34, "center", "display")
  d.text(w / 2, h * 0.18 + 38, "SHIP OS " .. shipos.version, t.muted, 11, "center")
  local steps = {
    { "CORE", "OK" },
    { "POWER BUS", pct(s.power) },
    { "HULL SCAN", pct(s.hull) },
    { "FUEL", pct(s.fuel) },
    { "DISPLAYS", tostring(#screens) },
    { "CREW LINK", opt(cfg.crew, "name", "IRIS") },
  }
  local y0 = h * 0.48
  local bw = math.min(w - 40, 200)
  for i, step in ipairs(steps) do
    if e > i * (BOOT - 0.6) / #steps then
      local ly = y0 + (i - 1) * 14
      d.text(w / 2 - bw / 2, ly, step[1], t.text, 10)
      d.text(w / 2 + bw / 2, ly, step[2], t.fg, 10, "right")
    end
  end
  d.bar(w / 2 - bw / 2, h - 18, bw, 4, math.min(1, e / BOOT), t.accent, t.dim)
end

local function draw_screen(d, scr)
  local t = th()
  local w, h = d.width, d.height
  if w <= 0 then return end
  if not booted then draw_boot(d, scr); return end
  d.clear()
  local app = apps[scr.app] or apps[order[1]]
  -- Status bar: OS and app on the left, an alert or the clock on the right.
  local alert = top_alert()
  if alert and math.floor(time() * 2) % 2 == 0 then d.rect(0, 0, w, TOP, t.alert) else d.rect(0, 0, w, TOP, t.dim) end
  local fg = alert and math.floor(time() * 2) % 2 == 0 and t.back or t.accent
  d.text(6, 3, shipos.name, fg, 11, "left", "display")
  d.text(6 + d.measure(shipos.name, 11, "display") + 8, 3, app.title, alert and fg or t.text, 11)
  local right = alert and alert.label or clock(time() - boot_at)
  d.text(w - 6, 3, right, alert and fg or t.muted, 11, "right")
  -- The app.
  local cy, ch = TOP + 4, h - TOP - TABS - 8
  app.draw(d, 6, cy, w - 12, ch, scr)
  -- The assistant, over the bottom of other apps while it talks.
  if talk.now and scr.app ~= "crew" then
    local text = talk.now.text:sub(1, math.floor(talk.shown))
    local lines = wrap(d, text, 11, w - 52)
    local bh = math.max(30, 8 + #lines * 14)
    local by = cy + ch - bh
    d.rect(4, by, w - 8, bh, t.panel)
    d.box(4, by, w - 8, bh, level_color(talk.now.level), 1)
    shipos.portrait(d, 20, by + bh / 2, 11, level_color(talk.now.level))
    for i, l in ipairs(lines) do d.text(38, by + 5 + (i - 1) * 14, l, t.text, 11) end
  end
  -- Tabs.
  local n = #order
  local tw = (w - 8 - (n - 1) * 3) / n
  for i, id in ipairs(order) do
    local a = apps[id]
    local label = tw >= 52 and a.title or (a.short or a.title:sub(1, 3))
    if d.measure(label, 11) > tw - 6 then label = a.short or a.title:sub(1, 3) end
    d.button("app:" .. id, 4 + (i - 1) * (tw + 3), h - TABS, tw, TABS - 4, label, id == scr.app and t.accent or t.muted)
  end
end

local function each_screen(f)
  for _, scr in ipairs(screens) do
    local d = displays.get(scr.key)
    if d and d.present then f(d, scr) end
  end
end

-- Find the displays (by declared port names, else by number) and give each a starting app.
local function find_screens()
  local keys = {}
  if cfg.displays then for _, name in ipairs(cfg.displays) do keys[#keys + 1] = name end
  else for i = 1, displays.count do keys[#keys + 1] = i end end
  local old = {}
  for _, scr in ipairs(screens) do old[tostring(scr.key)] = scr end
  local fresh = {}
  for i, k in ipairs(keys) do
    local scr = old[tostring(k)]
    if not scr then
      local start = cfg.screens and cfg.screens[k] or order[((i - 1) % #order) + 1]
      scr = { key = k, app = start, page = 1 }
    end
    fresh[#fresh + 1] = scr
  end
  screens = fresh
end

-- Running --------------------------------------------------------------------------------------------------------------

local scan_t = 0
function shipos.tick(dt)
  read_ship()
  scan_t = scan_t + dt
  if scan_t > 2 then scan_t = 0; find_screens() end
  if not booted and time() - boot_at >= BOOT then
    booted = true
    shipos.log(shipos.name .. " " .. shipos.version .. " ready on " .. #screens .. " display(s)", "good")
    -- Rules start from what's already true, so it doesn't list every old problem as news; boot names them instead.
    for _, r in ipairs(rules) do local ok, on = pcall(r.when, s); r.active, r.said = ok and on or false, time() end
    shipos.speak("boot")
  end
  if booted then check_rules() end
  talk_tick(dt)
  for _, id in ipairs(order) do if apps[id].tick then apps[id].tick(dt) end end
  if cfg.on_tick then cfg.on_tick(dt) end
  each_screen(draw_screen)
end

function shipos.touch(x, y, screen, down, button)
  if not down or not button or not booted then return end
  local scr
  for _, c in ipairs(screens) do if tostring(c.key) == tostring(screen) then scr = c end end
  if not scr then return end
  local id = button:match("^app:(.+)$")
  if id and apps[id] then scr.app = id; scr.page = 1
  else
    local app = apps[scr.app]
    if app and app.touch then app.touch(button, x, y, scr) end
    if cfg.on_touch then cfg.on_touch(button, x, y, scr) end
  end
  local d = displays.get(scr.key)
  if d and d.present then draw_screen(d, scr) end
end

-- opts: crew = { name, role, call, style ("calm" or "casual"), hold }, apps = { ids in tab order }, displays = {
-- port names }, screens = { [display] = starting app }, rules = false (no stock rules), name (the OS's name),
-- on_tick(dt), on_touch(button, x, y, scr).
function shipos.run(o)
  cfg = o or {}
  if cfg.name then shipos.name = cfg.name end
  cfg.crew = cfg.crew or {}
  if not cfg.apps and not mainframe then
    local keep = {}
    for _, id in ipairs(order) do if id ~= "server" then keep[#keep + 1] = id end end
    order = keep
  end
  if cfg.apps then
    local keep = {}
    for _, id in ipairs(cfg.apps) do if apps[id] and (id ~= "server" or mainframe) then keep[#keep + 1] = id end end
    order = keep
  end
  if cfg.rules ~= false then stock_rules() end
  boot_at, booted = time(), false
  math.randomseed(math.floor(time() * 1000) % 2147483647)
  rate = cfg.rate or 0.1
  find_screens()
  read_ship()
  shipos.log("boot", "info")
  tick = shipos.tick
  on_touch = shipos.touch
end
