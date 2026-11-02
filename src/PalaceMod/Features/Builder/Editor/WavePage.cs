using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PalaceMod.Builder.Model;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PalaceMod.Builder.Editor
{
    /// <summary>
    /// A wave set as a timeline: one row per night with its groups of enemies as chips. Selecting a night or a chip
    /// and applying opens its fields underneath (difficulty and order for a night; unit, count, timing, line and gold
    /// for a group).
    /// </summary>
    internal sealed class WavePage : EditorPage
    {
        public int Index;
        private int night = -1, spawn = -1;

        public WavePage() : base("Edit Wave Set")
        {
            Frame.SetContent(Page(Build));
        }

        public void ResetSelection() { night = -1; spawn = -1; }

        public void Expand(int nightIndex, int spawnIndex) { night = nightIndex; spawn = spawnIndex; }

        private WaveSetDef Set => Index >= 0 && Index < (Pack?.WaveSets.Count ?? 0) ? Pack.WaveSets[Index] : null;
        private string P => $"waveSets[{Index}]";
        private CatalogHost Host => EditorCatalog.Host(Set?.Host);

        private void Edit(string label, Action<WaveSetDef> change, bool structural = false, string coalesce = null)
        {
            int i = Index;
            Draft.Edit(label, p => change(p.WaveSets[i]), structural, coalesce);
        }

        private void Build(ModPage page)
        {
            chipKeys.Clear();
            var ws = Set;
            if (ws == null) { page.Text("This wave set no longer exists."); return; }
            ws.Nights ??= [];
            if (night >= ws.Nights.Count) { night = -1; spawn = -1; }
            if (night >= 0 && spawn >= (ws.Nights[night]?.Spawns?.Count ?? 0)) spawn = -1;
            Frame.SetTitle(string.IsNullOrWhiteSpace(ws.Name) ? "Edit Wave Set" : ws.Name);

            page.Header("Wave set");
            Text(page, "Name", P + ".name", () => Set?.Name, (p, v) => p.WaveSets[Index].Name = v, "Shown when choosing a custom run.");
            Text(page, "Id", P + ".id", () => Set?.Id, (p, v) =>
            {
                var w = p.WaveSets[Index];
                if (v == null || v == w.Id) return;
                // Levels name their wave set by id; keep them pointing at it.
                foreach (var level in p.Levels ?? []) if (level?.WaveSet == w.Id) level.WaveSet = v;
                w.Id = v;
            },
                "Unique within the pack: lowercase letters, digits, '.', '_' or '-'.", isId: true);
            Choice(page, "Map", P + ".host", EditorCatalog.Hosts.Select(h => (h.Scene, h.DisplayName)).ToList(), () => Set?.Host,
                (p, v) => p.WaveSets[Index].Host = v,
                "The map the run is played on. Spawn lines are the map's; groups on lines it doesn't have are marked.", structural: true,
                picker: () => Picker.One("Choose a Map", HostItems(), Set.Host, scene => Edit("Change map", w => w.Host = scene, structural: true)),
                display: () => Host?.DisplayName ?? Set?.Host ?? "none");
            Number(page, Schema.StartingGold, P + ".startingGold", () => Set?.StartingGold, (p, v) => p.WaveSets[Index].StartingGold = v is float f ? (int?)Mathf.RoundToInt(f) : null,
                baseValue: () => Host?.StartingGold);
            var test = page.Row("Test play", Dim(PackListPage.Count(ws.Nights.Count, "night")), () => "Saves the pack and plays this wave set. Scores and saves aren't recorded." +
                (Draft.ErrorCount > 0 ? "\nFix the pack's problems first (Problems tab)." : ""), () => BuilderEditor.TestWaveSet(Set.Id));

            page.Header("Nights");
            for (int n = 0; n < ws.Nights.Count; n++) BuildNight(page, n);
            page.Button("Add night", () =>
            {
                int added = Set.Nights.Count;
                Edit("Add night", w =>
                {
                    var last = w.Nights.LastOrDefault();
                    w.Nights.Add(last != null ? EditorCatalog.Copy(last) : new NightDef { Spawns = [DefaultSpawn(Pack, Host)] });
                }, structural: true);
                night = added;
                spawn = -1;
                Rebuild();
            }, "Adds a night after the last one, starting as a copy of it.");
            BuildTroops(page);
            if (Host != null)
                Danger(page, $"Replace with {Host.DisplayName}'s own waves", "Swaps every night for the map's vanilla nights (you can undo this).", () =>
                {
                    var vanilla = EditorCatalog.Copy(Host.VanillaWaves);
                    if (vanilla == null) return;
                    Edit("Replace with vanilla waves", w => { w.Nights = vanilla.Nights; w.StartingGold = null; }, structural: true);
                    ResetSelection();
                });
            Danger(page, "Delete wave set", "Deletes this wave set from the pack.", () =>
            {
                Draft.Edit("Delete wave set", p => p.WaveSets.RemoveAt(Index), structural: true);
                Frame.Close();
            });
        }

        // ---- Troops: custom player units standing in for the troops buildings train ------------------------------

        private void BuildTroops(ModPage page)
        {
            page.Header("Troops");
            var players = Pack.Units.Where(u => u?.Side == UnitSide.Player && u.Id != null).ToList();
            var troops = Set.Troops ?? [];
            if (troops.Count == 0)
                page.Text(players.Count == 0
                    ? "Buildings can train your own player units instead of their usual troops. Make one on the Units tab (New player unit) first."
                    : "Pick which of your player units the buildings train on this run.");
            var unitOptions = players.Select(u => (u.Id, string.IsNullOrWhiteSpace(u.Name) ? u.Id : u.Name)).ToList();
            for (int t = 0; t < troops.Count; t++)
            {
                int ti = t;
                string tp = $"{P}.troops[{t}]";
                var troop = troops[t];
                var options = unitOptions.ToList();
                if (troop?.Unit != null && options.All(o => o.Id != troop.Unit)) options.Add((troop.Unit, troop.Unit));
                Choice(page, $"Troop {t + 1}", tp + ".unit", options, () => Troop(ti)?.Unit, (p, v) => p.WaveSets[Index].Troops[ti].Unit = v,
                    "The player unit that stands in for its base troop.", structural: true);
                var baseUnit = EditorCatalog.BaseOf(Pack, troop?.Unit);
                var buildings = new List<(string Value, string Display)> { (null, "Every building that trains it") };
                buildings.AddRange((baseUnit?.Buildings ?? []).Select(b => (b, b)));
                if (troop?.Building != null && buildings.All(b => b.Value != troop.Building)) buildings.Add((troop.Building, troop.Building));
                Choice(page, "   Trained by", tp + ".building", buildings, () => Troop(ti)?.Building, (p, v) => p.WaveSets[Index].Troops[ti].Building = v,
                    $"Only this building's {baseUnit?.DisplayName ?? "troops"} are replaced, or every building that trains them.");
                page.Row("   Remove this troop", "", "Buildings train their usual troop again.", () =>
                    Edit("Remove troop", w => { w.Troops.RemoveAt(ti); if (w.Troops.Count == 0) w.Troops = null; }, structural: true));
            }
            if (players.Count > 0)
                page.Button("Add troop", () =>
                {
                    var used = new HashSet<string>((Set.Troops ?? []).Select(x => x?.Unit).Where(x => x != null));
                    var pick = players.FirstOrDefault(u => !used.Contains(u.Id)) ?? players[0];
                    Edit("Add troop", w => (w.Troops ??= []).Add(new TroopDef { Unit = pick.Id }), structural: true);
                }, "Buildings train one of your player units instead of their usual troop on this run.");
        }

        private TroopDef Troop(int i) => Set?.Troops != null && i < Set.Troops.Count ? Set.Troops[i] : null;

        // ---- Night ----------------------------------------------------------------------------------------------------

        private void BuildNight(ModPage page, int n)
        {
            var nd = Set.Nights[n];
            string np = $"{P}.nights[{n}]";
            if (nd == null) { Marked(page, $"Night {n + 1}", () => Dim("empty"), "", np, null); return; }
            nd.Spawns ??= [];
            int k = n;
            Marked(page, $"Night {n + 1}", () => NightSummary(Set?.Nights.ElementAtOrDefault(k)),
                "Apply to edit the night: difficulty, order, add a group.", np, () => Focus(k, -1));
            BuildChips(page, n, np);
            if (night == n && spawn < 0) BuildNightFields(page, n, np);
            if (night == n && spawn >= 0) BuildSpawnFields(page, n, spawn);
        }

        private void Focus(int n, int s)
        {
            if (night == n && spawn == s) { night = -1; spawn = -1; }
            else { night = n; spawn = s; }
            Rebuild();
        }

        private string NightSummary(NightDef nd)
        {
            if (nd == null) return "";
            int enemies = nd.Spawns.Sum(s => s?.Count ?? 0);
            int gold = nd.Spawns.Sum(s => s?.Gold ?? 0);
            float length = nd.Spawns.Count == 0 ? 0 : nd.Spawns.Max(s => s == null ? 0 : s.Delay + Math.Max(0, s.Count - 1) * s.Interval);
            string text = $"{enemies} enemies · {gold} gold · {Mathf.CeilToInt(length)} s";
            if (!Mathf.Approximately(nd.Difficulty, 1f)) text = $"×{EditorCatalog.Num(nd.Difficulty)} · " + text;
            return Dim(text);
        }

        private void BuildNightFields(ModPage page, int n, string np)
        {
            Number(page, Schema.Difficulty, np + ".difficulty", () => Set?.Nights.ElementAtOrDefault(n)?.Difficulty,
                (p, v) => p.WaveSets[Index].Nights[n].Difficulty = v ?? 1f, nullable: false, label: "    Difficulty");
            Text(page, "    Warning", np + ".warning", () => Set?.Nights.ElementAtOrDefault(n)?.Warning, (p, v) => p.WaveSets[Index].Nights[n].Warning = v,
                "A line shown to the player before the night starts.", 120);
            page.Button("    Add group", () => AddSpawn(n), "Adds a group of enemies to this night.");
            if (n > 0) page.Button("    Move earlier", () => MoveNight(n, -1), "Swaps this night with the one before.");
            if (n < Set.Nights.Count - 1) page.Button("    Move later", () => MoveNight(n, 1), "Swaps this night with the one after.");
            page.Button("    Duplicate night", () =>
            {
                Edit("Duplicate night", w => w.Nights.Insert(n + 1, EditorCatalog.Copy(w.Nights[n])), structural: true);
                night = n + 1;
                Rebuild();
            }, "Inserts a copy of this night after it.");
            if (Set.Nights.Count > 1)
                Danger(page, "    Delete night", "Removes this night.", () =>
                {
                    Edit("Delete night", w => w.Nights.RemoveAt(n), structural: true);
                    ResetSelection();
                    Rebuild();
                });
        }

        private void MoveNight(int n, int dir)
        {
            Edit(dir < 0 ? "Move night earlier" : "Move night later", w => (w.Nights[n], w.Nights[n + dir]) = (w.Nights[n + dir], w.Nights[n]), structural: true);
            night = n + dir;
            Rebuild();
        }

        private void AddSpawn(int n)
        {
            int added = Set.Nights[n].Spawns.Count;
            Edit("Add group", w =>
            {
                var spawns = w.Nights[n].Spawns;
                spawns.Add(spawns.Count > 0 ? EditorCatalog.Copy(spawns[spawns.Count - 1]) : DefaultSpawn(Pack, Host));
            }, structural: true);
            night = n;
            spawn = added;
            Rebuild();
        }

        internal static SpawnDef DefaultSpawn(DesignPack pack, CatalogHost host)
        {
            var custom = pack.Units.FirstOrDefault(u => u.Side == UnitSide.Enemy);
            string unit = custom?.Id ?? ContentRef.Vanilla(EditorCatalog.Units(UnitSide.Enemy).FirstOrDefault()?.Id ?? "E Melee");
            var b = EditorCatalog.BaseOf(pack, unit);
            var line = host?.SpawnLines.FirstOrDefault(l => b?.Flying == true ? l.Flying : l.SmallGround) ?? host?.SpawnLines.FirstOrDefault();
            return new SpawnDef { Unit = unit, Count = 5, Interval = 1f, Line = line?.Name, Gold = 5 };
        }

        // Chips other than a night's first aren't in the page's element list, so ModFrame can't keep them selected
        // across a rebuild; remember which chip (night, group; -1 for "+ group") was selected and select its new copy.
        private readonly Dictionary<TFUITextButton, (int Night, int Spawn)> chipKeys = [];

        private string ChipLabel(int n, int s)
        {
            var sp = Spawn(n, s);
            var issues = IssuesAt($"{P}.nights[{n}].spawns[{s}]").ToList();
            string mark = issues.Any(i => i.Severity == Severity.Error) ? $" <color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Error))}>\u25c6</color>"
                : issues.Count > 0 ? $" <color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Warning))}>\u25c6</color>" : "";
            return $"<b>{sp?.Count ?? 0}×</b> {Escape(Short(EditorCatalog.UnitName(Pack, sp?.Unit), 12))}{mark}\n" +
                $"<size=75%><color=#{ModStyle.Hex(ModStyle.Dim)}>{Escape(Short(sp?.Line ?? "no line", 14))} · {EditorCatalog.Num(sp?.Delay ?? 0)} s{(sp?.Elite == true ? " · elite" : "")}</color></size>";
        }

        // Value edits (count, delay, line, elite) don't rebuild the page; redraw the chips' text in place.
        protected override void OnFieldsUpdated()
        {
            foreach (var kv in chipKeys)
                if (kv.Key != null && kv.Value.Spawn >= 0 && Spawn(kv.Value.Night, kv.Value.Spawn) != null)
                    kv.Key.SetOriginalString(ChipLabel(kv.Value.Night, kv.Value.Spawn));
        }

        public override void Rebuild()
        {
            var selected = Frame.Frame?.CurrentSelection as TFUITextButton;
            bool wasChip = selected != null && chipKeys.TryGetValue(selected, out var key);
            if (!wasChip) { Frame.Refresh(); return; }
            chipKeys.TryGetValue(selected, out key);
            Frame.Refresh();
            var again = chipKeys.FirstOrDefault(kv => kv.Value == key).Key
                ?? chipKeys.FirstOrDefault(kv => kv.Value.Night == key.Night).Key;
            if (again != null) Frame.Frame?.Select(again);
        }

        // ---- Chips ----------------------------------------------------------------------------------------------------

        private const float ChipWidth = 200f, ChipHeight = 58f, ChipGap = 8f, ChipFont = 23f;

        private void BuildChips(ModPage page, int n, string np)
        {
            var spawns = Set.Nights[n].Spawns;
            int count = spawns.Count + 1;
            int perLine = Mathf.Max(1, Mathf.FloorToInt((page.Content.rect.width > 100 ? page.Content.rect.width : 830f) / (ChipWidth + ChipGap)));
            int lines = (count + perLine - 1) / perLine;
            var area = page.Custom($"Night {n + 1} groups", lines * (ChipHeight + ChipGap));
            var grid = area.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(ChipWidth, ChipHeight);
            grid.spacing = new Vector2(ChipGap, ChipGap);
            grid.padding = new RectOffset(28, 0, 0, 0);
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;

            var chips = new List<TFUITextButton>();
            for (int s = 0; s < spawns.Count; s++)
            {
                int k = s;
                var sp = spawns[s];
                string sp_ = $"{np}.spawns[{s}]";
                var chip = Chip(area, ChipLabel(n, s), EditorCatalog.UnitIcon(Draft, sp?.Unit), () => Focus(n, k), night == n && spawn == k);
                ModTooltip.Attach(chip, EditorCatalog.UnitName(Pack, Spawn(n, k)?.Unit), () =>
                {
                    var g = Spawn(n, k);
                    return $"{g?.Count} from {g?.Line}, one every {EditorCatalog.Num(g?.Interval ?? 0)} s, starting {EditorCatalog.Num(g?.Delay ?? 0)} s into the night; " +
                        $"{g?.Gold} gold in all." + string.Concat(IssuesAt(sp_).Select(i => "\n" + Capital(i.Message))) + "\nApply to edit.";
                });
                chips.Add(chip);
                chipKeys[chip] = (n, s);
            }
            var add = Chip(area, $"<color=#{ModStyle.Hex(ModStyle.Dim)}>+ group</color>", null, () => AddSpawn(n), false);
            ModTooltip.Attach(add, "Add group", () => "Adds a group of enemies to this night.");
            chips.Add(add);
            chipKeys[add] = (n, -1);

            // The first chip joins the page's up/down chain; the others follow it up and down, and left/right moves
            // along the row.
            page.AddElement(chips[0]);
            for (int i = 0; i < chips.Count; i++)
            {
                var chip = chips[i];
                chip.leftNav = i > 0 ? chips[i - 1] : null;
                chip.rightNav = i < chips.Count - 1 ? chips[i + 1] : null;
                if (i == 0) continue;
                var first = chips[0];
                chip.onEmptyNavigate.AddListener(direction =>
                {
                    var target = direction == ThronefallUIElement.NavigationDirection.Up ? first.topNav
                        : direction == ThronefallUIElement.NavigationDirection.Down ? first.botNav : null;
                    if (target != null) Frame.Frame?.Select(target);
                });
            }
        }

        private static TFUITextButton Chip(RectTransform parent, string label, Sprite icon, Action onClick, bool open)
        {
            var button = GameWidgets.Button(GameWidgets.MenuButton, parent, "Chip", label, onClick);
            var text = button.GetComponent<TextMeshProUGUI>();
            // The menu button's styles are sized for full rows; scale every state down to chip size.
            float baseSize = Traverse.Create(button).Field("defaultStyle").GetValue<TFUITextButton.Style>()?.fontSize ?? text.fontSize;
            float factor = ChipFont / Mathf.Max(1f, baseSize);
            foreach (var style in new[] { button.focussedStyle, button.selectedStyle, button.focussedAndSelectedStyle, button.overrideStyle })
                if (style != null) style.fontSize *= factor;
            var rest = Traverse.Create(button).Field("defaultStyle").GetValue<TFUITextButton.Style>();
            if (rest != null) rest.fontSize = ChipFont;
            text.fontSize = ChipFont;
            text.enableAutoSizing = false;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.lineSpacing = -20f;
            float left = 10f;
            // A plate behind the chip, like the game's charcoal panels; the open group's plate has the mod's edge.
            var plate = new GameObject("Plate", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
            plate.transform.SetParent(button.transform, false);
            plate.transform.SetAsFirstSibling();
            plate.GetComponent<LayoutElement>().ignoreLayout = true;
            var plateImage = plate.GetComponent<Image>();
            plateImage.color = open ? new Color(ModStyle.Accent.r, ModStyle.Accent.g, ModStyle.Accent.b, 0.22f) : new Color(1f, 1f, 1f, 0.035f);
            plateImage.raycastTarget = true;
            var plateRect = (RectTransform)plate.transform;
            plateRect.anchorMin = Vector2.zero;
            plateRect.anchorMax = Vector2.one;
            plateRect.offsetMin = new Vector2(-4f, 0f);
            plateRect.offsetMax = Vector2.zero;
            if (icon != null)
            {
                var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                iconObj.transform.SetParent(button.transform, false);
                var image = iconObj.GetComponent<Image>();
                image.sprite = icon;
                image.preserveAspect = true;
                image.raycastTarget = false;
                var r = (RectTransform)iconObj.transform;
                r.anchorMin = r.anchorMax = new Vector2(0f, 0.5f);
                r.pivot = new Vector2(0f, 0.5f);
                r.anchoredPosition = new Vector2(6f, 0f);
                r.sizeDelta = new Vector2(38f, 38f);
                left = 50f;
            }
            text.margin = new Vector4(left, 0f, 6f, 0f);
            button.SetOriginalString(label);
            button.gameObject.SetActive(true);
            return button;
        }

        private static string Short(string text, int max) => text == null || text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "\u2026";

        // ---- Spawn group ----------------------------------------------------------------------------------------------

        private SpawnDef Spawn(int n, int s) => Set?.Nights.ElementAtOrDefault(n)?.Spawns.ElementAtOrDefault(s);

        private void BuildSpawnFields(ModPage page, int n, int s)
        {
            string sp = $"{P}.nights[{n}].spawns[{s}]";
            const string I = "    ";
            Marked(page, I + "Unit", () => EditorCatalog.UnitName(Pack, Spawn(n, s)?.Unit), "Which enemy the group is. Apply to choose.", sp + ".unit",
                () => Picker.One("Choose an Enemy", UnitItems(Draft), Spawn(n, s)?.Unit, id => Edit("Change group unit", w => w.Nights[n].Spawns[s].Unit = id, structural: true)),
                EditorCatalog.UnitIcon(Draft, Spawn(n, s)?.Unit));
            Number(page, Schema.Count, sp + ".count", () => Spawn(n, s)?.Count, (p, v) => p.WaveSets[Index].Nights[n].Spawns[s].Count = Mathf.RoundToInt(v ?? 1), nullable: false, label: I + "Count");
            Number(page, Schema.Interval, sp + ".interval", () => Spawn(n, s)?.Interval, (p, v) => p.WaveSets[Index].Nights[n].Spawns[s].Interval = v ?? 0.5f, nullable: false, label: I + "Interval");
            Number(page, Schema.Delay, sp + ".delay", () => Spawn(n, s)?.Delay, (p, v) => p.WaveSets[Index].Nights[n].Spawns[s].Delay = v ?? 0f, nullable: false, label: I + "Delay");
            var lines = Host?.SpawnLines ?? [];
            Choice(page, I + "Spawn line", sp + ".line", lines.Select(l => (l.Name, l.Name)).ToList(), () => Spawn(n, s)?.Line,
                (p, v) => p.WaveSets[Index].Nights[n].Spawns[s].Line = v,
                "Where on the map the group comes from. Flying enemies need a flying line; large ones a line that allows them. Apply for the list.",
                picker: () => Picker.One("Spawn Line", LineItems(Host, EditorCatalog.BaseOf(Pack, Spawn(n, s)?.Unit)), Spawn(n, s)?.Line,
                    id => Edit("Change spawn line", w => w.Nights[n].Spawns[s].Line = id, structural: true)),
                display: () => Escape(Spawn(n, s)?.Line ?? "none"));
            Number(page, Schema.Gold, sp + ".gold", () => Spawn(n, s)?.Gold, (p, v) => p.WaveSets[Index].Nights[n].Spawns[s].Gold = Mathf.RoundToInt(v ?? 0), nullable: false, label: I + "Gold");
            Toggle(page, I + "Elite", sp + ".elite", () => Spawn(n, s)?.Elite == true, (p, v) => p.WaveSets[Index].Nights[n].Spawns[s].Elite = v,
                "Elite enemies are tougher and look golden, like the game's elites.");
            var count = Set.Nights[n].Spawns.Count;
            if (s > 0) page.Button(I + "Move left", () => MoveSpawn(n, s, -1), "Moves the group earlier in the night's list (timing is set by Delay).");
            if (s < count - 1) page.Button(I + "Move right", () => MoveSpawn(n, s, 1), "Moves the group later in the night's list.");
            page.Button(I + "Duplicate group", () =>
            {
                Edit("Duplicate group", w => w.Nights[n].Spawns.Insert(s + 1, EditorCatalog.Copy(w.Nights[n].Spawns[s])), structural: true);
                spawn = s + 1;
                Rebuild();
            }, "Inserts a copy of this group.");
            page.Button(I + "Remove group", () =>
            {
                Edit("Remove group", w => w.Nights[n].Spawns.RemoveAt(s), structural: true);
                spawn = -1;
                Rebuild();
            }, "Removes this group from the night (you can undo this).");
        }

        private void MoveSpawn(int n, int s, int dir)
        {
            Edit("Move group", w =>
            {
                var list = w.Nights[n].Spawns;
                (list[s], list[s + dir]) = (list[s + dir], list[s]);
            }, structural: true);
            spawn = s + dir;
            Rebuild();
        }

        // ---- Picker items ---------------------------------------------------------------------------------------------

        internal static IEnumerable<Picker.Item> HostItems() =>
            EditorCatalog.Hosts.Select(h => new Picker.Item
            {
                Id = h.Scene, Label = h.DisplayName,
                Value = $"{h.VanillaWaves?.Nights?.Count ?? 0} nights · {h.SpawnLines.Count} spawn lines",
                Tooltip = $"Starts with {h.StartingGold} gold. Spawn lines: {string.Join(", ", h.SpawnLines.Select(l => l.Name))}.",
            });

        /// <summary>Enemies a wave can spawn: this pack's, then vanilla ones.</summary>
        internal static IEnumerable<Picker.Item> UnitItems(PackDraft draft, string exclude = null)
        {
            var pack = draft.Pack;
            var mine = pack.Units.Where(u => u.Side == UnitSide.Enemy && u.Id != exclude).ToList();
            if (mine.Count > 0)
            {
                yield return new Picker.Item { Header = true, Label = "This pack" };
                foreach (var u in mine)
                {
                    var b = EditorCatalog.BaseOf(pack, u.Id);
                    yield return new Picker.Item
                    {
                        Id = u.Id, Label = string.IsNullOrWhiteSpace(u.Name) ? u.Id : u.Name, Icon = EditorCatalog.UnitIcon(draft, u.Id),
                        Value = Dim("based on ") + (b?.DisplayName ?? "?"), Tooltip = u.Description,
                    };
                }
            }
            yield return new Picker.Item { Header = true, Label = "Vanilla" };
            foreach (var c in EditorCatalog.Units(UnitSide.Enemy))
                yield return new Picker.Item
                {
                    Id = ContentRef.Vanilla(c.Id), Label = c.DisplayName, Icon = c.Icon, Value = $"HP {EditorCatalog.Num(c.MaxHp)}",
                    Tooltip = EditorCatalog.Stats(c),
                };
        }

        private static IEnumerable<Picker.Item> LineItems(CatalogHost host, CatalogUnit unit)
        {
            foreach (var l in host?.SpawnLines ?? [])
            {
                var kinds = new List<string>();
                if (l.SmallGround) kinds.Add("ground");
                if (l.BigGround) kinds.Add("large");
                if (l.Flying) kinds.Add("flying");
                bool fits = unit == null || (unit.Flying ? l.Flying : unit.Large ? l.BigGround : l.SmallGround);
                yield return new Picker.Item
                {
                    Id = l.Name, Label = fits ? l.Name : $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{l.Name}</color>",
                    Value = string.Join(" · ", kinds), Tooltip = fits ? $"Allows {string.Join(", ", kinds)} enemies." : $"Doesn't suit {unit?.DisplayName}: allows {string.Join(", ", kinds)} enemies.",
                };
            }
        }
    }
}
