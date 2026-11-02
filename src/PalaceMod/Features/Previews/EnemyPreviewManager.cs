using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Object = UnityEngine.Object;

/*
    Adds a "Spawns:" panel to the pause menu, beside the game's own night preview. Since 2.13 the game shows the wave's
    enemies (with stats on hover) in one grid; this splits them up by where they spawn, and at night shows how many of
    each have spawned so far. The native grid, its navigation and its hover panel are left alone.

    Layout: the panel lives in its own container ("PalaceMod Spawns") parented to the pause frame's "Title " column, so it
    scales with the menu. Its left edge sits just right of the Night selector (or the native grid, if that's wider), its
    bottom just above the "Units:" row, and it grows upward beside the menu buttons. It never reaches the right third of
    the screen, which the score panel uses.
*/
namespace PalaceMod
{
    public class EnemyPreviewManager : BaseManager
    {
        public override string FeatureSection => "Enemy previews";
        public override string FeatureDescription => "Adds a panel to the pause menu that groups the night's enemies by where they spawn. At night it shows how many of each have spawned so far.";

        private const string ContainerName = "PalaceMod Spawns";
        private const float IconSize = 50f, IconSpacing = 14f, CaptionHeight = 22f, RowSpacing = 24f;
        private const float LabelMaxWidth = 140f, LabelGap = 16f, GapFromNative = 40f, GapAboveUnits = 18f;
        private const float MinLeft = 300f, NightArrowsWidth = 35f, MaxRightFraction = 0.55f;
        private const int IconsPerLine = 4;

        private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, int> PreviewWave =
            AccessTools.FieldRefAccess<PauseUILoadoutHelper, int>("currentPreviewWavenumber");
        private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, int> TrueWave =
            AccessTools.FieldRefAccess<PauseUILoadoutHelper, int>("currentTrueWaveNumver");
        private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, List<ThronefallUIElement>> NativeEnemyGrid =
            AccessTools.FieldRefAccess<PauseUILoadoutHelper, List<ThronefallUIElement>>("enemyGrid");

        private PauseUILoadoutHelper helper;
        private GameObject container;
        private readonly HashSet<ThronefallUIElement> ownElements = [];

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("spawns", "spawns [wave] | spawns preview <wave>   list the spawn groups the pause menu shows (default: current wave), or page the open pause menu to a wave", args =>
            {
                if (EnemySpawner.instance == null) throw new InvalidOperationException("not in a level");
                if (args.Length == 2 && args[0] == "preview")
                {
                    if (helper == null || !helper.isActiveAndEnabled) throw new InvalidOperationException("pause menu not open");
                    helper.CycleNightPreview(int.Parse(args[1]) - 1 - PreviewWave(helper));
                    return $"previewing wave {PreviewWave(helper) + 1}";
                }
                int wave = args.Length > 0 ? int.Parse(args[0]) - 1 : CurrentWaveIndex();
                var sb = new StringBuilder($"wave {wave + 1}/{EnemySpawner.instance.WaveCount}{(IsLive(wave) ? " (live)" : "")}");
                foreach (var group in Gather(wave, IsLive(wave)))
                    sb.Append($"\n{group.Label}: ").Append(string.Join(", ", group.Enemies.Select(e =>
                        $"{(e.Info.eliteEnemy ? "elite " : "")}{e.Info.enemyName} {(IsLive(wave) ? $"{e.Spawned}/" : "")}{e.Info.enemyCount}")));
                return sb.ToString();
            });
        }

        protected override void OnFeatureEnabled()
        {
            if (helper != null && helper.isActiveAndEnabled) Rebuild(helper);
        }

        protected override void OnFeatureDisabled() => DestroyPanel();

        // ---- Data ----

        private sealed class EnemyEntry
        {
            public WaveEnemyInfo Info;
            public int Spawned;
        }

        private sealed class SpawnGroup
        {
            public Transform Line;
            public Vector3 Position;
            public string Label;
            public readonly List<EnemyEntry> Enemies = [];
        }

        private static int CurrentWaveIndex()
        {
            var spawner = EnemySpawner.instance;
            bool night = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;
            return Mathf.Clamp(night ? spawner.Wavenumber : spawner.Wavenumber + 1, 0, spawner.WaveCount - 1);
        }

        // Spawned counts are only meaningful for the wave that's running right now.
        private static bool IsLive(int wave) =>
            DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night &&
            EnemySpawner.instance != null && wave == EnemySpawner.instance.Wavenumber;

