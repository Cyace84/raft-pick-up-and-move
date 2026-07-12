# Pick Up & Move

A Raft mod that lets you move things you've already built. Aim at a placed object, press a key, and
the normal build ghost follows your cursor until you click to set it back down, with its contents
and state intact.

It moves crafted **placeables** (storage, devices, decor), **not** the raft structure itself
(foundations, walls, floors, pillars). No more emptying a chest into your hands just to nudge
it one tile over.

## What it moves (keeping state)

- Storage (small, medium, large, wall cabinets), items still inside.
- Cooking pot, grill, juicer, smelter, purifier: recipe, progress and slots survive.
- Crop plots, scarecrows, beehives: plants, integrity, combs all make the trip.
- Battery chargers, biofuel refiners and fuel tanks keep whatever's in them.
- Pipes rewire themselves around the new spot.
- Signs and plaques keep their text.
- Decor, furniture, plain placeables. Anything standing on top is carried along, and the
  placement ghost previews the whole group (live contents like batteries included), tinted
  vanilla green/red. Paint survives.

Two hard exceptions: the detail plank (its stretch mechanic doesn't survive a move) and a
zipline with a rope attached. Detach the rope first; the mod says so in-game. If a specific
move would lose device state (say, a filled container onto a different surface type), the mod
refuses with an on-screen note rather than risk it. Notes are localized (11 languages).
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

```bash
DOTNET_ROOT=$HOME/.dotnet dotnet build -c Release
# if Raft isn't at the default path:
#   dotnet build -c Release -p:GameDir="/path/to/Raft"
```

The DLL ends up in `bin/Release/`. Copy it to `Raft/BepInEx/plugins/`.

## License

[MIT](LICENSE).
