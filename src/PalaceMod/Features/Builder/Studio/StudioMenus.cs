using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PalaceMod.Builder.Editor;
using UnityEngine;

/*
    The Studio's popups (ModPopup over the dock): the context menu (right click), the Studio menu (≡ or Esc with nothing
    to cancel), Snap, View and Problems.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioMenus
    {
        private static string Esc(string s) => ModFields.TMPEscape(s ?? "");
        private static string Dim(string s) => $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{s}</color>";

        private static void Close() => ModPopup.Current?.Close();

        /// <summary>Runs a menu action after closing the popup (so a frame it opens isn't stacked over the popup).</summary>
        private static Action Then(Action a) => () => { Close(); a(); };

        // ---- Context menu ---------------------------------------------------------------------------------------

        public static void OpenContext(IReadOnlyList<string> keys, Vector2 screen, Vector3? ground)
        {
            var items = keys.Select(StudioScene.Find).Where(i => i != null).ToList();
            string title = items.Count == 0 ? "Here" : items.Count == 1 ? items[0].Name : $"{items.Count} objects";
            ModPopup.Open(title, page =>
            {
                if (items.Count > 0)
                {
                    page.Row("Properties", "", (string)null, Then(() => StudioDock.ShowPanel("inspector")));
                    page.Row("Duplicate", Dim(StudioKeys.Label(StudioKeys.Duplicate)), (string)null, Then(StudioShell.DuplicateAndGrab));
                    if (items.Count > 1) page.Row("Group", Dim(StudioKeys.Label(StudioKeys.Group)), (string)null, Then(() => { if (StudioEdits.Group(keys) is string g) StudioSelection.Select([g], SelectOp.Replace); }));
                    if (items.Any(i => StudioScene.GroupOf(i.Key) != null)) page.Row("Ungroup", "", (string)null, Then(() => StudioEdits.Ungroup(keys)));
                    var types = items.Select(i => i.TypeName).Distinct().ToList();
                    if (types.Count == 1)
                    {
                        string type = types[0];
                        page.Row("Select all of this kind", "", (string)null, Then(() => StudioSelection.Select(StudioScene.Pickable.Where(i => i.TypeName == type).Select(i => i.Key), SelectOp.Replace)));
                    }
                    if (items.Any(i => i.Moved && (i.Kind is StudioItemKind.Slot or StudioItemKind.Line)))
                        page.Row("Reset to the map's", "", (string)null, Then(() => { foreach (var i in items.Where(i => i.Moved)) StudioEdits.ResetPose(i); }));
                    bool restore = items.All(i => i.Kind == StudioItemKind.Slot && i.Removed);
                    page.Row(restore ? "Restore" : items.All(i => i.Kind == StudioItemKind.Slot) ? "Remove" : "Delete", Dim(StudioKeys.Label(StudioKeys.Delete)), (string)null, Then(() => StudioEdits.Delete(keys.ToList())));
                }
                else
                {
                    if (StudioEdits.ClipboardCount > 0) page.Row("Paste", Dim(StudioKeys.Label(StudioKeys.Paste)), (string)null, Then(() => StudioShell.PasteAtCursor(ground)));
                    if (StudioShell.Recent.Count > 0 && ground is Vector3 at)
                    {
                        var last = StudioShell.Recent[0];
                        page.Row("Place " + Esc(last.Name) + " here", "", (string)null, Then(() =>
                        {
                            string key = last.Place?.Invoke(StudioShell.Snap(at, false), StudioShell.Place.GhostBaseYaw);
                            if (key != null) StudioSelection.Select([key], SelectOp.Replace);
                        }));
                    }
                    page.Row("Frame all", Dim(StudioKeys.Label(StudioKeys.FrameAll)), (string)null, Then(StudioShell.FrameAll));
                }
                foreach (var (label, run) in Studio.ContextRows(keys))
                {
                    var r = run;
                    page.Row(label, "", (string)null, Then(() =>
                    {
                        try { r(); } catch (Exception e) { Log.For<BuilderManager>().Caught($"Studio context item \"{label}\" failed", e); }
                    }));
                }
                if (StudioServices.Play != null && ground is Vector3 g)
                    page.Row("Play from here", Dim(StudioKeys.Label(StudioKeys.Play)), (string)null, Then(() => PlayFrom(g)));
            }, screenPoint: screen, width: 380f);
        }

        private static void PlayFrom(Vector3 at)
        {
            var play = StudioServices.Play;
            if (play == null) return;
            if (!play.CanSwap(out string why)) { Notify.Show("Can't play: " + why, NotifyKind.Warning, 4f, "builder-studio"); return; }
            var view = StudioShell.CaptureViewNow();
            view.Cursor = at;
            Studio.View = view;
            play.EnterPlay(true);
        }

        // ---- Studio menu ----------------------------------------------------------------------------------------

        public static void OpenStudioMenu()
        {
            var draft = Studio.Draft;
            if (draft == null) return;
            ModPopup.Open("Studio", page =>
            {
                page.Row(draft.ReadOnly ? "Save a copy" : "Save", draft.Dirty ? $"<color=#{ModStyle.Hex(ModStyle.Accent)}>unsaved</color>" : Dim("saved"), (string)null, Then(StudioShell.Save));
                page.Row("Share…", "", (string)null, Then(() =>
                {
                    if (draft.Entry is { } entry && !draft.Dirty) Sharing.SharingUI.OpenPack(entry);
                    else Notify.Show(draft.ReadOnly ? "Save a copy first" : "Save first", NotifyKind.Info, 3f, "builder-studio");
                }));
                page.Row("Pack editor", "", "The pack's units, wave sets and levels. Close it to come back.", Then(() => BuilderEditor.OpenPack(draft.SourceId ?? draft.Pack.Id)));
                page.Row("Studio settings", "", (string)null, Then(OpenSettings));
                page.Row("Leave", "", "Back to the pack screen. Unsaved changes stay until the game quits.", Then(() => Studio.Leave()));
            }, near: StudioDock.Dock?.Panels.FirstOrDefault()?.Rect, width: 380f);
        }

        private static void OpenSettings()
        {
            ModPopup.Open("Studio settings", page =>
            {
                page.Choice("Labels", Enum.GetNames(typeof(StudioViewOptions.LabelMode)), () => (int)StudioViewOptions.Labels,
                    i => { StudioViewOptions.Labels = (StudioViewOptions.LabelMode)i; StudioViewOptions.RaiseChanged(); });
                page.Toggle("Pan at screen edges", () => StudioLabels.EdgePan, v => StudioLabels.EdgePan = v);
            }, width: 460f);
        }

        // ---- Snap -----------------------------------------------------------------------------------------------

        public static void OpenSnap(RectTransform near)
        {
            ModPopup.Open("Snap", page =>
            {
                foreach (var step in StudioShell.SnapSteps)
                {
                    float s = step;
                    page.Row(s <= 0f ? "Off" : $"{s:0.#} m", StudioShell.SnapStep == s ? "●" : "", (string)null, () => { StudioShell.SnapStep = s; StudioDock.RefreshAll(); ModPopup.Current?.Refresh(); });
                }
                page.Choice("Turn step", ["15°", "45°"], () => StudioShell.TurnStep >= 45f ? 1 : 0, i => { StudioShell.TurnStep = i == 1 ? 45f : 15f; StudioDock.RefreshAll(); });
            }, near: near, width: 320f);
        }

        // ---- View -----------------------------------------------------------------------------------------------

        public static void OpenView(RectTransform near)
        {
            ModPopup.Open("View", page =>
            {
                page.Choice("Labels", ["Off", "Names", "Costs", "Requirements"], () => (int)StudioViewOptions.Labels,
                    i => { StudioViewOptions.Labels = (StudioViewOptions.LabelMode)i; StudioViewOptions.RaiseChanged(); });
                page.Choice("Link arrows", ["Off", "Selection", "All"], () => (int)StudioViewOptions.Links,
                    i => { StudioViewOptions.Links = (StudioViewOptions.LinkMode)i; StudioViewOptions.RaiseChanged(); });
                page.Toggle("X-ray", () => StudioViewOptions.XRay, v => { StudioViewOptions.XRay = v; StudioViewOptions.RaiseChanged(); StudioScene.RefreshMarkers(); });
                page.Toggle("Routes", () => StudioViewOptions.Routes, v =>
                {
                    StudioViewOptions.Routes = v;
                    StudioViewOptions.RaiseChanged();
                    if (v) StudioShell.CheckPaths(); else StudioScene.ShowRoutes([]);
                });
                foreach (var t in Studio.ViewToggles)
                {
                    var toggle = t;
                    page.Toggle(t.Label, () => { try { return toggle.Get(); } catch { return false; } }, v =>
                    {
                        try { toggle.Set(v); } catch (Exception e) { Log.For<BuilderManager>().Caught($"View toggle {toggle.Id} failed", e); }
                        StudioViewOptions.RaiseChanged();
                    });
                }
                page.Header("Colours");
                page.Row("Castle", "", (string)null, null, null).SetValue(Swatch(Level.LayoutMarkers.CastleColor));
                page.Row("Income", "", (string)null).SetValue(Swatch(Level.LayoutMarkers.IncomeColor));
                page.Row("Military", "", (string)null).SetValue(Swatch(Level.LayoutMarkers.MilitaryColor));
                page.Row("Walls, gates", "", (string)null).SetValue(Swatch(Level.LayoutMarkers.WallColor));
                page.Row("Special", "", (string)null).SetValue(Swatch(Level.LayoutMarkers.SpecialColor));
                page.Row("Spawn lines", "", (string)null).SetValue(Swatch(Level.LayoutMarkers.LineColor));
                page.Row("Scenery", "", (string)null).SetValue(Swatch(Level.LayoutMarkers.PropColor));
            }, near: near, width: 440f);
        }

        private static string Swatch(Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>■■</color>";

        // ---- Problems -------------------------------------------------------------------------------------------

        public static void OpenProblems(RectTransform near)
        {
            ModPopup.Open("Problems", page =>
            {
                var issues = StudioDock.LevelIssues();
                if (issues.Count == 0) { page.Text("None"); return; }
                foreach (var issue in issues)
                {
                    var i = issue;
                    string key = KeyForPath(i.Path);
                    string color = ModStyle.Hex(i.Severity == Model.Severity.Error ? ModStyle.KindColor(NotifyKind.Error) : ModStyle.KindColor(NotifyKind.Warning));
                    var row = page.Row($"<color=#{color}>{(i.Severity == Model.Severity.Error ? "Error" : "Warning")}</color>  {Esc(i.Message)}", "", (string)null, () =>
                    {
                        if (key != null && StudioScene.Find(key) is { } item)
                        {
                            StudioSelection.Select([key], SelectOp.Replace);
                            StudioCamera.FrameItems([item]);
                        }
                        else StudioDock.ShowPanel("level");
                    });
                    var text = row.Button.GetComponent<TMPro.TextMeshProUGUI>();
                    if (text != null) { text.enableWordWrapping = true; text.fontSize = Mathf.Min(text.fontSize, 22f); }
                    var le = row.Button.GetComponent<UnityEngine.UI.LayoutElement>();
                    if (le != null) le.preferredHeight = -1f;
                }
            }, near: near, width: 620f);
        }

        // Issue paths have a fixed shape: "levels[0].buildSlots.add[2].requires", "levels[0].props[1]".
        private static readonly Regex PathRe = new(@"^levels\[(\d+)\]\.(buildSlots\.(add|edit|remove)|props|spawnLines\.(edit|add))\[(\d+)\]");

        /// <summary>The Studio item an issue path points at, or null.</summary>
        public static string KeyForPath(string path)
        {
            var m = PathRe.Match(path ?? "");
            var lv = Studio.Level;
            if (!m.Success || lv == null) return null;
            int i = int.Parse(m.Groups[5].Value);
            string what = m.Groups[2].Value;
            T At<T>(List<T> list) where T : class => list != null && i < list.Count ? list[i] : null;
            return what switch
            {
                "buildSlots.add" => At(lv.BuildSlots?.Add)?.Id is string id ? "add:" + id : null,
                "buildSlots.edit" => At(lv.BuildSlots?.Edit)?.Ref is string r ? "slot:" + r : null,
                "buildSlots.remove" => At(lv.BuildSlots?.Remove) is string n ? "slot:" + n : null,
                "props" => At(lv.Props)?.Id is string p ? "prop:" + p : null,
                "spawnLines.edit" => At(lv.SpawnLines?.Edit)?.Ref is string l ? "line:" + l : null,
                _ => null,
            };
        }
    }
}
