using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Rewired;
using System;

namespace PalaceMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    internal static Plugin Instance { get; private set; }
    private static Player input;
    // Rewired initializes after plugins load, so resolve the player on first use.
    internal static Player Input => input ??= ReInput.isReady ? ReInput.players.GetPlayer(0) : null;
    private readonly List<BaseManager> managers = [];
    internal IReadOnlyList<BaseManager> Managers => managers;

    // Managers in this namespace (src/PalaceMod/Experimental/) are never registered.
    private const string ExperimentalNamespace = "PalaceMod.Experimental";

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;

        // Clear separator for logs
        for (int i = 0; i < 10; i++)
        {
            Logger.LogInfo("==========================================================================");
        }

        // Every concrete BaseManager outside Experimental, ordered by [ManagerOrder] then name. Each one applies its
        // own nested Harmony patches (see BaseManager), so a feature is added just by adding its file.
        var types = AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(BaseManager).IsAssignableFrom(t))
            .Where(t => t.Namespace != ExperimentalNamespace && t.Namespace?.StartsWith(ExperimentalNamespace + ".") != true)
            .OrderBy(t => t.GetCustomAttribute<ManagerOrderAttribute>()?.Order ?? 0)
            .ThenBy(t => t.Name, StringComparer.Ordinal);
        foreach (var type in types)
        {
            try
            {
                var manager = (BaseManager)Activator.CreateInstance(type);
                manager.Initialize(this);
                typeof(Feature<>).MakeGenericType(type).GetProperty(nameof(Feature<BaseManager>.Instance))
                    .SetValue(null, manager);
                Keybinds.RegisterFeature(manager);
                managers.Add(manager);
            }
            catch (Exception e)
            {
                Logger.LogError($"{type.Name} failed to load and is skipped: {e}");
            }
        }

        foreach (var manager in managers.ToList())
        {
            Run(manager, "Awake", () => manager.Awake(this), removeOnError: true);
        }
        Logger.LogInfo($"Managers: {string.Join(", ", managers.Select(m => m.IsEnabled ? m.GetType().Name : $"{m.GetType().Name} (disabled)"))}");

        ShowMessage("PalaceMod loaded!");
    }

    private void Start()
    {
        foreach (var manager in managers.ToList()) Run(manager, "Start", manager.Start, removeOnError: true);
    }

    private readonly HashSet<string> reportedErrors = [];

    private void Update()
    {
        // Isolate managers so one failing feature does not stop the others from updating.
        foreach (var manager in managers)
        {
            if (manager.IsEnabled) Run(manager, "Update", manager.Update, removeOnError: false);
        }
    }

    private void Run(BaseManager manager, string phase, Action action, bool removeOnError)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            if (removeOnError)
            {
                // A manager that couldn't initialise is dropped, along with its patches.
                Logger.LogError($"{manager.GetType().Name}.{phase} failed; the manager is disabled: {e}");
                managers.Remove(manager);
                managerCache.Clear();
                manager.UnpatchAll();
                typeof(Feature<>).MakeGenericType(manager.GetType()).GetProperty(nameof(Feature<BaseManager>.Instance))
                    .SetValue(null, null);
            }
            else if (reportedErrors.Add($"{manager.GetType().Name}: {e}"))
            {
                Logger.LogError($"{manager.GetType().Name}.{phase} failed (further identical errors suppressed): {e}");
            }
        }
    }

    internal void ShowMessage(string message) => Notify.Show(message);

    private readonly Dictionary<Type, BaseManager> managerCache = [];

    // Prefer Feature<T>.Instance; kept for existing callers.
    internal T GetManager<T>() where T : class, IManager
    {
        if (!managerCache.TryGetValue(typeof(T), out var manager))
        {
            manager = managers.OfType<T>().FirstOrDefault() as BaseManager;
            managerCache[typeof(T)] = manager;
        }
        return manager as T;
    }

    private void OnDestroy()
    {
        foreach (var manager in managers)
        {
            manager.UnpatchAll();
            manager.OnDestroy();
        }
    }
}
