using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The Enemy Studio (docs/builder-ux.md, "Enemy Studio"): the custom enemy editor as an in-world tool around the live
    model. It runs on the map the Level Studio has loaded:

    - from the Level Studio: in place, no reload. The shell is suspended (StudioShell.Suspended) with its view captured;
      Back restores it exactly. The draft is the Level Studio's own, so enemy edits share its undo history.
    - on its own: it opens a throwaway arena first (a Studio edit session on Nordfels, cleared, never saved), then takes
      over the same way; Back leaves the Studio.

    This file is the lifecycle and the editing helpers; EnemyStage is the turntable and camera, EnemyArena the fight,
    EnemyDock the panels, EnemyStudioManager the keys, patches and the `builder-enemy` dev command.
*/
namespace PalaceMod.Builder.Studio.Enemies
{
    /// <summary>The Enemy Studio as the Level Studio and the library home open it (StudioServices.Enemies).</summary>
    internal sealed class EnemyStudioService : IStudioEnemyEditor
    {
        public bool Open(PackDraft draft, string unitId, bool returnToLevelStudio, Action<string> onReturn, out string error) =>
            EnemyStudio.Open(draft, unitId, onReturn, out error);
    }

    internal static class EnemyStudio
    {
        private enum State { Off, Opening, Active }

        private static State state;
        private static PackDraft draft;
        private static int index = -1;
        private static Action<string> onReturn;
        private static bool fromLevel;
        private static StudioViewState levelView;
        private static PackDraft arenaDraft;
        private static float rebuildAt = -1f;
        private static string lastModelKey;

        public static bool Active => state == State.Active;
        public static bool Running => state != State.Off;
        public static bool FromLevel => fromLevel;
        public static PackDraft Draft => draft;
        public static string ArenaHost = "Nordfels";

        /// <summary>The enemy being edited, or null.</summary>
        public static UnitDef Unit => draft != null && index >= 0 && index < draft.Pack.Units.Count ? draft.Pack.Units[index] : null;
        public static int UnitIndex => index;
        public static string UnitId => Unit?.Id;

        /// <summary>The pack editor's unit page, used for its fields (rendered into the dock's tabs).</summary>
        private static UnitPage page;
        public static UnitPage Page => page ??= new UnitPage();

        public static IEnumerable<UnitDef> Enemies => draft?.Pack.Units.Where(u => u != null && u.Side == UnitSide.Enemy) ?? [];

        // ---- Open and close -----------------------------------------------------------------------------------------

        /// <summary>Opens a unit of a draft (null id: a new enemy). onReturn gets the unit's id (or null) on Back.</summary>
        public static bool Open(PackDraft d, string unitId, Action<string> back, out string error) => Open(d, back, out error, () =>
        {
            Entities.EntityStudio.Reset();
            index = unitId == null ? -1 : d.Pack.Units.FindIndex(u => u?.Id == unitId);
            if (unitId != null && index < 0) return $"no unit \"{unitId}\"";
            if (index < 0) index = NewEnemy(null);
            return index < 0 ? "couldn't add an enemy" : null;
        });

        /// <summary>Opens the studio on a draft; <paramref name="pick"/> chooses what it edits (a unit, or a thing:
        /// Entities.EntityStudio) once the draft is set, and returns an error or null.</summary>
        internal static bool Open(PackDraft d, Action<string> back, out string error, Func<string> pick)
        {
            error = null;
            if (d == null) { error = "no draft"; return false; }
            if (Running) Close(returnToLevel: false);
            draft = d;
            onReturn = back;
            error = pick();
            if (error != null) { draft = null; onReturn = null; Entities.EntityStudio.Reset(); return false; }
            // A Level Studio is up: take over its scene in place, and give it back exactly as it was.
            if (Studio.Mode == StudioMode.Edit && StudioShell.Editing)
            {
                fromLevel = true;
                levelView = StudioShell.CaptureViewNow();
                Enter();
                return true;
            }
            // On its own: an arena first.
            fromLevel = false;
            var arena = new DesignPack { Id = "enemy-studio-arena", Name = "Enemy Studio" };
            arena.Levels.Add(new LevelDef { Id = "arena", Name = "Arena", Host = ArenaHost, Base = LevelBases.Cleared });
            arenaDraft = new PackDraft(arena);
            state = State.Opening;
            if (!Studio.Open(arenaDraft, "arena", out error)) { state = State.Off; draft = null; arenaDraft = null; return false; }
            return true;
        }

