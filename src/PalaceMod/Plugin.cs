using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Rewired;
using System;

namespace PalaceMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    private Harmony harmony;
    internal static Plugin Instance { get; private set; }
    internal static Player Input { get; private set; }
    private readonly List<IManager> managers = [];

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;

        harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        // Clear separator for logs
        for (int i = 0; i < 10; i++)
        {
            Logger.LogInfo("==========================================================================");
        }

        // Add managers
        managers.Add(new UIManager());
        managers.Add(new GoldManager());
        managers.Add(new BuildPreviewManager());
        managers.Add(new FormationManager());
        managers.Add(new EnemyPreviewManager());
        managers.Add(new BuildingDestructionEffectBugFixManager());
        managers.Add(new IncomeDisplayManager());
        managers.Add(new PauseMenuScoreManager());
        managers.Add(new HealthFixManager());
        managers.Add(new LoadoutRandomizerManager());
        managers.Add(new UnitRangeManager());
        managers.Add(new WikiDataManager());

        // Apply patches only for loaded manager types
        var loadedManagerTypes = managers.Select(m => m.GetType()).ToList();
        var allTypes = typeof(Plugin).Assembly.GetTypes();

        foreach (var type in allTypes)
        {
            bool shouldPatch = false;

            // If it's a manager type we've loaded
            if (loadedManagerTypes.Contains(type))
            {
                shouldPatch = true;
            }
            // If it's nested within a manager type we've loaded
            else if (type.IsNested)
            {
                Type declaringType = type.DeclaringType;
                while (declaringType != null)
                {
                    if (loadedManagerTypes.Contains(declaringType))
                    {
                        shouldPatch = true;
                        break;
                    }
                    declaringType = declaringType.DeclaringType; // Handle nested types within nested types
                }
            }

            if (shouldPatch) harmony.PatchAll(type);
        }

        // Initialize managers
        foreach (var manager in managers) manager.Awake(this);

        ShowMessage("PalaceMod loaded!");
    }

    private void Start()
    {
        Input = ReInput.players.GetPlayer(0);

        foreach (var manager in managers) manager.Start();
    }

    private void Update()
    {
        foreach (var manager in managers) manager.Update();
    }

    internal void ShowMessage(string message)
    {
        Logger.LogInfo(message);
        GetManager<UIManager>()?.ShowMessage(message);
    }

    internal T GetManager<T>() where T : class, IManager
    {
        return managers.OfType<T>().FirstOrDefault();
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
        foreach (var manager in managers) manager.OnDestroy();
    }
}
