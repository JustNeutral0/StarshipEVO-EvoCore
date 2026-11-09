# EvoCore

The shared base the other mods stand on. A mod that uses it references `EvoCore.csproj` (`Private="false"`) and declares `[assembly: MelonAdditionalDependencies("EvoCore")]` (BepInEx: `[BepInDependency("evo.core")]`). EvoCore loads first (`MelonPriority(-1000)`; under BepInEx the dependency orders it).

## The loader layer (`Loader.cs`, `LoaderBepInEx.cs`)

The only code that touches the mod loader. `Loader.cs` is the MelonLoader one and `LoaderBepInEx.cs` the BepInEx 5 one, with the same public names. A build picks one: `dotnet build -c Release -p:Loader=BepInEx` builds for BepInEx into `bin\BepInEx\Release\` (see `_ModDev\Directory.Build.props`); without it, MelonLoader into `bin\Release\`. See the "BepInEx port" note in the Obsidian vault; `_ModDev\NOTES.md` has the map. A mod on the layer keeps only its attributes as loader lines, one set per loader:

```csharp
#if !BEPINEX
[assembly: MelonLoader.MelonInfo(typeof(MyMod.Mod), "MyMod", "1.0.0", "Claude")]
[assembly: MelonLoader.MelonGame("Moonfire Entertainment", "Starship EVO")]
[assembly: MelonLoader.MelonAdditionalDependencies("EvoCore")]
#endif

#if BEPINEX
[BepInEx.BepInPlugin("evo.mymod", "MyMod", "1.0.0")]   // keep the guid fixed once released
[BepInEx.BepInDependency("evo.core")]
#endif
public class Mod : EvoMod
{
    static Settings settings;
    static Setting<float> speed;

