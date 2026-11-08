using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Applies a level's layout edits to the freshly loaded host (ILevelOverlay, called by the session on scene load:
    after Awake, before Start). Build slots are moved, removed, re-priced or cloned the way Falkenpass does it: a
    clone is made inactive, given a deterministic save GUID and its costs, then activated so the game's own
    Awake/Start register it like any native slot. Props are mesh objects; solid ones get an invisible blocker box
    (LayoutScene.SetSolid). Spawn lines are moved as a whole; new ones are copies of a host line under its spawner.
    The pathfinding graphs are rescanned once, a frame after Start (see BuilderLevelManager), then every ground spawn
    line is checked for a path to the castle.

    Requirements (docs/builder-ux.md, "Requirements") use the game's own mechanism only: each slot's one prerequisite
    is BuildSlot.activatorBuilding + activatorLevel (+ startDeactivated), and its upgrade cap requiredRootLevelDifference.
    They are set on host slots and clones alike after cloning and before Start, so BuildSlot.Start wires
    requiredRoot/isActivatorOf itself. A requirement that would close a loop is dropped (BuildSlot.Start walks the
    activator chain to its root and would never return).

    An empty base (LevelDef.base, EmptyBase) removes every slot but the castle and hides the scenery; it is applied in
    the layout editor's session too, where nothing else is (the editor shows the edits itself).
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
            bool editing = Studio.StudioShell.ClaimSession(pack, level);
            var scene = LayoutScene.Host(level.Host);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                issues.Add(new ValidationIssue(Severity.Error, "", $"{level.Host} isn't loaded"));
                return;
            }
            if (editing)
            {
                if (LevelBases.IsEmpty(level)) ApplyBaseLook(scene, level, issues);
                GroundLayer.Apply(scene, pack, level, issues);
                return;
            }
            if (!HasEdits(level)) return;
            int moved = 0, added = 0, removed = 0, props = 0, lines = 0, wired = 0;
            var all = LayoutScene.Slots(scene);
            var slots = all.GroupBy(s => s.name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var host = LayoutScene.HostSlots(all);

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

            // New slots: clone first (so they can unlock each other), then wire requirements, then activate.
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

            wired = ApplyRequirements(level, host, slots, clones.ToDictionary(c => SlotRefs.Added(c.def.Id), c => c.slot, StringComparer.Ordinal), issues);

            foreach (var (_, clone, parent) in clones)
            {
                clone.transform.SetParent(parent, true);
                added++;
            }
            UnityEngine.Object.Destroy(holder);

            // Removals: deactivated before Start so the slot never registers; slots it unlocks never appear.
            var removals = new List<BuildSlot>();
            foreach (var name in level.BuildSlots?.Remove ?? [])
            {
                if (name == null || !slots.TryGetValue(name, out var slot)) { issues.Add(Warn($"no build slot \"{name}\" on {level.Host} to remove")); continue; }
                if (!LayoutScene.CanMove(slot)) { issues.Add(Warn("the castle can't be removed")); continue; }
                removals.Add(slot);
            }
            if (LevelBases.IsEmpty(level)) removals.AddRange(all.Where(s => !EmptyBase.Keeps(s)));
            foreach (var slot in removals.Distinct())
            {
                if (slot == null) continue;
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
            int newLines = AddLines(level, hostLines, issues);
            // Removed host lines are switched off after the copies are made (a copy of an inactive line would be
            // inactive too). LayoutScene.Lines skips inactive lines, so the path check, routes and pictures drop them;
            // the validator refuses waves that still spawn from one.
            int gone = 0;
            var removedLines = new HashSet<string>(level.SpawnLines?.Remove ?? [], StringComparer.Ordinal);
            foreach (var name in removedLines)
            {
                if (name == null) continue;
                if (!hostLines.TryGetValue(name, out var line) || (level.SpawnLines?.Add ?? []).Any(a => a?.Id == name))
                { issues.Add(Warn($"no spawn line \"{name}\" on {level.Host} to remove")); continue; }
                line.gameObject.SetActive(false);
                gone++;
            }

            // Props.
            var bases = (level.Props?.Count ?? 0) > 0 ? LayoutScene.Props(scene).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First()) : [];
            foreach (var prop in level.Props ?? [])
            {
                if (prop?.Id == null) continue;
                // The host's own scenery first, then the cross-map library (Library/SceneryLibrary).
                if (prop.Base == null || !bases.TryGetValue(prop.Base, out var b)) b = Library.SceneryLibrary.Resolve(scene, prop.Base);
                if (b == null) { issues.Add(Warn($"prop \"{prop.Id}\": no scenery \"{prop.Base}\" on {level.Host} or in the library")); continue; }
                LayoutScene.MakeProp(scene, b, prop.Id, LayoutScene.ToVector(prop.Pos), prop.RotY, Mathf.Clamp(prop.Scale, 0.1f, 10f), prop.Solid);
                props++;
            }

            int hidden = LevelBases.IsEmpty(level) ? ApplyBaseLook(scene, level, issues) : 0;
            GroundLayer.Apply(scene, pack, level, issues);   // terrain (a flat field's ground, blocked cells, water)

            ScanAfterFrame = Time.frameCount + 2;
            ScanHost = level.Host;
            Log.For<BuilderManager>().Info($"level {pack.Id}/{level.Id} on {level.Host}{(level.Base != null ? $" ({level.Base})" : "")}: {moved} slots moved, {added} added, {removed} removed, " +
                $"{wired} requirements set, {lines} spawn lines moved, {newLines} added, {gone} removed, {props} props{(hidden > 0 ? $", {hidden} scenery renderers hidden" : "")}");
        }

        /// <summary>The empty base's look: the scenery hidden (and, for a flat field, the ground replaced).</summary>
        private static int ApplyBaseLook(Scene scene, LevelDef level, List<ValidationIssue> issues)
        {
            int hidden = EmptyBase.HideScenery(scene);
            // A flat field's ground is replaced by GroundLayer.Apply, called after this.
            return hidden;
        }

        public static bool HasEdits(LevelDef level) =>
            level.Base != null ||
            (level.BuildSlots?.Edit?.Count ?? 0) + (level.BuildSlots?.Add?.Count ?? 0) + (level.BuildSlots?.Remove?.Count ?? 0) +
            (level.SpawnLines?.Edit?.Count ?? 0) + (level.SpawnLines?.Add?.Count ?? 0) + (level.SpawnLines?.Remove?.Count ?? 0) +
            (level.Props?.Count ?? 0) > 0 || GroundLayer.Wants(level);

        // ---- Requirements -----------------------------------------------------------------------------------------

        /// <summary>Sets each slot's prerequisite and upgrade cap from the level. Returns how many slots were wired.</summary>
        internal static int ApplyRequirements(LevelDef level, HostSlots host, Dictionary<string, BuildSlot> slots, Dictionary<string, BuildSlot> clones, List<ValidationIssue> issues)
        {
            BuildSlot Find(string slotRef)
            {
                if (slotRef == null) return null;
                if (slotRef == SlotRefs.Castle) slotRef = host.Castle;
                if (slotRef == null) return null;
                if (SlotRefs.IsAdded(slotRef)) return clones.TryGetValue(slotRef, out var c) ? c : null;
                return LevelRequirements.IsRemoved(level, host, slotRef) ? null : slots.TryGetValue(slotRef, out var s) ? s : null;
            }

            // What each slot will need: explicit requirements, else (added slots on an empty base) castle level 1.
            var wanted = new Dictionary<string, SlotRequirement>(StringComparer.Ordinal);
            foreach (var e in level.BuildSlots?.Edit ?? [])
                if (e?.Ref != null && e.Requires != null) wanted[e.Ref] = e.Requires;
            foreach (var a in level.BuildSlots?.Add ?? [])
                if (a?.Id != null && clones.ContainsKey(SlotRefs.Added(a.Id)))
                    if (a.Requires != null) wanted[SlotRefs.Added(a.Id)] = a.Requires;
                    else if (LevelBases.IsEmpty(level)) wanted[SlotRefs.Added(a.Id)] = LevelRequirements.CastleLevel(1);

            // Drop what can't be applied, and loops (checked against the whole graph, host prerequisites included).
            foreach (var key in wanted.Keys.ToList())
            {
                var r = wanted[key];
                var self = Find(key);
                if (self == null) { wanted.Remove(key); continue; }
                if (LayoutScene.IsCastle(self)) { issues.Add(Warn($"{key}: the castle can't have a requirement")); wanted.Remove(key); continue; }
                if (self.ActivatorUpgradesThis) { issues.Add(Warn($"{key}: built through {self.ActivatorBuilding?.name}, so it can't have its own requirement")); wanted.Remove(key); continue; }
                if (r.Slot == null) continue;
                var target = Find(r.Slot);
                if (target == null)
                {
                    issues.Add(Warn($"{key}: needs \"{r.Slot}\", which isn't on the map; it needs castle level 1 instead"));
                    wanted[key] = LevelRequirements.CastleLevel(1);
                    target = Find(SlotRefs.Castle);
                }
                int max = target != null ? Math.Max(1, (target.ActivatorUpgradesThis && target.ActivatorBuilding != null ? target.ActivatorBuilding : target).OwnUpgrades.Count) : 1;
                if (wanted[key].Level > max - 1)
                {
                    issues.Add(Warn($"{key}: {r.Slot} only has {max} levels; it appears once that's reached"));
                    wanted[key] = new SlotRequirement { Slot = wanted[key].Slot, Level = max - 1 };
                }
            }
            var graph = LevelRequirements.Effective(level, host);
            foreach (var kv in wanted) graph[kv.Key] = kv.Value.Slot == null ? null : new SlotRequirement { Slot = kv.Value.Slot == SlotRefs.Castle ? host.Castle : kv.Value.Slot, Level = kv.Value.Level };
            foreach (var loop in LevelRequirements.Cycles(graph))
            {
                string breakAt = loop.FirstOrDefault(wanted.ContainsKey) ?? loop[0];
                issues.Add(Warn($"requirement loop {string.Join(" > ", loop)}: {breakAt} needs castle level 1 instead"));
                wanted[breakAt] = LevelRequirements.CastleLevel(1);
            }

            int n = 0;
            foreach (var kv in wanted)
            {
                var slot = Find(kv.Key);
                var target = Find(kv.Value.Slot);
                slot.SetActivatorBuilding(target);
                slot.ActivatorLevel = target != null ? Math.Max(0, kv.Value.Level) : 0;
                slot.StartDeactivated = target != null;
                n++;
            }
            foreach (var e in level.BuildSlots?.Edit ?? [])
                if (e?.UpgradeCap is int cap && Find(e.Ref) is BuildSlot s && !LayoutScene.IsCastle(s)) s.SetRequiredRootLevelDifference(cap);
            foreach (var a in level.BuildSlots?.Add ?? [])
                if (a?.UpgradeCap is int cap && a.Id != null && Find(SlotRefs.Added(a.Id)) is BuildSlot s) s.SetRequiredRootLevelDifference(cap);
            return n;
        }

        // ---- Spawn lines ------------------------------------------------------------------------------------------

        /// <summary>Moves a spawn line as a whole: pos is its new middle, rotY turns it about the middle.</summary>
        public static void MoveLine(Transform line, SpawnLineEdit edit)
        {
            var center = LayoutScene.LineCenter(line);
            if (edit.RotY is float yaw && Mathf.Abs(yaw) > 0.01f) line.RotateAround(center, Vector3.up, yaw);
            if (edit.Pos != null) line.position += LayoutScene.ToVector(edit.Pos) - center;
        }

        /// <summary>New spawn lines: a copy of a host line (one that spawns the same kinds, if any) under the same
        /// spawner, named for the line's id so wave spawns can name it, made a straight line of its width.</summary>
        private static int AddLines(LevelDef level, Dictionary<string, EnemySpawnLine> hostLines, List<ValidationIssue> issues)
        {
            int n = 0;
            foreach (var def in level.SpawnLines?.Add ?? [])
            {
                if (def?.Id == null) continue;
                if (hostLines.ContainsKey(def.Id)) { issues.Add(Warn($"spawn line \"{def.Id}\": the map already has one by that name")); continue; }
                var source = hostLines.Values.FirstOrDefault(l => l.canSpawnFlying == def.Flying && l.canSpawnSmallGround == def.Ground && l.canSpawnBigGround == def.Large)
                    ?? hostLines.Values.FirstOrDefault(l => l.canSpawnSmallGround || l.canSpawnBigGround) ?? hostLines.Values.FirstOrDefault();
                if (source == null) { issues.Add(Warn($"spawn line \"{def.Id}\": {level.Host} has no spawn line to copy")); continue; }
                var line = MakeLine(source, def);
                hostLines[def.Id] = line;
                n++;
            }
            return n;
        }

        internal static EnemySpawnLine MakeLine(EnemySpawnLine source, SpawnLineAdd def)
        {
            var go = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            go.name = def.Id;
            var line = go.GetComponent<EnemySpawnLine>();
            line.canSpawnSmallGround = def.Ground;
            line.canSpawnBigGround = def.Large;
            line.canSpawnFlying = def.Flying;
            line.mostlySharedAttackpaths = [];
            line.mostlySeperatedAttackPaths = [];
            var t = go.transform;
            for (int i = t.childCount - 1; i >= 2; i--) UnityEngine.Object.DestroyImmediate(t.GetChild(i).gameObject);
            while (t.childCount < 2) new GameObject("Point").transform.SetParent(t, false);
            var center = LayoutScene.ToVector(def.Pos);
            var half = Quaternion.Euler(0f, def.RotY, 0f) * Vector3.right * (Mathf.Clamp(def.Width, 1f, 200f) / 2f);
            t.position = center;
            t.rotation = Quaternion.identity;
            t.GetChild(0).position = center - half;
            t.GetChild(1).position = center + half;
            return line;
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