        private static void Enter()
        {
            state = State.Active;
            StudioShell.HideForSuspend();
            // Opened from a menu over the Studio (the wave page, a picker): the stage takes the screen.
            if (Picker.IsOpen) Picker.Close();
            if (ModPopup.Current != null && ModPopup.Current.IsOpen) ModPopup.Current.Close();
            if (UIFrameManager.instance != null && UIFrameManager.instance.ActiveFrame != null && !ModDock.IsDockFrame(UIFrameManager.instance.ActiveFrame))
                UIFrameManager.instance.CloseAllFrames();
            BuilderEditor.BindDraft(draft);
            Page.Index = index;
            draft.Changed += OnDraftChanged;
            var scene = StudioScene.Scene;
            Vector3 point = fromLevel ? StudioCamera.Pivot : StudioScene.Castle?.Pos + new Vector3(0f, 0f, -14f) ?? StudioCamera.Pivot;
            EnemyStage.Enter(scene, point);
            EnemyArena.DefaultDefenders();
            lastModelKey = null;
            RebuildModel();
            EnemyStage.ResetView();
            EnemyDock.Build();
            Log.For<BuilderManager>().Info($"Enemy Studio: {draft.Pack.Id}/{(Entities.EntityStudio.Active ? "entities/" + Entities.EntityStudio.Id : UnitId)} on {scene.name}{(fromLevel ? " (from the Level Studio)" : "")}");
        }

        /// <summary>Back: to the Level Studio (exactly as it was) or out of the arena.</summary>
        public static void Back()
        {
            if (Entities.EntityStudio.Back()) return;
            Close(returnToLevel: true);
        }

        public static void Close(bool returnToLevel)
        {
            if (state == State.Off) return;
            string id = Entities.EntityStudio.Active ? Entities.EntityStudio.Id : UnitId;
            var callback = onReturn;
            Entities.EntityTest.Clear();
            Entities.EntityStudio.Reset();
            bool wasFromLevel = fromLevel, wasActive = state == State.Active;
            EnemyArena.Clear();
            EnemyStage.Leave();
            EnemyDock.Destroy();
            if (draft != null) draft.Changed -= OnDraftChanged;
            state = State.Off;
            var view = levelView;
            levelView = null;
            draft = null;
            index = -1;
            onReturn = null;
            fromLevel = false;
            if (wasFromLevel && wasActive && StudioShell.Editing)
            {
                BuilderEditor.BindDraft(Studio.Draft);
                StudioShell.ResumeFromSuspend(view);
            }
            else if (Studio.Running && arenaDraft != null && returnToLevel) Studio.Leave();
            arenaDraft = null;
            try { callback?.Invoke(id); } catch (Exception e) { Log.For<BuilderManager>().Caught("Enemy Studio: return handler failed", e); }
        }

        // ---- Per frame ----------------------------------------------------------------------------------------------

