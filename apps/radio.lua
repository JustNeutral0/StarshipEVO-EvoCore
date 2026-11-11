-- Radio: what your own music player is playing, with play, pause and skip on the screen.
-- An app for the ship OS (lib\shipos.lua): install it from the APPS tab. EvoCore's stock app: edit away; delete this
-- file to get the stock one back.
-- It shows your computer's player (Spotify, YouTube Music, a browser tab: Windows' now-playing list), through `media`.

-- The dancing bars: no real audio levels to read, so they just move while something plays.
local function bars(d, x, y, w, h, playing, color, dim)
  local n = math.max(6, math.floor(w / 9))
  local bw = (w - (n - 1) * 2) / n
  local now = time()
  for i = 0, n - 1 do
    local level = playing and (0.25 + 0.75 * math.abs(math.sin(now * (2.1 + (i * 7 % 5) * 0.6) + i * 1.7))) or 0.08
    local bh = math.max(2, h * level)
    d.rect(x + i * (bw + 2), y + h - bh, bw, bh, playing and color or dim)
  end
end

return {
  title = "RADIO", short = "RAD", version = "1.0",
  -- A speaker dot with sound rings coming off it.
  icon = function(d, x, y, r, c)
    d.circle(x - r * 0.55, y, r * 0.28, c)
    for i = 1, 3 do d.arc(x - r * 0.55, y, r * (0.35 + i * 0.4), -50, 50, c, math.max(1.5, r * 0.14)) end
  end,
  draw = function(d, x, y, w, h)
    local t = ui.theme
    local cx = x + w / 2
    if not media then d.text(cx, y + 20, "No media here", t.muted, 12, "center"); return end
    if not media.ok then
      for i, l in ipairs(shipos.wrap(d, "Radio offline: " .. media.why, 11, w - 12)) do d.text(cx, y + 6 + i * 14, l, t.warn, 11, "center") end
      return
    end
    -- Buttons along the bottom, the progress line over them, the track above, bars in whatever is left.
    local bh = math.max(20, math.min(34, h * 0.2))
    local by = y + h - bh - 2
    local gap = 4
    local side = math.min(70, (w - 2 * gap - 4) * 0.3)
    local mid = w - 4 - 2 * (side + gap)
    local active = media.active
    local tint = active and t.accent or t.muted
    shipos.button(d, "radio_prev", x + 2, by, side, bh, "<<", tint)
    shipos.button(d, "radio_toggle", x + 2 + side + gap, by, mid, bh, media.playing and "PAUSE" or "PLAY", active and t.fg or t.muted)
    shipos.button(d, "radio_next", x + w - 2 - side, by, side, bh, ">>", tint)

    local py = by - 20
    local duration, position = media.duration, media.position
    if active and duration > 0 then
      d.rect(x + 4, py + 12, w - 8, 3, t.dim)
      d.rect(x + 4, py + 12, (w - 8) * math.min(1, position / duration), 3, t.accent)
      d.text(x + 4, py, shipos.clock(position), t.muted, 9)
      d.text(x + w - 4, py, shipos.clock(duration), t.muted, 9, "right")
    end

    if not active then
      d.text(cx, y + 8, "NOTHING PLAYING", t.muted, ui.fit(d, "NOTHING PLAYING", 13, w - 8, "display"), "center", "display")
      d.text(cx, y + 28, "Start your music player", t.muted, ui.fit(d, "Start your music player", 10, w - 8), "center")
      return
    end
    local title = media.title ~= "" and media.title or "Unknown track"
    local size = math.max(11, ui.fit(d, title, 16, w - 8, "display"))
    d.text(cx, y + 2, shipos.fit(d, title, size, w - 8, "display"), t.fg, size, "center", "display")
    local ay = y + size + 8
    if media.artist ~= "" then d.text(cx, ay, shipos.fit(d, media.artist, 11, w - 8), t.text, 11, "center"); ay = ay + 15 end
    local source = (media.playing and "PLAYING" or "PAUSED") .. (media.app ~= "" and "  " .. media.app:upper() or "")
    d.text(cx, ay, shipos.fit(d, source, 9, w - 8), media.playing and t.accent or t.muted, 9, "center")
    ay = ay + 16
    if py - ay > 16 then bars(d, x + 6, ay, w - 12, py - ay - 4, media.playing, t.accent, t.dim) end
  end,
  touch = function(button)
    if not media then return end
    if button == "radio_toggle" then media.toggle()
    elseif button == "radio_prev" then media.previous()
    elseif button == "radio_next" then media.next() end
  end,
}
