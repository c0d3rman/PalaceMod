using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PalaceMod.Builder.Model;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    The player's way into custom runs: a "Custom runs" entry in every map's pre-level menu (the level-select screen
    you get by walking onto a map). It opens the Custom Runs frame, which lists the installed designs (wave sets and
    levels of every pack), this map's first. Picking one returns to the pre-level menu "armed": the title says which
    design is loaded, the highscore shows the design's custom best, there's no "continue" save, and the game's own
    loadout and perk choice apply. Play starts the custom session. Picking a design for another map switches the
    pre-level menu to that map. Opened from anywhere else (SessionMenu.Open), a pick launches straight away.

    The editor (WP4a) can add "Create your own" / "Edit" rows by setting EditorAvailable, OpenEditor and
    OpenPackInEditor from its Awake.
*/
namespace PalaceMod.Builder
{
    public static class SessionMenu
    {
        /// <summary>Set by the editor: whether it can open now.</summary>
        public static Func<bool> EditorAvailable;
        /// <summary>Set by the editor: opens its pack list.</summary>
        public static Action OpenEditor;
        /// <summary>Set by the editor: opens one pack for editing.</summary>
        public static Action<string> OpenPackInEditor;

        private static SessionLauncher launcher;
        private static ModFrame frame;
        private static bool fromPreLevel;
        private static string contextHost;

        // The pre-level menu entry.
        private static TFUITextButton entry;
        private static LevelSelectUIFrameHelper entryHelper;
        private static ThronefallUIElement entryAbove, entryBelow;

        internal static bool Available => Feature<BuilderManager>.On;

        internal static void Init(SessionLauncher l)
        {
            launcher = l;
            frame = new ModFrame("Custom Runs").SetContent(Build);
        }

        public static bool IsOpen => frame?.IsOpen ?? false;

        /// <summary>Opens the list. From a map's pre-level menu a pick arms that menu; from anywhere else it launches.</summary>
        public static void Open()
        {
            if (frame == null) return;
            var helper = PreLevelHelper;
            fromPreLevel = helper != null && UIFrameManager.instance?.ActiveFrame == helper.target;
            contextHost = fromPreLevel ? LevelInteractor.lastActiveLevelInfo?.sceneName : SessionHost();
            frame.Open();
        }

        private static string SessionHost() => launcher.Current?.HostScene ?? UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

        // ------------------------------------------------------------------ the list

        private sealed class Design
        {
            public PackEntry Entry;
            public string Name, Host, WaveSetId, LevelId, Key;
            public WaveSetDef WaveSet;
            public LevelDef Level;
        }

        private static List<Design> Designs()
        {
            var list = new List<Design>();
            foreach (var p in BuilderServices.Packs.Packs)
            {
                if (p.Pack == null) continue;
                foreach (var ws in (p.Pack.WaveSets ?? []).Where(w => w != null))
                    list.Add(new Design { Entry = p, Name = ws.Name ?? ws.Id, Host = ws.Host, WaveSetId = ws.Id, WaveSet = ws, Key = ScoreStore.Key(p.Id, ws.Id, null) });
                foreach (var lv in (p.Pack.Levels ?? []).Where(l => l != null))
                    list.Add(new Design
                    {
                        Entry = p, Name = lv.Name ?? lv.Id, Host = lv.Host, LevelId = lv.Id, Level = lv,
                        WaveSet = lv.WaveSet != null ? p.Pack.FindWaveSet(lv.WaveSet) : null, Key = ScoreStore.Key(p.Id, null, lv.Id),
                    });
            }
            return list;
        }

