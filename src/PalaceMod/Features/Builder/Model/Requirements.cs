using System;
using System.Collections.Generic;
using System.Linq;

/*
    Slot requirements as data (docs/builder-ux.md, "Requirements"). Plain C#, no Unity: the overlay, the Studio's
    requirement tools and the tests share it.

    A slot is named by a "slot ref": the BuildSlot's name on the host, "add:<id>" for an added slot, or "castle" for the
    host's castle slot. Studio selection keys are "slot:<name>" and "add:<id>" (SlotRefs.FromKey / ToKey).
*/
namespace PalaceMod.Builder.Model
{
    public static class SlotRefs
    {
        public const string Castle = "castle";
        public const string AddPrefix = "add:";
        public const string SlotKeyPrefix = "slot:";

        public static string Added(string id) => AddPrefix + id;
        public static bool IsAdded(string slotRef) => slotRef != null && slotRef.StartsWith(AddPrefix, StringComparison.Ordinal);
        public static string AddedId(string slotRef) => IsAdded(slotRef) ? slotRef.Substring(AddPrefix.Length) : null;

        /// <summary>A Studio selection key ("slot:&lt;name&gt;", "add:&lt;id&gt;") as a slot ref, or null for other kinds.</summary>
        public static string FromKey(string key)
        {
            if (key == null) return null;
            if (key.StartsWith(SlotKeyPrefix, StringComparison.Ordinal)) return key.Substring(SlotKeyPrefix.Length);
            return IsAdded(key) ? key : null;
        }

        /// <summary>The Studio selection key of a slot ref ("castle" needs the castle's host name).</summary>
        public static string ToKey(string slotRef, string castleName = null)
        {
            if (slotRef == Castle) slotRef = castleName;
            if (slotRef == null) return null;
            return IsAdded(slotRef) ? slotRef : SlotKeyPrefix + slotRef;
        }
    }

    /// <summary>The level's starting map: as it is, cleared, or a flat field.</summary>
    public static class LevelBases
    {
        public const string Cleared = "cleared";
        public const string Flat = "flat";

        public static bool IsEmpty(LevelDef level) => level?.Base == Cleared || level?.Base == Flat;
        public static bool IsKnown(string b) => b == null || b == Cleared || b == Flat;
    }

    /// <summary>What the requirement code needs to know about the host's own slots (the overlay and the Studio fill it
    /// from the loaded scene; tests fill it by hand).</summary>
    public sealed class HostSlots
    {
        /// <summary>The castle slot's name on the host.</summary>
        public string Castle;
        /// <summary>Every host slot's own prerequisite (activator name + level), or null for "from the start".</summary>
        public Dictionary<string, SlotRequirement> Native = new(StringComparer.Ordinal);
        /// <summary>Upgrade levels each slot has (castle included); missing = unknown.</summary>
        public Dictionary<string, int> Levels = new(StringComparer.Ordinal);
        /// <summary>Add-ons built and upgraded through their activator (a mill's fields, wall pieces).</summary>
        public HashSet<string> AddOns = new(StringComparer.Ordinal);
    }

    public enum ForgetMode
    {
        /// <summary>Dependents of the removed slot now need what it needed (the chain closes up).</summary>
        CloseUp,
        /// <summary>Dependents become available from the start.</summary>
        FromStart,
    }

    public static class LevelRequirements
    {
        /// <summary>Castle level N (1 = once the castle is built) as a requirement.</summary>
        public static SlotRequirement CastleLevel(int n) => new() { Slot = SlotRefs.Castle, Level = Math.Max(0, n - 1) };

        public static readonly SlotRequirement FromStart = new() { Slot = null, Level = 0 };

        // ---- Reading and writing one slot ----------------------------------------------------------------------

        public static BuildSlotEdit FindEdit(LevelDef l, string slotRef) => l?.BuildSlots?.Edit?.FirstOrDefault(e => e?.Ref == slotRef);
        public static BuildSlotAdd FindAdd(LevelDef l, string slotRef)
        {
            string id = SlotRefs.AddedId(slotRef);
            return id == null ? null : l?.BuildSlots?.Add?.FirstOrDefault(a => a?.Id == id);
        }

