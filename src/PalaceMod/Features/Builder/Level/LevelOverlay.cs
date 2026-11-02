using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    Applies a level's layout edits to the freshly loaded host (ILevelOverlay, called by the session on scene load:
    after Awake, before Start). Build slots are moved, removed, re-priced or cloned the way Falkenpass does it: a
    clone is made inactive, given a deterministic save GUID, its activator and costs, then activated so the game's own
    Awake/Start register it like any native slot. Props are plain mesh objects on the obstacle layer. Spawn lines are
    moved as a whole. The pathfinding graphs are rescanned once, a frame after Start (see BuilderLevelManager), then
    every ground spawn line is checked for a path to the castle.

    In a layout-editing session nothing is applied: the editor shows the edits itself (LayoutEditor).
*/
namespace PalaceMod.Builder.Level
{
    internal sealed class LevelOverlay : ILevelOverlay
    {
        /// <summary>Set after an overlay with edits was applied; BuilderLevelManager rescans once Start has run.</summary>
        public static int ScanAfterFrame = -1;
        public static string ScanHost;

        public void Apply(DesignPack pack, LevelDef level, List<ValidationIssue> issues)
        {
            if (LayoutEditor.ClaimSession(pack, level)) return;
            var scene = LayoutScene.Host(level.Host);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                issues.Add(new ValidationIssue(Severity.Error, "", $"{level.Host} isn't loaded"));
                return;
            }
            if (!HasEdits(level)) return;
            int moved = 0, added = 0, removed = 0, props = 0, lines = 0;
            var slots = LayoutScene.Slots(scene).GroupBy(s => s.name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // Moves and price changes on the host's own slots.
            foreach (var edit in level.BuildSlots?.Edit ?? [])
            {
                if (edit?.Ref == null) continue;
                if (!slots.TryGetValue(edit.Ref, out var slot)) { issues.Add(Warn($"no build slot \"{edit.Ref}\" on {level.Host}; its edit is skipped")); continue; }
                if ((edit.Pos != null || edit.RotY != null) && LayoutScene.CanMove(slot))
                {
                    var t = slot.transform;
                    t.SetPositionAndRotation(edit.Pos != null ? LayoutScene.ToVector(edit.Pos) : t.position,
                        edit.RotY is float yaw ? LayoutScene.WithYaw(t.rotation, yaw) : t.rotation);
                    moved++;
                }
                ApplyCosts(slot, edit.Upgrades);
            }

            // New slots: clone first (so they can unlock each other), then wire activators, then activate.
            var holder = new GameObject("PalaceBuilder holder");
            holder.SetActive(false);
            var clones = new List<(BuildSlotAdd def, BuildSlot slot, Transform parent)>();
            foreach (var add in level.BuildSlots?.Add ?? [])
            {
                if (add?.Id == null) continue;
                var baseRef = ContentRef.Parse(add.Base ?? "");
                if (baseRef.Kind != RefKind.Scene || !slots.TryGetValue(baseRef.Id, out var source)) { issues.Add(Warn($"slot \"{add.Id}\": no build slot \"{add.Base}\" on {level.Host} to copy")); continue; }
                if (!LayoutScene.CanBeBase(source)) { issues.Add(Warn($"slot \"{add.Id}\": {source.name} can't be copied")); continue; }
                var go = UnityEngine.Object.Instantiate(source.gameObject, holder.transform);
                go.name = LayoutScene.AddedPrefix + add.Id;
                go.transform.SetPositionAndRotation(LayoutScene.ToVector(add.Pos), LayoutScene.WithYaw(source.transform.rotation, add.RotY));
                LayoutScene.SetGuid(go, LayoutScene.Guid(pack.Id, level.Id, add.Id));
                var clone = go.GetComponent<BuildSlot>();
                ApplyCosts(clone, add.Upgrades);
                clones.Add((add, clone, source.transform.parent));
            }
            foreach (var (def, clone, _) in clones)
            {
                if (def.Activator == null) continue;
                var activator = slots.TryGetValue(def.Activator, out var a) ? a : clones.FirstOrDefault(c => c.def.Id == def.Activator).slot;
                if (activator == null) { issues.Add(Warn($"slot \"{def.Id}\": no build slot \"{def.Activator}\" to unlock it; it unlocks like its base")); continue; }
                clone.SetActivatorBuilding(activator);
                clone.ActivatorLevel = Math.Max(0, def.ActivatorLevel);
                clone.StartDeactivated = true;
            }
            foreach (var (_, clone, parent) in clones)
            {
                clone.transform.SetParent(parent, true);
                added++;
            }
            UnityEngine.Object.Destroy(holder);

            // Removals: deactivated before Start so the slot never registers; slots it unlocks never appear.
            foreach (var name in level.BuildSlots?.Remove ?? [])
            {
                if (name == null || !slots.TryGetValue(name, out var slot)) { issues.Add(Warn($"no build slot \"{name}\" on {level.Host} to remove")); continue; }
                if (!LayoutScene.CanMove(slot)) { issues.Add(Warn("the castle can't be removed")); continue; }
                slot.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(slot.gameObject);
                removed++;
            }

            // Spawn lines.
            var hostLines = LayoutScene.Lines(scene).GroupBy(l => l.name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var edit in level.SpawnLines?.Edit ?? [])
            {
                if (edit?.Ref == null) continue;
                if (!hostLines.TryGetValue(edit.Ref, out var line)) { issues.Add(Warn($"no spawn line \"{edit.Ref}\" on {level.Host}")); continue; }
                MoveLine(line.transform, edit);
                lines++;
            }
            if ((level.SpawnLines?.Add?.Count ?? 0) > 0) issues.Add(Warn("new spawn lines aren't supported yet; they're skipped"));

            // Props.
            var bases = (level.Props?.Count ?? 0) > 0 ? LayoutScene.Props(scene).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First()) : [];
            foreach (var prop in level.Props ?? [])
            {
                if (prop?.Id == null) continue;
                if (prop.Base == null || !bases.TryGetValue(prop.Base, out var b)) { issues.Add(Warn($"prop \"{prop.Id}\": no scenery \"{prop.Base}\" on {level.Host}")); continue; }
                LayoutScene.MakeProp(scene, b, prop.Id, LayoutScene.ToVector(prop.Pos), prop.RotY, Mathf.Clamp(prop.Scale, 0.1f, 10f), prop.Solid);
                props++;
            }

