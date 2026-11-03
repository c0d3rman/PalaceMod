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
        private const float IconSize = 50f, IconSpacing = 14f, LineSpacing = 8f, CaptionHeight = 22f, CaptionGap = 4f, RowSpacing = 18f, GroupSpacing = 28f;
        private const float LabelGap = 10f, GapFromNative = 40f, GapAboveUnits = 10f, BadgeOverhang = 14f;
        private const float MinLeft = 280f, NightArrowsWidth = 35f, MaxRightFraction = 0.55f;
        private const int IconsPerLine = 4;

        // How far a token grows past its cell on each side when hovered or selected (the game scales it from its centre),
        // so the spawned count below it and the line above can be kept clear. Read from the game's button styles.
        private float hoverPad;

        private static float HoverPad(TFUIUnit button)
        {
            float scale = 1f;
            foreach (var style in new[] { button.focussedStyle, button.selectedStyle, button.focussedAndSelectedStyle })
                if (style != null) scale = Mathf.Max(scale, style.scale);
            return IconSize * (scale - 1f) / 2f;
        }

        private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, int> PreviewWave =
            AccessTools.FieldRefAccess<PauseUILoadoutHelper, int>("currentPreviewWavenumber");
        private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, int> TrueWave =
            AccessTools.FieldRefAccess<PauseUILoadoutHelper, int>("currentTrueWaveNumver");
        private static readonly AccessTools.FieldRef<PauseUILoadoutHelper, List<ThronefallUIElement>> NativeEnemyGrid =
            AccessTools.FieldRefAccess<PauseUILoadoutHelper, List<ThronefallUIElement>>("enemyGrid");

        private PauseUILoadoutHelper helper;
        private GameObject container;
        private readonly HashSet<ThronefallUIElement> ownElements = [];
        // Our icons line by line, top to bottom, for keyboard/controller navigation.
        private readonly List<List<ThronefallUIElement>> navLines = [];
        private static readonly System.Reflection.MethodInfo AssignNavigationTargets = AccessTools.Method(typeof(PauseUILoadoutHelper), "AssignNavigationTargets");

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("spawns", "spawns [wave] | spawns preview <wave>   list the spawn groups the pause menu shows (default: current wave), or page the open pause menu to a wave", args =>
            {
                if (EnemySpawner.instance == null) throw new InvalidOperationException("not in a level");
                if (args.Length == 2 && args[0] == "preview")
                {
                    if (helper == null || !helper.isActiveAndEnabled) throw new InvalidOperationException("pause menu not open");
                    helper.frame.Select(helper.nightButton); // as the game's own arrows do
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
            if (helper == null || !helper.isActiveAndEnabled) return;
            Rebuild(helper);
            AssignNavigationTargets.Invoke(helper, null);
        }

        protected override void OnFeatureDisabled()
        {
            DestroyPanel();
            // Our patches are gone, so this restores the native navigation links we redirected.
            if (helper != null && helper.isActiveAndEnabled) AssignNavigationTargets.Invoke(helper, null);
        }

        // ---- Data ----

        private sealed class EnemyEntry
        {
            public WaveEnemyInfo Info;
            public int Spawned;
        }

        private sealed class SpawnGroup
        {
            public Transform Line;
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
                    group = new SpawnGroup { Line = spawn.spawnLine, Label = SpawnLocations.Name(spawn.spawnLine) };
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
            navLines.Clear();
            if (frame != null)
            {
                frame.RefetchManagedElements();
                // The Night selector, not Continue: selecting anything else snaps the preview back to tonight.
                if (ownSelected && helper.nightButton != null) frame.Select(helper.nightButton);
            }
        }

        private bool rebuilding;

        private void Rebuild(PauseUILoadoutHelper pause)
        {
            if (rebuilding) return;
            rebuilding = true;
            try { RebuildPanel(pause); }
            finally { rebuilding = false; }
        }

        private void RebuildPanel(PauseUILoadoutHelper pause)
        {
            helper = pause;
            DestroyPanel();
            if (EnemySpawner.instance == null || pause.enemyPreview == null) return;

            int wave = PreviewWave(pause);
            bool live = IsLive(wave) && wave == TrueWave(pause);
            var groups = Gather(wave, live);
            if (groups.Count == 0) return;
            hoverPad = HoverPad(pause.enemyButtonPrefab);

            var column = pause.enemyPreview.transform.parent as RectTransform;
            var unitsLabel = column.Find("Units") as RectTransform;
            var headerSource = unitsLabel != null ? unitsLabel.GetComponent<TextMeshProUGUI>() : null;
            if (headerSource == null) { Log.Warn("Pause menu layout changed, \"Units\" label not found"); return; }

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
            header.SetActive(true); // the source is hidden while there are no units (Fixes)
            foreach (var loc in header.GetComponents<I2.Loc.Localize>()) Object.DestroyImmediate(loc);
            var headerText = header.GetComponent<TextMeshProUGUI>();
            headerText.text = "Spawns:";
            headerText.alignment = TextAlignmentOptions.BottomLeft;
            header.AddComponent<LayoutElement>().preferredHeight = ((RectTransform)headerSource.transform).rect.height;

            container.SetActive(true); // TMP measures text only while active

            // Groups flow left to right in bands as wide as one line of icons, so places with one or two enemy types
            // sit side by side instead of each taking a full row.
            float bandWidth = IconsPerLine * IconSize + (IconsPerLine - 1) * IconSpacing;
            Transform band = null;
            float used = 0f;
            var bands = new List<List<List<List<ThronefallUIElement>>>>(); // band -> group -> line -> icon
            foreach (var group in groups)
            {
                var (row, groupWidth, lines) = AddGroup(rect, group, headerSource, live);
                if (band == null || used + GroupSpacing + groupWidth > bandWidth)
                {
                    band = NewBand(rect);
                    bands.Add([]);
                    used = groupWidth;
                }
                else used += GroupSpacing + groupWidth;
                row.transform.SetParent(band, false);
                bands[bands.Count - 1].Add(lines);
            }
            // Navigation lines run across a band: line k of every group in it, left to right.
            foreach (var bandGroups in bands)
                for (int k = 0; k < bandGroups.Max(g => g.Count); k++)
                    navLines.Add(bandGroups.Where(g => k < g.Count).SelectMany(g => g[k]).ToList());

            // Left edge: a fixed column, unless the Night selector ("Night: 12(3)/13" plus arrows) or the native grid,
            // both centred on the menu, are wider than usual.
            var night = pause.nightPreviewText;
            float nightHalfWidth = night.GetPreferredValues(night.text).x / 2f + NightArrowsWidth;
            float nativeHalfWidth = Mathf.Max(nightHalfWidth, NativeGridWidth(pause) / 2f);
            // Bottom just above the "Units:" row, level with the bottom of the native enemy grid.
            float unitsTop = unitsLabel.anchoredPosition.y + unitsLabel.rect.height * (1f - unitsLabel.pivot.y);
            rect.anchoredPosition = new Vector2(Mathf.Max(MinLeft, nativeHalfWidth + GapFromNative), unitsTop + GapAboveUnits);

            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);

            // Shrink if a level has lots of spawn points: stay below the top of the menu buttons, and in the left part
            // of the screen (the score panel is on the right), clear of where the game shows the hover panel.
            var menu = column.Find("Menu Items") as RectTransform;
            float top = menu != null ? menu.anchoredPosition.y + menu.rect.height / 2f + column.rect.height * (menu.anchorMin.y - 1f) : 0f;
            float room = top - rect.anchoredPosition.y;
            float scale = rect.rect.height > room && room > 0 ? room / rect.rect.height : 1f;
            var frameRect = (RectTransform)pause.transform;
            float rightLimit = column.InverseTransformPoint(frameRect.TransformPoint(new Vector3(frameRect.rect.xMin + frameRect.rect.width * MaxRightFraction, 0f))).x - column.rect.center.x;
            if (column.Find("Tooltip Background") is RectTransform tooltip && tooltip.anchorMin == rect.anchorMin)
                rightLimit = Mathf.Min(rightLimit, tooltip.anchoredPosition.x - tooltip.rect.width * tooltip.pivot.x);
            float left = rect.anchoredPosition.x, width = rect.rect.width + BadgeOverhang;
            if (left + width * scale > rightLimit && rightLimit > left) scale = (rightLimit - left) / width;
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

        private static Transform NewBand(RectTransform parent)
        {
            var band = new GameObject("Band", typeof(RectTransform));
            band.transform.SetParent(parent, false);
            var hlg = band.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = GroupSpacing;
            hlg.childAlignment = TextAnchor.UpperLeft;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;
            return band.transform;
        }

        private (GameObject Row, float Width, List<List<ThronefallUIElement>> Lines) AddGroup(RectTransform parent, SpawnGroup group, TextMeshProUGUI style, bool live)
        {
            var row = new GameObject("Row " + group.Label, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var hlg = row.AddComponent<VerticalLayoutGroup>();
            hlg.spacing = LabelGap + hoverPad; // the place name stays clear of a grown token's count badge
            hlg.childAlignment = TextAnchor.UpperLeft;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;

            // A live cell is the token, room for it to grow on hover, then the spawned count.
            float cellHeight = IconSize + (live ? hoverPad + CaptionGap + CaptionHeight : 0f);

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
            label.enableWordWrapping = false;
            label.raycastTarget = false;

            var icons = new GameObject("Icons", typeof(RectTransform));
            icons.transform.SetParent(row.transform, false);
            var grid = icons.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(IconSize, cellHeight);
            grid.spacing = new Vector2(IconSpacing, LineSpacing + hoverPad);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = Mathf.Min(IconsPerLine, group.Enemies.Count);
            int lines = Mathf.CeilToInt(group.Enemies.Count / (float)IconsPerLine);
            var iconsLayout = icons.AddComponent<LayoutElement>();
            iconsLayout.preferredWidth = grid.constraintCount * IconSize + (grid.constraintCount - 1) * IconSpacing;
            iconsLayout.preferredHeight = lines * cellHeight + (lines - 1) * grid.spacing.y;

            var navigation = new List<List<ThronefallUIElement>>();
            for (int i = 0; i < group.Enemies.Count; i++)
            {
                if (i % IconsPerLine == 0) navigation.Add([]);
                navigation[navigation.Count - 1].Add(AddIcon(icons.transform, group.Enemies[i], style, live));
            }
            return (row, Mathf.Max(label.GetPreferredValues(label.text).x, iconsLayout.preferredWidth), navigation);
        }

        private TFUIEnemy AddIcon(Transform parent, EnemyEntry enemy, TextMeshProUGUI style, bool live)
        {
            var cell = new GameObject(enemy.Info.enemyName, typeof(RectTransform));
            cell.transform.SetParent(parent, false);

            var button = Object.Instantiate(helper.enemyButtonPrefab, cell.transform, false);
            var buttonRect = (RectTransform)button.transform;
            // Pivot in the middle, as in the game's grid, so the hover scale grows the token evenly instead of downward.
            buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(0.5f, 1f);
            buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(0f, -IconSize / 2f);
            buttonRect.sizeDelta = new Vector2(IconSize, IconSize);
            button.SetDataSimple(enemy.Info);
            if (!live) return button;

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
            return button;
        }

        // Runs after the game links its own grids. Our icons form a grid of their own: up from the top line goes to the
        // Night selector, left from the first column to the native enemy grid's last column (whose right now leads here),
        // and right/down go where the native grid's right/down went.
        private void LinkNavigation(PauseUILoadoutHelper pause)
        {
            if (navLines.Count == 0 || pause != helper) return;
            var native = NativeEnemyGrid(pause);
            int columns = (native.Count + 1) / 2;
            var nativeTop = native.Count > 0 ? native[2 * (columns - 1)] : null;
            var nativeBottom = native.Count > 0 ? native[Math.Min(native.Count - 1, 2 * (columns - 1) + 1)] : null;
            var rightExit = nativeTop?.rightNav;
            var downExit = nativeBottom?.botNav ?? pause.topmostButton;

            for (int r = 0; r < navLines.Count; r++)
            {
                var line = navLines[r];
                for (int c = 0; c < line.Count; c++)
                {
                    var e = line[c];
                    e.leftNav = c > 0 ? line[c - 1] : (r < navLines.Count / 2 || nativeBottom == null ? nativeTop : nativeBottom) ?? pause.nightButton;
                    e.rightNav = c < line.Count - 1 ? line[c + 1] : rightExit;
                    e.topNav = r > 0 ? navLines[r - 1][Math.Min(c, navLines[r - 1].Count - 1)] : pause.nightButton;
                    e.botNav = r < navLines.Count - 1 ? navLines[r + 1][Math.Min(c, navLines[r + 1].Count - 1)] : downExit;
                }
            }
            if (nativeTop != null) nativeTop.rightNav = navLines[0][0];
            if (nativeBottom != null && nativeBottom != nativeTop) nativeBottom.rightNav = navLines[navLines.Count - 1][0];
        }

        [HarmonyPatch(typeof(PauseUILoadoutHelper), "AssignNavigationTargets")]
        private static class AssignNavigationTargets_Patch
        {
            private static void Postfix(PauseUILoadoutHelper __instance) => Feature<EnemyPreviewManager>.Instance.LinkNavigation(__instance);
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
