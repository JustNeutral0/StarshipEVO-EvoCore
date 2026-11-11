-- Systems: the ship's labelled logic blocks as switches you can flip from the screen.
-- An app for the ship OS (lib\shipos.lua): install it from the APPS tab. EvoCore's stock app: edit away; delete this
-- file to get the stock one back.
local list, list_t = {}, -10

local function refresh()
  list = {}
  local found = ship.blocks()
  if found then for _, b in ipairs(found) do list[#list + 1] = b end end
end

return {
  title = "SYSTEMS", short = "SYS", version = "1.0",
  tick = function()
    if time() - list_t > 3 then list_t = time(); refresh() end
  end,
  draw = function(d, x, y, w, h, scr)
    local t = ui.theme
    if #list == 0 then
      local lines = shipos.wrap(d, "No labelled blocks. Name doors, lights and switches with the multitool's Tag mode and they show up here.", 11, w - 16)
      for i, l in ipairs(lines) do d.text(x + w / 2, y + 10 + (i - 1) * 15, l, t.muted, 11, "center") end
      return
    end
    local cols = w >= 280 and 2 or 1
    local bh, gap = 24, 4
    local rows = math.max(1, math.floor((h - 26) / (bh + gap)))
    local per = rows * cols
    local pages = math.max(1, math.ceil(#list / per))
    scr.page = math.min(scr.page or 1, pages)
    local bw = (w - (cols - 1) * gap) / cols
    for k = 1, per do
      local i = (scr.page - 1) * per + k
      local b = list[i]
      if not b then break end
      local col, row = (k - 1) % cols, math.floor((k - 1) / cols)
      local on = b.on
      d.button("sys:" .. i, x + col * (bw + gap), y + row * (bh + gap), bw, bh, shipos.fit(d, b.name, 11, bw - 40), on and t.fg or t.muted)
      d.text(x + col * (bw + gap) + bw - 6, y + row * (bh + gap) + 6, on and "ON" or "OFF", on and t.fg or t.muted, 10, "right")
    end
    local py = y + h - 22
    if pages > 1 then
      d.button("sys_prev", x, py, 40, 20, "<", t.accent)
      d.button("sys_next", x + w - 40, py, 40, 20, ">", t.accent)
    end
    d.text(x + w / 2, py + 4, string.format("%d BLOCKS  %d/%d", #list, scr.page, pages), t.muted, 10, "center")
  end,
  touch = function(button, x, y, scr)
    if button == "sys_prev" then scr.page = math.max(1, (scr.page or 1) - 1)
    elseif button == "sys_next" then scr.page = (scr.page or 1) + 1
    else
      local i = tonumber(button:match("^sys:(%d+)$"))
      local b = i and list[i]
      if b then b.toggle(); shipos.log((b.on and "ON  " or "OFF ") .. b.name) end
    end
  end,
}
