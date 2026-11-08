using System;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using PalaceMod.Builder;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio;
using PalaceMod.Builder.Studio.Requirements;
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
            RegisterStudio();
            RegisterDevCommand();
        }

        private static LinkTool link;
        private string thumbScene;
        private float thumbAt;

        // A main map seen as it is (the Studio on a map as it is, or a normal run) gets its start-popup thumbnail once,
        // a few seconds after it loaded.
        public override void Update()
        {
            var scene = SceneManager.GetActiveScene().name;
            if (scene != thumbScene) { thumbScene = scene; thumbAt = UnityEngine.Time.unscaledTime + 4f; return; }
            if (thumbAt < 0f || UnityEngine.Time.unscaledTime < thumbAt || !Feature<BuilderManager>.On) return;
            thumbAt = -1f;
            if (scene.Contains(' ') || Builder.Studio.Start.Thumbnails.Has(scene) || !Builder.Studio.Start.StartPopup.Maps().Any(h => h.Scene == scene)) return;
            var level = Studio.Running ? Studio.Level : BuilderServices.Sessions.Current?.Level;
            if (level?.Base != null) return;
            Builder.Studio.Start.Thumbnails.Capture(scene);
        }

        private static void RegisterStudio()
        {
            RequirementState.Init();
            var key = Keybinds.Register("Builder", "studio-link", "Studio: link", new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.K),
                "In the Studio: the Link tool (what needs what).", KeybindFlags.AllowInMenus, StudioContexts.Edit);
            BuiltPreview.HoldKey = Keybinds.Register("Builder", "studio-built-preview", "Studio: built preview (hold)", new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.LeftAlt),
                "In the Studio: while held, slots show their buildings.", KeybindFlags.AllowInMenus, StudioContexts.Edit);
            Studio.AddTool(link = new LinkTool(key));
            Studio.AddOverlay(RequirementArrows.Instance);
            Studio.AddOverlay(BuiltPreview.Instance);
            Studio.AddInspectorSection("requirements", 200, RequirementInspector.AppliesTo, RequirementInspector.Build);
            Studio.AddLabelProvider(SlotKinds.Provide);
            Studio.AddContextItems(RequirementHooks.ContextItems);
            Studio.AddDeleteGuard(RequirementHooks.GuardDelete);
            Studio.AddViewToggle("built", "Built preview", () => BuiltPreview.On, on => BuiltPreview.On = on);
            Studio.AddViewToggle("autolink", "Auto-link new slots", () => RequirementHooks.AutoLink, on => RequirementHooks.AutoLink = on);
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
                    case "base":
                    {
                        // An unsaved one-level pack on a map with an empty base (vanilla waves), played at once.
                        string kind = args.ElementAtOrDefault(1) ?? throw new ArgumentException("usage: builder-req base cleared|flat <map>");
                        string scene = string.Join(" ", args.Skip(2));
                        var pack = new DesignPack { Id = "dev.w3base", Name = "Base test", Author = "dev" };
                        pack.Levels.Add(new LevelDef { Id = "base", Name = $"{kind} {scene}", Host = scene, Base = kind });
                        if (!BuilderServices.Sessions.Launch(new SessionRequest { Pack = pack, LevelId = "base" }, out string error)) throw new InvalidOperationException(error);
                        return $"launching {kind} {scene}";
                    }
                    case "link":
                    {
                        // Studio: link <dep key>|<dep key>... > <prereq key> [level]
                        int gt = rest.LastIndexOf('>');
                        if (gt < 0) throw new ArgumentException("usage: builder-req link <key>|<key> > <key> [level]");
                        var deps = rest.Substring(0, gt).Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        var right = rest.Substring(gt + 1).Trim();
                        int lvl = 0;
                        int sp = right.LastIndexOf(' ');
                        if (sp > 0 && int.TryParse(right.Substring(sp + 1), out int parsed) && !right.EndsWith(")")) { lvl = parsed; right = right.Substring(0, sp).Trim(); }
                        string target = RequirementState.Ref(right) ?? throw new ArgumentException($"not a slot key: {right}");
                        bool castle = target == RequirementState.Castle;
                        int n = RequirementEdits.Apply(deps, new SlotRequirement { Slot = castle ? SlotRefs.Castle : target, Level = lvl }, null);
                        return $"{n} linked; {string.Join("; ", deps.Select(d => $"{d}: {RequirementState.DescribeFor(d)}"))}";
                    }
                    case "click":
                    {
                        // Studio: the Link tool's click on a key (ctrl/shift/alt flags first): click [ctrl] [shift] [alt] <key>
                        bool ctrl = false, shift = false, alt = false;
                        var words = args.Skip(1).ToList();
                        while (words.Count > 0 && words[0] is "ctrl" or "shift" or "alt")
                        {
                            if (words[0] == "ctrl") ctrl = true; else if (words[0] == "shift") shift = true; else alt = true;
                            words.RemoveAt(0);
                        }
                        Studio.SetTool(LinkTool.ToolId);
                        link.Click(string.Join(" ", words), ctrl, shift, alt, new UnityEngine.Vector2(UnityEngine.Screen.width / 2f, UnityEngine.Screen.height / 2f));
                        return $"pending: {string.Join(", ", link.Pending)}; tool {Studio.ToolId}";
                    }
                    case "chain":
                    {
                        // Studio: chain <branching|nearest|order> [apply] (over the selection)
                        var mode = args.ElementAtOrDefault(1) switch { "nearest" => ChainMode.NearestNeighbour, "order" => ChainMode.SelectionOrder, _ => ChainMode.Branching };
                        var plan = ChainPopup.Plan(Studio.Selection, mode, 1, 0);
                        if (args.Contains("apply")) ChainPopup.Apply();
                        else if (args.Contains("popup")) ChainPopup.Open(Studio.Selection);
                        return "\n" + string.Join("\n", plan.Select(p => $"{RequirementState.Name(p.slot)} needs {RequirementState.Describe(p.requires)}"));
                    }
                    case "label":
                    {
                        var l = Studio.LabelFor(rest);
                        return $"color={l.Color} icon={l.Icon?.name} costs={l.Costs} badge={l.Badge}\n{l.Card}";
                    }
                    case "needs":
                        return string.Join("\n", Studio.Items.Where(i => RequirementState.Ref(i.Key) != null && !i.Removed).Select(i => $"{i.Key}: {RequirementState.DescribeFor(i.Key)}"));
                    case "start":
                    {
                        // The start popup for a pack: start <pack> [create <map> <0|1|2> <name...>]
                        Builder.Editor.BuilderEditor.OpenPack(args.ElementAtOrDefault(1));
                        var draft = Builder.Editor.BuilderEditor.Draft ?? throw new InvalidOperationException("no draft");
                        Builder.Studio.Start.StartPopup.Open(draft);
                        if (args.ElementAtOrDefault(2) != "create") return $"start popup open; maps: {string.Join(", ", Builder.Studio.Start.StartPopup.Maps().Select(h => h.Scene))}";
                        Builder.Studio.Start.StartPopup.Choose(args.ElementAtOrDefault(3), int.Parse(args.ElementAtOrDefault(4) ?? "1"), args.Length > 5 ? string.Join(" ", args.Skip(5)) : null);
                        return $"created {Builder.Studio.Start.StartPopup.Create()}";
                    }
                    case "thumb":
                        return Builder.Studio.Start.Thumbnails.Capture(SceneManager.GetActiveScene().name, force: true) ? $"saved in {Builder.Studio.Start.Thumbnails.Folder}" : "not captured";
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
