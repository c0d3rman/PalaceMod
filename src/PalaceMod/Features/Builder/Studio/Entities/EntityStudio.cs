using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PalaceMod.Builder.Library;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;
using PalaceMod.Builder.Studio.Enemies;
using UnityEngine;

/*
    The Entity Studio (docs/builder-ux.md, "Entity Studio"): the Enemy Studio's stage, dock, undo and lifecycle, editing
    a non-unit template (pack `entities[]`: a projectile, prop, pickup, area or logic object) instead of a unit. It is a
    mode of the Enemy Studio, not a second tool: the palette's Enemies and Things tabs switch between the two, one undo
    history covers both, and Back works the same (to the level, out of the arena, or back to the enemy whose attack
    opened it).

    This file is the mode and its editing helpers; EntityPanels draws its palette, inspector and test panels, EntityLook
    builds the turntable model (the runtime's own BodyVisual) with its footprint gizmos, EntityTest runs the launch test
    in a throwaway behaviour world, EntityDev is `builder-entity`.
*/
namespace PalaceMod.Builder.Studio.Entities
{
    /// <summary>StudioServices.Entities: opens the Entity Studio on a thing of a draft.</summary>
    internal sealed class EntityStudioService : IStudioEntityEditor
    {
        public bool Open(PackDraft draft, string templateId, string newKind, Action<string> onReturn, out string error) =>
            EntityStudio.Open(draft, templateId, newKind, onReturn, out error);
    }

    internal static class EntityStudio
    {
        private static bool active;
        private static string id;
        private static string returnUnit;
        private static Action<string> onDone;
        private static string lastModelKey;

        /// <summary>The Enemy Studio is up and editing a thing (or choosing the kind of a new one: Id null).</summary>
        public static bool Active => active && EnemyStudio.Running;
        public static string Id => id;
        public static PackDraft Draft => EnemyStudio.Draft;
        public static EntityDef Entity => Active && id != null ? Draft?.Pack.FindEntity(id) : null;
        public static int Index => id == null ? -1 : Draft?.Pack.Entities?.FindIndex(e => e?.Id == id) ?? -1;
        public static string Kind => ThingKinds.Of(Entity);
        public static bool IsProjectile => Entity is { } e && (e.Projectile != null || ThingKinds.Of(e) == "projectile");
        /// <summary>The enemy whose attack opened this thing (Back returns to it), or null.</summary>
        public static string ReturnUnit => Active ? returnUnit : null;
        public static IEnumerable<EntityDef> All => Draft?.Pack.Entities?.Where(e => e?.Id != null) ?? [];
        public static string ModelError;

        private static readonly JsonSerializerSettings Json = new() { NullValueHandling = NullValueHandling.Ignore };

        // ---- Open, switch, leave ------------------------------------------------------------------------------------

        /// <summary>Opens the Entity Studio on a thing of a draft: in place over a running Level Studio, else on its own
        /// arena. <paramref name="templateId"/> null: a new thing of <paramref name="newKind"/>, or (kind null too) the
        /// kind picker. <paramref name="onReturn"/> gets the thing's id (or null) on Back.</summary>
        public static bool Open(PackDraft d, string templateId, string newKind, Action<string> onReturn, out string error) =>
            EnemyStudio.Open(d, onReturn, out error, () =>
            {
                Reset();
                active = true;
                if (templateId != null)
                {
                    if (d.Pack.FindEntity(templateId) == null) return $"no thing \"{templateId}\"";
                    id = templateId;
                }
                else if (newKind != null)
                {
                    id = Create(d, newKind);
                    if (id == null) return $"couldn't add a {newKind}";
                }
                EnemyDock.PaletteTab = "things";
                if (!PanelTabs.Any(t => t.id == EnemyDock.PanelTab)) EnemyDock.PanelTab = "look";
                return null;
            });

        /// <summary>The Level Studio's "Edit thing" (LogicStudio.OpenEntityStudio): switches in place when the Enemy
        /// Studio is already up, else opens it over the level.</summary>
        public static bool OpenFromLevel(string templateId)
        {
            if (EnemyStudio.Active && Draft != null && Draft.Pack.FindEntity(templateId) != null) { Enter(templateId); return true; }
            var draft = Studio.Draft;
            if (draft == null || !Studio.Running) return false;
            if (!Open(draft, templateId, null, back => Log.For<BuilderManager>().Info($"Entity Studio returned {back}"), out string error))
            {
                Notify.Show("Entity Studio: " + error, NotifyKind.Warning, 4f, "builder-entity");
                return false;
            }
            return true;
        }

        /// <summary>Switches the open studio to a thing (null: the kind picker). <paramref name="returnTo"/>: the enemy
        /// Back goes to; <paramref name="done"/> gets the thing's id when the studio leaves it.</summary>
        public static void Enter(string entityId, string returnTo = null, Action<string> done = null, bool fireDone = true)
        {
            if (!EnemyStudio.Active) return;
            Leave(fireDone);
            EnemyArena.Clear();
            active = true;
            id = entityId;
            returnUnit = returnTo;
            onDone = done;
            lastModelKey = null;
            if (!PanelTabs.Any(t => t.id == EnemyDock.PanelTab)) EnemyDock.PanelTab = "look";
            if (EnemyDock.PaletteTab == "enemies" || EnemyDock.PaletteTab == "base") EnemyDock.PaletteTab = "things";
            RebuildModel(true);
            EnemyStage.ResetView();
            EnemyDock.RefreshAll();
        }