        public static void Update()
        {
            if (state == State.Off) return;
            if (state == State.Opening)
            {
                if (!Studio.Running) { state = State.Off; draft = null; arenaDraft = null; return; }
                if (Studio.Mode == StudioMode.Edit && StudioShell.Editing) Enter();
                return;
            }
            // The scene went (the session ended, Builder switched off, a swap): drop out quietly.
            if (Studio.Mode != StudioMode.Edit || !StudioShell.Editing || !EnemyStage.Ready) { Close(returnToLevel: false); return; }
            if (Entities.EntityStudio.Active) Entities.EntityStudio.Tick();
            else if (Unit == null && draft != null)
            {
                // The unit was deleted (undo of its creation, or Delete): edit another, else leave.
                index = draft.Pack.Units.FindIndex(u => u?.Side == UnitSide.Enemy);
                if (index < 0) { Back(); return; }
                EnemyDock.RefreshAll();
                lastModelKey = null;
            }
            LocalGamestate.Instance?.SetPlayerFreezeState(true);
            StudioInput.Tick();
            float dt = Time.unscaledDeltaTime;
            if (rebuildAt >= 0f && Time.unscaledTime >= rebuildAt) { rebuildAt = -1f; RebuildModel(); }
            EnemyDock.Tick();
            EnemyControls.Update();
            EnemyStage.Update(dt);
            EnemyArena.Update(Time.deltaTime);
            Entities.EntityTest.Update();
        }

        private static void OnDraftChanged(bool structural)
        {
            if (state != State.Active) return;
            rebuildAt = Time.unscaledTime + (structural ? 0f : 0.12f);
            EnemyDock.OnChanged(structural);
        }

        /// <summary>Shows the unit's current template on the turntable (only when it changed).</summary>
        public static void RebuildModel(bool force = false)
        {
            if (Entities.EntityStudio.Active) { Entities.EntityStudio.RebuildModel(force); return; }
            var u = Unit;
            if (u == null || draft == null) { EnemyStage.Show(null, -1f); return; }
            string key = u.Id + "|" + PackSerializer.WriteCompact(draft.Pack);
            if (!force && key == lastModelKey) return;
            lastModelKey = key;
            if (EnemyArena.Running || EnemyArena.HasResult) EnemyArena.Clear();
            var template = BuilderServices.Units.Resolve(draft.Pack, u.Id, out string error);
            if (template == null)
            {
                ModelError = error;
                // Show the base unit at least, so the stage isn't empty while a field is mid-edit.
                template = BuilderServices.Catalog?.FindUnit(ContentRef.Parse(u.Base).Id)?.Prefab;
            }
            else ModelError = null;
            bool hadModel = EnemyStage.Model != null;
            EnemyStage.Show(template, PreviewWalk ? Effective().MoveSpeed : -1f);
            if (!hadModel) EnemyStage.FrameModel();
            EnemyDock.OnModelRebuilt();
        }

        public static string ModelError;

        /// <summary>The stage's model walks in place (the procedural walk; the base's rig shows its own pose).</summary>
        public static bool PreviewWalk;

        public static void SetPreviewWalk(bool on)
        {
            PreviewWalk = on;
            EnemyStage.SetPreviewWalk(on ? Math.Max(1f, Effective().MoveSpeed) : -1f);
        }

        // ---- Editing ------------------------------------------------------------------------------------------------

        /// <summary>One undo step on the unit being edited.</summary>
        public static void Edit(string label, Action<UnitDef> change, bool structural = false, string coalesce = null)
        {
            if (draft == null || Unit == null) return;
            int i = index;
            draft.Edit(label, p => change(p.Units[i]), structural, coalesce);
        }

        /// <summary>Changes the visuals, dropping them when empty.</summary>
        public static void EditVisuals(string label, Action<VisualsDef> change, bool structural = false, string coalesce = null) => Edit(label, u =>
        {
            u.Visuals ??= new VisualsDef();
            change(u.Visuals);
            if (UnitPage.IsEmpty(u.Visuals)) u.Visuals = null;
        }, structural, coalesce);

        /// <summary>Switches to another unit of the draft (undo stays the draft's).</summary>
        public static void Select(string unitId)
        {
            int i = draft?.Pack.Units.FindIndex(u => u?.Id == unitId) ?? -1;
            if (i < 0 || (i == index && !Entities.EntityStudio.Active)) return;
            Entities.EntityStudio.Leave();
            index = i;
            Page.Index = i;
            EnemyArena.Clear();
            lastModelKey = null;
            RebuildModel();
            EnemyStage.ResetView();
            EnemyDock.RefreshAll();
        }

