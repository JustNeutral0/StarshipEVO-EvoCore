-- ui: ready-made pieces for Lua displays: panels, meters, gauges, rings, reticles, a compass tape, an artificial
-- horizon, graphs and alerts. Each takes the display first: ui.gauge(display, x, y, r, value, { label = "FUEL" }).
-- Sizes are canvas units (a display is 240 tall). Colours come from ui.theme unless an option says otherwise.
-- EvoCore's stock library (UserData\EvoCore\lib\ui.lua). Every file in lib loads into every script before the script
-- itself, and scripts restart when one changes, so edit away. Delete this file to get the stock one back.
ui = {}

ui.theme = {
  fg = "#42ff8c",      -- main lines and fills
  accent = "#42ffe8",  -- highlights
  warn = "#ffb03a",
  alert = "#ff4d5a",
  text = "#d8efe4",
  muted = "#6f8a80",
  dim = "#16261f",     -- empty parts of bars and dials
  panel = "#08110d",
  back = "#030605",
}

local function opt(o, k, v) if o ~= nil and o[k] ~= nil then return o[k] end return v end
local function clamp(v, a, b) if v < a then return a elseif v > b then return b end return v end

-- The colour for a level: theme fg, or warn/alert once past those thresholds. With low = true, low is bad.
function ui.level(value, o)
  local t = ui.theme
  local warn, alert, low = opt(o, "warn", nil), opt(o, "alert", nil), opt(o, "low", false)
  local function past(limit) if low then return value <= limit else return value >= limit end end
  if alert and past(alert) then return t.alert end
  if warn and past(warn) then return t.warn end
  return opt(o, "color", t.fg)
end

local function percent(value, o)
  local lo, hi = opt(o, "min", 0), opt(o, "max", 1)
  if lo == 0 and hi == 1 then return string.format("%.0f%%", value * 100) end
  return string.format(opt(o, "format", "%.0f"), value) .. opt(o, "unit", "")
end

-- A framed panel with bright corners and an optional title strip. Returns the inside: x, y, w, h.
function ui.panel(d, x, y, w, h, title, o)
  local t = ui.theme
  local color = opt(o, "color", t.fg)
  d.rect(x, y, w, h, opt(o, "back", t.panel))
  d.box(x, y, w, h, t.dim, 1)
  local c = math.min(10, w / 4, h / 4)
  d.rect(x, y, c, 2, color); d.rect(x, y, 2, c, color)
  d.rect(x + w - c, y, c, 2, color); d.rect(x + w - 2, y, 2, c, color)
  d.rect(x, y + h - 2, c, 2, color); d.rect(x, y + h - c, 2, c, color)
  d.rect(x + w - c, y + h - 2, c, 2, color); d.rect(x + w - 2, y + h - c, 2, c, color)
  if title then
    d.rect(x + 2, y + 2, w - 4, 18, t.dim)
    d.text(x + 8, y + 4, title, color, 12, "left", "display")
    return x + 6, y + 26, w - 12, h - 32
  end
  return x + 6, y + 6, w - 12, h - 12
end

-- A labelled level bar in segments: label left, value right, bar below. 24 tall.
-- opts: segments (16), warn, alert, low, color, min, max
function ui.meter(d, x, y, w, value, label, o)
  local t = ui.theme
  local lo, hi = opt(o, "min", 0), opt(o, "max", 1)
  local f = clamp((value - lo) / (hi - lo), 0, 1)
  local color = ui.level(value, o)
  if label then d.text(x, y, label, t.text, 12) end
  d.text(x + w, y, percent(value, o), color, 12, "right")
  local n = opt(o, "segments", 16)
  local gap = 2
  local sw = (w - gap * (n - 1)) / n
  local lit = math.floor(f * n + 0.5)
  for i = 0, n - 1 do
    d.rect(x + i * (sw + gap), y + 15, sw, 8, i < lit and color or t.dim)
  end
end

-- A dial: a 240 degree arc with ticks, a needle, the value in the middle and a label under it.
-- opts: min, max, label, unit, format, ticks (6), warn, alert, low, color
function ui.gauge(d, x, y, r, value, o)
  local t = ui.theme
  local lo, hi = opt(o, "min", 0), opt(o, "max", 1)
  local f = clamp((value - lo) / (hi - lo), 0, 1)
  local color = ui.level(value, o)
  local from, to = -120, 120
  local thick = math.max(2, r * 0.12)
  d.arc(x, y, r, from, to, t.dim, thick)
  if f > 0 then d.arc(x, y, r, from, from + (to - from) * f, color, thick) end
  local n = opt(o, "ticks", 6)
  for i = 0, n do
    local a = math.rad(from + (to - from) * i / n)
    local s, c = math.sin(a), -math.cos(a)
    d.line(x + s * r * 0.68, y + c * r * 0.68, x + s * r * 0.8, y + c * r * 0.8, t.muted, 1.5)
  end
  local a = math.rad(from + (to - from) * f)
  d.line(x, y, x + math.sin(a) * r * 0.78, y - math.cos(a) * r * 0.78, t.text, 2)
  d.circle(x, y, math.max(2, r * 0.08), t.text)
  d.text(x, y + r * 0.22, percent(value, o), color, math.max(9, r * 0.3), "center")
  local label = opt(o, "label", nil)
  if label then d.text(x, y + r * 0.62, label, t.muted, math.max(8, r * 0.2), "center", "display") end
