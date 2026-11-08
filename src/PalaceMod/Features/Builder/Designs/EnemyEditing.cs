using System;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio;

/*
    Opening an enemy for editing, from the library home or from the Level Studio's nights. Goes to the Enemy Studio
    (StudioServices.Enemies) when it's installed, else to the pack editor's unit page over the current screen.
*/
namespace PalaceMod.Builder
{
    internal static class EnemyEditing
    {
        /// <summary>Edits a unit of the draft. From the Level Studio pass Studio.Draft: the Enemy Studio comes back to
        /// it, and enemy and level edits share its undo and its save.</summary>
        public static void Edit(PackDraft draft, string unitId, Action<string> onReturn = null)
        {
            if (draft == null || unitId == null) return;
            var editor = StudioServices.Enemies;
            if (editor != null)
            {
                bool fromStudio = Studio.Studio.Running && Studio.Studio.Draft == draft;
                if (editor.Open(draft, unitId, fromStudio, onReturn, out string error)) return;
                Notify.Show("Can't open the Enemy Studio: " + error, NotifyKind.Warning, 5f, "builder-library");
            }
            int index = draft.Pack.Units.FindIndex(u => u?.Id == unitId);
            if (index < 0) return;
            BuilderEditor.BindDraft(draft);
            BuilderEditor.OpenUnit(index);
        }

        /// <summary>Starts a new enemy (a copy of <paramref name="baseRef"/>, e.g. "vanilla:E Racer") in the draft and
        /// opens it; created gets its id right away (e.g. to put it in a spawn group).</summary>
        public static void New(PackDraft draft, string baseRef, Action<string> created = null)
        {
            if (draft == null) return;
            var b = BuilderServices.Catalog.FindUnit(ContentRef.Parse(baseRef).Id);
            string name = b == null ? "New enemy" : "My " + b.DisplayName;
            var unit = DesignLibrary.NewEnemy(draft, baseRef, name);
            if (unit == null) return;
            created?.Invoke(unit.Id);
            Edit(draft, unit.Id);
        }

        /// <summary>Picks a vanilla enemy to start from, then New.</summary>
        public static void PickAndNew(PackDraft draft, Action<string> created = null, string side = UnitSide.Enemy)
        {
            Picker.One("Start From", UnitPage.BaseItems(draft.Pack, side), null, baseId => New(draft, ContentRef.Vanilla(baseId), created),
                "A new enemy starts as a copy of a game enemy; then change what you like.");
        }

        public static bool IsCustom(PackDraft draft, string unitRef)
        {
            var r = ContentRef.Parse(unitRef);
            return r.Kind == RefKind.LocalUnit && draft?.Pack.FindUnit(r.Id) != null;
        }

        public static string IdOf(string unitRef) => ContentRef.Parse(unitRef).Id;

        /// <summary>Picker rows for a unit: "this pack" became the library.</summary>
        public static string MineHeader(PackDraft draft) => draft?.IsLibrary == true ? "My enemies" : "This pack";

        public static int CountUses(PackDraft draft, string unitId) =>
            draft.Pack.WaveSets.Sum(w => w?.Nights?.Sum(n => n?.Spawns?.Count(s => EnemyEditing.IdOf(s?.Unit) == unitId) ?? 0) ?? 0);
    }
}
