# Pick Up & Move

A Raft mod for repositioning things you've already built. Aim at a placed object, press a key, and
the normal build ghost follows your cursor until you click to set it back down — with its contents
and state intact.

It moves crafted **placeables** (storage, devices, decor), **not** the raft structure itself
(foundations, walls, floors, pillars). No more emptying a chest into your hands just to nudge
it one tile over.

## What it moves (keeping state)

- **Storage** — small, medium, large, wall cabinets. Items stay inside.
- **Cooking** — cooking pot, grill, juicer, smelter, purifier. Recipe, progress and slots survive.
- **Crop plots, scarecrows, beehives** — plants, integrity and combs survive.
- **Battery chargers, biofuel refiners, fuel tanks** — batteries, fuel and contents stay in.
- **Pipes** — the network rewires itself around the new spot.
- **Signs / plaques** — keeps the written text.
- **Decor, furniture, plain placeables.** Items standing on top are carried along — the placement
  ghost previews the whole group (and live contents like batteries), tinted vanilla green/red.
  Paint survives.

Two hard exceptions: the **detail plank** (its stretch mechanic doesn't survive a move) and a
**zipline with a rope attached** — detach the rope first (the mod says so in-game). If a specific
move would lose device state (e.g. a filled container onto a different surface type), the mod
**refuses with an on-screen note** rather than risk it. Notes are localized (11 languages).
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

Works in co-op, as host or as a client — anyone with the mod can move things. To *see* a move
happen live, a player needs the mod too: moves travel over the mod's own channel, so a modless
peer won't see the object shift until they rejoin (the new position is saved, so it's correct
after a reload). For a clean co-op experience, everyone should run the mod.

## Build from source

```bash
DOTNET_ROOT=$HOME/.dotnet dotnet build -c Release
# if Raft isn't at the default path:
#   dotnet build -c Release -p:GameDir="/path/to/Raft"
```

The DLL ends up in `bin/Release/`. Copy it to `Raft/BepInEx/plugins/`.

## License

[MIT](LICENSE).