end

-- A full ring that fills clockwise from the top, with the value in the middle.
-- opts: label, warn, alert, low, color, thickness
function ui.ring(d, x, y, r, value, o)
  local t = ui.theme
  local f = clamp(value, 0, 1)
  local color = ui.level(value, o)
  local thick = opt(o, "thickness", math.max(2, r * 0.18))
  d.ring(x, y, r, t.dim, thick)
  if f > 0 then d.arc(x, y, r, 0, 360 * f, color, thick) end
  d.text(x, y - r * 0.28, percent(value, o), color, math.max(9, r * 0.42), "center")
  local label = opt(o, "label", nil)
  if label then d.text(x, y + r * 0.2, label, t.muted, math.max(8, r * 0.24), "center", "display") end
end

-- An aiming mark. style: "cross" (default), "circle", "brackets", "dot" or "diamond".
function ui.reticle(d, x, y, size, style, color)
  local t = ui.theme
  color = color or t.accent
  local s = size / 2
  style = style or "cross"
  if style == "cross" then
    local g = s * 0.3
    d.line(x - s, y, x - g, y, color, 2); d.line(x + g, y, x + s, y, color, 2)
    d.line(x, y - s, x, y - g, color, 2); d.line(x, y + g, x, y + s, color, 2)
    d.circle(x, y, 1.5, color)
  elseif style == "circle" then
    d.ring(x, y, s, color, 2)
    d.line(x - s * 1.3, y, x - s * 0.7, y, color, 2); d.line(x + s * 0.7, y, x + s * 1.3, y, color, 2)
    d.line(x, y + s * 0.7, x, y + s * 1.3, color, 2)
    d.circle(x, y, 1.5, color)
  elseif style == "brackets" then
    local c = s * 0.4
    for _, sx in ipairs({ -1, 1 }) do
      for _, sy in ipairs({ -1, 1 }) do
        d.line(x + sx * s, y + sy * s, x + sx * (s - c), y + sy * s, color, 2)
        d.line(x + sx * s, y + sy * s, x + sx * s, y + sy * (s - c), color, 2)
      end
    end
  elseif style == "dot" then
    d.circle(x, y, math.max(1.5, s * 0.15), color)
    d.ring(x, y, s * 0.5, color, 1)
  elseif style == "diamond" then
    d.line(x, y - s, x + s, y, color, 2); d.line(x + s, y, x, y + s, color, 2)
    d.line(x, y + s, x - s, y, color, 2); d.line(x - s, y, x, y - s, color, 2)
    d.circle(x, y, 1.5, color)
  end
end

-- A heading tape: marks every 5 degrees, numbers every 30, N E S W, the heading on a pointer in the middle.
-- opts: span (degrees across the width, 90), color
function ui.compass(d, x, y, w, h, heading, o)
  local t = ui.theme
  local color = opt(o, "color", t.fg)
  local span = opt(o, "span", 90)
  heading = heading % 360
  d.rect(x, y, w, h, t.panel)
  d.box(x, y, w, h, t.dim, 1)
  local names = { [0] = "N", [90] = "E", [180] = "S", [270] = "W" }
  local first = math.floor((heading - span / 2) / 5) * 5
  for deg = first, heading + span / 2, 5 do
    local px = x + w / 2 + (deg - heading) / span * w
    if px > x + 2 and px < x + w - 2 then
      local norm = deg % 360
      if norm % 30 == 0 then
        d.line(px, y + h, px, y + h * 0.55, color, 1.5)
        d.text(px, y + 2, names[norm] or tostring(norm), names[norm] and t.accent or t.muted, math.max(8, h * 0.38), "center")
      else
        d.line(px, y + h, px, y + h * 0.75, t.muted, 1)
      end
    end
  end
  d.tri(x + w / 2 - 5, y + h + 7, x + w / 2 + 5, y + h + 7, x + w / 2, y + h + 1, t.accent)
end