        private static void Build(ModPage page)
        {
            if (!Available)
            {
                page.Text("The Builder is turned off. Turn it on in Settings > Mods to play custom runs.");
                return;
            }
            var designs = Designs();
            var armed = launcher.Armed;

            if (fromPreLevel && armed != null)
            {
                page.Row($"Play {MapName(contextHost)} normally", null, "Unload the custom design and play the map as usual.", () =>
                {
                    launcher.Disarm();
                    frame.Close();
                });
            }

            var here = designs.Where(d => d.Host == contextHost).ToList();
            var elsewhere = designs.Where(d => d.Host != contextHost).OrderBy(d => MapName(d.Host)).ThenBy(d => d.Name).ToList();

            if (here.Count > 0)
            {
                page.Header(MapName(contextHost));
                foreach (var d in here) DesignRow(page, d, armed, showMap: false);
            }
            if (elsewhere.Count > 0)
            {
                page.Header(here.Count > 0 ? "Other maps" : "Designs");
                foreach (var d in elsewhere) DesignRow(page, d, armed, showMap: true);
            }

            var broken = BuilderServices.Packs.Packs.Where(p => p.Pack == null).ToList();
            if (broken.Count > 0)
            {
                page.Header("Couldn't read");
                foreach (var p in broken)
                    page.Row(p.Id, "error", IssuesText(p.Issues), () => Notify.Show($"{p.Id}: {FirstError(p.Issues)}", NotifyKind.Error, 6f, "builder-menu"));
            }

            if (designs.Count == 0 && broken.Count == 0)
                page.Text("No designs installed yet. Put design packs in BepInEx/PalaceBuilder/packs.");

            if (EditorAvailable?.Invoke() == true && OpenEditor != null)
            {
                page.Space(10f);
                page.Button("Create your own", () => OpenEditor(), "Open the Builder to make units and wave sets.");
            }

            page.Space(10f);
            page.Text("Custom runs keep their own scores. They never change your saves, highscores, XP or achievements.");
        }

        private static void DesignRow(ModPage page, Design d, SessionLauncher.ArmedDesign armed, bool showMap)
        {
            var record = ScoreStore.Get(d.Key);
            bool broken = d.Entry.HasErrors;
            bool isArmed = armed != null && armed.Request.Pack.Id == d.Entry.Id &&
                armed.Request.WaveSetId == d.WaveSetId && armed.Request.LevelId == d.LevelId;

            string value = broken ? "has errors"
                : record?.Best != null ? $"{(record.EverBeaten ? "won · " : "")}best {record.Best.Score}"
                : "new";
            if (showMap) value = $"{MapName(d.Host)} · {value}";
            string label = (isArmed ? "> " : "") + d.Name;

            var row = page.Row(label, value, () => Tooltip(d, record), () => Pick(d));
            if (broken)
            {
                var text = row.Button.GetComponent<TextMeshProUGUI>();
                if (text != null) text.color = ModStyle.Dim;
            }
            if (OpenPackInEditor != null && EditorAvailable?.Invoke() == true && !d.Entry.ReadOnly && broken)
                page.Row($"   Edit {d.Entry.Pack.Name ?? d.Entry.Id}", null, "Fix the pack's problems in the Builder.", () => OpenPackInEditor(d.Entry.Id));
        }

        private static string Tooltip(Design d, DesignRecord record)
        {
            var p = d.Entry.Pack;
            var lines = new List<string>();
            string by = string.IsNullOrEmpty(p.Author) ? "" : $" by {p.Author}";
            lines.Add($"{p.Name ?? p.Id}{by}");
            int nights = d.WaveSet?.Nights?.Count ?? 0;
            lines.Add(d.WaveSet != null ? $"{nights} night{(nights == 1 ? "" : "s")} on {MapName(d.Host)}" : $"Vanilla waves on {MapName(d.Host)}");
            string desc = d.Level?.Description ?? p.Description;
            if (!string.IsNullOrEmpty(desc)) lines.Add(desc);
            if (record?.Best != null)
                lines.Add($"Best {record.Best.Score} ({record.Best.DateUtc.ToLocalTime():d MMM yyyy}). Played {record.Plays}, won {record.Wins}.");
            if (d.Entry.HasErrors) lines.Add("Can't be played:\n" + IssuesText(d.Entry.Issues));
            else if (d.Entry.Issues.Count > 0) lines.Add($"{d.Entry.Issues.Count} warning(s)");
            return string.Join("\n", lines);
        }

