using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using MPUIKIT;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    Loadout randomizer: dice buttons in the Weapon / Perks / Mutators headers of the loadout screen, plus a
    "Randomize all" button beside Play.

    The buttons are ThronefallUIElements living inside the loadout UIFrame, so the frame itself handles mouse hover,
    clicks and controller/keyboard navigation, and plays the native select/apply sounds (TFUIAudioHelper). Applying a
    button makes it the frame's LastApplied, so the game's TrySelectEquippableForLoadout (run from the frame's onApply)
    ignores it; no click interception is needed. Navigation is the game's own, with a postfix that threads the dice in
    between the grids. Picks go through the same PerkManager.SetEquipped / Pick / UnPick calls the game uses.

    Settings shape what a roll picks (perk count, mutator count and odds, keeping hand picks); a keybind excludes the
    hovered item from rolls and another undoes the last roll.
*/
namespace PalaceMod
{
    public class LoadoutRandomizerManager : BaseManager
    {
        public override string FeatureSection => "Loadout randomizer";
        public override string FeatureDescription => "Adds dice to the loadout screen that roll a random weapon, perks or mutators, and a Randomize all button.";

        public enum MutatorOdds { FavorFewer, Even }
        internal enum Category { Weapon, Perks, Mutators }

        private ConfigEntry<int> perkCount;
        private ConfigEntry<int> minMutators;
        private ConfigEntry<int> maxMutators;
        private ConfigEntry<MutatorOdds> mutatorOdds;
        private ConfigEntry<bool> keepHandPicks;
        private ConfigEntry<string> excludedEntry;
        private Keybind excludeKey;
        private Keybind undoKey;

        private readonly System.Random random = new();
        private Sprite diceSprite;
        private Sprite excludedSprite;
        private readonly HashSet<string> excluded = new();
        // Items the player picked by hand on the current screen; kept on reroll when keepHandPicks is on.
        private readonly HashSet<Equippable> handPicked = new();
        // Loadouts before each roll, for undo. Cleared whenever the loadout screen opens.
        private readonly List<HashSet<Equippable>> history = new();
        private const int MaxHistory = 30;
        private const double FavorFewerLambda = 0.3;
        private bool hintShown;

        // ---- Game accessors -----------------------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<LoadoutUIHelper, List<TFUIEquippable>> WeaponsRef = AccessTools.FieldRefAccess<LoadoutUIHelper, List<TFUIEquippable>>("weapons");
        private static readonly AccessTools.FieldRef<LoadoutUIHelper, List<TFUIEquippable>> PerksRef = AccessTools.FieldRefAccess<LoadoutUIHelper, List<TFUIEquippable>>("perks");
        private static readonly AccessTools.FieldRef<LoadoutUIHelper, List<TFUIEquippable>> MutatorsRef = AccessTools.FieldRefAccess<LoadoutUIHelper, List<TFUIEquippable>>("mutators");
        private static readonly AccessTools.FieldRef<LoadoutUIHelper, List<TFUIEquippable>> EquippedWeaponsRef = AccessTools.FieldRefAccess<LoadoutUIHelper, List<TFUIEquippable>>("equippedWeapons");
        private static readonly AccessTools.FieldRef<LoadoutUIHelper, List<TFUIEquippable>> EquippedPerksRef = AccessTools.FieldRefAccess<LoadoutUIHelper, List<TFUIEquippable>>("equippedPerks");
        private static readonly AccessTools.FieldRef<LoadoutUIHelper, List<TFUIEquippable>> EquippedMutatorsRef = AccessTools.FieldRefAccess<LoadoutUIHelper, List<TFUIEquippable>>("equippedMutators");
        private static readonly AccessTools.FieldRef<LoadoutUIHelper, LevelInfo> LevelInfoRef = AccessTools.FieldRefAccess<LoadoutUIHelper, LevelInfo>("levelInfo");
        private static readonly Action<LoadoutUIHelper> UpdatePerkText = AccessTools.MethodDelegate<Action<LoadoutUIHelper>>(AccessTools.Method(typeof(LoadoutUIHelper), "UpdatePerkPointsRemainingText"));
        private static readonly Action<LoadoutUIHelper> UpdateMutatorText = AccessTools.MethodDelegate<Action<LoadoutUIHelper>>(AccessTools.Method(typeof(LoadoutUIHelper), "UpdateMutatorBonusText"));
        private static readonly Action<LoadoutUIHelper> RecomputeNavigation = AccessTools.MethodDelegate<Action<LoadoutUIHelper>>(AccessTools.Method(typeof(LoadoutUIHelper), "RecomputeNavigation"));
        private static readonly AccessTools.FieldRef<TFUIEquippable, Color> WeaponBgRef = AccessTools.FieldRefAccess<TFUIEquippable, Color>("weaponBgColor");
        private static readonly AccessTools.FieldRef<TFUIEquippable, Color> PerkBgRef = AccessTools.FieldRefAccess<TFUIEquippable, Color>("perkBgColor");
        private static readonly AccessTools.FieldRef<TFUIEquippable, Color> MutatorBgRef = AccessTools.FieldRefAccess<TFUIEquippable, Color>("mutatorBgColor");

        private static List<TFUIEquippable> Items(LoadoutUIHelper h, Category c) => c switch
        {
            Category.Weapon => WeaponsRef(h),
            Category.Perks => PerksRef(h),
            _ => MutatorsRef(h),
        };

        private static List<TFUIEquippable> Equipped(LoadoutUIHelper h, Category c) => c switch
        {
            Category.Weapon => EquippedWeaponsRef(h),
            Category.Perks => EquippedPerksRef(h),
            _ => EquippedMutatorsRef(h),
        };

        private static bool HasMutators(LoadoutUIHelper h) => h.mode == LocalGamestate.GameMode.Classic && h.mutatorsParent != null;

        /// <summary>Randomizing applies to classic-mode loadouts the player is free to choose.</summary>
        private static bool Randomizable(LoadoutUIHelper h)
        {
            var info = LevelInfoRef(h);
            return h.mode == LocalGamestate.GameMode.Classic && info != null && !info.HasFixedLoadout && WeaponsRef(h).Count > 0;
        }

        // ---- Setup --------------------------------------------------------------------------------------------

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            diceSprite = LoadSpriteFromAssets("dice_icon.png");
            excludedSprite = MakeExcludedSprite();

            var config = plugin.Config;
            perkCount = config.Bind(FeatureSection, "Perks to roll", 0, new ConfigDescription(
                "How many perks the perk dice picks. Never more than the level's perk slots.",
                new AcceptableValueRange<int>(0, 10), new SettingTags { ZeroLabel = "All slots" }));
            minMutators = config.Bind(FeatureSection, "Fewest mutators", 1, new ConfigDescription(
                "The mutator dice picks at least this many mutators. Set fewest and most equal for a fixed count.",
                new AcceptableValueRange<int>(0, 10)));
            maxMutators = config.Bind(FeatureSection, "Most mutators", 0, new ConfigDescription(
                "The mutator dice picks at most this many mutators.",
                new AcceptableValueRange<int>(0, 15), new SettingTags { ZeroLabel = "No limit" }));
            mutatorOdds = config.Bind(FeatureSection, "Mutator odds", MutatorOdds.FavorFewer, new ConfigDescription(
                "Favor fewer: small mutator counts are likelier. Even: every count in the range is equally likely."));
            keepHandPicks = config.Bind(FeatureSection, "Keep hand picks", true, new ConfigDescription(
                "Perks and mutators you click on this screen stay when you roll, and the dice fill the remaining slots. Randomize all also keeps a hand-picked weapon."));
            excludedEntry = config.Bind(FeatureSection, "Excluded", "", new ConfigDescription(
                "Items the dice never pick, by internal name, comma-separated. Toggle them on the loadout screen with the exclude key.",
                null, new SettingTags { Hidden = true }));
            LoadExcluded();
            excludedEntry.SettingChanged += (_, _) => { LoadExcluded(); RefreshExcludedMarks(); };

            excludeKey = Keybinds.Register(FeatureSection, "exclude", "Exclude from randomizer", new KeyboardShortcut(KeyCode.X),
                "On the loadout screen, toggles whether the dice may pick the hovered or selected item.", KeybindFlags.AllowInMenus);
            undoKey = Keybinds.Register(FeatureSection, "undo", "Undo loadout roll", new KeyboardShortcut(KeyCode.Z),
                "On the loadout screen, puts back the loadout from before the last roll.", KeybindFlags.AllowInMenus);

            RegisterDevCommands();
        }

        public override void Update()
        {
            var ui = ActiveUI();
            if (ui == null) return;
            if (excludeKey.Down) ToggleExcludeFocused(ui.helper);
            if (undoKey.Down) Undo(ui.helper);
        }

        protected override void OnFeatureDisabled()
        {
            foreach (var ui in UnityEngine.Object.FindObjectsOfType<LoadoutRandomizerUI>(true))
            {
                var helper = ui.helper;
                ui.Teardown();
                if (helper != null && Randomizable(helper)) RecomputeNavigation(helper); // vanilla nav; our postfix is gone
            }
        }

        public override void OnDestroy() => OnFeatureDisabled();

        /// <summary>The randomizer UI on the loadout screen the player is looking at, if any.</summary>
        private static LoadoutRandomizerUI ActiveUI()
        {
            var active = UIFrameManager.instance != null ? UIFrameManager.instance.ActiveFrame : null;
            if (active == null) return null;
            var ui = active.GetComponentInChildren<LoadoutRandomizerUI>();
            if (ui == null) ui = active.GetComponentInParent<LoadoutRandomizerUI>();
            return ui != null && ui.helper != null && ui.helper.frame == active && ui.Visible ? ui : null;
        }

        // ---- Patches ------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(LoadoutUIHelper), nameof(LoadoutUIHelper.OnShow))]
        private static class OnShowPatch
        {
            private static void Postfix(LoadoutUIHelper __instance)
            {
                var manager = Feature<LoadoutRandomizerManager>.Instance;
                if (manager == null || __instance == null || !__instance.gameObject.activeSelf) return;
                manager.history.Clear();
                manager.handPicked.Clear();
                var ui = __instance.GetComponent<LoadoutRandomizerUI>();
                if (ui == null) ui = __instance.gameObject.AddComponent<LoadoutRandomizerUI>();
                ui.Build(manager, __instance);
                if (WeaponsRef(__instance).Count > 0) RecomputeNavigation(__instance);
                manager.RefreshExcludedMarks();
            }
        }

        // Threads the dice into the game's own navigation: each dice sits between the previous grid (or Play) and the
        // first row of its grid; Randomize all sits left of Play.
        [HarmonyPatch(typeof(LoadoutUIHelper), "RecomputeNavigation")]
        private static class RecomputeNavigationPatch
        {
            private static void Postfix(LoadoutUIHelper __instance)
            {
                var ui = __instance.GetComponent<LoadoutRandomizerUI>();
                if (ui == null || !ui.Visible) return;
                int columns = Mathf.Max(1, __instance.weaponsParent.constraintCount);
                var play = __instance.playButton;

                var all = new List<ThronefallUIElement> { play };
                foreach (Category c in Enum.GetValues(typeof(Category)))
                    if (c != Category.Mutators || HasMutators(__instance)) all.AddRange(Items(__instance, c));

                foreach (Category c in Enum.GetValues(typeof(Category)))
                {
                    if (!ui.dice.TryGetValue(c, out var dice) || dice == null) continue;
                    var items = Items(__instance, c);
                    if (items.Count == 0) continue;
                    var set = new HashSet<ThronefallUIElement>(items);
                    dice.topNav = items[0].topNav;
                    dice.leftNav = items[0].leftNav;
                    dice.botNav = items[0];
                    dice.rightNav = items[0];
                    foreach (var e in all)
                    {
                        if (e == null) continue;
                        bool inside = set.Contains(e);
                        if (!inside)
                        {
                            if (set.Contains(e.topNav)) e.topNav = dice;
                            if (set.Contains(e.botNav)) e.botNav = dice;
                            if (set.Contains(e.leftNav)) e.leftNav = dice;
                            if (set.Contains(e.rightNav)) e.rightNav = dice;
                        }
                        else
                        {
                            int index = items.IndexOf((TFUIEquippable)e);
                            if (index < columns && !set.Contains(e.topNav)) e.topNav = dice;
                            if (index == 0) e.leftNav = dice;
                        }
                    }
                    // Other dice that pointed into this grid now point at this dice.
                    foreach (var other in ui.dice.Values)
                    {
                        if (other == null || other == dice) continue;
                        if (set.Contains(other.topNav)) other.topNav = dice;
                        if (set.Contains(other.leftNav)) other.leftNav = dice;
                    }
                }

                if (ui.all != null)
                {
                    ui.all.rightNav = play;
                    ui.all.topNav = play.topNav;
                    ui.all.leftNav = play.leftNav;
                    ui.all.botNav = play.botNav;
                    play.leftNav = ui.all;
                }
            }
        }

        // A tile the player clicks by hand is a hand pick (kept on reroll); clicking it off forgets it.
        [HarmonyPatch(typeof(LoadoutUIHelper), nameof(LoadoutUIHelper.TrySelectEquippableForLoadout))]
        private static class TrySelectPatch
        {
            private static void Postfix(LoadoutUIHelper __instance)
            {
                var manager = Feature<LoadoutRandomizerManager>.Instance;
                if (manager == null || __instance.frame == null) return;
                if (__instance.frame.LastApplied is not TFUIEquippable tile || tile.Locked || tile.Data == null) return;
                if (PerkManager.IsEquipped(tile.Data)) manager.handPicked.Add(tile.Data);
                else manager.handPicked.Remove(tile.Data);
            }
        }

        // Shows what a dice does in the loadout screen's tooltip panel.
        [HarmonyPatch(typeof(PerkSelectionTooltipHelper))]
        private static class TooltipPatch
        {
            [HarmonyPatch(nameof(PerkSelectionTooltipHelper.OnSelection)), HarmonyPostfix]
            private static void OnSelection(PerkSelectionTooltipHelper __instance) => Show(__instance, __instance.targetFrame?.CurrentSelection);

            [HarmonyPatch(nameof(PerkSelectionTooltipHelper.OnFocus)), HarmonyPostfix]
            private static void OnFocus(PerkSelectionTooltipHelper __instance) =>
                Show(__instance, __instance.targetFrame?.CurrentFocus ?? __instance.targetFrame?.CurrentSelection);

            private static void Show(PerkSelectionTooltipHelper tooltip, ThronefallUIElement element)
            {
                if (element is not RandomizeButton button) return;
                var manager = Feature<LoadoutRandomizerManager>.Instance;
                if (manager == null) return;
                if (tooltip.tooltipParent != null) tooltip.tooltipParent.SetActive(true);
                tooltip.selectionTitle.text = button.Title;
                tooltip.selectionDescription.text = manager.Describe(button.category, button.isAll);
                tooltip.selectionIcon.sprite = manager.diceSprite;
                tooltip.selectionIcon.color = Color.white;
                tooltip.background.enabled = true;
                tooltip.background.color = button.TileColor;
                if (tooltip.eliteBackground != null) tooltip.eliteBackground.enabled = false;
                if (tooltip.enemyNumberBg != null) tooltip.enemyNumberBg.gameObject.SetActive(false);
                if (tooltip.UnitStatsDisplay != null) tooltip.UnitStatsDisplay.gameObject.SetActive(false);
                if (tooltip.sizer != null) tooltip.sizer.Trigger();
            }
        }

        private string Describe(Category category, bool isAll)
        {
            string keep = keepHandPicks.Value ? " Items you picked by hand stay." : "";
            string keys = $"\n<size=80%>{ShortcutLabel(excludeKey)}: exclude the hovered item from rolls. {ShortcutLabel(undoKey)}: undo a roll.</size>";
            if (isAll) return "Rolls a random weapon, perks and mutators." + keep + keys;
            return category switch
            {
                Category.Weapon => "Rolls a random weapon." + keys,
                Category.Perks => (perkCount.Value == 0 ? "Fills your perk slots with random perks." : $"Rolls {perkCount.Value} random perks.") + keep + keys,
                _ => $"Rolls {MutatorRangeText()} random mutators." + keep + keys,
            };
        }

        private string MutatorRangeText()
        {
            int min = minMutators.Value, max = maxMutators.Value;
            if (max == 0) return $"{min} or more";
            if (min >= max) return max.ToString();
            return $"{min} to {max}";
        }

        private static string ShortcutLabel(Keybind k) => k.Shortcut.MainKey == KeyCode.None ? "(unbound)" : k.Shortcut.ToString();

        // ---- Rolling ------------------------------------------------------------------------------------------

        internal void Roll(LoadoutUIHelper helper, Category? only)
        {
            if (helper == null || !Randomizable(helper)) return;
            if (!hintShown)
            {
                hintShown = true;
                Notify.Show($"Loadout dice: {ShortcutLabel(excludeKey)} excludes the hovered item from rolls, {ShortcutLabel(undoKey)} undoes a roll.",
                    NotifyKind.Info, 6f, "loadout-randomizer-hint");
            }
            PushHistory(helper);
            var changed = new List<TFUIEquippable>();
            if (only == null || only == Category.Weapon) changed.AddRange(RollWeapon(helper, keepHand: only == null && keepHandPicks.Value));
            if (only == null || only == Category.Perks) changed.AddRange(RollPerks(helper));
            if ((only == null || only == Category.Mutators) && HasMutators(helper)) changed.AddRange(RollMutators(helper));
            RefreshTexts(helper);
            var ui = helper.GetComponent<LoadoutRandomizerUI>();
            if (ui != null) ui.PopTiles(changed);
            Plugin.Logger.LogInfo($"Loadout rolled ({only?.ToString() ?? "all"}): {LoadoutText(helper)}");
        }

        private bool Rollable(TFUIEquippable t) => t != null && !t.Locked && t.Data != null && t.Data.IsUnlocked && !excluded.Contains(t.Data.name);

        private IEnumerable<TFUIEquippable> RollWeapon(LoadoutUIHelper helper, bool keepHand)
        {
            var equipped = Equipped(helper, Category.Weapon);
            if (keepHand && equipped.Any(w => handPicked.Contains(w.Data))) return Array.Empty<TFUIEquippable>();
            var pool = Items(helper, Category.Weapon).Where(Rollable).ToList();
            if (pool.Count == 0) { Notify.Show("Every weapon is excluded from rolls.", NotifyKind.Warning, 3f, "loadout-randomizer"); return Array.Empty<TFUIEquippable>(); }
            // Prefer a different weapon so a roll always visibly changes something.
            var fresh = pool.Where(w => !equipped.Contains(w)).ToList();
            if (fresh.Count > 0) pool = fresh;
            var pick = pool[random.Next(pool.Count)];
            return SetPicks(helper, Category.Weapon, new List<TFUIEquippable> { pick });
        }

        private IEnumerable<TFUIEquippable> RollPerks(LoadoutUIHelper helper)
        {
            int slots = LevelInfoRef(helper).maxPerkCount;
            int target = perkCount.Value == 0 ? slots : Math.Min(perkCount.Value, slots);
            return RollMany(helper, Category.Perks, target);
        }

        private IEnumerable<TFUIEquippable> RollMutators(LoadoutUIHelper helper)
        {
            var kept = Kept(helper, Category.Mutators);
            int available = kept.Count + Items(helper, Category.Mutators).Count(t => Rollable(t) && !kept.Contains(t));
            int min = Math.Max(minMutators.Value, kept.Count);
            int max = maxMutators.Value == 0 ? available : Math.Max(maxMutators.Value, kept.Count);
            max = Math.Min(max, available);
            min = Math.Min(min, max);
            return RollMany(helper, Category.Mutators, SampleCount(min, max));
        }

        private List<TFUIEquippable> Kept(LoadoutUIHelper helper, Category c) =>
            keepHandPicks.Value ? Equipped(helper, c).Where(t => handPicked.Contains(t.Data)).ToList() : new List<TFUIEquippable>();

        private IEnumerable<TFUIEquippable> RollMany(LoadoutUIHelper helper, Category c, int target)
        {
            var picks = Kept(helper, c);
            var pool = Items(helper, c).Where(t => Rollable(t) && !picks.Contains(t)).ToList();
            while (picks.Count < target && pool.Count > 0)
            {
                int i = random.Next(pool.Count);
                picks.Add(pool[i]);
                pool.RemoveAt(i);
            }
            return SetPicks(helper, c, picks);
        }

        private int SampleCount(int min, int max)
        {
            if (max <= min) return min;
            if (mutatorOdds.Value == MutatorOdds.Even) return random.Next(min, max + 1);
            double total = 0;
            for (int i = min; i <= max; i++) total += Math.Exp(-FavorFewerLambda * i);
            double r = random.NextDouble() * total;
            for (int i = min; i <= max; i++)
            {
                r -= Math.Exp(-FavorFewerLambda * i);
                if (r <= 0) return i;
            }
            return max;
        }

        /// <summary>Makes exactly <paramref name="picks"/> the equipped items of a category, the way the game does.
        /// Returns the newly picked tiles.</summary>
        private static List<TFUIEquippable> SetPicks(LoadoutUIHelper helper, Category c, List<TFUIEquippable> picks)
        {
            var equipped = Equipped(helper, c);
            foreach (var t in equipped.Where(t => !picks.Contains(t)).ToList())
            {
                PerkManager.SetEquipped(t.Data, false);
                t.UnPick();
                equipped.Remove(t);
            }
            var added = new List<TFUIEquippable>();
            foreach (var t in picks.Where(t => !equipped.Contains(t)))
            {
                PerkManager.SetEquipped(t.Data, true);
                t.Pick();
                equipped.Add(t);
                added.Add(t);
            }
            return added;
        }

        private static void RefreshTexts(LoadoutUIHelper helper)
        {
            UpdatePerkText(helper);
            if (HasMutators(helper)) UpdateMutatorText(helper);
        }

        private void PushHistory(LoadoutUIHelper helper)
        {
            history.Add(new HashSet<Equippable>(AllCategories(helper).SelectMany(c => Equipped(helper, c)).Select(t => t.Data)));
            if (history.Count > MaxHistory) history.RemoveAt(0);
        }

        internal bool Undo(LoadoutUIHelper helper)
        {
            if (history.Count == 0)
            {
                Notify.Show("Nothing to undo.", NotifyKind.Info, 2f, "loadout-randomizer");
                return false;
            }
            var snapshot = history[history.Count - 1];
            history.RemoveAt(history.Count - 1);
            var changed = new List<TFUIEquippable>();
            foreach (var c in AllCategories(helper))
                changed.AddRange(SetPicks(helper, c, Items(helper, c).Where(t => snapshot.Contains(t.Data)).ToList()));
            RefreshTexts(helper);
            helper.GetComponent<LoadoutRandomizerUI>()?.PopTiles(changed);
            Plugin.Logger.LogInfo($"Loadout roll undone: {LoadoutText(helper)}");
            return true;
        }

        private static IEnumerable<Category> AllCategories(LoadoutUIHelper helper) =>
            HasMutators(helper) ? new[] { Category.Weapon, Category.Perks, Category.Mutators } : new[] { Category.Weapon, Category.Perks };

        private static string LoadoutText(LoadoutUIHelper helper) =>
            string.Join("; ", AllCategories(helper).Select(c => $"{c}: {string.Join(", ", Equipped(helper, c).Select(t => t.Data.name))}"));

        // ---- Exclusions ---------------------------------------------------------------------------------------

        private void LoadExcluded()
        {
            excluded.Clear();
            foreach (var name in excludedEntry.Value.Split(','))
                if (!string.IsNullOrWhiteSpace(name)) excluded.Add(name.Trim());
        }

        private void ToggleExcludeFocused(LoadoutUIHelper helper)
        {
            var frame = helper.frame;
            var tile = (frame.CurrentFocus ?? frame.CurrentSelection) as TFUIEquippable;
            if (tile == null || tile.Locked || tile.Data == null) return;
            ToggleExcluded(tile.Data);
        }

        internal bool ToggleExcluded(Equippable e)
        {
            bool nowExcluded = !excluded.Remove(e.name);
            if (nowExcluded) excluded.Add(e.name);
            excludedEntry.Value = string.Join(",", excluded.OrderBy(n => n)); // also refreshes the marks
            string name = TextTranslator.Translate(e.LOCIDENTIFIER_NAME);
            Notify.Show(nowExcluded ? $"{name} won't be rolled." : $"{name} can be rolled again.", NotifyKind.Info, 2f, "loadout-randomizer");
            return nowExcluded;
        }

        private void RefreshExcludedMarks()
        {
            foreach (var ui in UnityEngine.Object.FindObjectsOfType<LoadoutRandomizerUI>())
                ui.RefreshExcludedMarks(t => t.Data != null && !t.Locked && excluded.Contains(t.Data.name), excludedSprite);
        }

        // A small red "no" badge (circle with a slash) for excluded tiles.
        private static Sprite MakeExcludedSprite()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float c = (size - 1) / 2f, outer = size / 2f - 1f, ring = 7f, bar = 3.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                    // Distance to the "\" diagonal (top-left to bottom-right in screen space).
                    float diag = Mathf.Abs(dx + dy) / Mathf.Sqrt(2f);
                    float stroke = Mathf.Max(Mathf.Clamp01(1f - Mathf.Abs(d - (outer - ring / 2f)) + ring / 2f - 0.5f),
                        d < outer - ring / 2f ? Mathf.Clamp01(bar - diag + 0.5f) : 0f);
                    float fill = d < outer ? 0.55f : 0f;
                    var col = Color.Lerp(new Color(0.1f, 0.05f, 0.05f, fill), new Color(0.9f, 0.25f, 0.2f, 1f), stroke);
                    if (d > outer + 0.5f) col.a = 0f;
                    pixels[y * size + x] = col;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        // ---- Dev commands -------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("loadout", "loadout [scene]   open the loadout screen for a level (from level select); no args prints the loadout", args =>
            {
                if (args.Length == 0)
                {
                    var helper = ActiveUI()?.helper ?? UnityEngine.Object.FindObjectOfType<LoadoutUIHelper>();
                    if (helper == null) throw new InvalidOperationException("no loadout screen");
                    return LoadoutText(helper);
                }
                var transitions = SceneTransitionManager.instance ?? throw new InvalidOperationException("not loaded yet");
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != transitions.levelSelectScene)
                    throw new InvalidOperationException("go to level select first: `cmd levelselect`");
                var info = LevelProgressManager.instance.GetLevelInfoFromSceneName(args[0]) ?? throw new ArgumentException($"no level \"{args[0]}\"");
                if (UIFrameManager.instance.ActiveFrame != null) UIFrameManager.instance.CloseActiveFrame();
                LevelInteractor.lastActiveLevelInfo = info;
                if (!UIFrameManager.TryOpenLevelSelect()) throw new InvalidOperationException("couldn't open the loadout screen");
                return $"opened {info.sceneName} (fixed loadout: {info.HasFixedLoadout}, perk slots: {info.maxPerkCount})";
            });
            DevCommands.Register("randomize", "randomize <weapon|perks|mutators|all|undo|exclude <item>|dump>   drive the loadout randomizer", args =>
            {
                var ui = ActiveUI() ?? throw new InvalidOperationException("no randomizable loadout screen open");
                string what = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
                switch (what)
                {
                    case "weapon": ui.Press(ui.dice[Category.Weapon]); break;
                    case "perks": ui.Press(ui.dice[Category.Perks]); break;
                    case "mutators": ui.Press(ui.dice[Category.Mutators]); break;
                    case "all": ui.Press(ui.all); break;
                    case "undo": Undo(ui.helper); break;
                    case "exclude":
                    {
                        if (args.Length < 2) throw new ArgumentException("usage: randomize exclude <item name>");
                        var e = PerkManager.instance.allEquippables.FirstOrDefault(x => x.name.Equals(args[1], StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"no item \"{args[1]}\"");
                        return $"{e.name} excluded: {ToggleExcluded(e)}";
                    }
                    case "dump": return ui.Dump();
                    default: throw new ArgumentException("unknown action");
                }
                return LoadoutText(ui.helper);
            });
        }

        // ---- UI -----------------------------------------------------------------------------------------------

        /// <summary>Per-loadout-screen UI: the dice, the Randomize all button and the excluded badges.</summary>
        internal class LoadoutRandomizerUI : MonoBehaviour
        {
            public LoadoutUIHelper helper;
            public readonly Dictionary<Category, RandomizeButton> dice = new();
            public RandomizeButton all;
            private readonly Dictionary<Category, TextMeshProUGUI> headers = new();
            private readonly List<GameObject> badges = new();
            private LoadoutRandomizerManager manager;

            public bool Visible { get; private set; }

            public void Build(LoadoutRandomizerManager owner, LoadoutUIHelper loadout)
            {
                manager = owner;
                helper = loadout;
                Visible = Randomizable(loadout);
                if (dice.Count == 0)
                {
                    foreach (Category c in Enum.GetValues(typeof(Category)))
                    {
                        var header = FindHeader(c);
                        if (header == null) continue;
                        headers[c] = header;
                        dice[c] = RandomizeButton.Create(owner, loadout, header.transform, c, false);
                    }
                    all = RandomizeButton.Create(owner, loadout, loadout.playButton.transform.parent, Category.Weapon, true);
                    PlaceAllButton();
                    helper.frame.RefetchManagedElements();
                }
                foreach (var pair in dice) pair.Value.gameObject.SetActive(Visible && (pair.Key != Category.Mutators || HasMutators(loadout)));
                if (all != null) all.gameObject.SetActive(Visible);
                LateUpdate();
            }

            private TextMeshProUGUI FindHeader(Category c)
            {
                if (c == Category.Perks && helper.perksText != null) return helper.perksText;
                var grid = c switch { Category.Weapon => helper.weaponsParent, Category.Perks => helper.perksParent, _ => helper.mutatorsParent };
                if (grid == null) return null;
                var t = grid.transform.parent.Find(c + " Header");
                return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
            }

            // Puts Randomize all to the left of Play, matching its height.
            private void PlaceAllButton()
            {
                var play = (RectTransform)helper.playButton.transform;
                var rt = (RectTransform)all.transform;
                rt.anchorMin = play.anchorMin;
                rt.anchorMax = play.anchorMax;
                rt.pivot = new Vector2(1f, play.pivot.y);
                float h = play.rect.height;
                rt.sizeDelta = new Vector2(h, h);
                rt.anchoredPosition = new Vector2(play.anchoredPosition.x - play.rect.width * play.pivot.x - h * 0.3f, play.anchoredPosition.y);
                rt.SetSiblingIndex(play.GetSiblingIndex());
            }

            // Keeps each dice just right of its header's text, which changes length ("Perks (3/5)").
            private void LateUpdate()
            {
                foreach (var pair in dice)
                {
                    if (!headers.TryGetValue(pair.Key, out var header) || header == null || pair.Value == null) continue;
                    var rt = (RectTransform)pair.Value.transform;
                    float size = Mathf.Max(18f, header.fontSize * 1.25f);
                    rt.sizeDelta = new Vector2(size, size);
                    var bounds = header.textBounds;
                    if (bounds.size.x <= 0f) continue;
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0f, 0.5f);
                    var r = header.rectTransform.rect;
                    rt.anchoredPosition = new Vector2(bounds.max.x - r.center.x + size * 0.35f, bounds.center.y - r.center.y);
                }
            }

            public void Press(RandomizeButton button)
            {
                if (button == null || !button.gameObject.activeInHierarchy) throw new InvalidOperationException("that button isn't shown");
                helper.frame.Select(button);
                helper.frame.Apply();
            }

            public void PopTiles(List<TFUIEquippable> tiles)
            {
                for (int i = 0; i < tiles.Count; i++)
                    if (tiles[i] != null) StartCoroutine(Pop(tiles[i].transform, i * 0.035f));
            }

            private static IEnumerator Pop(Transform t, float delay)
            {
                float start = Time.unscaledTime + delay;
                while (Time.unscaledTime < start) yield return null;
                const float duration = 0.28f;
                for (float e = 0f; e < duration && t != null; e = Time.unscaledTime - start)
                {
                    float x = e / duration;
                    t.localScale = Vector3.one * (1f + 0.22f * Mathf.Sin(x * Mathf.PI) * (1f - x * 0.5f));
                    yield return null;
                }
                if (t != null) t.localScale = Vector3.one;
            }

            public void RefreshExcludedMarks(Func<TFUIEquippable, bool> isExcluded, Sprite badge)
            {
                foreach (var b in badges) if (b != null) Destroy(b);
                badges.Clear();
                if (!Visible) return;
                foreach (var c in AllCategories(helper))
                {
                    foreach (var tile in Items(helper, c))
                    {
                        if (tile == null || !isExcluded(tile)) continue;
                        var go = new GameObject("RandomizerExcluded", typeof(RectTransform), typeof(Image));
                        // Under the background so it follows the tile's focus scaling.
                        go.transform.SetParent(tile.backgroundImg.transform, false);
                        var rt = (RectTransform)go.transform;
                        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
                        rt.pivot = new Vector2(0.5f, 0.5f);
                        float size = ((RectTransform)tile.transform).rect.width * 0.38f;
                        rt.sizeDelta = new Vector2(size, size);
                        rt.anchoredPosition = new Vector2(-size * 0.2f, -size * 0.2f);
                        var img = go.GetComponent<Image>();
                        img.sprite = badge;
                        img.raycastTarget = false;
                        badges.Add(go);
                    }
                }
            }

            public string Dump()
            {
                var sb = new System.Text.StringBuilder();
                void Walk(Transform t, int depth)
                {
                    var rt = t as RectTransform;
                    sb.Append(new string(' ', depth * 2)).Append(t.name)
                        .Append(" [").Append(string.Join(",", t.GetComponents<Component>().Select(x => x.GetType().Name))).Append("]");
                    if (rt != null) sb.Append($" pos={rt.anchoredPosition} size={rt.rect.size} anchors={rt.anchorMin}-{rt.anchorMax} pivot={rt.pivot}");
                    sb.Append('\n');
                    if (depth < 3) foreach (Transform child in t) Walk(child, depth + 1);
                }
                Walk(helper.equippableButtonPrefab.transform, 0);
                Walk(helper.playButton.transform, 0);
                Walk(helper.weaponsParent.transform.parent, 0);
                return sb.ToString();
            }

            public void Teardown()
            {
                foreach (var b in badges) if (b != null) Destroy(b);
                foreach (var d in dice.Values) if (d != null) DestroyImmediate(d.gameObject);
                if (all != null) DestroyImmediate(all.gameObject);
                if (helper != null && helper.frame != null) helper.frame.RefetchManagedElements();
                DestroyImmediate(this);
            }
        }

        /// <summary>A dice button styled like the loadout tiles: same background shape and colors, same focus scaling.</summary>
        internal class RandomizeButton : ThronefallUIElement
        {
            public Category category;
            public bool isAll;
            private LoadoutRandomizerManager manager;
            private LoadoutUIHelper helper;
            private MPImage background;
            private Image icon;
            private Transform scaled;
            private Vector3 iconHome;
            private Color outlineDefault, outlineFocus;
            private float scaleFrom = 1f, scaleTo = 1f, outlineT, outlineFrom, outlineTo, clock = 1f;
            private const float AnimTime = 0.2f;

            public string Title => isAll ? "Randomize all" : category switch
            {
                Category.Weapon => "Randomize weapon",
                Category.Perks => "Randomize perks",
                _ => "Randomize mutators",
            };

            public Color TileColor => background != null ? background.color : Color.gray;

            public static RandomizeButton Create(LoadoutRandomizerManager manager, LoadoutUIHelper helper, Transform parent, Category category, bool isAll)
            {
                var go = new GameObject(isAll ? "Randomize All" : $"Randomize {category}", typeof(RectTransform));
                go.transform.SetParent(parent, false);
                go.layer = parent.gameObject.layer;
                go.AddComponent<LayoutElement>().ignoreLayout = true;
                var button = go.AddComponent<RandomizeButton>();
                button.manager = manager;
                button.helper = helper;
                button.category = category;
                button.isAll = isAll;

                // Copy the look of a loadout tile: clone its background (with its icon child) from the prefab.
                var prefab = helper.equippableButtonPrefab;
                var bgClone = Instantiate(prefab.backgroundImg.gameObject, go.transform, false);
                bgClone.name = "Background";
                foreach (var stray in bgClone.GetComponentsInChildren<ThronefallUIElement>(true)) DestroyImmediate(stray);
                var bgRect = (RectTransform)bgClone.transform;
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.offsetMin = bgRect.offsetMax = Vector2.zero;
                bgRect.localScale = Vector3.one;
                button.background = bgClone.GetComponent<MPImage>();
                button.scaled = bgClone.transform;

                button.icon = bgClone.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == prefab.iconImg.name && i.gameObject != bgClone);
                if (button.icon == null)
                {
                    var iconGo = Instantiate(prefab.iconImg.gameObject, bgClone.transform, false);
                    button.icon = iconGo.GetComponent<Image>();
                }
                button.icon.sprite = manager.diceSprite;
                button.icon.preserveAspect = true;
                button.icon.color = prefab.iconImg.color;
                button.iconHome = button.icon.transform.localPosition;

                button.background.color = isAll ? Color.Lerp(WeaponBgRef(prefab), PerkBgRef(prefab), 0.5f) : category switch
                {
                    Category.Weapon => WeaponBgRef(prefab),
                    Category.Perks => PerkBgRef(prefab),
                    _ => MutatorBgRef(prefab),
                };
                button.outlineDefault = button.background.OutlineColor;
                button.outlineFocus = prefab.focussedStyle != null ? prefab.focussedStyle.outlineColor : Color.white;

                go.AddComponent<TFUIAudioHelper>(); // native select/apply sounds
                return button;
            }

            private float StateScale(SelectionState s) => s switch
            {
                SelectionState.Focussed => helper.equippableButtonPrefab.focussedStyle?.scale ?? 1.1f,
                SelectionState.Selected => helper.equippableButtonPrefab.selectedStyle?.scale ?? 1.15f,
                SelectionState.FocussedAndSelected => helper.equippableButtonPrefab.focussedAndSelectedStyle?.scale ?? 1.2f,
                _ => 1f,
            };

            private void Animate(SelectionState s)
            {
                scaleFrom = scaled != null ? scaled.localScale.x : 1f;
                scaleTo = StateScale(s);
                outlineFrom = outlineT;
                outlineTo = s == SelectionState.Default ? 0f : 1f;
                clock = 0f;
            }

            private void Update()
            {
                if (clock >= 1f || scaled == null) return;
                clock = Mathf.Min(1f, clock + Time.unscaledDeltaTime / AnimTime);
                float t = Mathf.SmoothStep(0f, 1f, clock);
                scaled.localScale = Vector3.one * Mathf.Lerp(scaleFrom, scaleTo, t);
                outlineT = Mathf.Lerp(outlineFrom, outlineTo, t);
                background.OutlineColor = Color.Lerp(outlineDefault, outlineFocus, outlineT);
            }

            protected override void OnApply()
            {
                manager.Roll(helper, isAll ? null : category);
                StopCoroutine(nameof(RollAnimation));
                StartCoroutine(nameof(RollAnimation));
            }

            // The dice tumbles: a quick spin with a jolt, settling back upright.
            private IEnumerator RollAnimation()
            {
                var t = icon.transform;
                const float duration = 0.35f;
                float start = Time.unscaledTime;
                for (float e = 0f; e < duration; e = Time.unscaledTime - start)
                {
                    float x = e / duration;
                    float ease = 1f - Mathf.Pow(1f - x, 3f);
                    t.localRotation = Quaternion.Euler(0f, 0f, -360f * ease);
                    t.localScale = Vector3.one * (1f + 0.25f * Mathf.Sin(x * Mathf.PI));
                    t.localPosition = iconHome + new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f), 0f) * 2.5f * (1f - x);
                    yield return null;
                }
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
                t.localPosition = iconHome;
            }

            protected override void OnClear() => Animate(SelectionState.Default);
            protected override void OnFocus() => Animate(SelectionState.Focussed);
            protected override void OnSelect() => Animate(SelectionState.Selected);
            protected override void OnFocusAndSelect() => Animate(SelectionState.FocussedAndSelected);

            protected override void OnHardStateSet(SelectionState s)
            {
                clock = 1f;
                if (scaled != null) scaled.localScale = Vector3.one * StateScale(s);
                outlineT = s == SelectionState.Default ? 0f : 1f;
                if (background != null) background.OutlineColor = Color.Lerp(outlineDefault, outlineFocus, outlineT);
            }
        }
    }
}