        /// <summary>The level's own requirement for a slot, or null when it keeps the host's (or its base's).</summary>
        public static SlotRequirement Explicit(LevelDef l, string slotRef) =>
            SlotRefs.IsAdded(slotRef) ? FindAdd(l, slotRef)?.Requires : FindEdit(l, slotRef)?.Requires;

        public static int? ExplicitCap(LevelDef l, string slotRef) =>
            SlotRefs.IsAdded(slotRef) ? FindAdd(l, slotRef)?.UpgradeCap : FindEdit(l, slotRef)?.UpgradeCap;

        /// <summary>Sets a slot's requirement; null goes back to the host's (or its base's).</summary>
        public static void Set(LevelDef l, string slotRef, SlotRequirement r)
        {
            r = r?.Copy();
            if (SlotRefs.IsAdded(slotRef)) { var a = FindAdd(l, slotRef); if (a != null) a.Requires = r; return; }
            var e = EnsureEdit(l, slotRef, r != null);
            if (e == null) return;
            e.Requires = r;
            PruneEdit(l, e);
        }

        /// <summary>Sets a slot's upgrade cap (requiredRootLevelDifference); null goes back to the host's.</summary>
        public static void SetCap(LevelDef l, string slotRef, int? cap)
        {
            if (SlotRefs.IsAdded(slotRef)) { var a = FindAdd(l, slotRef); if (a != null) a.UpgradeCap = cap; return; }
            var e = EnsureEdit(l, slotRef, cap != null);
            if (e == null) return;
            e.UpgradeCap = cap;
            PruneEdit(l, e);
        }

        private static BuildSlotEdit EnsureEdit(LevelDef l, string name, bool create)
        {
            var e = FindEdit(l, name);
            if (e != null || !create) return e;
            l.BuildSlots ??= new BuildSlotOverlay();
            l.BuildSlots.Edit ??= [];
            l.BuildSlots.Edit.Add(e = new BuildSlotEdit { Ref = name });
            return e;
        }

        private static void PruneEdit(LevelDef l, BuildSlotEdit e)
        {
            if (e.Pos != null || e.RotY != null || e.Requires != null || e.UpgradeCap != null || (e.Extra?.Count ?? 0) > 0) return;
            if (e.Upgrades != null && e.Upgrades.Any(u => u != null && (u.Cost != null || (u.Extra?.Count ?? 0) > 0))) return;
            l.BuildSlots.Edit.Remove(e);
        }

        // ---- The whole level ------------------------------------------------------------------------------------

        /// <summary>Whether the level removes a host slot (listed in remove, or cleared away by an empty base).</summary>
        public static bool IsRemoved(LevelDef l, HostSlots host, string name)
        {
            if (name == null || SlotRefs.IsAdded(name)) return false;
            if (name == SlotRefs.Castle || name == host?.Castle) return false;
            return LevelBases.IsEmpty(l) || (l.BuildSlots?.Remove?.Contains(name) ?? false);
        }

        /// <summary>
        /// Every slot's prerequisite as it will play: host slots (minus removed ones) and added ones, with the level's
        /// requirements applied, "castle" resolved to the castle's name, and refs to slots that aren't there dropped
        /// (such a slot never appears in play; Problems reports it). An added slot without its own requirement takes
        /// its base's; on an empty base it needs castle level 1 instead (its base's prerequisite is gone).
        /// </summary>
        public static Dictionary<string, SlotRequirement> Effective(LevelDef l, HostSlots host, bool keepMissing = false)
        {
            var map = new Dictionary<string, SlotRequirement>(StringComparer.Ordinal);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in host.Native)
                if (!IsRemoved(l, host, kv.Key)) present.Add(kv.Key);
            if (host.Castle != null) present.Add(host.Castle);
            foreach (var a in l.BuildSlots?.Add ?? [])
                if (a?.Id != null) present.Add(SlotRefs.Added(a.Id));