        private static void Pick(Design d)
        {
            if (!Available)
            {
                Notify.Show("The Builder is turned off in Settings > Mods.", NotifyKind.Warning, 5f, "builder-menu");
                frame.Close();
                return;
            }
            if (d.Entry.HasErrors)
            {
                Notify.Show($"{d.Name} can't be played: {FirstError(d.Entry.Issues)}", NotifyKind.Error, 6f, "builder-menu");
                return;
            }
            var request = new SessionRequest { Pack = d.Entry.Pack, WaveSetId = d.WaveSetId, LevelId = d.LevelId };
            string error;
            if (!fromPreLevel)
            {
                if (!launcher.Launch(request, out error)) Notify.Show($"Couldn't start {d.Name}: {error}", NotifyKind.Error, 6f, "builder-menu");
                return;
            }
            if (!launcher.Arm(request, out error))
            {
                Notify.Show($"Couldn't load {d.Name}: {error}", NotifyKind.Error, 6f, "builder-menu");
                return;
            }
            // Another map: the pre-level menu shows that map instead (it reads lastActiveLevelInfo when shown).
            if (d.Host != LevelInteractor.lastActiveLevelInfo?.sceneName)
            {
                var info = LevelProgressManager.instance?.GetLevelInfoFromSceneName(d.Host);
                if (info == null)
                {
                    launcher.Disarm();
                    Notify.Show($"Couldn't find the map {d.Host}", NotifyKind.Error, 6f, "builder-menu");
                    return;
                }
                LevelInteractor.lastActiveLevelInfo = info;
            }
            frame.Close(); // back to the pre-level menu, which re-shows itself armed
        }

        private static string IssuesText(IEnumerable<ValidationIssue> issues) =>
            string.Join("\n", issues.Where(i => i.Severity == Severity.Error).Take(4).Select(i => string.IsNullOrEmpty(i.Path) ? i.Message : $"{i.Path}: {i.Message}"));

        private static string FirstError(IEnumerable<ValidationIssue> issues) =>
            issues.FirstOrDefault(i => i.Severity == Severity.Error)?.Message ?? "unknown problem";

        internal static string MapName(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return "?";
            var info = LevelProgressManager.instance?.GetLevelInfoFromSceneName(scene);
            string name = info != null ? info.LocalizedDisplayName : null;
            return string.IsNullOrEmpty(name) ? scene : name;
        }

        // ------------------------------------------------------------------ pre-level menu entry

        private static LevelSelectUIFrameHelper PreLevelHelper
        {
            get
            {
                var ui = UIFrameManager.instance;
                if (ui == null) return null;
                var f = Traverse.Create(ui).Field("levelSelectFrame").GetValue<UIFrame>();
                return f != null ? f.GetComponentInChildren<LevelSelectUIFrameHelper>(true) : null;
            }
        }

        /// <summary>After the game set up the pre-level menu (LevelSelectUIFrameHelper.OnShow): add or update the
        /// Custom runs entry and, when a design is armed, hide the "continue" save.</summary>
        internal static void OnPreLevelShow(LevelSelectUIFrameHelper helper)
        {
            var armed = launcher.Armed;
            if (armed != null && armed.Host != LevelInteractor.lastActiveLevelInfo?.sceneName) launcher.Disarm();
            armed = launcher.Armed;
            if (armed != null)
            {
                // Same as the game's no-save branch: a custom run always starts fresh.
                helper.hasSaveParent.SetActive(false);
                helper.hasNoSaveParent.SetActive(true);
                helper.target.firstSelected = helper.noSavePlayButton;
                helper.highscoreButton.topNav = helper.noSavePlayButton;
                helper.highscoreButton.botNav = helper.noSavePlayButton;
            }

            bool show = Available && IsHostMap(LevelInteractor.lastActiveLevelInfo);
            if (!show)
            {
                HideEntry();
                return;
            }
            if (entry == null || entryHelper != helper)
            {
                if (!BuildEntry(helper)) return;
            }
            entry.SetOriginalString(armed != null ? $"Custom: {armed.Name}" : "Custom runs");
            entry.gameObject.SetActive(true);
            Grow(true);
            // Navigation: the game re-links the highscore button on every show; put the entry right after it.
            var hs = helper.highscoreButton;
            entryAbove = hs;
            entryBelow = hs.botNav;
            hs.botNav = entry;
            entry.topNav = hs;
            entry.botNav = entryBelow;
            if (entryBelow != null && entryBelow.topNav == hs) entryBelow.topNav = entry;
        }

