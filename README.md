# EvoCore

A Starship EVO mod that adds the **Lua Computer**, a programmable logic block, and the shared base that other
mods (such as [Cockpit Displays](https://github.com/JustNeutral0/StarshipEVO-CockpitDisplays)) build on.

> Early test release. Built and tested against Starship EVO early-access **26w45a** (Unity 6 beta), single-player, creative.
> Game updates may break it until it's rebuilt. Expect rough edges and please report them in Issues.

## What it adds

- **Lua Computer block**: with the logic blocks. Link blocks into it with the game's link tool and they become its
  inputs; link it to blocks and they become its outputs. Press the activate key on it to open its program.
- **Computer screen**: a full editor with tabs:
  - **Graph**: wire up nodes (logic, maths, timers, ship values, blocks) instead of writing code.
  - **Script**: write Lua directly, with syntax colours and live error checking.
  - **Nodes**: write your own graph nodes in Lua.
  - **Anim**: poses and clips for the ship's rotators, hinges, pistons and rails, with a live skeleton preview.
  - **Help**: every node, every Lua function and every library function, with what each input and output means.
- **Monitors**: link a computer to a monitor block and draw on it from Lua: gauges, meters, reticles, compass
  tapes, horizons, graphs and text, with an old-CRT look. Ready-made pieces are in the `ui` library.
- **Animation and walking**: clips that blend and layer, and gaits that drive walk/run/turn/land clips from the
  ship's real movement, so legs keep pace with the ground.
- **Programs are stored in the computer block**, so they travel with the ship.

## Install

You need [Starship Uplink](https://github.com/MadTheDog/Starship-Uplink) (it sets up BepInEx for the game).

**First, the game build:** these mods are built for the **Unity 6 beta** of Starship EVO, not the default build. In Steam, right-click Starship EVO, open **Properties > Betas**, pick **unity6**, and let it update. The mods don't work on the default build (character creation breaks, for one).

1. In Uplink, choose **Add from GitHub** and enter `JustNeutral0/StarshipEVO-EvoCore`.
2. Pick the `justneutral.evocore-<version>.zip` asset, then install it into your profile.
3. Launch the game through Uplink.

Updates: use Uplink's **Update** on the installed mod.

Your scripts, graphs, custom nodes and libraries live in `UserData\EvoCore\` in the game folder.

## Known limits

- Single-player only for now: joint animation and some display links aren't synced in multiplayer.
- The Lua Computer uses brick id 500 (declared in the package, so Uplink can warn about clashes).

## Building from source

You need the .NET SDK and Starship EVO with BepInEx (Starship Uplink sets it up). ```
dotnet build EvoCore.csproj -c Release -p:Loader=BepInEx -p:Game="C:\path\to\Starship EVO"
```

`-p:Game` is your game folder (it defaults to the usual Steam location). The DLL lands in `bin\BepInEx\Release\`.

[MODDING.md](MODDING.md) explains how other mods build on EvoCore.

## Licence

MIT, see [LICENSE](LICENSE). EvoCore bundles MoonSharp (BSD 3-Clause), see
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Starship EVO itself belongs to its developers.

Part of [Starship-EVO-Mods](https://github.com/JustNeutral0/Starship-EVO-Mods).
