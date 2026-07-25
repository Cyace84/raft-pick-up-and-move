using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PickUpMove
{
    // BepInEx host. One of two entry points (the other is rml/Host.Rml.cs, shipped inside the .rmod
    // and never compiled into this assembly - see the csproj's Compile Remove). Everything in here is
    // BepInEx-only: the plugin attribute, the config file, the logger. It fills in the host contract
    // declared in Plugin.cs and hands over to InitCommon().
    [BepInPlugin(Guid, "Pick Up & Move", "1.0.0")]
    public partial class Plugin : BaseUnityPlugin
    {
        public static ConfigEntry<KeyboardShortcut> MoveKey;
        public static ConfigEntry<bool> RelayLogs;
        public static ConfigEntry<bool> LogToConsole;
        public static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            MoveKey = Config.Bind(
                "General",
                "MoveStorageKey",
                new KeyboardShortcut(KeyCode.M),
                "Aim at a storage and press this to pick it up (with its contents) into placement mode. " +
                "Left-click to drop it at the new spot. Press the key again or right-click to cancel.");

            LogToConsole = Config.Bind(
                "Logging",
                "LogToConsole",
                false,
                "Show this mod's diagnostic lines in the BepInEx console / LogOutput.log. Off by default " +
                "for a quiet game; turn on to watch what the mod is doing. Warnings and errors always show, " +
                "and the one load line (with the build stamp) always shows.");
            LogConsole = LogToConsole.Value;

            RelayLogs = Config.Bind(
                "Logging",
                "RelayLogs",
                false,
                "Debug aid, off by default. When playing as a client, relay this mod's log lines to " +
                "the host over Steam P2P so a co-op issue can be diagnosed from one machine. Sends this " +
                "mod's own lines plus the game's errors/exceptions and block/storage lookup failures - " +
                "nothing else. Per-session files (BepInEx/PickUpMoveLogs/) are always written locally " +
                "regardless of this setting.");

            // ---- host contract ----
            LogSink = (lvl, msg) => Log?.Log(ToBepInEx(lvl), msg);
            MoveKeyDown = () => MoveKey.Value.IsDown();
            MoveKeyMain = () => MoveKey.Value.MainKey;   // read live: the key is rebindable at runtime
            LogDir = Path.Combine(Paths.BepInExRootPath, "PickUpMoveLogs");
            VersionText = $"{Info.Metadata.Name} {Info.Metadata.Version}";
            UnpatchSelf = () => Harmony.UnpatchID(Guid);   // HarmonyX spelling

            LogRelay.Init(RelayLogs.Value);
            InitCommon();
        }

        private static LogLevel ToBepInEx(PumLevel lvl)
        {
            switch (lvl)
            {
                case PumLevel.Error:   return LogLevel.Error;
                case PumLevel.Warning: return LogLevel.Warning;
                case PumLevel.Debug:   return LogLevel.Debug;
                default:               return LogLevel.Info;
            }
        }
    }
}
