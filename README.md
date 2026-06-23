# Movable Storages

A Raft mod for picking up a placed storage and setting it back down somewhere else, with
everything still inside. Aim at a chest, press a key, and the normal build ghost follows your
cursor until you click to drop it.

No more emptying a chest into your hands just to nudge it one tile over.

## Install

1. Install [BepInEx 5](https://docs.bepinex.dev/) (Mono build) for Raft.
2. Put `RaftMovableStorage.dll` in `Raft/BepInEx/plugins/`.
3. Start the game once so the config file gets written.

## How to use

Look at a storage and press the move key (default `M`). The chest lifts into placement mode and a
ghost tracks your aim. Left-click to place it. Right-click, or press the key again, to cancel.

Change the key in `BepInEx/config/com.cyace84.raftmovablestorage.cfg`.

## Multiplayer

Works in co-op, as host or as a client. Only the player moving a chest needs the mod. Everyone else
just watches it move, contents and all, with stock Raft.

## Compatibility

Handles the regular storages (small, medium, large). Single-player and co-op both work.

## Build from source

```bash
DOTNET_ROOT=$HOME/.dotnet dotnet build -c Release
# if Raft isn't at the default path:
#   dotnet build -c Release -p:GameDir="/path/to/Raft"
```

The DLL ends up in `bin/Release/`. Copy it to `Raft/BepInEx/plugins/`.

## License

[MIT](LICENSE).