            ScanAfterFrame = Time.frameCount + 2;
            ScanHost = level.Host;
            Log.For<BuilderManager>().Info($"level {pack.Id}/{level.Id} on {level.Host}: {moved} slots moved, {added} added, {removed} removed, {lines} spawn lines moved, {props} props");
        }

        public static bool HasEdits(LevelDef level) =>
            (level.BuildSlots?.Edit?.Count ?? 0) + (level.BuildSlots?.Add?.Count ?? 0) + (level.BuildSlots?.Remove?.Count ?? 0) +
            (level.SpawnLines?.Edit?.Count ?? 0) + (level.SpawnLines?.Add?.Count ?? 0) + (level.Props?.Count ?? 0) > 0;

        /// <summary>Moves a spawn line as a whole: pos is its new middle, rotY turns it about the middle.</summary>
        public static void MoveLine(Transform line, SpawnLineEdit edit)
        {
            var center = LayoutScene.LineCenter(line);
            if (edit.RotY is float yaw && Mathf.Abs(yaw) > 0.01f) line.RotateAround(center, Vector3.up, yaw);
            if (edit.Pos != null) line.position += LayoutScene.ToVector(edit.Pos) - center;
        }

        private static void ApplyCosts(BuildSlot slot, List<UpgradeEdit> edits)
        {
            if (edits == null || slot == null) return;
            for (int i = 0; i < edits.Count && i < slot.OwnUpgrades.Count; i++)
                if (edits[i]?.Cost is int cost) slot.OwnUpgrades[i].cost = Math.Max(0, cost);
        }

        private static ValidationIssue Warn(string message) => new(Severity.Warning, "", message);
    }
}