        /// <summary>Another thing, keeping the way back.</summary>
        public static void Select(string entityId) => Enter(entityId, returnUnit, onDone, fireDone: false);

        /// <summary>Leaves thing mode (the Enemy Studio goes back to its unit). The pending "done" (an attack waiting for
        /// a projectile) gets the thing's id.</summary>
        public static void Leave(bool fireDone = true)
        {
            if (!active) return;
            var done = onDone;
            string cur = Entity != null ? id : null;
            EntityTest.Clear();
            active = false;
            id = null;
            returnUnit = null;
            onDone = null;
            lastModelKey = null;
            if (fireDone && done != null && cur != null)
            {
                try { done(cur); } catch (Exception e) { Log.For<BuilderManager>().Caught("Entity Studio: return handler failed", e); }
            }
            if (EnemyStudio.Active && !PanelTabsUnit.Contains(EnemyDock.PanelTab)) EnemyDock.PanelTab = "stats";
            if (EnemyDock.PaletteTab == "things") EnemyDock.PaletteTab = "enemies";
        }

        private static readonly string[] PanelTabsUnit = ["stats", "attack", "tags", "look", "behaviour", "moves"];

        /// <summary>Back with a way back: to the enemy (true), else false (the studio closes as usual).</summary>
        public static bool Back()
        {
            if (!Active || returnUnit == null) return false;
            string unit = returnUnit;
            if (Draft?.Pack.Units.Any(u => u?.Id == unit) != true) { returnUnit = null; return false; }
            EnemyStudio.Select(unit);
            return true;
        }

        /// <summary>Forgets the mode without callbacks (the studio opened or closed).</summary>
        public static void Reset()
        {
            active = false;
            id = null;
            returnUnit = null;
            onDone = null;
            lastModelKey = null;
            ModelError = null;
        }

        /// <summary>Per frame while editing a thing: a deleted thing (Delete, or undo of its creation) falls back to
        /// another one, else to the kind picker.</summary>
        public static void Tick()
        {
            if (id != null && Entity == null)
            {
                id = All.FirstOrDefault()?.Id;
                lastModelKey = null;
                RebuildModel(true);
                EnemyDock.RefreshAll();
            }
        }

        // ---- Things -------------------------------------------------------------------------------------------------

        /// <summary>Adds a thing of a kind (one undo step) and returns its id.</summary>
        public static string Create(PackDraft d, string kind)
        {
            if (d == null) return null;
            var k = ThingKinds.Find(kind) ?? ThingKinds.Find("projectile");
            string name = "New " + k.Label.ToLowerInvariant();
            var taken = new HashSet<string>(d.Pack.Units.Select(u => u?.Id).Concat(d.Pack.Entities?.Select(e => e?.Id) ?? []).Where(x => x != null));
            if (d.IsLibrary) foreach (var item in DesignLibrary.Items(DesignKind.Enemy)) taken.Add(item.Id);
            string newId = EditorIds(Ids.FromName(name), taken);
            var def = ThingKinds.Make(k.Id, newId, name, DefaultMesh(k.Id));
            d.Edit("New " + k.Label.ToLowerInvariant(), p => (p.Entities ??= []).Add(def), structural: true);
            return d.Pack.FindEntity(newId) != null ? newId : null;
        }

        private static string EditorIds(string baseId, IEnumerable<string> taken) => Editor.EditorCatalog.UniqueId(baseId, taken);