-- An artificial horizon in a circle: pitch and roll in degrees. opts: scale (canvas units per degree of pitch)
function ui.horizon(d, x, y, r, pitch, roll, o)
  local t = ui.theme
  local scale = opt(o, "scale", r / 45)
  local a = math.rad(roll)
  local cx, cy = math.cos(a), math.sin(a)   -- along the horizon
  local nx, ny = -cy, cx                    -- down from the horizon
  d.circle(x, y, r, t.panel)
  -- A line across the circle at offset off (towards the ground), clipped to the circle; half the given width.
  local function across(off, half, color, thick)
    if math.abs(off) >= r then return end
    local chord = math.sqrt(r * r - off * off)
    half = math.min(half or chord, chord)
    local mx, my = x + nx * off, y + ny * off
    d.line(mx - cx * half, my - cy * half, mx + cx * half, my + cy * half, color, thick)
  end
  across(pitch * scale, nil, t.accent, 2)
  for step = -60, 60, 10 do
    if step ~= 0 then
      across((pitch - step) * scale, r * (step % 20 == 0 and 0.35 or 0.2), t.muted, 1)
    end
  end
  d.ring(x, y, r, t.dim, 2)
  d.line(x - r * 0.45, y, x - r * 0.12, y, t.warn, 2)
  d.line(x + r * 0.12, y, x + r * 0.45, y, t.warn, 2)
  d.circle(x, y, 2, t.warn)
end

-- Remembers the last n values under a name, for ui.graph: ui.history("speed", ship.speed, 60).
local histories = {}
function ui.history(name, value, n)
  local h = histories[name]
  if not h then h = {}; histories[name] = h end
  h[#h + 1] = value
  while #h > (n or 60) do table.remove(h, 1) end
  return h
end

-- A line graph of a list of numbers (oldest first). opts: min, max (from the data otherwise), color, label
function ui.graph(d, x, y, w, h, values, o)
  local t = ui.theme
  local color = opt(o, "color", t.fg)
  d.rect(x, y, w, h, t.panel)
  d.box(x, y, w, h, t.dim, 1)
  for i = 1, 3 do d.line(x, y + h * i / 4, x + w, y + h * i / 4, t.dim, 1) end
  local n = #values
  if n >= 2 then
    local lo, hi = opt(o, "min", nil), opt(o, "max", nil)
    if not lo or not hi then
      local a, b = values[1], values[1]
      for i = 2, n do a = math.min(a, values[i]); b = math.max(b, values[i]) end
      lo, hi = lo or a, hi or b
    end
    if hi - lo < 1e-6 then hi = lo + 1 end
    local function py(v) return y + h - 2 - (clamp(v, lo, hi) - lo) / (hi - lo) * (h - 4) end
    for i = 2, n do
      d.line(x + (i - 2) / (n - 1) * w, py(values[i - 1]), x + (i - 1) / (n - 1) * w, py(values[i]), color, 1.5)
    end
  end
  local label = opt(o, "label", nil)
  if label then d.text(x + 4, y + 2, label, t.muted, 10) end
end

-- A flashing banner across the width at y (top by default). opts: color (alert), y, height
function ui.alert(d, text, o)
  local t = ui.theme
  local color = opt(o, "color", t.alert)
  local y, h = opt(o, "y", 0), opt(o, "height", 22)
  local on = math.floor(time() * 3) % 2 == 0
  if on then
    d.rect(0, y, d.width, h, color)
    d.text(d.width / 2, y + h * 0.18, text, t.back, h * 0.6, "center", "display")
  else
    d.box(0, y, d.width, h, color, 2)
    d.text(d.width / 2, y + h * 0.18, text, color, h * 0.6, "center", "display")
  end
end

-- Name / value rows with a leader line. rows: { { "SPEED", "120 m/s" }, ... } or { { "FUEL", 0.4, "%" } }.
-- opts: size (12), gap (row height), color
function ui.rows(d, x, y, w, rows, o)
  local t = ui.theme
  local size = opt(o, "size", 12)
  local gap = opt(o, "gap", size + 6)
  for i, row in ipairs(rows) do
    local ry = y + (i - 1) * gap
    local value = row[2]
    if type(value) == "number" then value = (row[3] == "%") and string.format("%.0f%%", value * 100) or string.format("%.1f", value) .. (row[3] or "") end
    d.text(x, ry, row[1], t.muted, size)
    d.text(x + w, ry, tostring(value), opt(o, "color", t.text), size, "right")
    d.line(x, ry + size + 2, x + w, ry + size + 2, t.dim, 1)
  end
end

-- A button-shaped box with a label; lit when on. (Pressing comes with terminal input.)
function ui.button(d, x, y, w, h, label, on, o)
  local t = ui.theme
  local color = opt(o, "color", t.fg)
  if on then d.rect(x, y, w, h, color) else d.rect(x, y, w, h, t.panel); d.box(x, y, w, h, color, 1) end
  d.text(x + w / 2, y + (h - h * 0.5) / 2, label, on and t.back or color, h * 0.5, "center", "display")
end
