-- Navigation: speed, where the ship is, the target, and a speed history.
-- An app for the ship OS (lib\shipos.lua): install it from the APPS tab. EvoCore's stock app: edit away; delete this
-- file to get the stock one back.
local s = shipos.state
local speed_hist, hist_t = {}, 0

return {
  title = "NAVIGATION", short = "NAV", version = "1.0",
  tick = function(dt)
    hist_t = hist_t + dt
    if hist_t >= 0.5 then hist_t = 0; speed_hist = ui.history("nav_speed", s.speed, 60) end
  end,
  draw = function(d, x, y, w, h)
    local r = math.min(h * 0.3, w * 0.2)
    ui.gauge(d, x + r + 6, y + r + 4, r, s.speed, { max = math.max(100, math.ceil(s.speed / 100) * 100), format = "%.0f", label = "M/S" })
    local rx, rw = x + 2 * r + 20, w - 2 * r - 24
    local mode = s.warping and "WARP" or s.cruising and "CRUISE" or "NORMAL"
    ui.rows(d, rx, y + 2, rw, {
      { "LOCATION", s.planet ~= "" and shipos.fit(d, s.planet, 11, rw * 0.6) or "open space" },
      { "ALTITUDE", s.altitude and shipos.distance(s.altitude) or "-" },
      { "DRIVE", mode },
      { "TARGET", s.target and shipos.fit(d, s.target.name, 11, rw * 0.6) or "none" },
      { "RANGE", s.target and shipos.distance(s.target.distance) or "-" },
    }, { size = 11 })
    local gy = y + 2 * r + 14
    if h - (gy - y) > 24 then ui.graph(d, x + 2, gy, w - 4, h - (gy - y) - 2, speed_hist, { min = 0, label = "SPEED" }) end
  end,
}
