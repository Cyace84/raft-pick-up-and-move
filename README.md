# Pick Up & Move

A Raft mod that lets you move things you've already built. Aim at a placed object, press a key, and
the normal build ghost follows your cursor until you click to set it back down, with its contents
and state intact.

It moves crafted **placeables** (storage, devices, decor), **not** the raft structure itself
(foundations, walls, floors, pillars). No more emptying a chest into your hands just to nudge
it one tile over.

## What it moves (keeping state)

- Storage of any size, items still inside.
- Cooking pot, grill, juicer, smelter, purifier: recipe, progress and slots survive.
- Crop plots take the plants and their growth stage; beehives keep the combs.
- Battery chargers, biofuel refiners and fuel tanks keep whatever's in them.
- Pipes rewire themselves around the new spot.
- The receiver keeps its frequency; antennas move on their own, wire and all.
- Sprinklers keep their water and battery.
- Sails, steering wheels, engines and anchors move without a rebuild.
- Signs keep their text.
- Decor, furniture, lights; paint stays on. Anything standing on top is carried along, and the
  placement ghost previews the whole group (live contents like batteries and purifier water
  included), tinted vanilla green/red.

One exception: the detail plank, whose stretch mechanic doesn't survive a move. If a move can't
go through, a short note at the top of the screen tells you why, in your game's language.
Nothing is ever silently dropped.

## Install

1. Install [BepInEx 5](https://docs.bepinex.dev/) (Mono build) for Raft.
2. Put `PickUpMove.dll` in `Raft/BepInEx/plugins/`.
3. Start the game once so the config file gets written.

## How to use

Look at a movable object and press the move key (default `M`). It lifts into placement mode and a
ghost tracks your aim. Left-click to place it. Right-click, or press the key again, to cancel and
leave it where it was.

Change the key in `BepInEx/config/com.cyace84.pickupmove.cfg`.

## Multiplayer

Works in co-op, as host or as a client. Anyone with the mod can move things. To *see* a move
happen live, a player needs the mod too: moves travel over the mod's own channel, so a modless
peer won't see the object shift until they rejoin (the new position is saved, so it's correct
after a reload). For a clean co-op game, have everyone run the mod.

## Build from source

One source tree builds for both loaders. Reference paths default to a stock Windows Steam install;
anywhere else (Proton/Wine/CrossOver, other Steam library) copy `Local.props.example` to
`Local.props` and set `GameDir` / `RmlRoot` — it is gitignored, so machine paths never land in the
repo. A wrong path fails with one readable sentence, not a wall of CS0246.

```bash
export DOTNET_ROOT=$HOME/.dotnet

# BepInEx 5 plugin -> bin/Release/PickUpMove.dll, copy to Raft/BepInEx/plugins/
dotnet build -c Release

# Raft Mod Loader package -> dist/"Pick Up & Move <ver>.rmod", copy to Raft/mods/
./pack-rmod.sh
```

Everything can also be set per-invocation: `dotnet build -c Release -p:GameDir="/path/to/Raft"`.

The `.rmod` ships **sources** (RML compiles mods at load time), so `pack-rmod.sh` is what produces
the shipped artifact. `rml/PickUpMove.Rml.csproj` compiles that same file set against RML's own
assemblies purely to catch errors at the desk instead of at a game launch — it builds nothing that
ships. Both builds embed the git short SHA (`gen-stamp.sh`) and log it at startup, so the running
build is always identifiable; commit before building if you want a clean stamp.

## License

[MIT](LICENSE).
