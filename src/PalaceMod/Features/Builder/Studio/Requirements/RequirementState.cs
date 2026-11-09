using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    What the requirement tools know about the open level: the host's own slots (their prerequisites, levels and
    per-level income, read once per Studio scene from the inactive host slots) and every slot's effective prerequisite
    (recomputed after each edit). Shared by the Link tool, the arrows, the inspector section and the labels.
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal static class RequirementState
    {
        private static string hostScene;
        private static HostSlots host;
        private static readonly Dictionary<string, List<int>> income = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> caps = new(StringComparer.Ordinal);
        private static Dictionary<string, SlotRequirement> effective;
        private static LevelDef effectiveFor;
        private static int effectiveVersion = -1, version;

        public static void Init()
        {
            Studio.ItemsChanged += () => version++;
            Studio.ModeChanged += _ => { version++; hostScene = null; };
        }

        /// <summary>The host's own slots (null outside the Studio).</summary>
        public static HostSlots Host
        {
            get
            {
                var level = Studio.Level;
                if (level == null) return null;
                if (host != null && hostScene == level.Host) return host;
                var scene = LayoutScene.Host(level.Host);
                if (!scene.IsValid() || !scene.isLoaded) return null;
                var slots = LayoutScene.Slots(scene);
                host = LayoutScene.HostSlots(slots);
                income.Clear();
                caps.Clear();
                foreach (var s in slots)
                {
                    if (income.ContainsKey(s.name)) continue;
                    caps[s.name] = s.RequiredRootLevelDifference;
                    income[s.name] = s.OwnUpgrades.Select(u => u?.upgradeBranches?.FirstOrDefault()?.goldIncomeChange ?? 0).ToList();
                }
                // The game's standard buildings, the bases of added kinds the host lacks ("building:mill").
                foreach (var (id, s) in BuildingPrefabs.All())
                {
                    string name = BuildingRefs.Ref(id);
                    caps[name] = s.RequiredRootLevelDifference;
                    income[name] = s.OwnUpgrades.Select(u => u?.upgradeBranches?.FirstOrDefault()?.goldIncomeChange ?? 0).ToList();
                }
                hostScene = level.Host;
                version++;
                return host;
            }
        }

        /// <summary>Every slot's prerequisite as it will play (slot names, castle by its name).</summary>
        public static Dictionary<string, SlotRequirement> Effective
        {
            get
            {
                var level = Studio.Level;
                var h = Host;
                if (level == null || h == null) return new(StringComparer.Ordinal);
                if (effective == null || effectiveVersion != version || effectiveFor != level)
                {
                    effective = LevelRequirements.Effective(level, h);
                    effectiveVersion = version;
                    effectiveFor = level;
                }
                return effective;
            }
        }

        public static void Invalidate() => version++;

        public static string Castle => Host?.Castle;
        public static string CastleKey => SlotRefs.ToKey(Castle);

        /// <summary>A Studio key as a slot name in the effective graph ("slot:X" -> "X", "add:id" stays).</summary>
        public static string Ref(string key) => SlotRefs.FromKey(key);
        public static string Key(string slotRef) => SlotRefs.ToKey(slotRef, Castle);

        /// <summary>Whether a key is a slot that can have a requirement (not the castle, an add-on or removed).</summary>
        public static bool CanNeed(string key)
        {
            var r = Ref(key);
            if (r == null || r == Castle) return false;
            var item = Studio.Find(key);
            if (item == null || item.Removed) return false;
            return !(Host?.AddOns.Contains(item.HostSlot ?? r) ?? false) || SlotRefs.IsAdded(r);
        }

        /// <summary>Whether a key can be a prerequisite (any slot on the map, the castle included).</summary>
        public static bool CanBeNeeded(string key)
        {
            var r = Ref(key);
            if (r == null) return false;
            var item = Studio.Find(key);
            return item != null && !item.Removed;
        }

        /// <summary>Upgrade levels of a slot (castle included); 1 when unknown.</summary>
        public static int Levels(string slotRef)
        {
            if (slotRef == SlotRefs.Castle) slotRef = Castle;
            var item = Studio.Find(Key(slotRef));
            string hostName = SlotRefs.IsAdded(slotRef) ? item?.HostSlot : slotRef;
            return hostName != null && Host != null && Host.Levels.TryGetValue(hostName, out int n) ? Math.Max(1, n) : Math.Max(1, item?.BaseCosts.Count ?? 1);
        }

        /// <summary>Income each level adds (index 0 = the build), from the host slot (or the base of an added one).</summary>
        public static IReadOnlyList<int> Income(string key)
        {
            var item = Studio.Find(key);
            string name = item?.HostSlot ?? Ref(key);
            _ = Host;
            return name != null && income.TryGetValue(name, out var list) ? list : [];
        }

        /// <summary>A slot's upgrade cap as it will play (the level's, else the host slot's or its base's; -100 = none).</summary>
        public static int Cap(string key)
        {
            string r = Ref(key);
            if (LevelRequirements.ExplicitCap(Studio.Level, r) is int own) return own;
            var item = Studio.Find(key);
            _ = Host;
            return caps.TryGetValue(item?.HostSlot ?? r ?? "", out int c) ? c : -100;
        }

        /// <summary>Display name of a slot ref ("Castle" for the castle).</summary>
        public static string Name(string slotRef)
        {
            if (slotRef == null) return "the start";
            if (slotRef == SlotRefs.Castle || slotRef == Castle) return "Castle";
            return Studio.Find(Key(slotRef))?.Name ?? slotRef;
        }

        /// <summary>"Castle level 2", "House 6", "House 6 at level 2", "From the start".</summary>
        public static string Describe(SlotRequirement r)
        {
            if (r?.Slot == null) return "From the start";
            if (r.Slot == SlotRefs.Castle || r.Slot == Castle) return $"Castle level {r.Level + 1}";
            return r.Level == 0 ? Name(r.Slot) : $"{Name(r.Slot)} at level {r.Level + 1}";
        }

        /// <summary>What a slot needs, with the castle level it waits for through a chain: "House 6 (castle 3 via House 6)".</summary>
        public static string DescribeFor(string key)
        {
            string r = Ref(key);
            if (r == null) return null;
            if (r == Castle) return "Castle";
            var eff = Effective;
            if (!eff.TryGetValue(r, out var req)) return null;
            string text = Describe(req);
            if (req?.Slot != null && req.Slot != Castle && LevelRequirements.CastleLevelVia(eff, Castle, r) is int via && via > 1)
                text += $" (castle {via} via {Name(req.Slot)})";
            return text;
        }

        /// <summary>The ground point of a slot ref (for arrows), or null.</summary>
        public static Vector3? Pos(string slotRef) => Studio.Find(Key(slotRef))?.Pos;
    }
}
