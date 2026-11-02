using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

namespace PalaceMod.Builder.Editor
{
    /// <summary>
    /// The editor's working copy of one pack: the pack being edited (EditHistory.Current, so undo/redo replace it),
    /// whether it differs from what's on disk, and its validation issues. Every change goes through Edit, which records
    /// an undo step first. UI code must re-read objects through Pack after any change (undo swaps the whole object).
    /// </summary>
    internal sealed class PackDraft
    {
        /// <summary>Id of the pack on disk this draft edits; null for a pack that has never been saved.</summary>
        public string SourceId { get; private set; }
        public PackSource Source { get; private set; }
        public bool ReadOnly => Source != PackSource.User;
        public EditHistory History { get; private set; }
        public DesignPack Pack => History.Current;

        private string savedJson, json;
        private List<ValidationIssue> issues;
        private string lastKey;
        private float lastTime;

        /// <summary>Raised after every change; true when the change altered structure (items added, removed, moved,
        /// or the whole pack replaced by undo), false for a value edit.</summary>
        public event Action<bool> Changed;

        public PackDraft(PackEntry entry)
        {
            SourceId = entry.Id;
            Source = entry.Source;
            History = new EditHistory(PackSerializer.Clone(entry.Pack));
            savedJson = Json;
        }

        public PackDraft(DesignPack fresh)
        {
            Source = PackSource.User;
            History = new EditHistory(fresh);
            savedJson = null;
        }

        private string Json => json ??= PackSerializer.WriteCompact(Pack);

        public bool Dirty => Json != savedJson;

        /// <summary>The JSON on disk when the draft was opened or last saved.</summary>
        public string SavedJson => savedJson;

        public IReadOnlyList<ValidationIssue> Issues => issues ??= Validate();

        public int ErrorCount => Issues.Count(i => i.Severity == Severity.Error);

        /// <summary>
        /// Records an undo step named <paramref name="label"/>, then applies <paramref name="change"/> to the pack.
        /// Consecutive edits with the same <paramref name="coalesce"/> key within a second and a half share one undo
        /// step (holding a stepper shouldn't fill the history).
        /// </summary>
        public void Edit(string label, Action<DesignPack> change, bool structural = false, string coalesce = null)
        {
            float now = Time.unscaledTime;
            if (coalesce == null || coalesce != lastKey || now - lastTime > 1.5f) History.Record(label);
            lastKey = coalesce;
            lastTime = now;
            try
            {
                change(Pack);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Builder editor: \"{label}\" failed: {e}");
                History.Undo();
                lastKey = null;
                structural = true;
            }
            Invalidate(structural);
        }

        public bool Undo()
        {
            if (!History.CanUndo) return false;
            History.Undo();
            lastKey = null;
            Invalidate(true);
            return true;
        }

        public bool Redo()
        {
            if (!History.CanRedo) return false;
            History.Redo();
            lastKey = null;
            Invalidate(true);
            return true;
        }

        /// <summary>Throws away unsaved changes (an undoable step).</summary>
        public void Revert()
        {
            if (savedJson == null) return;
            var saved = PackSerializer.Read(savedJson, null);
            if (saved == null) return;
            Edit("Revert to saved", pack => CopyInto(saved, pack), structural: true);
        }

        private static void CopyInto(DesignPack from, DesignPack to)
        {
            foreach (var p in typeof(DesignPack).GetProperties().Where(p => p.CanRead && p.CanWrite)) p.SetValue(to, p.GetValue(from));
        }

        private void Invalidate(bool structural)
        {
            json = null;
            issues = null;
            try { Changed?.Invoke(structural); }
            catch (Exception e) { Plugin.Logger.LogError($"Builder editor: refresh failed: {e}"); }
        }

        /// <summary>Revalidates (e.g. after other packs changed on disk).</summary>
        public void Revalidate()
        {
            issues = null;
            Changed?.Invoke(false);
        }

        private List<ValidationIssue> Validate()
        {
            List<ValidationIssue> list;
            try { list = BuilderServices.Packs.Validate(Pack); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Builder editor: validation failed: {e}");
                list = Validator.Validate(Pack);
            }
            list.AddRange(EditorChecks.Check(Pack));
            return list;
        }

        /// <summary>Saves to disk. Built-in and plugin packs are saved as a new user pack (the draft then edits that
        /// copy). Changing a saved pack's id moves it: the new folder is written, then the old one goes to trash.</summary>
        public bool Save(out string message)
        {
            var repo = BuilderServices.Packs;
            var pack = Pack;
            try
            {
                if (!Ids.IsValid(pack.Id)) { message = "The pack id isn't valid (Pack tab)"; return false; }
                pack.GameVersion = Application.version;
                if (ReadOnly || SourceId == null)
                {
                    var installed = repo.InstallNew(pack);
                    if (installed == null) { message = "Couldn't save the pack"; return false; }
                    bool renamed = installed.Id != pack.Id;
                    message = ReadOnly ? $"Saved a copy as \"{installed.Id}\"" : renamed ? $"Saved as \"{installed.Id}\" (the id was taken)" : "Saved";
                    if (renamed) pack.Id = installed.Id;
                    SourceId = installed.Id;
                    Source = PackSource.User;
                }
                else
                {
                    if (pack.Id != SourceId && repo.Find(pack.Id) != null) { message = $"Another pack already has the id \"{pack.Id}\""; return false; }
                    repo.Save(pack);
                    if (pack.Id != SourceId) repo.Delete(SourceId);
                    SourceId = pack.Id;
                    message = "Saved";
                }
                json = null;
                savedJson = Json;
                Revalidate();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Builder editor: saving {pack.Id} failed: {e}");
                message = "Couldn't save: " + e.Message;
                return false;
            }
        }

        /// <summary>The pack's installed entry (for assets), if it has been saved.</summary>
        public PackEntry Entry => SourceId == null ? null : BuilderServices.Packs.Find(SourceId);
    }

    /// <summary>Editor-only checks on top of the validator (things that work but are probably mistakes).</summary>
    internal static class EditorChecks
    {
        public static IEnumerable<ValidationIssue> Check(DesignPack pack)
        {
            ICatalog catalog;
            try { catalog = BuilderServices.Catalog; }
            catch (InvalidOperationException) { yield break; }
            for (int w = 0; w < (pack.WaveSets?.Count ?? 0); w++)
            {
                var ws = pack.WaveSets[w];
                var host = ws?.Host == null ? null : catalog.FindHost(ws.Host);
                if (host == null) continue;
                for (int n = 0; n < (ws.Nights?.Count ?? 0); n++)
                    for (int s = 0; s < (ws.Nights[n]?.Spawns?.Count ?? 0); s++)
                    {
                        var sp = ws.Nights[n].Spawns[s];
                        var line = host.SpawnLines.FirstOrDefault(l => l.Name == sp?.Line);
                        var unit = EditorCatalog.BaseOf(pack, sp?.Unit);
                        if (line == null || unit == null) continue;
                        string problem = unit.Flying ? (line.Flying ? null : "a flying unit on a line for ground units")
                            : unit.Large ? (line.BigGround ? null : "a large unit on a line that doesn't allow large units")
                            : line.SmallGround ? null : "a ground unit on a line for flying units";
                        if (problem != null)
                            yield return new ValidationIssue(Severity.Warning, $"waveSets[{w}].nights[{n}].spawns[{s}].line", problem + "; it may get stuck");
                    }
            }
        }
    }
}