        /// <summary>A scenery mesh for a new thing's look (by the kind's hints), or null.</summary>
        private static string DefaultMesh(string kind)
        {
            try
            {
                var available = SceneryLibrary.Available.ToList();
                foreach (var hint in ThingKinds.MeshHints(kind))
                {
                    var e = available.FirstOrDefault(x => x.Name.StartsWith(hint, StringComparison.OrdinalIgnoreCase)) ?? available.FirstOrDefault(x => x.Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (e != null) return e.Ref;
                }
            }
            catch (Exception e) { Log.For<BuilderManager>().Debug($"Entity Studio: default mesh: {e.Message}"); }
            return null;
        }

        /// <summary>A new thing of a kind in the open studio.</summary>
        public static void New(string kind)
        {
            string newId = Create(Draft, kind);
            if (newId != null) Select(newId);
        }

        public static void Duplicate()
        {
            var e = Entity;
            if (e == null) return;
            var taken = new HashSet<string>(Draft.Pack.Units.Select(u => u?.Id).Concat(All.Select(x => x.Id)).Where(x => x != null));
            string name = (e.Name ?? e.Id) + " Copy";
            string newId = EditorIds(Ids.FromName(name), taken);
            var copy = JsonConvert.DeserializeObject<EntityDef>(JsonConvert.SerializeObject(e, Json), Json);
            copy.Id = newId;
            copy.Name = name;
            Draft.Edit("Duplicate thing", p => (p.Entities ??= []).Add(copy), structural: true);
            Select(newId);
        }

        public static void Delete()
        {
            int i = Index;
            if (i < 0) return;
            Draft.Edit("Delete thing", p => p.Entities.RemoveAt(i), structural: true);
            id = All.FirstOrDefault()?.Id;
            lastModelKey = null;
            RebuildModel(true);
            EnemyDock.RefreshAll();
        }

        /// <summary>One undo step on the thing being edited.</summary>
        public static void Edit(string label, Action<EntityDef> change, bool structural = false, string coalesce = null)
        {
            if (Entity == null) return;
            string target = id;
            Draft.Edit(label, p => { var e = p.FindEntity(target); if (e != null) change(e); }, structural, coalesce);
        }

        /// <summary>Changes the look, dropping it when empty.</summary>
        public static void EditVisual(string label, Action<VisualsDef> change, bool structural = false, string coalesce = null) => Edit(label, e =>
        {
            e.Visual ??= new VisualsDef();
            change(e.Visual);
            if (Editor.UnitPage.IsEmpty(e.Visual)) e.Visual = null;
        }, structural, coalesce);

        // ---- The stage ----------------------------------------------------------------------------------------------

        /// <summary>Shows the thing's look on the turntable, built as the runtime builds it (only when it changed).</summary>
        public static void RebuildModel(bool force = false)
        {
            var e = Entity;
            string key = e == null ? "-" : id + "|" + JsonConvert.SerializeObject(e, Json) + "|" + StudioScene.Scene.handle;
            if (!force && key == lastModelKey) return;
            lastModelKey = key;
            if (EntityTest.Running || EntityTest.HasResult) EntityTest.Clear();
            if (e == null) { ModelError = null; EnemyStage.Show(null, -1f); EnemyStage.FrameModel(); EnemyDock.OnModelRebuilt(); return; }
            bool hadModel = EnemyStage.Model != null;
            var built = EntityLook.Build(e, Draft, out ModelError);
            try { EnemyStage.Show(built, -1f); }
            finally { UnityEngine.Object.Destroy(built); }
            if (!hadModel) EnemyStage.FrameModel();
            EnemyDock.OnModelRebuilt();
        }

        // ---- Portraits ----------------------------------------------------------------------------------------------

        public static string PortraitFile(string packId, string entityId) => EnemyStudio.PortraitFile(packId, "thing-" + entityId);

        private static readonly Dictionary<string, (DateTime time, Sprite sprite)> portraits = [];

        /// <summary>Saves the stage's picture of the thing for its palette tile (a cache file per pack and thing).</summary>
        public static string SavePortrait()
        {
            var e = Entity;
            if (e == null || Draft == null || EnemyStage.Model == null) return null;
            var tex = EnemyStage.Portrait(256);
            if (tex == null) return null;
            try
            {
                string file = PortraitFile(Draft.Pack.Id, e.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, tex.EncodeToPNG());
                return file;
            }
            catch (Exception ex)
            {
                Log.For<BuilderManager>().Warn($"Entity Studio: couldn't save the picture of {e.Id}: {ex.Message}");
                return null;
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }

        /// <summary>A thing's saved picture, or null.</summary>
        public static Sprite Portrait(string entityId)
        {
            if (Draft == null) return null;
            string file = PortraitFile(Draft.Pack.Id, entityId);
            if (!File.Exists(file)) return null;
            var time = File.GetLastWriteTimeUtc(file);
            if (portraits.TryGetValue(file, out var c) && c.time == time) return c.sprite;
            Sprite sprite = null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                if (tex.LoadImage(File.ReadAllBytes(file), true)) sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception ex) { Log.For<BuilderManager>().Debug($"Entity Studio: picture {file}: {ex.Message}"); }
            portraits[file] = (time, sprite);
            return sprite;
        }

        // ---- Inspector tabs -----------------------------------------------------------------------------------------

        public static readonly (string id, string label)[] PanelTabs = [("look", "Look"), ("parts", "Parts"), ("behaviour", "Behaviour")];

        public static string Describe()
        {
            var e = Entity;
            string json = e == null ? "-" : JsonConvert.SerializeObject(e, Formatting.None, Json);
            return $"entity studio {Draft?.Pack.Id}/{id ?? "(kind picker)"} kind={Kind ?? "-"} name={e?.Name ?? "-"} mesh={e?.Visual?.Mesh ?? "-"} fromLevel={EnemyStudio.FromLevel} returnTo={returnUnit ?? "-"}; " +
                $"palette={EnemyDock.PaletteTab} panel={EnemyDock.PanelTab}; stage={EnemyStage.Point} model={(EnemyStage.Model != null ? EnemyStage.ModelBounds.size.ToString() : "none")}{(ModelError != null ? " error=" + ModelError : "")}; " +
                $"dirty={Draft?.Dirty} undo={Draft?.History.UndoLabel ?? "-"}; test: {EntityTest.Describe()}; json {json}";
        }
    }
}