    protected override void Init()            // OnInitializeMelon / Awake; Harmony patches are already applied
    {
        settings = Settings.For("MyMod", "My mod");
        speed = settings.Add("Speed", 1f, "How fast");
        speed.Changed += (was, now) => Log.Msg($"speed {was} -> {now}");
    }
    protected override void Tick() { }        // Update; also LateTick, FixedTick, Gui, EarlyInit, AllLoaded
}
```

| Piece | What it gives |
|---|---|
| `EvoMod` | entry class: `Log`, `Name`, `Version`, `Harmony`, and the lifecycle overrides above (Perf times `Tick`/`LateTick`/`FixedTick`/`Gui`) |
| `Log.For(name)` | a `ModLog` (`Msg`, `Warning`, `Error`) for code with no `EvoMod` at hand |
| `Settings` / `Setting<T>` | one category per mod (under BepInEx one file each, `BepInEx\config\Evo\<id>.cfg`); `Value`, `Default`, `Reset()`, `Changed`; `settings.Save()`; `Settings.All` lists everything |
| `Paths` | `UserData` (`<game>\UserData` under both loaders), `GameRoot`, `LoaderLog` (`MelonLoader\Latest.log` or `BepInEx\LogOutput.log`), `DataFor(mod)`, `PluginFolder(assembly)` |
| `Mods.Loaded` | name, version, author (MelonLoader only), assembly of every loaded mod or plugin (on the layer or not) |
| `Run.Coroutine` / `Run.Stop` | coroutines (under BepInEx they run on EvoCore's plugin object) |

Under BepInEx, plugins wake while Unity is still loading its assemblies, far earlier than MelonLoader starts mods, and much of Unity can't be used yet (MoonSharp's `Resources.LoadAll` crashed the game there). So `Awake` runs only `EarlyInit` and the mod's patches (EvoCore wakes first, so its `EarlyInit` still comes before every mod's patches; keep `EarlyInit` to plain C# and Harmony). Every mod's `Init`, then every `AllLoaded`, run on the first frame, in load order. A mod whose `Init` throws is logged and not started. The plugin class itself declares only `Awake` and `Update`: on Unity 6000.6, adding a plugin that declares `Start`, `LateUpdate` or `FixedUpdate` crashes the game, because BepInEx adds plugins while Unity is still loading assemblies. `LateTick`, `FixedTick` and `Gui` are carried by a small relay component added on that first `Update`. So a mod's entry class must not declare Unity messages of its own either.

`EvoCore.Paths` hides the game's `Common.SkyApplication.Paths` in files that use both namespaces; write the game's in full there.

## Per-save data: `Saves`

Each character's save gets a `mods\<Mod>\` folder next to its own files (`%USERPROFILE%\AppData\LocalLow\Moonfire Entertainment\Starship EVO\...\<character>\mods\`), so mod data is copied, backed up and deleted with the save.

```csharp
Saves.Loaded   += MyState.Load;   // a world was loaded (host or solo only)
Saves.Saving   += MyState.Save;   // the game just saved: every minute, and when quitting to the menu
Saves.Unloaded += MyState.Clear;  // back to the title screen
var lines = Saves.ReadLines("MyMod", "state.txt");     // null if not there yet
Saves.WriteLines("MyMod", "state.txt", lines);          // written to a side file first
Saves.AdoptLegacy("MyMod", "state.txt", oldGlobalPath); // first save loaded takes over an old global file
```

Write only on `Saving`. Then mod state and the game's own files are always from the same moment: a vial poured into a tank and the vial leaving the backpack are saved, or lost, together.

## Ids: `Ids`

Brick, recipe (schematic), research and material ids end up in saves, so they must never move. Claim each one once, with a stable key and the id you've always used:

```csharp
public static readonly int BrickId = Ids.Claim(IdSpace.Brick, "MyMod.Widget", 530);
```

The registry (`UserData\EvoCore\ids.txt`) keeps every claim for good. If another key already holds the id you want, you get the next free one, and the clash is logged. The Core tab also checks the live tables: who built each brick (the game or which DLL), recipes and research added by mods that don't use the registry, and claimed ids that clash with the game's.

**Bigger tables** (`Limits`). At startup, before any mod's patches, EvoCore grows the game's tables:

| Table | Stock | With EvoCore | The game uses |
|---|---|---|---|
| Bricks | 1024 | 2048 | 346 |
| Materials | 1024 | 2048 | 799 |
| Recipes | 256 | 512 | 194 |

Brick ids are 16-bit in saves, so the save format doesn't change. But a save holding a block, material or recipe above the stock size needs EvoCore to load. Prefer ids below the stock sizes while they last. The Core tab and the smoke test report whether the growth fully applied; after a game update it may not, and claims above the stock sizes then show as problems.

## Simulation work: `Sim`

```csharp
Sim.Every("MyMod sync", 1f, Tick);             // on a timer, timed in the overlay
Sim.Sliced("MyMod catch-up", () => DoABit());  // return true while more is waiting this frame
Sim.Background("MyMod plan", () => Plan(copy), result => Apply(result));
```

Sliced steps share one per-frame budget (`EvoCore > FrameBudgetMs`, 2 ms by default) and take turns. Background work runs on a worker thread and its result is applied on the main thread. The work must not touch Unity objects or anything the main thread changes, so copy what it needs before starting it.

## Skipped time: `WorldTime`

```csharp
WorldTime.Skipped += seconds => CatchUp(seconds);  // someone skipped time, e.g. the player slept
WorldTime.Skip(8 * 3600, "DayNight sleep");       // tell everyone time passed
```

Seconds are game seconds at normal speed (the unit of `Time.deltaTime`); a mod with its own time scale applies it. DayNight calls `Skip` when the player wakes; Economy fast-forwards its markets.

## Timing: `Perf`

Every mod's `OnUpdate`, `OnLateUpdate`, `OnFixedUpdate` and `OnGUI` is timed, plus `Sim` work and save handlers. Turn on the overlay in the Core tab (Mod Settings, F11). Time spent in a mod's Harmony patches on game methods isn't counted; it shows as game time.

## Checks

The Core tab shows `Core.Report()` and runs `Core.SelfTest()`. `DevBridge\evo.ps1 smoke [character]` runs the whole check from outside: it restarts the game, loads the character, runs the self-test, makes the game save, checks each mod's save files, the id tables and the log, then quits and prints PASS or FAIL.

- **Turning the table growth off:** create `UserData\EvoCore\no-grow.txt` and every table stays at its stock size at the next start. This is for tracking down a problem; saves holding blocks above 1023 then won't load them.

## Lua scripts on ships: `Lua`

Scripts run on [MoonSharp](https://www.moonsharp.org/) 2.0.0, a Lua interpreter in plain C# under the MIT licence. It ships as `UserLibs\MoonSharp.Interpreter.dll`; the source copy is `_ModDev\_libs\moonsharp`. A text screen runs a script:

- `[lua: name]` runs `UserData\EvoCore\scripts\name.lua` on the screen's ship. Saving the file reloads the script.
- Screen text that starts with `--lua` is itself the script.

The script's body runs once. If it defines `tick(dt)`, that runs every `rate` seconds; `rate` is a global, 0.1 by default.

Each call may run `Lua.InstructionLimit` steps (200,000). A script that goes over is stopped and its error is shown. The sandbox is MoonSharp's soft sandbox: no files, no OS, no `load`. Errors and the last `print` lines show in the Scripts tab, and through DevBridge with `get EvoCore.Lua.Report()`.

### The script editor

The **Scripts** tab of the Mod Settings window (F11) has an editor, so scripts can be written without leaving the game. The window holds the game's controls while it's open, so typing doesn't fly the ship.

- Pick a script from the list on the left, or type a name under **New script** and press **Create**.
- **Save** (or Ctrl+S) checks the syntax, shows any error with its line, and writes the file anyway. Every screen or computer running it reloads within a second. **Revert** drops unsaved edits.
- Tab indents with two spaces. Unsaved edits survive closing the window.
- Under the text: each screen or computer running the script, its status, its last printed lines, and a **Restart** button.
- Files saved from another editor show up in it; if both changed, Save keeps the in-game version.

Every script gets these globals:
- `ship` (read only):
  - `name`, `speed` (m/s), `mass`.
  - Levels from 0 to 1: `fuel`, `power`, `heat`, `hull`, `shield`. Raw amounts: `fuel_stored`, `fuel_capacity`, `power_stored`, `power_capacity`, `power_in`, `power_use`, `hull_points`, `hull_max`.
  - `stalled` (out of power), `shield_on`, `warping`, `cruising` (light cruise).
  - `planet`: the nearest planet, moon or star; `altitude`: metres above its sea-level radius (not the ground under you).
  - `target`: nil with nothing selected, else `name`, `distance` (m), `speed`.
  - `block(label)`: the logic block on this ship with that Tag label, or nil. `blocks(label)`: all of them, as a table in label order; `blocks()` lists every labelled logic block. Matching ignores case, and a name also matches the game's numbered labels (`"Piston"` finds `Piston #1`, `Piston #2`). A block has `name`, `kind`, `state`, `on` (read or set), `set(value)` and `toggle()`; setting it works like a link from the script, so it passes the value on through its own links.

    ```lua
    local door = ship.block('cargo door')
    if door and ship.speed > 5 then door.on = false end
    for _, light in ipairs(ship.blocks('hull light')) do light.on = ship.power > 0.2 end
    ```

  - `velocity()`: three numbers, the speed along the ship's own right, up and forward axes.

  `pilot` adds a script's flight input to yours, only while you're flying the ship from its seat on your machine and never while the autopilot has it. (The game stops updating a ship's flight soon after its pilot leaves, so scripts assist; they can't fly an empty ship.) `pilot.move(right, up, forward)` and `pilot.turn(pitch, yaw, roll)` each take -1 to 1, are held until changed, and add to your keys. `pilot.mode` reads `standard`, `boost`, `cruise`, `warp` or `dropoff`; setting it to `boost` or `standard` switches the boost. `pilot.release()` lets go; a stopped script lets go by itself. `pilot.flying` and `pilot.autopilot` tell you who has the ship.

  Starter scripts: `hold.lua` (a computer labelled `hold` with a switch in: cancels drift so the ship hangs in place), `status.lua` (put `[lua: status]` on a screen) and `warnings.lua` (a computer labelled `warnings`).
