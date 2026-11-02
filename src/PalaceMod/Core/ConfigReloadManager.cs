using System;
using System.IO;
using UnityEngine;

/*
    Reloads BepInEx/config/PalaceMod.cfg within a second or two of it changing on disk (e.g. `dev.py config set`), so
    settings, including each feature's Enabled switch, can be changed while the game runs. Changed entries raise
    their SettingChanged events as usual.
*/
namespace PalaceMod
{
    [ManagerOrder(-90)]
    public class ConfigReloadManager : BaseManager
    {
        private DateTime writeTime;
        private float nextCheck;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            writeTime = File.GetLastWriteTimeUtc(plugin.Config.ConfigFilePath);
        }

        public override void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            var path = Plugin.Config.ConfigFilePath;
            if (!File.Exists(path)) return;
            var written = File.GetLastWriteTimeUtc(path);
            if (written == writeTime) return;
            Plugin.Config.Reload();
            // Reloading saves changed values back, which touches the file again; don't treat that as another edit.
            writeTime = File.GetLastWriteTimeUtc(path);
            Log.Debug("Reloaded PalaceMod.cfg");
        }
    }
}
