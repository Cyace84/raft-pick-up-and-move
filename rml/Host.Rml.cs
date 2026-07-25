using System;
using System.IO;
using HMLLibrary;
using UnityEngine;

namespace PickUpMove
{
    // Raft Mod Loader host. Ships as SOURCE inside the .rmod - RML compiles every .cs in the archive
    // at load time - and is never part of the BepInEx assembly (the csproj drops the whole rml/
    // folder). Mirror image of src/hosts/Host.BepInEx.cs: it fills the same five contract members
    // declared in Plugin.cs and hands over to InitCommon(). All 3800 lines of actual mod logic are
    // shared between the two builds, unmodified.
    //
    // RML constraint, from the HMLCoreLibrary decompile (ModManager, the types.Count != 1 branch):
    // the archive must contain EXACTLY ONE subclass of Mod or the loader rejects it with "the mod
    // codebase doesn't specify a mod class or specifies more than one". Plugin is a partial class
    // spread over nine files, so it counts as one. Do not add a second Mod subclass here.
    public partial class Plugin : Mod
    {
        private void Start()
        {
            // Snapshot the loader-owned values: the lambdas below outlive this component (they hang
            // off static fields on the DontDestroyOnLoad ticker), and Mod.name / Mod.DataFolder both
            // dereference modlistEntry, which is gone once RML destroys the mod object.
            string modName = name;
            string dataFolder = DataFolder;
            var cfg = ModSettings.Load(dataFolder);

            LogSink = (lvl, msg) =>
            {
                string line = "[" + modName + "] : " + msg;
                if (lvl == PumLevel.Error) Debug.LogError(line);
                else if (lvl == PumLevel.Warning) Debug.LogWarning(line);
                else Debug.Log(line);
            };
            MoveKeyDown = () => Input.GetKeyDown(cfg.MoveKey);
            MoveKeyMain = () => cfg.MoveKey;
            LogDir = dataFolder;
            VersionText = modName + " " + version;
            LogConsole = cfg.Verbose;
            UnpatchSelf = () => { if (_harmony != null) _harmony.UnpatchAll(Guid); };   // stock Harmony spelling

            LogRelay.Init(cfg.Relay);
            InitCommon();
        }

        // RML unloads by destroying the mod object, which would leave our ticker and the Harmony
        // postfix behind on a live game. Reuse the dev-loader teardown so unload actually unloads.
        public override void UnloadMod()
        {
            __MonoLabUnload();
        }

        // Settings without a UI and without a dependency on ExtraSettingsAPI: a plain key=value file
        // in the mod's own data folder, written with defaults on first run so it is discoverable.
        // Same three knobs the BepInEx build exposes through its config file.
        private sealed class ModSettings
        {
            public KeyCode MoveKey = KeyCode.M;
            public bool Verbose;
            public bool Relay;

            public static ModSettings Load(string folder)
            {
                var s = new ModSettings();
                string path = Path.Combine(folder, "settings.txt");
                try
                {
                    if (!File.Exists(path))
                    {
                        File.WriteAllText(path,
                            "# Pick Up & Move settings. Restart the game (or reload the mod) to apply.\n" +
                            "# key: any Unity KeyCode name, e.g. M, G, LeftAlt, Mouse2.\n" +
                            "key=M\n" +
                            "# verbose: show this mod's diagnostic lines in the game log. Warnings and\n" +
                            "# errors always show regardless.\n" +
                            "verbose=false\n" +
                            "# relay: write per-session log files here and, as a client, relay them to\n" +
                            "# the host, so a co-op issue can be read off one machine.\n" +
                            "relay=false\n");
                        return s;
                    }

                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "key")
                        {
                            try { s.MoveKey = (KeyCode)Enum.Parse(typeof(KeyCode), v, true); }
                            catch { Debug.LogWarning("[Pick Up & Move] : unknown key '" + v + "' in settings.txt, keeping M."); }
                        }
                        else if (k == "verbose") s.Verbose = v.ToLowerInvariant() == "true";
                        else if (k == "relay") s.Relay = v.ToLowerInvariant() == "true";
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Pick Up & Move] : could not read settings.txt (" + ex.Message + "), using defaults.");
                }
                return s;
            }
        }
    }
}