- `screen`: `text`. Setting it changes what the screen shows, not its saved text.
- `time()`.

Mods add globals of their own:

```csharp
Lua.Expose("displays", context => new DisplaysApi(context)); // one object per script; its public members are the API
context.Stopped += Undo;                                     // the script stopped: undo what it changed
```

Use lower-case member names. Cockpit Displays adds `displays`.

### The Lua Computer block

Brick 507, claimed as `EvoCore.LuaComputer`, sits in the Commands tab and looks like the Sequencer Gate. It runs `scripts\<label>.lua`, where the label is the one the multitool's Tag mode gives the block; with no label it runs `computer.lua`.

Wire it with the link tool:
- Everything linked into it is an `inputs` entry, numbered from 1 in the order linked.
- Everything it's linked to is an `outputs` entry, numbered the same way.
- It never passes its own state on by itself; the script decides what each output gets.

Using it (the activate key, while looking at it) opens the Mod Settings window on the Scripts tab with its script open, and creates the script from a template if it doesn't exist yet. That needs Mod Settings 0.3.0.

```lua
function on_input(i, value)          -- called when input i changes (and for every input when links change)
  outputs.set(i, value)              -- outputs.set(i, number or true/false), outputs.all(v)
end
-- inputs.count, inputs.get(i), inputs.on(i), inputs.name(i); outputs.count, outputs.get(i), outputs.name(i)
```

Hovering over the block shows its script, its status, how many inputs and outputs it has, and its last `print` line.

## Custom block looks: `Looks`

A mod block can have its own mesh made by hand, while a stock block supplies its collider, bounds and materials. See `BLOCK-LOOKS.md` for how the game draws blocks and what's still to come (new colours, glow, textures).

```csharp
Looks.Register(BrickId, "MyMod.Widget");   // while registering the brick; uses UserData\EvoCore\Looks\MyMod.Widget.obj if it exists
```

- Start from a stock block's mesh: `get EvoCore.Looks.Export(48)` writes `Looks\export\48_Sequencer_Gate.obj`. Its header lists the block's bounds and material slots.
- In Blender, keep the default OBJ axes (Y up, -Z forward), and keep the mesh inside the bounds. A material named `slot0`, `slot1`, ... reuses that slot of the block's materials.
- Any other material name is a new surface, coloured from the `.mtl` Blender writes beside the OBJ: `Kd` is the colour, `Ke` the glow (a glowing surface takes Ke's colour). A name containing `paint` takes the paint tool's colour.
- `get EvoCore.Looks.Reload()` re-reads the `.mtl` files while playing; new surfaces or mesh changes need a restart.
- UVs run 0 to 1 per face.
- Mod bricks take their material rows from 1024 up (`Limits`), so a mod brick loading among stock ids no longer shifts the stock bricks after it.
- If the file is missing, or a mesh is broken or too big, the block keeps its stock look, and the Core tab's "Looks" line says why. Looks are read at game start, so restart after editing.