        /// <summary>Adds a new enemy (one undo step) from a base (default: the first ground enemy) and returns its index.</summary>
        public static int NewEnemy(string baseId)
        {
            if (draft == null) return -1;
            var catalog = BuilderServices.Catalog;
            var b = baseId != null ? catalog?.FindUnit(baseId) : null;
            b ??= catalog?.Units.FirstOrDefault(u => u.Side == UnitSide.Enemy && u.Id == "E Racer") ?? catalog?.Units.FirstOrDefault(u => u.Side == UnitSide.Enemy && !u.Flying && !u.Large);
            string baseRef = ContentRef.Vanilla(b?.Id ?? "E Racer");
            string name = "New " + (b?.DisplayName ?? "Enemy");
            if (draft.IsLibrary)
            {
                // A free id in the whole library, not just this workspace.
                var made = DesignLibrary.NewEnemy(draft, baseRef, name);
                return made == null ? -1 : draft.Pack.Units.IndexOf(made);
            }
            draft.Edit("New enemy", p =>
            {
                p.Units.Add(new UnitDef
                {
                    Id = EditorCatalog.UniqueId(Ids.FromName(name), p.Units.Select(u => u.Id)),
                    Side = UnitSide.Enemy,
                    Base = baseRef,
                    Name = name,
                });
            }, structural: true);
            return draft.Pack.Units.Count - 1;
        }

        public static void New(string baseId)
        {
            if (draft == null) return;
            int i = NewEnemy(baseId);
            if (i < 0) return;
            Select(draft.Pack.Units[i].Id);
        }

        public static void Duplicate()
        {
            var u = Unit;
            if (u == null) return;
            string newId = null;
            var taken = new HashSet<string>(draft.Pack.Units.Select(o => o.Id));
            if (draft.IsLibrary) foreach (var item in DesignLibrary.Items(DesignKind.Enemy)) taken.Add(item.Id);
            draft.Edit("Duplicate enemy", p =>
            {
                var copy = EditorCatalog.Copy(p.Units[index]);
                copy.Name = (copy.Name ?? copy.Id) + " Copy";
                copy.Id = newId = EditorCatalog.UniqueId(Ids.FromName(copy.Name), taken);
                p.Units.Add(copy);
            }, structural: true);
            if (newId != null) Select(newId);
        }

        public static void Undo()
        {
            if (draft == null) return;
            string label = draft.History.UndoLabel;
            Notify.Show(draft.Undo() ? $"Undid: {label}" : "Nothing to undo", NotifyKind.Info, 2f, "builder-undo");
        }

        public static void Redo()
        {
            if (draft == null) return;
            string label = draft.History.RedoLabel;
            Notify.Show(draft.Redo() ? $"Redid: {label}" : "Nothing to redo", NotifyKind.Info, 2f, "builder-undo");
        }

        /// <summary>Saves the draft and the enemy's portrait.</summary>
        public static void Save()
        {
            if (draft == null) return;
            bool ok = draft.Save(out string message);
            if (ok) { if (Entities.EntityStudio.Active) Entities.EntityStudio.SavePortrait(); else SavePortrait(); }
            Notify.Show(message, ok ? NotifyKind.Success : NotifyKind.Error, key: "builder-enemy");
            EnemyDock.OnChanged(false);
        }

        // ---- Portraits ----------------------------------------------------------------------------------------------

        /// <summary>Where saved portraits go: R2-C1's library API once it's there (EnemyPortraits.Sink), else a cache
        /// file per pack and unit.</summary>
        public static string PortraitFile(string packId, string unitId)
        {
            var bad = Path.GetInvalidFileNameChars();
            string Safe(string s) => new string((s ?? "_").Select(c => bad.Contains(c) ? '_' : c).ToArray());
            return Path.Combine(Paths.BepInExRootPath, "PalaceBuilder", "cache", "portraits", Safe(packId), Safe(unitId) + ".png");
        }

