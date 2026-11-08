using System;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using PalaceMod.Builder;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine.SceneManagement;

/*
    Builder workstream W3 (docs/builder-ux.md): slot requirements, the built-look previews and the empty-map bases.
    Part of the Builder; registers the Studio's Link tool, requirement overlays, inspector section and start screen,
    and the `builder-req` dev command for checking requirements on a live level.
*/
namespace PalaceMod
{
    public class BuilderRequirementsManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            RegisterDevCommand();
        }

        private static void RegisterDevCommand()
        {
            DevCommands.Register("builder-req",
                "builder-req import <pack.json> | play <pack> <level> | slots [filter] | upgrade <slot> | unlocks <slot> | proxy <slot> [level]   requirements, bases and previews on the live level", args =>
            {
                string sub = args.FirstOrDefault()?.ToLowerInvariant();
                string rest = string.Join(" ", args.Skip(1));
                switch (sub)
                {
                    case "import":
                    {
                        var issues = new System.Collections.Generic.List<ValidationIssue>();
                        var pack = PackSerializer.Read(File.ReadAllText(rest), issues) ?? throw new InvalidOperationException(string.Join("; ", issues));
                        BuilderServices.Packs.Save(pack);
                        BuilderServices.Packs.Refresh();
                        var problems = BuilderServices.Packs.Validate(pack);
                        return $"saved {pack.Id} (v{pack.Version}); {problems.Count} issues{(problems.Count > 0 ? ":\n" + string.Join("\n", problems) : "")}";
                    }
                    case "play":
                    {
                        var entry = BuilderServices.Packs.Find(args.ElementAtOrDefault(1)) ?? throw new ArgumentException($"no pack \"{args.ElementAtOrDefault(1)}\"");
                        string level = args.ElementAtOrDefault(2) ?? entry.Pack.Levels.FirstOrDefault()?.Id;
                        if (!BuilderServices.Sessions.Launch(new SessionRequest { Pack = entry.Pack, LevelId = level }, out string error)) throw new InvalidOperationException(error);
                        return $"launching {entry.Id}/{level}";
                    }
                    case "slots":
                    {
                        var sb = new StringBuilder();
                        foreach (var s in LayoutScene.Slots(SceneManager.GetActiveScene()).Where(s => rest.Length == 0 || s.name.IndexOf(rest, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            int cap = s.RequiredRootLevelDifference;
                            sb.AppendLine($"{s.name}: active={s.gameObject.activeSelf} level={s.Level}/{s.OwnUpgrades.Count} needs={(s.StartDeactivated ? $"{s.ActivatorBuilding?.name ?? "(missing)"}>{s.ActivatorLevel}" : "start")}" +
                                $"{(cap != -100 ? $" cap={cap}" : "")}{(s.ActivatorUpgradesThis ? " addon" : "")}{(LayoutScene.IsCastle(s) ? " castle" : "")} at {s.transform.position.x:0},{s.transform.position.y:0.#},{s.transform.position.z:0}");
                        }
                        return "\n" + sb;
                    }
                    case "upgrade":
                    {
                        var slot = Slot(rest);
                        if (!slot.CanBeUpgraded) return $"{slot.name} can't be upgraded (level {slot.Level})";
                        slot.TryToBuildOrUpgradeAndPay(null, false);
                        return $"{slot.name} is now level {slot.Level}";
                    }
                    case "unlocks":
                    {
                        var slot = Slot(rest);
                        var list = slot.GetBuildSlotsThatWillUnlockWhenUpgraded();
                        return $"{slot.name} at level {slot.Level}: the game previews {list.Count}: {string.Join(", ", list.Select(s => s.name))}";
                    }
                    case "proxy":
                    {
                        // Built preview check: makes a proxy of a slot beside it and shows a level.
                        var parts = rest.Split(' ');
                        int level = parts.Length > 1 && int.TryParse(parts[parts.Length - 1], out int l) ? l : 1;
                        string name = parts.Length > 1 && int.TryParse(parts[parts.Length - 1], out _) ? string.Join(" ", parts.Take(parts.Length - 1)) : rest;
                        var slot = Slot(name);
                        var proxy = LayoutScene.Proxy(slot.gameObject, null, "PalaceBuilder proxy test");
                        proxy.transform.position = slot.transform.position + new UnityEngine.Vector3(8f, 0f, 0f);
                        var copy = UnityEngine.Object.Instantiate(proxy);
                        copy.transform.position = slot.transform.position + new UnityEngine.Vector3(-8f, 0f, 0f);
                        bool ok = LayoutScene.ShowBuilt(proxy, level) & LayoutScene.ShowBuilt(copy, level, ghost: false);
                        return $"proxy of {slot.name}: {LayoutScene.BuiltLevels(proxy)} levels, copy keeps them: {LayoutScene.BuiltLevels(copy)}; shown at {level}: {ok}";
                    }
                    default:
                        throw new ArgumentException("usage: builder-req import|play|slots|upgrade|unlocks|proxy");
                }
            });
        }

        private static BuildSlot Slot(string name) =>
            LayoutScene.Slots(SceneManager.GetActiveScene()).FirstOrDefault(s => s.name == name) ?? throw new ArgumentException($"no build slot \"{name}\"");
    }
}