        private static List<SpawnGroup> Gather(int wave, bool live)
        {
            var spawner = EnemySpawner.instance;
            if (spawner == null || wave < 0 || wave >= spawner.waves.Count) return [];

            // The game's own per-type info (stats for the hover panel, arachnophobia icon), to copy into each group.
            var native = EnemySpawner.GetWaveInfoByNumber(wave).enemies;
            var groups = new List<SpawnGroup>();
            foreach (var spawn in spawner.waves[wave].spawns)
            {
                if (spawn.enemyPrefab == null || spawn.spawnLine == null) continue;
                var marker = spawn.enemyPrefab.GetComponent<ScreenMarkerIcon>();
                if (marker == null) continue; // the game hides these too (scripted events, not enemies)

                var group = groups.FirstOrDefault(g => g.Line == spawn.spawnLine);
                if (group == null)
                {
                    group = new SpawnGroup { Line = spawn.spawnLine, Position = MarkerPosition(spawn.spawnLine) };
                    groups.Add(group);
                }

                var sprite = spawn.enemyPrefab.GetComponent<ScreenMarkerIconArachnophobia>()?.defaultSprite ?? marker.sprite;
                var entry = group.Enemies.FirstOrDefault(e => e.Info.enemyName == marker.UnitName && e.Info.enemyIcon == sprite && e.Info.eliteEnemy == spawn.eliteEnemies);
                if (entry == null)
                {
                    var source = native.FirstOrDefault(e => e.enemyName == marker.UnitName && e.enemyIcon == sprite && e.eliteEnemy == spawn.eliteEnemies);
                    entry = new EnemyEntry { Info = Copy(source) ?? new WaveEnemyInfo { enemyName = marker.UnitName, enemyDescription = marker.UnitDescription, enemyIcon = sprite, eliteEnemy = spawn.eliteEnemies } };
                    entry.Info.enemyCount = 0;
                    group.Enemies.Add(entry);
                }
                entry.Info.enemyCount += spawn.count;
                if (live) entry.Spawned += spawn.SpawnedUnits;
            }
            SpawnNames.Assign(groups);
            return Merge(groups);
        }

        // Lines that share a name (four statues of the same kind, two unnamed lines to the west) read as one place.
        private static List<SpawnGroup> Merge(List<SpawnGroup> groups)
        {
            var merged = new List<SpawnGroup>();
            foreach (var group in groups)
            {
                var into = merged.FirstOrDefault(m => m.Label == group.Label);
                if (into == null) { merged.Add(group); continue; }
                foreach (var e in group.Enemies)
                {
                    var same = into.Enemies.FirstOrDefault(o => o.Info.enemyName == e.Info.enemyName && o.Info.enemyIcon == e.Info.enemyIcon && o.Info.eliteEnemy == e.Info.eliteEnemy);
                    if (same == null) into.Enemies.Add(e);
                    else { same.Info.enemyCount += e.Info.enemyCount; same.Spawned += e.Spawned; }
                }
            }
            return merged;
        }

        private static WaveEnemyInfo Copy(WaveEnemyInfo e) => e == null ? null : new WaveEnemyInfo
        {
            enemyIcon = e.enemyIcon, AltEnemyIcon = e.AltEnemyIcon, enemyName = e.enemyName, enemyDescription = e.enemyDescription,
            enemyCount = e.enemyCount, eliteEnemy = e.eliteEnemy, maxHP = e.maxHP, speed = e.speed, range = e.range,
            attackDamage = e.attackDamage, attackCooldown = e.attackCooldown, projectileSpeed = e.projectileSpeed,
        };

        // Same point the game puts the daytime spawn marker at.
        private static Vector3 MarkerPosition(Transform line) => line.childCount > 0
            ? (line.GetChild(0).position + line.GetChild(line.childCount - 1).position) / 2f
            : line.position;

        // ---- Names ----

        // Level designers named most spawn lines after landmarks ("Main Bridge", "Graveyard"); some are generic
        // ("Spawn 3", "Marker Pos" under a summoning statue, "SpawnLine" under a boss). Those get the landmark they're
        // attached to, or else a compass direction from the castle (north = up the screen). Same-named lines are merged.
        private static class SpawnNames
        {
            private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
                { "", "spawn", "spawnline", "spawn line", "spawnpoint", "spawn point", "marker", "marker pos", "markerpos", "line", "pos", "position" };