        /// <summary>Takes the portrait and saves it: into the library (its tile picture) for a library workspace, else a
        /// cache file. Returns where, or null.</summary>
        public static string SavePortrait()
        {
            var u = Unit;
            if (u == null || draft == null) return null;
            var tex = EnemyStage.Portrait(256);
            if (tex == null) return null;
            try
            {
                if (draft.IsLibrary && DesignLibrary.SavePreview(DesignKind.Enemy, u.Id, tex)) return DesignLibrary.PreviewPath(DesignKind.Enemy, u.Id) ?? "library";
                string file = PortraitFile(draft.Pack.Id, u.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, tex.EncodeToPNG());
                PortraitCache.Remove(file);
                return file;
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Enemy Studio: couldn't save the portrait of {u.Id}: {e.Message}");
                return null;
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }

        private static readonly Dictionary<string, Sprite> PortraitCache = [];

        /// <summary>A saved portrait for a tile, or null.</summary>
        public static Sprite PortraitSprite(string packId, string unitId)
        {
            if (draft?.IsLibrary == true) return DesignLibrary.Preview(DesignKind.Enemy, unitId);
            string file = PortraitFile(packId, unitId);
            if (PortraitCache.TryGetValue(file, out var s)) return s;
            Sprite sprite = null;
            if (File.Exists(file))
            {
                try
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                    if (tex.LoadImage(File.ReadAllBytes(file), true))
                        sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
                catch (Exception e) { Log.For<BuilderManager>().Debug($"Enemy Studio: portrait {file}: {e.Message}"); }
            }
            return PortraitCache[file] = sprite;
        }

        // ---- Numbers ------------------------------------------------------------------------------------------------

        public static CatalogUnit BaseUnit => BuilderServices.Catalog?.FindUnit(ContentRef.Parse(Unit?.Base).Id);

        public static CatalogWeapon BaseWeapon()
        {
            var catalog = BuilderServices.Catalog;
            var u = Unit;
            if (catalog == null || u == null) return null;
            string b = u.Weapon?.Base;
            if (b != null && b != "inherit") return catalog.FindWeapon(ContentRef.Parse(b).Id);
            return BaseUnit?.Weapon == null ? null : catalog.FindWeapon(BaseUnit.Weapon);
        }

        public static EffectiveUnit Effective() => EnemyMath.Effective(Unit, BaseUnit, BaseWeapon());

        public static string Describe()
        {
            if (state == State.Off) return "Enemy Studio closed";
            if (state == State.Opening) return "Enemy Studio: opening the arena";
            if (Entities.EntityStudio.Active) return Entities.EntityStudio.Describe();
            var e = Effective();
            return $"enemy studio {draft?.Pack.Id}/{UnitId} ({Unit?.Name}) base={Unit?.Base} mesh={Unit?.Visuals?.Mesh ?? "-"} fromLevel={fromLevel}; hp={e.MaxHp} speed={e.MoveSpeed} " +
                $"cooldown={e.AttackCooldown} tags=[{string.Join(",", e.Tags)}]; stage={EnemyStage.Point} cam yaw={EnemyStage.Yaw:0} pitch={EnemyStage.Pitch:0} dist={EnemyStage.Distance:0.0}/{EnemyStage.MaxDistance:0} focus={EnemyStage.Focus} hidden={EnemyStage.HiddenCount}; " +
                $"model={(EnemyStage.Model != null ? EnemyStage.ModelBounds.size.ToString() : "none")}{(ModelError != null ? " error=" + ModelError : "")}; dirty={draft?.Dirty} undo={draft?.History.UndoLabel ?? "-"}; arena: {EnemyArena.Describe()}";
        }
    }
}