            foreach (var name in present)
            {
                SlotRequirement r;
                if (SlotRefs.IsAdded(name))
                {
                    var a = FindAdd(l, name);
                    r = a.Requires;
                    if (r == null)
                    {
                        string baseName = ContentRef.Parse(a.Base ?? "").Id;
                        // A standard building (building:<kind>) has no map prerequisite of its own: castle level 1.
                        r = LevelBases.IsEmpty(l) || BuildingRefs.IsBuilding(a.Base) ? CastleLevel(1)
                            : baseName != null && host.Native.TryGetValue(baseName, out var nb) ? nb : null;
                    }
                }
                else
                {
                    r = name == host.Castle ? null : FindEdit(l, name)?.Requires ?? (host.Native.TryGetValue(name, out var n) ? n : null);
                }
                if (r?.Slot != null)
                {
                    string target = r.Slot == SlotRefs.Castle ? host.Castle : r.Slot;
                    if (target == null || (!present.Contains(target) && !keepMissing)) r = null;
                    else r = new SlotRequirement { Slot = target, Level = r.Level };
                }
                map[name] = r?.Slot == null ? null : r;
            }
            return map;
        }

        /// <summary>The slots whose prerequisite is slot (directly).</summary>
        public static List<string> Dependents(Dictionary<string, SlotRequirement> effective, string slot) =>
            effective.Where(kv => kv.Value?.Slot == slot).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        /// <summary>The chain from a slot up to its root (the slot itself first). Stops at a repeat (a cycle).</summary>
        public static List<string> ChainUp(Dictionary<string, SlotRequirement> effective, string slot)
        {
            var chain = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (string s = slot; s != null && seen.Add(s); s = effective.TryGetValue(s, out var r) ? r?.Slot : null) chain.Add(s);
            return chain;
        }

        /// <summary>Whether making dependent need prereq would close a loop.</summary>
        public static bool WouldCycle(Dictionary<string, SlotRequirement> effective, string dependent, string prereq) =>
            prereq != null && ChainUp(effective, prereq).Contains(dependent);

        /// <summary>Slots that sit on a requirement loop (each loop once, as its members).</summary>
        public static List<List<string>> Cycles(Dictionary<string, SlotRequirement> effective)
        {
            var loops = new List<List<string>>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in effective.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (done.Contains(start)) continue;
                var path = new List<string>();
                var index = new Dictionary<string, int>(StringComparer.Ordinal);
                string s = start;
                while (s != null && !done.Contains(s) && !index.ContainsKey(s))
                {
                    index[s] = path.Count;
                    path.Add(s);
                    s = effective.TryGetValue(s, out var r) ? r?.Slot : null;
                }
                if (s != null && index.TryGetValue(s, out int at)) loops.Add(path.Skip(at).ToList());
                foreach (var p in path) done.Add(p);
            }
            return loops;
        }

        /// <summary>The castle level a slot effectively waits for through its chain (1 = once the castle is built), or
        /// null when its chain doesn't reach the castle.</summary>
        public static int? CastleLevelVia(Dictionary<string, SlotRequirement> effective, string castle, string slot)
        {
            var chain = ChainUp(effective, slot);
            if (chain.Count < 2 || chain[chain.Count - 1] != castle) return null;
            var last = effective[chain[chain.Count - 2]];
            return last.Level + 1;
        }

        /// <summary>
        /// Before removing a slot: rewires the slots that need it. CloseUp gives them what the removed slot needed
        /// (castle level or its prerequisite, at the same level), FromStart makes them available from the start.
        /// Returns how many slots changed.
        /// </summary>
        public static int ForgetSlot(LevelDef l, HostSlots host, string slotRef, ForgetMode mode)
        {
            var eff = Effective(l, host, keepMissing: true);
            string name = slotRef == SlotRefs.Castle ? host.Castle : slotRef;
            eff.TryGetValue(name ?? "", out var own);
            int n = 0;
            foreach (var dep in Dependents(eff, name))
            {
                SlotRequirement r = mode == ForgetMode.FromStart || own == null ? FromStart.Copy()
                    : new SlotRequirement { Slot = own.Slot == host.Castle ? SlotRefs.Castle : own.Slot, Level = own.Level };
                Set(l, dep, r);
                n++;
            }
            return n;
        }

        /// <summary>The slot ref to write for a slot name (the castle as "castle").</summary>
        public static string RefFor(HostSlots host, string name) => name != null && name == host?.Castle ? SlotRefs.Castle : name;
    }
}
