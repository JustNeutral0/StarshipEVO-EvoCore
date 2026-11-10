-- Mainframe: the ship OS on every monitor linked to the mainframe, and on the monitors of any Lua Computer linked to
-- it that has no program of its own (a terminal). Server Racks on the ship add capacity (the SERVER tab shows the
-- load and power draw). Change the assistant here, or add apps and rules: see lib\shipos.lua.
shipos.run{
  crew = { name = "IRIS", role = "Ship assistant", call = "Captain", style = "calm" },
  apps = { "status", "nav", "systems", "server", "crew", "log" },
}
