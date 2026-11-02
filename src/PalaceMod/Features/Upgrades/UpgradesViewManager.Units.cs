using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Upgraded units. A unit counts as upgraded when the building that trains it has been upgraded since it was built;
    it gets one star per upgrade. Stars appear on the pause menu's unit icons and on the command-mode icons over the
    units' heads. The pause menu normally lumps every unit of one type into a single icon; with this on, units whose
    buildings took different upgrades get separate icons, and the hover text lists the upgrades.
*/
namespace PalaceMod
{
    public partial class UpgradesViewManager
    {
        private const string StarsName = "PalaceMod Stars";

        internal struct UnitUpgrades
        {
            public BuildSlot Slot;
            public int Stars;
            public string Key;          // groups units with the same upgrade path
            public List<string> Names;  // the upgrades' names, oldest first
        }

        private static readonly Dictionary<Hp, BuildSlot> unitSlots = new();
        private static float unitSlotsTime = -10f;

        // Badge data for the UnitInfo entries made by the patched GetUnitsInfo, keyed by object identity.
        private static readonly Dictionary<UnitInfo, UnitUpgrades> infoBadges = new();

        private readonly List<GameObject> commandStars = new();

        private bool MarkingUnits => IsEnabled && MarkUnits.Value;

        /// <summary>Which building trains each unit; rebuilt at most once a second.</summary>
        private static bool RefreshUnitSlots(bool force = false)
        {
            if (!force && Time.unscaledTime - unitSlotsTime < 1f) return false;
            unitSlotsTime = Time.unscaledTime;
            unitSlots.Clear();
            foreach (var respawner in Object.FindObjectsOfType<UnitRespawnerForBuildings>())
            {
                if (respawner == null || respawner.myBuildSlot == null || respawner.units == null) continue;
                var slot = UpgradeInfo.Owner(respawner.myBuildSlot);
                foreach (var hp in respawner.units)
                    if (hp != null) unitSlots[hp] = slot;
            }
            return true;
        }

        internal static UnitUpgrades? UpgradesOf(GameObject unit)
        {
            var hp = unit != null ? unit.GetComponent<Hp>() : null;
            if (hp == null || !unitSlots.TryGetValue(hp, out var slot) || slot == null) return null;
            var taken = UpgradeInfo.Taken(slot);
            if (taken.Count == 0) return null;
            var names = taken.Select(s => UpgradeInfo.StepText(slot, s)).Select(Shorten).Where(n => !string.IsNullOrEmpty(n)).ToList();
            return new UnitUpgrades
            {
                Slot = slot,
                Stars = taken.Count,
                Key = string.Join(",", taken.Select(s => s.BranchIndex)) + "|" + taken.Count,
                Names = names,
            };
        }

        // ---------- Command-mode icons ----------

        private void UpdateUnitMarks()
        {
            if (!MarkUnits.Value || CommandUnits.instance == null)
            {
                if (commandStars.Count > 0) ClearCommandStars();
                return;
            }
            // Stars only change when a building is upgraded, so a once-a-second pass while the icons show is plenty.
            if (!CommandUnits.instance.ShouldShowUnitTypes || !RefreshUnitSlots()) return;
            foreach (var (hp, _) in unitSlots)
            {
                if (hp == null) continue;
                var display = hp.GetComponent<UnitTypeDisplay>();
                if (display == null || display.icon == null) continue;
                var info = UpgradesOf(hp.gameObject);
                var existing = display.icon.transform.Find(StarsName);
                int stars = info?.Stars ?? 0;
                if (existing != null && existing.childCount == stars) continue;
                if (existing != null)
                {
                    commandStars.Remove(existing.gameObject);
                    Object.Destroy(existing.gameObject);
                }
                if (stars > 0) commandStars.Add(AddStars(display.icon.transform, stars, worldSpace: true));
            }
        }

        private void ClearCommandStars()
        {
            foreach (var obj in commandStars)
                if (obj != null) Object.Destroy(obj);
            commandStars.Clear();
        }