            public static void Assign(List<SpawnGroup> groups)
            {
                var basis = CameraBasis();
                var named = groups.Select(g =>
                {
                    var (name, air) = Clean(g.Line.name);
                    string landmark = IsGeneric(name) ? Landmark(g.Line.parent) : name;
                    return (Group: g, Name: landmark, Air: air, Compass: Compass(g.Position, basis));
                }).ToList();

                foreach (var n in named) n.Group.Label = n.Air ? $"{n.Name ?? n.Compass} (air)" : n.Name ?? n.Compass;
            }

            private static (string Name, bool Air) Clean(string raw)
            {
                string name = raw.Trim();
                bool air = false;
                if (name.StartsWith("Fly - ", StringComparison.OrdinalIgnoreCase)) { name = name.Substring(6); air = true; }
                if (name.EndsWith(" air", StringComparison.OrdinalIgnoreCase)) { name = name.Substring(0, name.Length - 4); air = true; }
                if (name.Equals("air", StringComparison.OrdinalIgnoreCase)) { name = ""; air = true; }
                name = StripCopySuffix(name);
                if (name.StartsWith("Bot ", StringComparison.Ordinal)) name = "Bottom " + name.Substring(4);
                if (name.Any(char.IsLetter) && !name.Any(char.IsLower)) name = TitleCase(name);
                return (name.Trim(), air);
            }

            // "Eldermolde Statue (2)" -> "Eldermolde Statue"
            private static string StripCopySuffix(string name)
            {
                name = name.Trim();
                int open = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (open > 0 && name.EndsWith(")") && name.Substring(open + 2, name.Length - open - 3).All(char.IsDigit)) name = name.Substring(0, open);
                return name;
            }

            private static bool IsGeneric(string name) => Generic.Contains(name.TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));

            private static string Landmark(Transform parent)
            {
                if (parent == null) return null;
                string name = StripCopySuffix(parent.name);
                if (name.StartsWith("ES ", StringComparison.Ordinal)) return null; // the level's spawn container, not a place
                if (name.StartsWith("E ", StringComparison.Ordinal)) name = name.Substring(2); // enemy prefab prefix
                name = Clean(name).Name;
                return IsGeneric(name) ? null : name;
            }

