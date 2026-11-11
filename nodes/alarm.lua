-- alarm.lua: a stock node (EvoCore; delete it to get the stock one back).
-- What would take two compares, two hysteresis nodes and an or: wire its outputs into lamp, alert or blink nodes.
return {
  help = "warning and alert levels in one node, steady near the edges: x past warn, past alert",
  category = "Shortcuts",
  ins = { "x" },
  outs = { "warn", "alert", "any" },
  settings = { warn_at = 0.25, alert_at = 0.1, low = 1, band = 0.02 },
  kinds = { warn = "onoff", alert = "onoff", any = "onoff" },
  choices = { low = { 0, 1 } },
  docs = {
    x = "the value to watch",
    warn_at = "warn past this", alert_at = "alert past this",
    low = "1: low is bad (fuel, hull), so past means below; 0: high is bad (heat, load)",
    band = "how far back it must come before it clears, so it doesn't flicker",
    warn = "1 while past warn_at (alert too)", alert = "1 while past alert_at", any = "1 while either is on",
  },
  run = function(i, s, state)
    local function past(limit, on)
      local edge = on and (s.low == 1 and limit + s.band or limit - s.band) or limit
      if s.low == 1 then return i.x <= edge end
      return i.x >= edge
    end
    state.warn = past(s.warn_at, state.warn)
    state.alert = past(s.alert_at, state.alert)
    return { warn = state.warn or state.alert, alert = state.alert, any = state.warn or state.alert }
  end,
}