        private static bool IsHostMap(LevelInfo info)
        {
            if (info == null || info.enemySpawner == null) return false;
            // Challenge maps (they have a subtitle) only get the entry when a design is made for them.
            if (info.useSubtitle && !Designs().Any(d => d.Host == info.sceneName)) return false;
            try { return BuilderServices.Catalog.FindHost(info.sceneName) != null; }
            catch (InvalidOperationException) { return true; } // catalog not installed yet: any map with a spawner
        }

        private static bool BuildEntry(LevelSelectUIFrameHelper helper)
        {
            try
            {
                if (entry != null) Object.Destroy(entry.gameObject);
                entry = null;
                var source = GameWidgets.MenuButton;
                var hs = helper.highscoreButton;
                if (source == null || hs == null) return false;
                entry = GameWidgets.Button(source, hs.transform.parent, "PalaceMod Custom Runs", "Custom runs", Open);
                entry.transform.SetSiblingIndex(hs.transform.GetSiblingIndex() + 1);
                PlaceBelow((RectTransform)entry.transform, (RectTransform)hs.transform);
                var follower = ModStyle.AddEmblem(entry.transform, 9f).gameObject.AddComponent<EmblemFollower>();
                follower.Text = entry.GetComponent<TextMeshProUGUI>();
                follower.Gap = 16f;
                panel = hs.transform.parent as RectTransform;
                panelHeight = panel != null ? panel.sizeDelta.y : 0f;
                grown = false;
                ModTooltip.Attach(entry, "Custom runs", () => launcher.Armed != null
                    ? $"Playing {launcher.Armed.Name}. Pick another design, or the map's normal waves."
                    : "Play this map with custom enemies and waves from design packs.");
                entryHelper = helper;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Builder: couldn't add the Custom runs entry: {e}");
                if (entry != null) Object.Destroy(entry.gameObject);
                entry = null;
                return false;
            }
        }

        // The menu panel is laid out by hand (title anchored to the top, play buttons to the bottom, scores in the
        // middle), so the entry sits under "Show Leaderboard" and the panel grows by one row to make room for it.
        private const float RowHeight = 50f;
        private static RectTransform panel;
        private static float panelHeight;
        private static bool grown;

        private static void PlaceBelow(RectTransform rect, RectTransform above)
        {
            rect.anchorMin = above.anchorMin;
            rect.anchorMax = above.anchorMax;
            rect.pivot = above.pivot;
            rect.sizeDelta = new Vector2(above.sizeDelta.x, RowHeight);
            rect.anchoredPosition = above.anchoredPosition - new Vector2(0f, RowHeight + 12f);
        }

        private static void Grow(bool on)
        {
            if (panel == null || grown == on) return;
            grown = on;
            panel.sizeDelta = new Vector2(panel.sizeDelta.x, panelHeight + (on ? RowHeight : 0f));
        }

        /// <summary>Hides the entry (feature switched off).</summary>
        internal static void HideEntry()
        {
            if (entry == null) return;
            entry.gameObject.SetActive(false);
            Grow(false);
            // Give the game's elements their links back.
            if (entryAbove != null && entryAbove.botNav == entry) entryAbove.botNav = entryBelow;
            if (entryBelow != null && entryBelow.topNav == entry) entryBelow.topNav = entryAbove;
        }
    }
}
