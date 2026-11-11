-- shipos: a small ship operating system for a Lua Computer and its monitors. It boots to a desktop of app icons on
-- every linked monitor; an app opens in a window (title bar, close button) over a taskbar (the start button, a
-- button per app, the clock). It raises alerts, and has a crew assistant: a scripted NPC (no real AI) who watches
-- the ship and speaks up. Start it from a script with
--   shipos.run{ crew = { name = "IRIS", style = "calm" } }
-- Add your own apps with shipos.app{...} and your own assistant rules with shipos.rule{...} before shipos.run.
-- Apps installed on the computer (UserData\EvoCore\apps, the APPS tab; EvoCore's Apps.cs) join the desktop and taskbar after the
-- built-in ones (STATUS, CREW, LOG, SERVER, APPS). An app that errors is marked crashed instead of stopping the OS.
-- Needs ui.lua (the stock lib). EvoCore's stock library (UserData\EvoCore\lib\shipos.lua): edit away; delete this
-- file to get the stock one back.
shipos = { name = "HELM", version = "0.3" }

local store = apps              -- the computer's installed apps (nil on a text screen); `apps` below is the tabs
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
local TOP, TABS = 18, 26        -- window title bar and taskbar heights

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

-- Whether an alert is on the status bar (for the ship os node's alert output).
function shipos.alert() return top_alert() ~= nil end

-- Apps ----------------------------------------------------------------------------------------------------------------
-- app = { id, title, short (taskbar label), draw(d, x, y, w, h, scr), touch(button, x, y, scr), tick(dt),
--         icon(d, x, y, r, color) (its desktop icon, drawn round x, y within r; its short name when it has none) }
-- draw gets the content area; buttons it makes should have names unique to the app (they come back to touch).

local defined = {}              -- ids of the tabs made with shipos.app, in the order they were made
local installed = {}            -- ids of the computer's installed apps, in install order (below)

-- The tab order: cfg.apps when the script gives one (installed apps it leaves out go after it, then APPS); else
-- STATUS, the installed apps, then the other built-in tabs. SERVER only on a mainframe, APPS only on a computer.
local function arrange()
  local keep, seen = {}, {}
  local function add(id)
    if seen[id] or not apps[id] then return end
    if (id == "server" and not mainframe) or (id == "apps" and not store) then return end
    seen[id] = true
    keep[#keep + 1] = id
  end
  if cfg.apps then
    for _, id in ipairs(cfg.apps) do add(id) end
    for _, id in ipairs(installed) do add(id) end
    add("apps")
  else
    add("status")
    for _, id in ipairs(installed) do add(id) end
    for _, id in ipairs(defined) do add(id) end
  end
  order = keep
end

function shipos.app(app)
  if not apps[app.id] then defined[#defined + 1] = app.id end
  apps[app.id] = app
  arrange()
  return app
end

-- Status: the four levels as rings, heat and power flow below.
shipos.app{ id = "status", title = "STATUS", short = "STA",
  icon = function(d, x, y, r, c)
    d.arc(x, y, r, -120, 120, c, math.max(1.5, r * 0.18))
    d.line(x, y, x + r * 0.5, y - r * 0.45, c, math.max(1.5, r * 0.14))
    d.circle(x, y, r * 0.16, c)
  end,
  draw = function(d, x, y, w, h)
    local t = th()
    -- Four across, or two by two on a narrow screen (a tall Lua Screen Wedge), where four labels can't fit.
    local n = w >= 150 and 4 or 2
    local r = math.min((w / n) * 0.36, h * (n == 4 and 0.24 or 0.16))
    local cy = y + r + 6
    local rings = {
      { s.fuel, "FUEL", { warn = 0.25, alert = 0.08, low = true } },
      { s.power, "POWER", { warn = 0.2, alert = 0.05, low = true } },
      { s.hull, "HULL", { warn = 0.75, alert = 0.35, low = true } },
      { s.shield_on and s.shield or 0, s.shield_on and "SHIELD" or "SHLD OFF", { warn = 0.3, low = true } },
    }
    for i, ring in ipairs(rings) do
      local col, row = (i - 1) % n, math.floor((i - 1) / n)
      ui.ring(d, x + w * (col + 0.5) / n, cy + row * (2 * r + 8), r, ring[1], { label = ring[2], width = w / n - 3, warn = ring[3].warn, alert = ring[3].alert, low = true })
    end
    local my = cy + (math.ceil(#rings / n) - 1) * (2 * r + 8) + r + 12
    ui.meter(d, x + 4, my, w - 8, s.heat, "HEAT", { warn = 0.7, alert = 0.85 })
    local flow = s.power_in - s.power_use
    ui.rows(d, x + 4, my + 32, w - 8, {
      { "POWER IN / USE", string.format("%.0f / %.0f", s.power_in, s.power_use) },
      { "NET", string.format("%+.0f", flow) },
    }, { size = 11 })
  end }

-- Server: the mainframe's racks, load and power draw (only on a mainframe; `mainframe` is nil elsewhere).
local load_hist, load_t = {}, 0
shipos.app{ id = "server", title = "SERVER", short = "SRV",
  icon = function(d, x, y, r, c)
    for i = 0, 2 do
      local ry = y - r * 0.9 + i * r * 0.64
      d.box(x - r * 0.85, ry, r * 1.7, r * 0.5, c, math.max(1, r * 0.1))
      d.rect(x - r * 0.6, ry + r * 0.17, r * 0.2, r * 0.16, c)
    end
  end,
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
  icon = function(d, x, y, r, c) shipos.portrait(d, x, y, r * 0.85, c) end,
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
  icon = function(d, x, y, r, c)
    for i = 0, 3 do d.rect(x - r * 0.8, y - r * 0.8 + i * r * 0.48, r * (i == 3 and 0.9 or 1.6), r * 0.2, c) end
  end,
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

-- Installed apps -------------------------------------------------------------------------------------------------------
-- Apps from the computer's store (`apps` in Lua, EvoCore's Apps.cs) run like the tabs above, each in its own
-- environment with its own saved `data`. Their calls go through run_app, so one that errors is marked crashed.

-- Calls app[name](...) unless the app crashed; an error marks it crashed and logs it.
local function run_app(app, name, ...)
  local f = app[name]
  if not f or app.crashed then return end
  local ok, err = pcall(f, ...)
  if not ok then
    app.crashed = tostring(err)
    shipos.log(app.title .. " crashed: " .. app.crashed, "alert")
  end
end
shipos.run_app = run_app

-- A circuit app (EvoCore Apps.cs builds its chunk): its Display nodes draw during its tick onto a recorder, which stands
-- in for its monitor; draw replays that inside the tab, moved to the tab's corner. Touches on its buttons go to its own
-- on_touch, and are let go of on its next tick (the OS only hears presses).
-- Which arguments of each drawing call are x, y pairs (the rest are sizes, colours, text).
local XY = { button = { 2 }, line = { 1, 3 }, tri = { 1, 3, 5 } }
-- ...and the arguments that are lengths (sizes, radii, thicknesses, text size), scaled with them.
local LEN = { rect = { 3, 4 }, box = { 3, 4, 6 }, bar = { 3, 4 }, line = { 6 }, arc = { 3, 7 }, ring = { 3, 5 }, circle = { 3 },
  text = { 5 }, button = { 4, 5 } }
local unpack_ = table.unpack or unpack

-- How far right and down one recorded drawing reaches (for fitting a circuit app into its tab).
local function extent(d, name, a, mx, my)
  local function n(i, default) return type(a[i]) == "number" and a[i] or default end
  if name == "rect" or name == "box" or name == "bar" then return math.max(mx, n(1, 0) + n(3, 0)), math.max(my, n(2, 0) + n(4, 0))
  elseif name == "button" then return math.max(mx, n(2, 0) + n(4, 0)), math.max(my, n(3, 0) + n(5, 0))
  elseif name == "line" then return math.max(mx, n(1, 0), n(3, 0)), math.max(my, n(2, 0), n(4, 0))
  elseif name == "tri" then return math.max(mx, n(1, 0), n(3, 0), n(5, 0)), math.max(my, n(2, 0), n(4, 0), n(6, 0))
  elseif name == "arc" or name == "ring" or name == "circle" then return math.max(mx, n(1, 0) + n(3, 0)), math.max(my, n(2, 0) + n(3, 0))
  elseif name == "text" then
    local size = n(5, 12)
    local wide = d.measure(tostring(a[3] or ""), size, a[7])
    local left = a[6] == "right" and n(1, 0) - wide or a[6] == "center" and n(1, 0) - wide / 2 or n(1, 0)
    return math.max(mx, left + wide), math.max(my, n(2, 0) + size)
  end
  return mx, my
end

function shipos.graph_app(title, short, tick, on_touch, set_display)
  local calls, size, real, ox, oy, pressed, scale = {}, { w = 300, h = 200 }, nil, 0, 0, false, 1
  local recorder = setmetatable({ present = true }, { __index = function(_, k)
    if k == "width" then return size.w elseif k == "height" then return size.h end
    if k == "measure" then return function(...) return real and real.measure(...) or 0 end end
    if k == "hover" then return function() return nil end end
    if k == "clear" then return function() calls = {} end end
    return function(...) calls[#calls + 1] = { k, select("#", ...), { ... } } end
  end })
  set_display(recorder, { count = 1, get = function() return recorder end })
  return {
    title = title, short = short, graph = true,
    tick = function(dt)
      if pressed and on_touch then pressed = false; on_touch(0, 0, 1, false, nil) end
      if tick then tick(dt) end
    end,
    draw = function(d, x, y, w, h)
      real, ox, oy, size.w, size.h = d, x, y, w, h
      -- A circuit laid out bigger than the tab (its nodes have fixed places) is shrunk evenly to fit, sizes and all.
      local mx, my = 0, 0
      for _, c in ipairs(calls) do mx, my = extent(d, c[1], c[3], mx, my) end
      scale = math.min(1, w / math.max(1, mx), h / math.max(1, my))
      for _, c in ipairs(calls) do
        local name, n, args = c[1], c[2], {}
        for i = 1, n do args[i] = c[3][i] end
        for _, at in ipairs(XY[name] or { 1 }) do
          if type(args[at]) == "number" then args[at] = args[at] * scale + x end
          if type(args[at + 1]) == "number" then args[at + 1] = args[at + 1] * scale + y end
        end
        for _, at in ipairs(LEN[name] or {}) do
          if type(args[at]) == "number" then args[at] = args[at] * scale end
        end
        local f = d[name]
        if f then f(unpack_(args, 1, n)) end
      end
    end,
    touch = function(button, x, y)
      if on_touch then pressed = true; on_touch((x - ox) / scale, (y - oy) / scale, 1, true, button) end
    end,
  }
end

-- Loads an installed app and runs its file: the table it returns is the app.
local function load_app(id)
  local chunk, err = store.load(id)
  local app
  if chunk then
    local ok, res = pcall(chunk)
    if ok and type(res) == "table" then app = res
    elseif ok then err = "its file has to end with return { title = ..., draw = function ... }"
    else err = tostring(res) end
  end
  app = app or {}
  app.id, app.installed = id, true
  app.title = app.title or id:upper()
  app.short = app.short or app.title:sub(1, 3)
  if err then app.crashed = err; shipos.log(app.title .. " didn't load: " .. err, "alert") end
  return app
end

-- Makes the tabs match the store: drops removed apps, loads new ones (or `reload`, after an update), keeps the rest.
-- An installed app named like a built-in tab is left out (the built-in one keeps the name).
local function sync_installed(reload)
  if not store then return end
  local want, wanted = {}, {}
  for _, a in ipairs(store.list()) do want[#want + 1] = a.id; wanted[a.id] = true end
  for id, a in pairs(apps) do if a.installed and not wanted[id] then apps[id] = nil end end
  for _, id in ipairs(want) do
    local a = apps[id]
    if not a or (a.installed and id == reload) then apps[id] = load_app(id)
    elseif not a.installed and id ~= reload then shipos.log("App " .. id .. " has a built-in app's name; rename its file", "warn") end
  end
  installed = want
  arrange()
  -- Screens showing an app that's gone go back to the desktop.
  for _, scr in ipairs(screens) do if scr.app ~= "home" and not apps[scr.app] then scr.app = "home"; scr.page = 1 end end
end
shipos.sync_installed = sync_installed

-- Apps: what's installed and what can be, with the computer's slots.
local store_rows, store_t = {}, -10
local function refresh_store()
  store_rows = {}
  if not store then return end
  for _, a in ipairs(store.list()) do store_rows[#store_rows + 1] = { id = a.id, size = a.size, about = a.about, installed = true } end
  for _, a in ipairs(store.available()) do
    if not a.installed then store_rows[#store_rows + 1] = { id = a.id, size = a.size, about = a.about, kind = a.kind } end
  end
end

-- A touch button whose label shrinks to fit it (display.button draws its label at one size, which runs over a small
-- button on a narrow screen).
function shipos.button(d, name, x, y, w, h, label, color)
  local size = math.min(18, h * 0.55)
  local fit = ui.fit(d, label, size, w - 4)
  if fit >= size then d.button(name, x, y, w, h, label, color); return end
  d.button(name, x, y, w, h, "", color)
  d.text(x + w / 2, y + (h - fit) / 2 - 1, label, color, fit, "center")
end

shipos.app{ id = "apps", title = "APPS", short = "APP",
  icon = function(d, x, y, r, c)
    local q = r * 0.7
    for i = 0, 3 do d.rect(x - r * 0.8 + (i % 2) * (q + r * 0.2), y - r * 0.8 + math.floor(i / 2) * (q + r * 0.2), q, q, c) end
  end,
  tick = function()
    if time() - store_t > 5 then store_t = time(); refresh_store() end
  end,
  draw = function(d, x, y, w, h, scr)
    local t = th()
    if not store then d.text(x + w / 2, y + 20, "No app storage here", t.muted, 12, "center"); return end
    d.text(x + 2, y + 2, string.format("SLOTS %d / %d", store.used, store.slots), store.used >= store.slots and t.warn or t.accent, 11, "left", "display")
    -- Wide: one line per app (name and description, state, button). Narrow: two, the name across the whole row on
    -- top, the state and a smaller button under it.
    local bw = math.min(80, math.floor(w * 0.42))
    local wide = w - bw - 100 >= 60
    local rh, gap, top = wide and 30 or 40, 3, y + 20
    local per = math.max(1, math.floor((h - 46) / (rh + gap)))
    local pages = math.max(1, math.ceil(#store_rows / per))
    scr.page = math.min(scr.page or 1, pages)
    if #store_rows == 0 then d.text(x + w / 2, top + 10, "No apps in UserData\\EvoCore\\apps", t.muted, 11, "center") end
    for k = 1, per do
      local i = (scr.page - 1) * per + k
      local row = store_rows[i]
      if not row then break end
      local ry = top + (k - 1) * (rh + gap)
      local app = row.installed and apps[row.id]
      local title = app and app.title or row.id:upper()
      local state = not row.installed and string.format("%s %.1f KB", row.kind == "graph" and "CIRCUIT" or "LUA", row.size / 1024)
        or app and app.crashed and "CRASHED" or "INSTALLED"
      local color = app and app.crashed and t.alert or row.installed and t.fg or t.text
      d.rect(x, ry, w, rh, t.panel)
      local label, bcolor = row.installed and "REMOVE" or "INSTALL", row.installed and t.muted or t.accent
      if wide then
        local tw = w - bw - 100
        d.text(x + 6, ry + 3, shipos.fit(d, title, 11, tw, "display"), color, 11, "left", "display")
        d.text(x + 6, ry + 16, shipos.fit(d, row.about ~= "" and row.about or "-", 9, tw), t.muted, 9)
        d.text(x + w - bw - 8, ry + 9, state, color, 10, "right")
        shipos.button(d, "store:" .. i, x + w - bw - 4, ry + 4, bw, rh - 8, label, bcolor)
      else
        d.text(x + 6, ry + 3, shipos.fit(d, title, 11, w - 12, "display"), color, 11, "left", "display")
        d.text(x + 6, ry + 23, state, color, ui.fit(d, state, 9, w - bw - 16), "left")
        shipos.button(d, "store:" .. i, x + w - bw - 4, ry + 19, bw, 18, label, bcolor)
      end
    end
    local py = y + h - 22
    if pages > 1 then
      d.button("store_prev", x, py, 40, 20, "<", t.accent)
      d.button("store_next", x + w - 40, py, 40, 20, ">", t.accent)
      d.text(x + w / 2, py + 4, string.format("%d/%d", scr.page, pages), t.muted, 10, "center")
    end
  end,
  touch = function(button, x, y, scr)
    if button == "store_prev" then scr.page = math.max(1, (scr.page or 1) - 1); return end
    if button == "store_next" then scr.page = (scr.page or 1) + 1; return end
    local i = tonumber(button:match("^store:(%d+)$"))
    local row = i and store_rows[i]
    if not row or not store then return end
    if row.installed then
      local title = apps[row.id] and apps[row.id].title or row.id
      if store.remove(row.id) then shipos.log("Removed " .. title) end
    else
      local ok, why = store.install(row.id)
      if ok then shipos.log("Installed " .. row.id, "good") else shipos.log("Can't install " .. row.id .. ": " .. tostring(why), "warn") end
    end
    sync_installed()
    refresh_store()
  end }

-- Drawing ------------------------------------------------------------------------------------------------------------

-- The screen's own background, the theme's dark panel unless the script gives one (shipos.run{ background = ... }):
-- otherwise a stock monitor shows its paint behind the picture, and its bevel carries that paint on round the edge.
local function backdrop(d, scr)
  local want = cfg.background or th().panel
  if scr.background ~= want then d.background = want; scr.background = want end
end

local function draw_boot(d, scr)
  local t = th()
  local w, h = d.width, d.height
  local e = time() - boot_at
  backdrop(d, scr)
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

-- An app's desktop icon: its own, or its short name.
local function draw_icon(d, a, x, y, r, color)
  if a.icon and not a.crashed and pcall(a.icon, d, x, y, r, color) then return end
  local label = a.short or a.title:sub(1, 3)
  local size = ui.fit(d, label, r * 0.9, r * 1.9, "display")
  d.text(x, y - size * 0.6, label, color, size, "center", "display")
end

-- The desktop: every app as an icon, in a grid that shrinks until they all fit.
local function draw_desktop(d, scr, w, h)
  local t = th()
  local edge = math.max(6, w * 0.07)
  d.text(edge, 4, shipos.name, t.accent, 12, "left", "display")
  local ver = "SHIP OS " .. shipos.version
  if d.measure(ver, 9) < w - 2 * edge - d.measure(shipos.name, 12, "display") - 8 then d.text(w - edge, 6, ver, t.muted, 9, "right") end
  local top, ah, aw = TOP + 6, h - TOP - TABS - 10, w - 8
  local n = #order
  local cell, cols, rows = 76
  while true do
    cols, rows = math.max(1, math.floor(aw / cell)), math.max(1, math.floor(ah / cell))
    if cols * rows >= n or cell <= 34 then break end
    cell = cell - 2
  end
  cols = math.min(cols, n)
  local gx = (w - cols * cell) / 2
  local size = cell * 0.58
  local ls = math.max(7, math.min(10, cell * 0.15))
  for i, id in ipairs(order) do
    if i > cols * rows then break end
    local a = apps[id]
    local cx = gx + ((i - 1) % cols + 0.5) * cell
    local ty = top + math.floor((i - 1) / cols) * cell
    local color = a.crashed and t.alert or t.fg
    d.rect(cx - size / 2, ty, size, size, t.dim)
    d.button("app:" .. id, cx - size / 2, ty, size, size, "", color)
    draw_icon(d, a, cx, ty + size / 2, size * 0.3, color)
    local label = d.measure(a.title, ls) <= cell - 4 and a.title or (a.short or a.title:sub(1, 3))
    d.text(cx, ty + size + 3, label, t.text, ls, "center")
  end
end

-- The taskbar: the start button (to the desktop), a button per app as far as they fit (the rest are on the desktop),
-- and the clock, with a blinking mark in front of it during an alert.
local function draw_taskbar(d, scr, w, h, alert)
  local t = th()
  local y, bh = h - TABS, TABS - 6
  d.rect(0, y, w, TABS, t.dim)
  local sw = math.min(52, w * 0.3)
  shipos.button(d, "os:home", 3, y + 3, sw, bh, shipos.name, scr.app == "home" and t.accent or t.fg)
  local right = clock(time() - boot_at)
  local cw = d.measure(right, 10) + 10
  d.text(w - 5, y + TABS / 2 - 7, right, t.muted, 10, "right")
  if alert and math.floor(time() * 2) % 2 == 0 then d.text(w - cw - 2, y + TABS / 2 - 8, "!", t.alert, 12, "right", "display"); end
  if alert then cw = cw + 10 end
  local x0 = 3 + sw + 4
  local room = w - cw - x0
  local n = #order
  local tw = (room - (n - 1) * 3) / n
  if tw < 30 then tw = 30; n = math.max(0, math.floor((room + 3) / 33)) end
  tw = math.min(tw, 60)
  for i = 1, n do
    local id = order[i]
    local a = apps[id]
    shipos.button(d, "app:" .. id, x0 + (i - 1) * (tw + 3), y + 3, tw, bh, a.short or a.title:sub(1, 3), id == scr.app and t.accent or t.muted)
  end
end

local function draw_screen(d, scr)
  local t = th()
  local w, h = d.width, d.height
  if w <= 0 then return end
  if not booted then draw_boot(d, scr); return end
  backdrop(d, scr)
  d.clear()
  local alert = top_alert()
  local app = apps[scr.app]
  local cy, ch = TOP + 4, h - TOP - TABS - 8
  if not app then
    scr.app = "home"
    draw_desktop(d, scr, w, h)
  else
    -- The window: a title bar (it flashes, with the alert's name, during an alert) with a close button, then the app.
    local flash = alert and math.floor(time() * 2) % 2 == 0
    d.rect(0, 0, w, TOP, flash and t.alert or t.dim)
    d.box(1, TOP, w - 2, h - TOP - TABS, t.dim, 1)
    local fg = flash and t.back or t.accent
    -- In from the edges by a share of the width: monitors cut their top corners off.
    local edge = math.max(6, w * 0.07)
    local xw = 16
    shipos.button(d, "os:close", w - edge - xw, 2, xw, TOP - 4, "X", flash and t.back or t.muted)
    local room = w - 2 * edge - xw - 6
    local title = app.title
    if d.measure(title, 11, "display") > room then title = app.short or "" end
    d.text(edge, 3, title, fg, 11, "left", "display")
    if alert then
      local tx = edge + d.measure(title, 11, "display") + 8
      local left = w - edge - xw - 6 - tx
      if left > 30 then d.text(w - edge - xw - 6, 3, alert.label, flash and fg or t.alert, ui.fit(d, alert.label, 11, left), "right") end
    end
    if app.crashed then
      d.text(w / 2, cy + 10, app.title .. " CRASHED", t.alert, 14, "center", "display")
      local lines = wrap(d, app.crashed, 10, w - 24)
      for i, l in ipairs(lines) do d.text(12, cy + 32 + (i - 1) * 13, l, t.text, 10) end
      d.text(w / 2, cy + ch - 14, "Fix it, then reinstall it from APPS", t.muted, 10, "center")
    else
      run_app(app, "draw", d, 6, cy, w - 12, ch, scr)
    end
  end
  -- The assistant, over the bottom of whatever is open while it talks.
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
  draw_taskbar(d, scr, w, h, alert)
end

local function each_screen(f)
  for _, scr in ipairs(screens) do
    local d = displays.get(scr.key)
    if d and d.present then f(d, scr) end
  end
end

-- Find the displays (by declared port names, else by number); a new one starts on the desktop.
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
      local start = cfg.screens and cfg.screens[k] or "home"   -- the desktop, unless the script names an app
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
  for _, id in ipairs(order) do run_app(apps[id], "tick", dt) end
  if cfg.on_tick then cfg.on_tick(dt) end
  each_screen(draw_screen)
end

function shipos.touch(x, y, screen, down, button)
  if not down or not button or not booted then return end
  local scr
  for _, c in ipairs(screens) do if tostring(c.key) == tostring(screen) then scr = c end end
  if not scr then return end
  local id = button:match("^app:(.+)$")
  if button == "os:home" or button == "os:close" then scr.app = "home"; scr.page = 1
  elseif id and apps[id] then scr.app = id; scr.page = 1
  else
    local app = apps[scr.app]
    if app then run_app(app, "touch", button, x, y, scr) end
    if cfg.on_touch then cfg.on_touch(button, x, y, scr) end
  end
  local d = displays.get(scr.key)
  if d and d.present then draw_screen(d, scr) end
end

-- opts: crew = { name, role, call, style ("calm" or "casual"), hold }, apps = { ids in taskbar order }, displays = {
-- port names }, screens = { [display] = starting app, or "home" for the desktop }, rules = false (no stock rules),
-- name (the OS's name),
-- background (the screens' colour behind everything, and so their bevels'; the theme's dark panel by default),
-- default_apps = { app ids installed the first time on a computer that never had apps; default nav and systems },
-- on_tick(dt), on_touch(button, x, y, scr).
function shipos.run(o)
  cfg = o or {}
  if cfg.name then shipos.name = cfg.name end
  cfg.crew = cfg.crew or {}
  -- A computer that never had apps gets the stock ones (NAVIGATION and SYSTEMS were part of the OS before 0.2).
  if store and store.fresh then
    for _, id in ipairs(cfg.default_apps or { "nav", "systems" }) do store.install(id) end
  end
  read_ship()   -- apps read shipos.state when they load
  sync_installed()
  arrange()
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
