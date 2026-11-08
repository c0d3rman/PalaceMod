using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The Builder's in-game editor (WP4a, docs/builder-design.md): packs -> pack (units, wave sets, details, problems)
    -> unit property panel / wave set timeline. Entry points: the "Builder" button in the title and pause menus, and
    BuilderEditor.Open / OpenPack for other packages (WP3's custom-runs menu links here).

    Drafts: opening a pack makes an in-memory working copy (PackDraft) that's kept until the game quits, so leaving the
    editor never loses work; Save writes it. Read-only packs (built-in, plugin) can be edited and saved as a copy.
*/
namespace PalaceMod.Builder.Editor
{
    public static class BuilderEditor
    {
        private static readonly Dictionary<string, PackDraft> drafts = [];
        private static PackListPage list;
        private static PackPage packPage;
        private static UnitPage unitPage;
        private static WavePage wavePage;

        internal static PackDraft Draft { get; private set; }

        /// <summary>Whether the editor can be opened now (the Builder feature is on and the game UI is up).</summary>
        public static bool IsAvailable => Feature<BuilderManager>.On && UIFrameManager.instance != null;

        /// <summary>Opens the pack list.</summary>
        public static void Open()
        {
            if (!CheckAvailable()) return;
            list ??= new PackListPage();
            list.Frame.Open();
        }

        /// <summary>Opens a pack's editor (from the pack list, or directly from another menu).</summary>
        public static void OpenPack(string packId)
        {
            if (!CheckAvailable()) return;
            var draft = DraftFor(packId);
            if (draft == null) return;
            Bind(draft);
            packPage ??= new PackPage();
            packPage.Frame.SetTitle(Title(draft));
            // Opened from one of its own child screens (a unit, a wave set, a picker): go back down to it rather than
            // stacking a second copy. From the pack list or another menu, open it on top as usual.
            var manager = UIFrameManager.instance;
            for (int guard = 0; guard < 8 && !packPage.Frame.IsOpen && IsChildScreen(manager.ActiveFrame); guard++) manager.CloseActiveFrame();
            Show(packPage);
        }

        internal static void OpenUnit(int index)
        {
            unitPage ??= new UnitPage();
            unitPage.Index = index;
            Show(unitPage);
        }

        /// <summary>Opens a level of the open pack in the Studio (on its map).</summary>
        internal static void OpenLevel(int index)
        {
            var lv = Draft != null && index >= 0 && index < Draft.Pack.Levels.Count ? Draft.Pack.Levels[index] : null;
            if (lv == null) return;
            // On success the editor stays up under the clouds; the scene transition closes it.
            if (!Studio.Studio.Open(Draft, lv.Id, out string error))
                Notify.Show("Can't open the Studio: " + error, NotifyKind.Error, 6f, key: "builder-editor");
        }

        internal static void OpenWaveSet(int index)
        {
            wavePage ??= new WavePage();
            if (wavePage.Index != index) wavePage.ResetSelection();
            wavePage.Index = index;
            Show(wavePage);
        }

        // Opens a page, or rebuilds it if it's already the open one (it may now show another unit or wave set).
        private static void Show(EditorPage page)
        {
            if (page.Frame.IsOpen) page.Frame.Refresh();
            else page.Frame.Open();
        }

        private static bool CheckAvailable()
        {
            if (IsAvailable) return true;
            Notify.Show(Feature<BuilderManager>.On ? "The Builder can't open right now" : "The Builder isn't available (see the log)", NotifyKind.Warning, key: "builder-editor");
            return false;
        }

        internal static string Title(PackDraft draft) => string.IsNullOrWhiteSpace(draft?.Pack.Name) ? "Edit Pack" : draft.Pack.Name;

        // ---- Drafts ---------------------------------------------------------------------------------------------

        internal static IEnumerable<PackDraft> Drafts => drafts.Values;

        internal static PackDraft ExistingDraft(string packId) => packId != null && drafts.TryGetValue(packId, out var d) ? d : null;

        private static PackDraft DraftFor(string packId)
        {
            var entry = BuilderServices.Packs.Find(packId);
            if (drafts.TryGetValue(packId, out var draft))
            {
                // Picked up an edit made on disk (a text editor) if the draft has no unsaved work of its own.
                if (entry?.Pack != null && !draft.Dirty && PackSerializer.WriteCompact(entry.Pack) != draft.SavedJson)
                    draft = drafts[packId] = new PackDraft(entry);
                return draft;
            }
            if (entry == null) { Notify.Show($"No pack \"{packId}\"", NotifyKind.Warning, key: "builder-editor"); return null; }
            if (entry.Pack == null)
            {
                Notify.Show($"{entry.Id} can't be read: {entry.Issues.FirstOrDefault()?.Message}", NotifyKind.Error, 6f, key: "builder-editor");
                return null;
            }
            return drafts[packId] = new PackDraft(entry);
        }

        private static void Bind(PackDraft draft)
        {
            if (Draft == draft) return;
            if (Draft != null) Draft.Changed -= OnChanged;
            Draft = draft;
            Draft.Changed += OnChanged;
            foreach (var page in Pages) page?.MarkStale();
        }

        private static IEnumerable<EditorPage> Pages => [list, packPage, unitPage, wavePage];

        /// <summary>Makes a draft the open one (the layout editor edits a level of it on the map).</summary>
        internal static void BindDraft(PackDraft draft)
        {
            if (draft != null) Bind(draft);
        }

        private static void OnChanged(bool structural)
        {
            foreach (var page in Pages) page?.OnDraftChanged(structural);
            if (packPage != null && Draft != null) packPage.Frame.SetTitle(Title(Draft));
            // Keep the draft findable by its saved id after a save or a "Save a copy".
            // Library workspaces are kept by DesignLibrary, not by pack id.
            if (Draft?.SourceId != null && !Draft.IsLibrary && ExistingDraft(Draft.SourceId) != Draft)
            {
                foreach (var key in drafts.Where(kv => kv.Value == Draft).Select(kv => kv.Key).ToList()) drafts.Remove(key);
                drafts[Draft.SourceId] = Draft;
            }
        }

        /// <summary>Creates, saves and opens a new empty pack.</summary>
        internal static void NewPack()
        {
            string author = SafeSteamName();
            string name = "My Designs";
            var taken = BuilderServices.Packs.Packs.Select(p => p.Id);
            var pack = new DesignPack
            {
                Id = EditorCatalog.UniqueId(Ids.FromName((author != null ? author + "." : "") + "my-designs"), taken),
                Name = name,
                Author = author,
                GameVersion = Application.version,
            };
            var draft = new PackDraft(pack);
            if (!draft.Save(out string message)) { Notify.Show(message, NotifyKind.Error, key: "builder-editor"); return; }
            drafts[draft.SourceId] = draft;
            OpenPack(draft.SourceId);
        }

        // The game has no player name we can read without Steam's API; packs start without an author.
        private static string SafeSteamName() => null;

        // ---- Actions shared by pages ----------------------------------------------------------------------------

        internal static void Save()
        {
            if (Draft == null) return;
            bool ok = Draft.Save(out string message);
            Notify.Show(message, ok ? NotifyKind.Success : NotifyKind.Error, key: "builder-editor");
            OnChanged(true);
        }

        internal static void Undo()
        {
            if (Draft == null) return;
            if (Picker.IsOpen) Picker.Close();
            string label = Draft.History.UndoLabel;
            if (Draft.Undo()) Notify.Show($"Undid: {label}", NotifyKind.Info, 2f, key: "builder-undo");
            else Notify.Show("Nothing to undo", NotifyKind.Info, 2f, key: "builder-undo");
        }

        internal static void Redo()
        {
            if (Draft == null) return;
            if (Picker.IsOpen) Picker.Close();
            string label = Draft.History.RedoLabel;
            if (Draft.Redo()) Notify.Show($"Redid: {label}", NotifyKind.Info, 2f, key: "builder-undo");
            else Notify.Show("Nothing to redo", NotifyKind.Info, 2f, key: "builder-undo");
        }

        /// <summary>Whether one of the editor's screens (or its picker) is the open menu.</summary>
        internal static bool EditorFocused
        {
            get
            {
                var active = UIFrameManager.instance?.ActiveFrame;
                return active != null && (Picker.IsOpen || Pages.Any(p => p != null && p.Frame.Frame == active));
            }
        }

        private static bool IsChildScreen(UIFrame frame) =>
            frame != null && (Picker.IsOpen || (unitPage != null && unitPage.Frame.Frame == frame) || (wavePage != null && wavePage.Frame.Frame == frame));

        internal static void CloseAll()
        {
            var manager = UIFrameManager.instance;
            if (manager == null) return;
            for (int guard = 0; guard < 8 && EditorFocused; guard++) manager.CloseActiveFrame();
        }

        /// <summary>Saves (when possible) and plays a wave set of the draft.</summary>
        internal static void TestWaveSet(string waveSetId)
        {
            if (Draft == null) return;
            if (!Draft.ReadOnly && Draft.Dirty && Draft.ErrorCount == 0 && !Draft.Save(out string saveError))
                Notify.Show(saveError, NotifyKind.Warning, key: "builder-editor");
            Launch(Draft.Pack, waveSetId);
        }

        /// <summary>Plays one unit: an unsaved copy of the pack with a generated two-night wave set of it.</summary>
        internal static void TestUnit(int unitIndex)
        {
            if (Draft == null) return;
            var unit = unitIndex >= 0 && unitIndex < Draft.Pack.Units.Count ? Draft.Pack.Units[unitIndex] : null;
            if (unit == null) return;
            if (unit.Side == UnitSide.Player) { TestTroop(unit); return; }
            var copy = PackSerializer.Clone(Draft.Pack);
            var baseUnit = EditorCatalog.BaseOf(copy, unit.Id);
            string hostScene = Draft.Pack.WaveSets.Select(w => w.Host).FirstOrDefault(h => EditorCatalog.Host(h) != null)
                ?? EditorCatalog.Hosts.FirstOrDefault(h => h.Scene == "Nordfels")?.Scene ?? EditorCatalog.Hosts.FirstOrDefault()?.Scene;
            var host = EditorCatalog.Host(hostScene);
            var line = host?.SpawnLines.FirstOrDefault(l => baseUnit?.Flying == true ? l.Flying : baseUnit?.Large == true ? l.BigGround : l.SmallGround)
                ?? host?.SpawnLines.FirstOrDefault();
            if (host == null || line == null) { Notify.Show("No map to test on", NotifyKind.Error, key: "builder-editor"); return; }
            string id = EditorCatalog.UniqueId("editor-test", copy.WaveSets.Select(w => w.Id));
            copy.WaveSets.Add(new WaveSetDef
            {
                Id = id,
                Name = $"Test: {unit.Name ?? unit.Id}",
                Host = host.Scene,
                StartingGold = Math.Max(host.StartingGold, 30),
                Nights =
                [
                    new NightDef { Spawns = [new SpawnDef { Unit = unit.Id, Count = 3, Interval = 1.5f, Line = line.Name, Gold = 6 }] },
                    new NightDef { Spawns = [new SpawnDef { Unit = unit.Id, Count = 8, Interval = 1f, Line = line.Name, Gold = 10 }] },
                ],
            });
            Launch(copy, id);
        }

        // A player unit plays as a troop: the map's own nights, with the buildings training it instead of its base.
        private static void TestTroop(UnitDef unit)
        {
            var copy = PackSerializer.Clone(Draft.Pack);
            string hostScene = Draft.Pack.WaveSets.Select(w => w.Host).FirstOrDefault(h => EditorCatalog.Host(h) != null)
                ?? EditorCatalog.Hosts.FirstOrDefault(h => h.Scene == "Nordfels")?.Scene ?? EditorCatalog.Hosts.FirstOrDefault()?.Scene;
            var host = EditorCatalog.Host(hostScene);
            var ws = EditorCatalog.Copy(host?.VanillaWaves);
            if (host == null || ws == null) { Notify.Show("No map to test on", NotifyKind.Error, key: "builder-editor"); return; }
            ws.Id = EditorCatalog.UniqueId("editor-test", copy.WaveSets.Select(w => w.Id));
            ws.Name = $"Test: {unit.Name ?? unit.Id}";
            ws.Host = host.Scene;
            ws.Troops = [new TroopDef { Unit = unit.Id }];
            copy.WaveSets.Add(ws);
            Launch(copy, ws.Id);
        }

        private static void Launch(DesignPack pack, string waveSetId)
        {
            // The editor stays up under the clouds, like the game's own menus when they load a level: the scene
            // transition closes every menu at its midpoint, so nothing is left open over the loading map.
            if (!BuilderServices.Sessions.Launch(new SessionRequest { Pack = pack, WaveSetId = waveSetId }, out string error))
                Notify.Show("Can't test: " + error, NotifyKind.Error, 6f, key: "builder-editor");
            else EditorReturn.Remember();
        }

        internal static void CopyShareCode()
        {
            if (Draft == null) return;
            try
            {
                string code = BuilderServices.Share.Encode(Draft.Pack);
                GUIUtility.systemCopyBuffer = code;
                Notify.Show($"Share code copied ({code.Length} characters)", NotifyKind.Success, key: "builder-editor");
            }
            catch (Exception e)
            {
                Notify.Show("Can't make a share code: " + e.Message, NotifyKind.Warning, key: "builder-editor");
            }
        }

        internal static void ImportShareCode()
        {
            string code = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrWhiteSpace(code)) { Notify.Show("Copy a share code first, then import", NotifyKind.Info, key: "builder-editor"); return; }
            var issues = new List<ValidationIssue>();
            DesignPack pack;
            try { pack = BuilderServices.Share.Decode(code, issues); }
            catch (Exception e) { pack = null; issues.Add(new ValidationIssue(Severity.Error, "", e.Message)); }
            if (pack == null)
            {
                Notify.Show("Can't import: " + (issues.FirstOrDefault()?.Message ?? "not a share code"), NotifyKind.Warning, 5f, key: "builder-editor");
                return;
            }
            var entry = BuilderServices.Packs.InstallNew(pack);
            Notify.Show($"Imported \"{entry?.Pack?.Name ?? entry?.Id}\"", NotifyKind.Success, key: "builder-editor");
            list?.OnDraftChanged(true);
        }

        internal static void OpenFolder()
        {
            string root = BuilderServices.Packs.Root;
            if (root == null) return;
            Directory.CreateDirectory(Path.Combine(root, "packs"));
            Application.OpenURL("file://" + Path.Combine(root, "packs"));
        }

        // ---- Problem navigation ---------------------------------------------------------------------------------

        // Issue paths have a fixed shape ("units[2].stats.maxHp", "waveSets[0].nights[1].spawns[3].line").
        private static readonly Regex UnitPath = new(@"^units\[(\d+)\]");
        private static readonly Regex LevelPath = new(@"^levels\[(\d+)\]");
        private static readonly Regex WavePath = new(@"^waveSets\[(\d+)\](?:\.nights\[(\d+)\](?:\.spawns\[(\d+)\])?)?");

        /// <summary>Opens the screen holding the field an issue is about and selects it.</summary>
        internal static void GoTo(string path)
        {
            path ??= "";
            var unit = UnitPath.Match(path);
            if (unit.Success)
            {
                OpenUnit(int.Parse(unit.Groups[1].Value));
                unitPage.SelectPath(path);
                return;
            }
            var wave = WavePath.Match(path);
            if (wave.Success)
            {
                wavePage ??= new WavePage();
                int ws = int.Parse(wave.Groups[1].Value);
                wavePage.Index = ws;
                wavePage.Expand(wave.Groups[2].Success ? int.Parse(wave.Groups[2].Value) : -1, wave.Groups[3].Success ? int.Parse(wave.Groups[3].Value) : -1);
                Show(wavePage);
                wavePage.SelectPath(path);
                return;
            }
            var level = LevelPath.Match(path);
            if (level.Success)
            {
                OpenLevel(int.Parse(level.Groups[1].Value));
                return;
            }
            packPage?.SelectPath(path);
        }

        /// <summary>The open pack and the editor screen showing now, as a GoTo path ("" for the pack page, null for
        /// the pack list); false when the editor isn't the active menu.</summary>
        internal static bool CurrentPlace(out string packId, out string path)
        {
            packId = path = null;
            var active = UIFrameManager.instance?.ActiveFrame;
            if (active == null) return false;
            if (list != null && list.Frame.Frame == active) return true;
            packId = drafts.FirstOrDefault(kv => kv.Value == Draft).Key;
            if (packId == null) return false;
            if (unitPage != null && unitPage.Frame.Frame == active) path = $"units[{unitPage.Index}]";
            else if (wavePage != null && wavePage.Frame.Frame == active) path = $"waveSets[{wavePage.Index}]";
            else if (packPage != null && packPage.Frame.Frame == active) path = "";
            else return false;
            return true;
        }

        /// <summary>Opens the editor the way the player had walked into it: pack list, then the pack, then the screen
        /// at <paramref name="path"/> (see CurrentPlace).</summary>
        internal static void Reopen(string packId, string path)
        {
            Open();
            if (packId == null || list == null || !list.Frame.IsOpen) return;
            OpenPack(packId);
            if (string.IsNullOrEmpty(path) || Draft == null || !packPage.Frame.IsOpen) return;
            if (!InRange(path)) return;
            GoTo(path);
        }

        private static bool InRange(string path)
        {
            var m = Regex.Match(path, @"^(levels|units|waveSets)\[(\d+)\]$");
            if (!m.Success) return false;
            int i = int.Parse(m.Groups[2].Value);
            int count = m.Groups[1].Value switch { "levels" => Draft.Pack.Levels.Count, "units" => Draft.Pack.Units.Count, _ => Draft.Pack.WaveSets.Count };
            return i < count;
        }

        /// <summary>Closes the editor and forgets the open pack (the feature was switched off).</summary>
        internal static void Shutdown()
        {
            CloseAll();
        }
    }
}