        private void ClearUnitMarks()
        {
            ClearCommandStars();
            infoBadges.Clear();
            // Pause-menu icons are rebuilt by the game every time it opens, so nothing else lingers.
        }

        // ---------- Stars ----------

        /// <summary>A row of <paramref name="count"/> stars along the top edge of <paramref name="icon"/>.</summary>
        private static GameObject AddStars(Transform icon, int count, bool worldSpace)
        {
            var row = new GameObject(StarsName, typeof(RectTransform));
            row.transform.SetParent(icon, false);
            var rect = (RectTransform)row.transform;
            var iconRect = icon as RectTransform;
            float iconSize = iconRect != null ? Mathf.Max(iconRect.rect.width, iconRect.rect.height) : 1f;
            float star = iconSize * (worldSpace ? 0.42f : 0.34f);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, worldSpace ? star * 0.55f : -star * 0.05f);
            rect.sizeDelta = new Vector2(star * count, star);
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = -star * 0.15f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = layout.childControlHeight = false;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var le = row.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            for (int i = 0; i < count; i++)
            {
                var s = new GameObject("Star", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                s.transform.SetParent(row.transform, false);
                ((RectTransform)s.transform).sizeDelta = new Vector2(star, star);
                var img = s.GetComponent<Image>();
                img.sprite = StarSprite;
                img.raycastTarget = false;
            }
            return row;
        }

        private static Sprite starSprite;

        /// <summary>A gold five-pointed star with a dark rim, drawn once.</summary>
        private static Sprite StarSprite
        {
            get
            {
                if (starSprite != null) return starSprite;
                const int n = 64;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var fill = ModStyle.Bright;
                var rim = new Color(0.16f, 0.12f, 0.08f, 1f);
                var pixels = new Color[n * n];
                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector2((x + 0.5f) / n * 2f - 1f, (y + 0.5f) / n * 2f - 1f);
                    float outer = StarDistance(p, 0.98f);   // >0 outside
                    float inner = StarDistance(p, 0.72f);
                    float aOuter = Mathf.Clamp01(0.5f - outer * n * 0.5f);
                    float aInner = Mathf.Clamp01(0.5f - inner * n * 0.5f);
                    var c = Color.Lerp(rim, fill, aInner);
                    c.a = aOuter;
                    pixels[y * n + x] = c;
                }
                tex.SetPixels(pixels);
                tex.Apply();
                starSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
                starSprite.name = "PalaceMod Star";
                return starSprite;
            }
        }

