using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    Every change the Studio makes to the level, as one PackDraft edit (one undo step) each, however many objects it
    touches. The scene follows through StudioScene.Reconcile.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioEdits
    {
        public static void Prune(LevelDef lv) => StudioMath.Prune(lv);

        private static BuildSlotEdit SlotEdit(LevelDef lv, string name)
        {
            lv.BuildSlots ??= new BuildSlotOverlay();
            lv.BuildSlots.Edit ??= [];
            var e = lv.BuildSlots.Edit.FirstOrDefault(x => x?.Ref == name);
            if (e == null) lv.BuildSlots.Edit.Add(e = new BuildSlotEdit { Ref = name });
            return e;
        }

        public static bool Movable(StudioItem i) => i != null && i.Kind != StudioItemKind.Group && !i.Removed && !(i.Locked && i.Kind == StudioItemKind.Slot);

        /// <summary>Writes one item's pose into the level (inside an edit).</summary>
        private static void ApplyPose(LevelDef lv, StudioItem item, Vector3 pos, float yaw)
        {
            yaw = Mathf.Repeat(yaw, 360f);
            float[] p = LayoutScene.FromVector(pos);
            float ry = LayoutScene.Round(yaw);
            switch (item.Kind)
            {
                case StudioItemKind.Slot:
                {
                    var n = StudioScene.NativeFor(item);
                    if (n == null || !n.CanMove) return;
                    var e = SlotEdit(lv, item.Id);
                    bool home = (pos - n.Pos).sqrMagnitude < 0.0004f;
                    bool sameYaw = Mathf.Abs(Mathf.DeltaAngle(yaw, n.Rot.eulerAngles.y)) < 0.05f;
                    e.Pos = home ? null : p;
                    e.RotY = sameYaw ? null : ry;
                    break;
                }
                case StudioItemKind.Added:
                {
                    var a = lv.BuildSlots?.Add?.FirstOrDefault(x => x?.Id == item.Id);
                    if (a != null) { a.Pos = p; a.RotY = ry; }
                    break;
                }
                case StudioItemKind.Prop:
                {
                    var d = lv.Props?.FirstOrDefault(x => x?.Id == item.Id);
                    if (d != null) { d.Pos = p; d.RotY = ry; }
                    break;
                }
                case StudioItemKind.Line:
                {
                    var l = StudioScene.LineFor(item);
                    if (l == null) return;
                    lv.SpawnLines ??= new SpawnLineOverlay();
                    lv.SpawnLines.Edit ??= [];
                    var e = lv.SpawnLines.Edit.FirstOrDefault(x => x?.Ref == item.Id);
                    if (e == null) lv.SpawnLines.Edit.Add(e = new SpawnLineEdit { Ref = item.Id });
                    e.Pos = p;
                    e.RotY = Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)) < 0.05f ? null : LayoutScene.Round(Mathf.DeltaAngle(0f, yaw));
                    if ((pos - l.Center).sqrMagnitude < 0.0004f && e.RotY == null) lv.SpawnLines.Edit.Remove(e);
                    break;
                }
            }
        }

        /// <summary>Moves and turns items to new poses in one undo step. Locked ones are skipped with a notice.</summary>
        public static void SetPoses(IReadOnlyList<(StudioItem item, Vector3 pos, float yaw)> poses, string label, string coalesce = null)
        {
            var movable = poses.Where(p => Movable(p.item)).ToList();
            if (poses.Any(p => p.item.IsCastle)) Notify.Show("Castle stays put", NotifyKind.Info, 2f, "builder-studio");
            else if (movable.Count < poses.Count) Notify.Show("Locked pieces stay put", NotifyKind.Info, 2f, "builder-studio");
            if (movable.Count == 0) { StudioScene.RefreshMarkers(); StudioScene.Reconcile(Studio.Level); return; }
            Studio.Edit(label, lv => { foreach (var (item, pos, yaw) in movable) ApplyPose(lv, item, pos, yaw); }, coalesce);
        }

        public static string Count(int n, string one) => n == 1 ? one : $"{n} objects";

        public static void Translate(IEnumerable<StudioItem> items, Vector3 delta, string label = null, string coalesce = null)
        {
            var list = items.ToList();
            SetPoses(list.Select(i => (i, StudioScene.Ground(i.Pos + delta), i.Yaw)).ToList(), label ?? $"Move {Count(list.Count, list.FirstOrDefault()?.Name)}", coalesce);
        }

        /// <summary>Turns items about their common centre (each also turns in place).</summary>
        public static void Rotate(IEnumerable<StudioItem> items, float degrees, string coalesce = null)
        {
            var list = items.Where(Movable).ToList();
            if (list.Count == 0) { if (items.Any(i => i.IsCastle)) Notify.Show("Castle stays put", NotifyKind.Info, 2f, "builder-studio"); return; }
            var center = list.Aggregate(Vector3.zero, (a, i) => a + i.Pos) / list.Count;
            var rot = Quaternion.Euler(0f, degrees, 0f);
            SetPoses(list.Select(i => (i, list.Count == 1 ? i.Pos : StudioScene.Ground(center + rot * (i.Pos - center)), i.Yaw + degrees)).ToList(),
                $"Turn {Count(list.Count, list.First().Name)}", coalesce ?? "turn:" + string.Join(",", list.Select(i => i.Key)));
        }

        public static void ResetPose(StudioItem item)
        {
            if (item.Kind == StudioItemKind.Slot && StudioScene.NativeFor(item) is { } n) SetPoses([(item, n.Pos, n.Rot.eulerAngles.y)], $"Reset {item.Name}");
            else if (item.Kind == StudioItemKind.Line && StudioScene.LineFor(item) is { } l) SetPoses([(item, l.Center, 0f)], $"Reset {item.Name}");
        }

        // ---- Delete -------------------------------------------------------------------------------------------------

        /// <summary>Deletes items in one undo step: added slots and props go, host slots are removed from the map (or
        /// restored if they all were), spawn lines go back to the map's place. Runs the delete guards first.</summary>
        public static void Delete(IReadOnlyList<string> keys)
        {
            var items = StudioSelection.Expand(keys).Distinct().Select(StudioScene.Find).Where(i => i != null).ToList();
            if (items.Count == 0) return;
            var list = items.Select(i => i.Key).ToList();
            if (Studio.GuardDelete(list, () => DeleteNow(items))) return;
            DeleteNow(items);
        }

        private static void DeleteNow(List<StudioItem> items)
        {
            bool castle = items.Any(i => i.IsCastle);
            var targets = items.Where(i => !i.IsCastle && !(i.Kind == StudioItemKind.Slot && i.Locked)).ToList();
            if (castle) Notify.Show("The castle can't be removed", NotifyKind.Info, 2f, "builder-studio");
            if (targets.Count == 0) return;
            var hostSlots = targets.Where(i => i.Kind == StudioItemKind.Slot).ToList();
            bool restore = hostSlots.Count > 0 && hostSlots.All(i => i.Removed) && targets.All(i => i.Kind != StudioItemKind.Added && i.Kind != StudioItemKind.Prop);
            string label = targets.Count == 1
                ? (targets[0].Kind == StudioItemKind.Slot ? (restore ? "Restore " : "Remove ") : targets[0].Kind == StudioItemKind.Line ? "Reset " : "Delete ") + targets[0].Name
                : $"{(restore ? "Restore" : "Delete")} {targets.Count} objects";
            Studio.Edit(label, lv =>
            {
                foreach (var item in targets)
                {
                    switch (item.Kind)
                    {
                        case StudioItemKind.Slot:
                            lv.BuildSlots ??= new BuildSlotOverlay();
                            lv.BuildSlots.Remove ??= [];
                            if (restore) lv.BuildSlots.Remove.Remove(item.Id);
                            else if (!lv.BuildSlots.Remove.Contains(item.Id)) lv.BuildSlots.Remove.Add(item.Id);
                            break;
                        case StudioItemKind.Added:
                            LevelRequirements.ForgetSlot(lv, StudioScene.HostSlots, SlotRefs.Added(item.Id), ForgetMode.CloseUp);
                            lv.BuildSlots?.Add?.RemoveAll(a => a?.Id == item.Id);
                            break;
                        case StudioItemKind.Prop:
                            lv.Props?.RemoveAll(x => x?.Id == item.Id);
                            break;
                        case StudioItemKind.Line:
                            lv.SpawnLines?.Edit?.RemoveAll(e => e?.Ref == item.Id);
                            break;
                    }
                }
            });
        }

        // ---- Add ----------------------------------------------------------------------------------------------------

        public static string UniqueId(string prefix, LevelDef lv, ISet<string> extra = null)
        {
            var taken = new HashSet<string>((lv.BuildSlots?.Add ?? []).Select(a => a?.Id).Concat((lv.Props ?? []).Select(p => p?.Id)).Concat((lv.Groups ?? []).Select(g => g?.Id)).Where(x => x != null));
            if (extra != null) taken.UnionWith(extra);
            for (int i = 1; ; i++) if (!taken.Contains($"{prefix}-{i}")) return $"{prefix}-{i}";
        }

        public static string AddSlot(string baseName, Vector3 pos, float yaw)
        {
            var n = StudioScene.Native(baseName);
            if (n == null || !n.CanBase) return null;
            string id = null;
            Studio.Edit($"Add {n.Type}", lv =>
            {
                lv.BuildSlots ??= new BuildSlotOverlay();
                lv.BuildSlots.Add ??= [];
                id = UniqueId("slot", lv);
                var add = new BuildSlotAdd { Id = id, Base = "scene:" + baseName, Pos = LayoutScene.FromVector(pos), RotY = LayoutScene.Round(Mathf.Repeat(yaw, 360f)) };
                // On an empty base the slot it copies is gone, so it would never unlock: it needs the castle instead.
                if (LevelBases.IsEmpty(lv)) add.Requires = LevelRequirements.CastleLevel(1);
                lv.BuildSlots.Add.Add(add);
                Studio.RunPlaceHooks(lv, "add:" + id);
            });
            if (id != null) Studio.RaiseItemPlaced("add:" + id);
            return id == null ? null : "add:" + id;
        }

        public static string AddProp(string baseRef, string name, Vector3 pos, float yaw, float scale)
        {
            string id = null;
            Studio.Edit($"Add {name?.ToLowerInvariant() ?? "scenery"}", lv =>
            {
                lv.Props ??= [];
                id = UniqueId("prop", lv);
                lv.Props.Add(new PropDef { Id = id, Base = baseRef, Pos = LayoutScene.FromVector(pos), RotY = LayoutScene.Round(Mathf.Repeat(yaw, 360f)), Scale = scale });
                Studio.RunPlaceHooks(lv, "prop:" + id);
            });
            if (id != null) Studio.RaiseItemPlaced("prop:" + id);
            return id == null ? null : "prop:" + id;
        }

        /// <summary>Some scenery is huge (cliff rocks): new props start at most about 6 m across.</summary>
        public static float StartScale(PropBase b)
        {
            var size = Vector3.Scale(b.Mesh.bounds.size, b.Scale);
            float fit = Mathf.Min(1f, 6f / Mathf.Max(0.01f, Mathf.Max(size.x, size.y, size.z)));
            return Mathf.Clamp(Mathf.Round(fit * 10f) / 10f, 0.3f, 1f);
        }

        /// <summary>The slot of a kind nearest a spot, to base a copy on (it unlocks along with its neighbours).</summary>
        public static string NearestBase(string type, Vector3 near) =>
            StudioScene.Natives.Where(n => n.CanBase && n.Type == type).OrderBy(n => (n.Pos - near).sqrMagnitude).FirstOrDefault()?.Name;

        // ---- Copy, paste, duplicate -------------------------------------------------------------------------------

        /// <summary>What's on the clipboard: slots as their base and props as their definition, relative to a centre.</summary>
        private sealed class Clip
        {
            public string Base;      // host slot name (slots) or PropDef base (props)
            public bool IsProp;
            public Vector3 Offset;
            public float Yaw, Scale = 1f;
            public bool Solid = true;
            public List<UpgradeEdit> Upgrades;
            public SlotRequirement Requires;
            public int? UpgradeCap;
        }

        private static List<Clip> clipboard = [];

        public static int ClipboardCount => clipboard.Count;

        private static List<Clip> Snapshot(IEnumerable<StudioItem> items, out Vector3 center)
        {
            var list = items.Where(i => i.Kind is StudioItemKind.Slot or StudioItemKind.Added or StudioItemKind.Prop).ToList();
            center = list.Count == 0 ? Vector3.zero : list.Aggregate(Vector3.zero, (a, i) => a + i.Pos) / list.Count;
            var lv = Studio.Level;
            var clips = new List<Clip>();
            foreach (var i in list)
            {
                if (i.Kind == StudioItemKind.Prop)
                {
                    var d = lv?.Props?.FirstOrDefault(p => p?.Id == i.Id);
                    if (d == null) continue;
                    clips.Add(new Clip { Base = d.Base, IsProp = true, Offset = i.Pos - center, Yaw = i.Yaw, Scale = d.Scale, Solid = d.Solid });
                }
                else
                {
                    var n = StudioScene.NativeFor(i);
                    if (n == null || !n.CanBase) continue;
                    var edit = i.Kind == StudioItemKind.Slot ? lv?.BuildSlots?.Edit?.FirstOrDefault(e => e?.Ref == i.Id) : null;
                    var add = i.Kind == StudioItemKind.Added ? lv?.BuildSlots?.Add?.FirstOrDefault(a => a?.Id == i.Id) : null;
                    var ups = edit?.Upgrades ?? add?.Upgrades;
                    clips.Add(new Clip
                    {
                        Base = n.Name, Offset = i.Pos - center, Yaw = i.Yaw,
                        Upgrades = ups?.Select(u => u == null ? null : new UpgradeEdit { Cost = u.Cost }).ToList(),
                        Requires = (edit?.Requires ?? add?.Requires)?.Copy(),
                        UpgradeCap = edit?.UpgradeCap ?? add?.UpgradeCap,
                    });
                }
            }
            return clips;
        }

        public static int Copy(IEnumerable<StudioItem> items)
        {
            var clips = Snapshot(items, out _);
            if (clips.Count > 0) clipboard = clips;
            return clips.Count;
        }

        /// <summary>Pastes the clipboard (or given clips) around a point; returns the new keys.</summary>
        public static List<string> Paste(Vector3 at) => PasteClips(clipboard, at, "Paste");

        private static List<string> PasteClips(List<Clip> clips, Vector3 at, string verb)
        {
            var keys = new List<string>();
            if (clips.Count == 0) return keys;
            Studio.Edit($"{verb} {(clips.Count == 1 ? "1 object" : $"{clips.Count} objects")}", lv =>
            {
                keys.Clear();
                var made = new HashSet<string>();
                foreach (var c in clips)
                {
                    var pos = StudioScene.Ground(at + c.Offset);
                    if (c.IsProp)
                    {
                        lv.Props ??= [];
                        string id = UniqueId("prop", lv, made);
                        made.Add(id);
                        lv.Props.Add(new PropDef { Id = id, Base = c.Base, Pos = LayoutScene.FromVector(pos), RotY = LayoutScene.Round(Mathf.Repeat(c.Yaw, 360f)), Scale = c.Scale, Solid = c.Solid });
                        keys.Add("prop:" + id);
                    }
                    else
                    {
                        lv.BuildSlots ??= new BuildSlotOverlay();
                        lv.BuildSlots.Add ??= [];
                        string id = UniqueId("slot", lv, made);
                        made.Add(id);
                        var requires = c.Requires?.Copy() ?? (LevelBases.IsEmpty(lv) ? LevelRequirements.CastleLevel(1) : null);
                        lv.BuildSlots.Add.Add(new BuildSlotAdd { Id = id, Base = "scene:" + c.Base, Pos = LayoutScene.FromVector(pos), RotY = LayoutScene.Round(Mathf.Repeat(c.Yaw, 360f)), Upgrades = c.Upgrades, Requires = requires, UpgradeCap = c.UpgradeCap });
                        keys.Add("add:" + id);
                    }
                    Studio.RunPlaceHooks(lv, keys[keys.Count - 1]);
                }
            });
            foreach (var k in keys) Studio.RaiseItemPlaced(k);
            return keys;
        }

        /// <summary>Copies the items in place (offset 2 m) and returns the copies' keys.</summary>
        public static List<string> Duplicate(IEnumerable<StudioItem> items)
        {
            var clips = Snapshot(items, out var center);
            if (clips.Count == 0) return [];
            return PasteClips(clips, center + new Vector3(2f, 0f, -2f), "Duplicate");
        }

        // ---- Costs and props ------------------------------------------------------------------------------------

        /// <summary>A level's cost override for an item, or null for the base value.</summary>
        public static int? CostOverride(StudioItem item, int level)
        {
            var lv = Studio.Level;
            var list = item.Kind == StudioItemKind.Slot ? lv?.BuildSlots?.Edit?.FirstOrDefault(e => e?.Ref == item.Id)?.Upgrades
                : lv?.BuildSlots?.Add?.FirstOrDefault(a => a?.Id == item.Id)?.Upgrades;
            return list != null && level < list.Count ? list[level]?.Cost : null;
        }

        public static int Cost(StudioItem item, int level) => CostOverride(item, level) ?? (level < item.BaseCosts.Count ? item.BaseCosts[level] : 0);

        /// <summary>Sets one level's cost on several slots (null = back to each one's map value).</summary>
        public static void SetCost(IReadOnlyList<StudioItem> items, int level, int? cost)
        {
            var slots = items.Where(i => (i.Kind is StudioItemKind.Slot or StudioItemKind.Added) && i.BaseCosts.Count > level).ToList();
            if (slots.Count == 0) return;
            string what = level == 0 ? "build cost" : $"upgrade {level} cost";
            string label = slots.Count == 1 ? $"Set {what}" : $"Set {what} on {slots.Count} slots";
            Studio.Edit(label, lv =>
            {
                foreach (var item in slots)
                {
                    List<UpgradeEdit> list;
                    if (item.Kind == StudioItemKind.Slot) list = SlotEdit(lv, item.Id).Upgrades ??= [];
                    else
                    {
                        var a = lv.BuildSlots?.Add?.FirstOrDefault(x => x?.Id == item.Id);
                        if (a == null) continue;
                        list = a.Upgrades ??= [];
                    }
                    while (list.Count <= level) list.Add(null);
                    int? c = cost != null && cost == item.BaseCosts[level] ? null : cost;
                    if (c == null) { if (list[level] != null) list[level].Cost = null; if (list[level] != null && (list[level].Extra?.Count ?? 0) == 0) list[level] = null; }
                    else (list[level] ??= new UpgradeEdit()).Cost = c;
                    while (list.Count > 0 && list[list.Count - 1] == null) list.RemoveAt(list.Count - 1);
                }
            }, coalesce: $"cost:{level}:{string.Join(",", slots.Select(s => s.Key))}");
        }

        public static PropDef PropDef(StudioItem item) => Studio.Level?.Props?.FirstOrDefault(p => p?.Id == item.Id);

        public static void SetProps(IReadOnlyList<StudioItem> items, string label, Action<PropDef> change, string coalesce = null)
        {
            var ids = new HashSet<string>(items.Where(i => i.Kind == StudioItemKind.Prop).Select(i => i.Id));
            if (ids.Count == 0) return;
            Studio.Edit(ids.Count == 1 ? label : $"{label} ({ids.Count})", lv => { foreach (var d in lv.Props ?? []) if (d?.Id != null && ids.Contains(d.Id)) change(d); }, coalesce);
        }

        // ---- Groups ---------------------------------------------------------------------------------------------

        public static string Group(IReadOnlyList<string> keys)
        {
            var members = keys.Where(k => StudioScene.Find(k) is { Kind: not StudioItemKind.Group }).Distinct().ToList();
            if (members.Count < 2) { Notify.Show("Select at least 2 objects to group", NotifyKind.Info, 2f, "builder-studio"); return null; }
            string id = null;
            Studio.Edit($"Group {members.Count} objects", lv =>
            {
                id = StudioMath.Group(lv, members, UniqueId("g", lv));
            });
            return id == null ? null : "group:" + id;
        }

        public static void Ungroup(IReadOnlyList<string> keys)
        {
            var groups = keys.Select(StudioScene.GroupOf).Where(g => g != null).Distinct().Select(g => g.Substring("group:".Length)).ToList();
            if (groups.Count == 0) return;
            Studio.Edit(groups.Count == 1 ? "Ungroup" : $"Ungroup {groups.Count} groups", lv => lv.Groups?.RemoveAll(g => groups.Contains(g?.Id)));
        }

        public static void RenameGroup(string groupId, string name) =>
            Studio.Edit("Rename group", lv => { var g = lv.Groups?.FirstOrDefault(x => x?.Id == groupId); if (g != null) g.Name = name; });
    }
}
