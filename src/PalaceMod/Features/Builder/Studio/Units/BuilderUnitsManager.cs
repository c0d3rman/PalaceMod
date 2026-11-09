using System;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using PalaceMod.Builder;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Studio;
using PalaceMod.Builder.Studio.Units;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Builder: where a training building's units stand (docs/builder-ux.md, "Unit positions"). Registers the Studio's
    unit discs, Inspector section and context row (Studio/Units/StudioUnits.cs) and the `builder-units` dev command.
    The play side (unit homes moved onto walkable ground at level start) is Level/UnitHomes, run by BuilderLevelManager.
    Part of the Builder feature.
*/
namespace PalaceMod
{
    public class BuilderUnitsManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            Studio.AddOverlay(StudioUnits.Instance);
            Studio.AddInspectorSection("units", 150, keys => keys.Select(Studio.Find).Any(StudioUnits.Trains), BuildSection);
            Studio.AddContextItems(keys =>
            {
                var items = keys.Select(Studio.Find).Where(StudioUnits.Trains).ToList();
                return items.Any(i => StudioUnits.Spots(i).Any(s => !s.Ok))
                    ? [("Move units onto ground", () => StudioUnits.MoveOntoGround(items))]
                    : [];
            });
            Studio.ItemsChanged += StudioUnits.Invalidate;
            RegisterDevCommand();
        }

        private static void BuildSection(ModPage page, System.Collections.Generic.IReadOnlyList<string> keys)
        {
            var items = keys.Select(Studio.Find).Where(StudioUnits.Trains).ToList();
            if (items.Count == 0) return;
            var spots = items.SelectMany(StudioUnits.Spots).ToList();
            int off = spots.Count(s => !s.Ok);
            StudioDock.SmallHeader(page, "Units");
            string red = ModStyle.Hex(ModStyle.KindColor(NotifyKind.Error));
            const string tip = "Where the building's units stand (the discs on the map). Drag a disc to move one. In play, units off walkable ground go to the nearest ground.";
            if (off > 0) page.Row("Off the ground", $"<color=#{red}>{off} of {spots.Count}</color>", tip);
            else page.Row("On the ground", $"{spots.Count} of {spots.Count}", tip);
            if (off > 0)
                page.Row("Move units onto ground", "", "Moves each unit in water, on blocked or steep ground to the nearest free ground.", () => StudioUnits.MoveOntoGround(items));
            if (items.Any(StudioUnits.HasCustom))
                page.Row("Reset unit positions", "", "Back to the map's positions around the building.", () => StudioUnits.Reset(items));
        }

        private static void RegisterDevCommand()
        {
            DevCommands.Register("builder-units",
                "builder-units [live] | select | settle | studio [key] | ground [key] | set <key> <index> <x> <z> | reset [key]   training slots' unit positions (live level or Studio)", args =>
            {
                string sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "live";
                StudioItem One(int at)
                {
                    if (args.Length > at) return Studio.Find(args[at]) ?? throw new ArgumentException($"no Studio item {args[at]}");
                    return Studio.Selection.Select(Studio.Find).FirstOrDefault(StudioUnits.Trains) ?? throw new ArgumentException("select a training slot or name its key");
                }
                System.Collections.Generic.IEnumerable<StudioItem> Many(int at) =>
                    args.Length > at ? [One(at)] : StudioScene.Items.Where(StudioUnits.Trains);
                switch (sub)
                {
                    case "live":
                    {
                        var scene = SceneManager.GetActiveScene();
                        var lines = LayoutScene.Slots(scene).Select(s => (s, u: UnitHomes.Units(s))).Where(x => x.u.Count > 0).Select(x =>
                            $"{x.s.name} @ {F(x.s.transform.position)}: " + string.Join("  ", x.u.Select(u =>
                            {
                                var p = u.transform.position;
                                bool on = UnitHomes.Nearest(p, UnitHomes.MaskOf(u)) is Vector3 n && new Vector2(n.x - p.x, n.z - p.z).magnitude <= UnitSpread.OnGround;
                                return $"{F(p)}{(UnitHomes.HomeOf(u) is Vector3 h ? $" home {F(h)}" : "")}{(on ? "" : " OFF")}{(u.gameObject.activeInHierarchy ? " active" : "")}";
                            })));
                        return UnitHomes.LastSettle + "\n" + string.Join("\n", lines);
                    }
                    case "select":
                    {
                        // Every "command these units" ring (SelectUnitUI) in the level and whether its selector drives it.
                        var driven = new System.Collections.Generic.HashSet<SelectUnitUI>(
                            UnityEngine.Object.FindObjectsOfType<UnitProductionSelector>(true)
                                .Select(s => Traverse.Create(s).Field("mySelectUnitUI").GetValue<SelectUnitUI>()).Where(u => u != null));
                        return string.Join("\n", UnityEngine.Object.FindObjectsOfType<SelectUnitUI>(true).Select(ui =>
                        {
                            var canvas = Traverse.Create(ui).Field("canvas").GetValue<GameObject>();
                            var fill = Traverse.Create(ui).Field("mpImage").GetValue<UnityEngine.UI.Image>();
                            var slot = ui.GetComponentInParent<BuildSlot>(true);
                            return $"{slot?.name ?? ui.transform.root.name}: {(driven.Contains(ui) ? "driven" : "ORPHAN")} " +
                                $"shown={canvas != null && canvas.activeInHierarchy} fill={(fill != null ? fill.fillAmount.ToString("0.00", CultureInfo.InvariantCulture) : "-")} @ {F(ui.transform.position)}";
                        }));
                    }
                    case "settle":
                        UnitHomes.Settle(SceneManager.GetActiveScene());
                        return UnitHomes.LastSettle;
                    case "studio":
                        return string.Join("\n", Many(1).Select(i => $"{i.Key} ({i.Name}){(StudioUnits.HasCustom(i) ? " custom" : "")}: " +
                            string.Join("  ", StudioUnits.Spots(i).Select(s => $"{s.Index} {s.Spot.Name} {F(s.World)}{(s.Ok ? "" : " OFF")}"))));
                    case "ground":
                        return $"{StudioUnits.MoveOntoGround(Many(1))} units moved onto ground";
                    case "set":
                    {
                        if (args.Length < 5) throw new ArgumentException("set <key> <index> <x> <z>");
                        var item = One(1);
                        float x = float.Parse(args[3], CultureInfo.InvariantCulture), z = float.Parse(args[4], CultureInfo.InvariantCulture);
                        StudioUnits.SetOffset(item, int.Parse(args[2], CultureInfo.InvariantCulture), new Vector3(x, 0f, z));
                        return string.Join("  ", StudioUnits.Spots(item).Select(s => $"{s.Index} {F(s.World)}{(s.Ok ? "" : " OFF")}"));
                    }
                    case "reset":
                        StudioUnits.Reset(Many(1));
                        return "reset";
                    default:
                        throw new ArgumentException("builder-units [live] | select | settle | studio [key] | ground [key] | set <key> <index> <x> <z> | reset [key]");
                }
            });
        }

        private static string F(Vector3 v) => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";
    }
}