        // Signed-ish distance from p to a five-pointed star of the given outer radius (positive outside).
        private static float StarDistance(Vector2 p, float radius)
        {
            // Polygon test with distance to the nearest edge.
            var pts = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float a = Mathf.PI / 2f + i * Mathf.PI / 5f;
                float r = i % 2 == 0 ? radius : radius * 0.45f;
                pts[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r - new Vector2(0f, radius * 0.06f);
            }
            bool inside = false;
            float best = float.MaxValue;
            for (int i = 0, j = 9; i < 10; j = i++)
            {
                var a = pts[i];
                var b = pts[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                var ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return inside ? -best : best;
        }

        // ---------- Pause menu unit list ----------

        private static string AppendUpgrades(string description, UnitUpgrades u)
        {
            var sb = new StringBuilder(description ?? "");
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("<b>Upgrades:</b> ");
            sb.Append(u.Names.Count > 0 ? string.Join(", ", u.Names) : $"{u.Stars}");
            return sb.ToString();
        }

        /// <summary>The game's grouping (same icon and name), additionally split by the training building's upgrades.</summary>
        [HarmonyPatch(typeof(PlayerUnitsInfo), nameof(PlayerUnitsInfo.GetUnitsInfo))]
        private static class GetUnitsInfoPatch
        {
            private static bool Prefix(ref UnitsInfo __result)
            {
                var self = Feature<UpgradesViewManager>.Instance;
                if (self == null || !self.MarkingUnits || TagManager.instance == null) return true;
                RefreshUnitSlots(force: true);
                infoBadges.Clear();
                var found = new List<TaggedObject>();
                TagManager.instance.FindAllTaggedObjectsWithTags(found, [TagManager.ETag.PlayerUnit], []);
                var result = new UnitsInfo();
                var groups = new Dictionary<(Sprite, string, string), UnitInfo>();
                foreach (var item in found)
                {
                    var go = item.gameObject;
                    var marker = go.GetComponent<ScreenMarkerIcon>();
                    if (marker == null) continue;
                    var upgrades = UpgradesOf(go);
                    var key = (marker.sprite, marker.UnitName, upgrades?.Key ?? "");
                    if (groups.TryGetValue(key, out var existing))
                    {
                        existing.unitCount++;
                        continue;
                    }
                    var info = Describe(go, marker);
                    if (upgrades is { } u)
                    {
                        info.unitDescription = UpgradesViewManager.AppendUpgrades(info.unitDescription, u);
                        infoBadges[info] = u;
                    }
                    groups[key] = info;
                    result.Units.Add(info);
                }
                // Unupgraded first, then by star count, so the stars read left to right within a type.
                result.Units = result.Units
                    .Select((info, index) => (info, index))
                    .OrderBy(t => result.Units.FindIndex(o => o.unitIcon == t.info.unitIcon && o.unitName == t.info.unitName))
                    .ThenBy(t => infoBadges.TryGetValue(t.info, out var b) ? b.Stars : 0)
                    .ThenBy(t => t.index)
                    .Select(t => t.info).ToList();
                __result = result;
                return false;
            }

            // Same stats the game reads in PlayerUnitsInfo.GetUnitsInfo.
            private static UnitInfo Describe(GameObject go, ScreenMarkerIcon marker)
            {
                var info = new UnitInfo
                {
                    unitIcon = marker.sprite,
                    unitName = marker.UnitName,
                    unitDescription = marker.UnitDescription,
                    unitCount = 1,
                };
                var hp = go.GetComponent<Hp>();
                if (hp != null) info.maxHP = hp.maxHp;
                var move = go.GetComponent<PathfindMovementPlayerunit>();
                if (move != null) info.speed = move.movementSpeed;
                var attack = go.GetComponent<AutoAttack>();
                if (attack != null)
                {
                    info.attackCooldown = attack.cooldownDuration;
                    if (attack.targetPriorities.Count > 0)
                    {
                        info.range = attack.targetPriorities[0].range;
                        foreach (var priority in attack.targetPriorities)
                        {
                            if (priority.mustHaveTags.Contains(TagManager.ETag.EnemyOwned))
                            {
                                info.range = priority.range;
                                break;
                            }
                        }
                    }
                    if (attack.weapon != null)
                    {
                        float damage = 0f;
                        foreach (var modifier in attack.weapon.directDamage)
                        {
                            if (modifier.requiredTags.Count == 1 && modifier.requiredTags[0] == TagManager.ETag.EnemyOwned)
                            {
                                damage += modifier.damageAdded;
                                damage *= modifier.damageMultiplyer;
                            }
                        }
                        info.attackDamage = damage;
                        info.projectileSpeed = attack.weapon.projectileSpeed;
                    }
                }
                return info;
            }
        }

        [HarmonyPatch(typeof(TFUIPlayerUnit), nameof(TFUIPlayerUnit.SetDataSimple))]
        private static class PlayerUnitIconPatch
        {
            private static void Postfix(TFUIPlayerUnit __instance, UnitInfo e)
            {
                var old = __instance.IconImg != null ? __instance.IconImg.transform.parent.Find(StarsName) : null;
                if (old != null) Object.Destroy(old.gameObject);
                var self = Feature<UpgradesViewManager>.Instance;
                if (self == null || !self.MarkingUnits || e == null || !infoBadges.TryGetValue(e, out var badge) || badge.Stars <= 0) return;
                if (__instance.IconImg == null) return;
                // On the icon's parent (the tile), so the stars aren't tinted with the icon.
                AddStars(__instance.IconImg.transform.parent, badge.Stars, worldSpace: false);
            }
        }
    }
}