            private static string TitleCase(string s) =>
                string.Join(" ", s.ToLowerInvariant().Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1)));

            private static (Vector3 Up, Vector3 Right) CameraBasis()
            {
                var cam = Camera.main;
                if (cam == null) return (Vector3.forward, Vector3.right);
                var up = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
                if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
                up.Normalize();
                return (up, Vector3.Cross(Vector3.up, up));
            }

            private static readonly string[] Directions = ["North", "Northeast", "East", "Southeast", "South", "Southwest", "West", "Northwest"];

            private static string Compass(Vector3 position, (Vector3 Up, Vector3 Right) basis)
            {
                var d = position - CastleCenter.CastleCenterPosition;
                float angle = Mathf.Atan2(Vector3.Dot(d, basis.Right), Vector3.Dot(d, basis.Up)) * Mathf.Rad2Deg;
                int sector = Mathf.RoundToInt((angle + 360f) % 360f / 45f) % 8;
                return Directions[sector];
            }
        }

        // ---- UI ----

        private void DestroyPanel()
        {
            if (container == null) return;
            var frame = helper != null ? helper.frame : null;
            bool ownSelected = frame != null && frame.CurrentSelection != null && ownElements.Contains(frame.CurrentSelection);
            // Detach first so the frame's refetch below no longer sees the old elements.
            container.transform.SetParent(null, false);
            Object.Destroy(container);
            container = null;
            ownElements.Clear();
            if (frame != null)
            {
                frame.RefetchManagedElements();
                if (ownSelected && helper.continueElement != null) frame.Select(helper.continueElement);
            }
        }

        private void Rebuild(PauseUILoadoutHelper pause)
        {
            helper = pause;
            DestroyPanel();
            if (EnemySpawner.instance == null || pause.enemyPreview == null) return;

            int wave = PreviewWave(pause);
            bool live = IsLive(wave) && wave == TrueWave(pause);
            var groups = Gather(wave, live);
            if (groups.Count == 0) return;

            var column = pause.enemyPreview.transform.parent as RectTransform;
            var unitsLabel = column.Find("Units") as RectTransform;
            var headerSource = unitsLabel != null ? unitsLabel.GetComponent<TextMeshProUGUI>() : null;
            if (headerSource == null) { Plugin.Logger.LogWarning("Enemy previews: pause menu layout changed, \"Units\" label not found"); return; }

            container = new GameObject(ContainerName, typeof(RectTransform));
            container.SetActive(false); // so the cloned header's Localize doesn't run before it's removed
            var rect = (RectTransform)container.transform;
            rect.SetParent(column, false);
            rect.anchorMin = rect.anchorMax = unitsLabel.anchorMin;
            rect.pivot = new Vector2(0f, 0f);
            var vlg = container.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = RowSpacing;
            vlg.childAlignment = TextAnchor.LowerLeft;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = vlg.childForceExpandHeight = false;
            var fitter = container.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = Object.Instantiate(headerSource.gameObject, rect, false);
            header.name = "Header";
            foreach (var loc in header.GetComponents<I2.Loc.Localize>()) Object.DestroyImmediate(loc);
            var headerText = header.GetComponent<TextMeshProUGUI>();
            headerText.text = "Spawns:";
            headerText.alignment = TextAlignmentOptions.BottomLeft;
            header.AddComponent<LayoutElement>().preferredHeight = ((RectTransform)headerSource.transform).rect.height;

            var labels = new List<TextMeshProUGUI>();
            foreach (var group in groups) labels.Add(AddRow(rect, group, headerSource, live));

            // Left edge: a fixed column, unless the Night selector ("Night: 12(3)/13" plus arrows) or the native grid,
            // both centred on the menu, are wider than usual.
            var night = pause.nightPreviewText;
            float nightHalfWidth = night.GetPreferredValues(night.text).x / 2f + NightArrowsWidth;
            float nativeHalfWidth = Mathf.Max(nightHalfWidth, NativeGridWidth(pause) / 2f);
            float unitsTop = unitsLabel.anchoredPosition.y + unitsLabel.rect.height * (1f - unitsLabel.pivot.y);
            rect.anchoredPosition = new Vector2(Mathf.Max(MinLeft, nativeHalfWidth + GapFromNative), unitsTop + GapAboveUnits);

            container.SetActive(true);
            float labelWidth = Mathf.Min(LabelMaxWidth, labels.Max(l => l.GetPreferredValues(l.text).x));
            foreach (var l in labels) l.GetComponent<LayoutElement>().preferredWidth = l.GetComponent<LayoutElement>().minWidth = labelWidth;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);

            // Shrink if a level has lots of spawn points: stay below the top of the menu buttons, and in the left part
            // of the screen (the score panel is on the right).
            var menu = column.Find("Menu Items") as RectTransform;
            float top = menu != null ? menu.anchoredPosition.y + menu.rect.height / 2f + column.rect.height * (menu.anchorMin.y - 1f) : 0f;
            float room = top - rect.anchoredPosition.y;
            float scale = rect.rect.height > room && room > 0 ? room / rect.rect.height : 1f;
            var frameRect = (RectTransform)pause.transform;
            float rightLimit = frameRect.rect.xMin + frameRect.rect.width * MaxRightFraction;
            float left = frameRect.InverseTransformPoint(rect.TransformPoint(rect.rect.min)).x;
            if (left + rect.rect.width * scale > rightLimit && rightLimit > left) scale = (rightLimit - left) / rect.rect.width;
            rect.localScale = new Vector3(scale, scale, 1f);

            foreach (var e in container.GetComponentsInChildren<ThronefallUIElement>(true)) ownElements.Add(e);
            pause.frame.RefetchManagedElements(); // so mouse hover over our icons shows the game's stats panel
        }

        private static float NativeGridWidth(PauseUILoadoutHelper pause)
        {
            var grid = pause.enemyPreview;
            int count = NativeEnemyGrid(pause)?.Count ?? 0;
            int rows = grid.constraint == GridLayoutGroup.Constraint.FixedRowCount ? Mathf.Max(1, grid.constraintCount) : 2;
            int columns = Mathf.CeilToInt(count / (float)rows);
            return columns * grid.cellSize.x + Mathf.Max(0, columns - 1) * grid.spacing.x;
        }

        private TextMeshProUGUI AddRow(RectTransform parent, SpawnGroup group, TextMeshProUGUI style, bool live)
        {
            var row = new GameObject("Row " + group.Label, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = LabelGap;
            hlg.childAlignment = TextAnchor.UpperLeft;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;

            float cellHeight = IconSize + (live ? CaptionHeight : 0f);

            var labelObj = new GameObject("Label", typeof(RectTransform));
            labelObj.transform.SetParent(row.transform, false);
            var label = labelObj.AddComponent<TextMeshProUGUI>();
            label.text = group.Label;
            label.font = style.font;
            label.fontSharedMaterial = style.fontSharedMaterial;
            label.color = style.color;
            label.fontSize = 24;
            label.fontStyle = FontStyles.Normal;
            label.characterSpacing = 0;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = true;
            label.overflowMode = TextOverflowModes.Overflow;
            label.lineSpacing = -20f;
            label.raycastTarget = false;
            var labelLayout = labelObj.AddComponent<LayoutElement>();
            labelLayout.minHeight = IconSize; // long names wrap onto a second line

            var icons = new GameObject("Icons", typeof(RectTransform));
            icons.transform.SetParent(row.transform, false);
            var grid = icons.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(IconSize, cellHeight);
            grid.spacing = new Vector2(IconSpacing, IconSpacing);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = Mathf.Min(IconsPerLine, group.Enemies.Count);
            int lines = Mathf.CeilToInt(group.Enemies.Count / (float)IconsPerLine);
            var iconsLayout = icons.AddComponent<LayoutElement>();
            iconsLayout.preferredWidth = grid.constraintCount * IconSize + (grid.constraintCount - 1) * IconSpacing;
            iconsLayout.preferredHeight = lines * cellHeight + (lines - 1) * IconSpacing;

            foreach (var enemy in group.Enemies) AddIcon(icons.transform, enemy, style, live);
            return label;
        }

        private void AddIcon(Transform parent, EnemyEntry enemy, TextMeshProUGUI style, bool live)
        {
            var cell = new GameObject(enemy.Info.enemyName, typeof(RectTransform));
            cell.transform.SetParent(parent, false);

            var button = Object.Instantiate(helper.enemyButtonPrefab, cell.transform, false);
            var buttonRect = (RectTransform)button.transform;
            buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = new Vector2(0.5f, 1f);
            buttonRect.anchoredPosition = Vector2.zero;
            buttonRect.sizeDelta = new Vector2(IconSize, IconSize);
            button.SetDataSimple(enemy.Info);
            if (!live) return;

            bool done = enemy.Spawned >= enemy.Info.enemyCount;
            if (done) cell.AddComponent<CanvasGroup>().alpha = 0.45f;

            var captionObj = new GameObject("Spawned", typeof(RectTransform));
            captionObj.transform.SetParent(cell.transform, false);
            var captionRect = (RectTransform)captionObj.transform;
            captionRect.anchorMin = new Vector2(0f, 0f);
            captionRect.anchorMax = new Vector2(1f, 0f);
            captionRect.pivot = new Vector2(0.5f, 0f);
            captionRect.offsetMin = new Vector2(-6f, 0f);
            captionRect.offsetMax = new Vector2(6f, CaptionHeight);
            var caption = captionObj.AddComponent<TextMeshProUGUI>();
            caption.text = $"{enemy.Spawned}/{enemy.Info.enemyCount}";
            caption.font = button.numberText.font;
            caption.fontSharedMaterial = button.numberText.fontSharedMaterial;
            caption.color = style.color;
            caption.fontSize = 19;
            caption.alignment = TextAlignmentOptions.Bottom;
            caption.enableWordWrapping = false;
            caption.raycastTarget = false;
        }

        [HarmonyPatch(typeof(PauseUILoadoutHelper), "RefreshOnlyEnemyPreview")]
        private static class RefreshOnlyEnemyPreview_Patch
        {
            private static void Postfix(PauseUILoadoutHelper __instance) => Feature<EnemyPreviewManager>.Instance.Rebuild(__instance);
        }

        // Selecting one of our icons while previewing another night shouldn't snap the preview back to tonight.
        [HarmonyPatch(typeof(PauseUILoadoutHelper), nameof(PauseUILoadoutHelper.OnFrameSelectionChange))]
        private static class OnFrameSelectionChange_Patch
        {
            private static bool Prefix(PauseUILoadoutHelper __instance)
            {
                var selected = __instance.frame.CurrentSelection;
                return selected == null || !Feature<EnemyPreviewManager>.Instance.ownElements.Contains(selected);
            }
        }
    }
}
