using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;

/*
    House-chain presets (docs/builder-ux.md, "Editing"): given a selection of slots and the castle, which slot needs
    which. Plain C# (ground positions as x/z), unit-tested. Every slot gets one prerequisite (the game's native single
    activator), so the result is a tree rooted at the castle and can't make a loop among the selection.

    - SelectionOrder: the first needs the castle, each next one needs the one before it.
    - NearestNeighbour: a greedy path; the slot nearest the castle needs the castle, then each next one is the
      unlinked slot nearest to the last one.
    - Branching (default): a minimum spanning tree (Prim's) over the selection plus the castle, rooted at the castle;
      each slot needs its tree parent. Fans out the way vanilla maps do.
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal enum ChainMode { Branching, NearestNeighbour, SelectionOrder }

    internal sealed class ChainOptions
    {
        public ChainMode Mode = ChainMode.Branching;
        /// <summary>The castle level the links from the castle need (1 = once it's built).</summary>
        public int CastleLevel = 1;
        /// <summary>The level of a slot its dependents wait for (0 = once it's built).</summary>
        public int LinkLevel;
    }

    internal readonly struct ChainNode(string slotRef, float x, float z)
    {
        public readonly string Ref = slotRef;
        public readonly float X = x, Z = z;
    }

    internal static class ChainPreset
    {
        public static string Label(ChainMode mode) => mode switch
        {
            ChainMode.SelectionOrder => "In selection order",
            ChainMode.NearestNeighbour => "Nearest neighbour",
            _ => "Branching",
        };

        /// <summary>Each selected slot's new requirement, in selection order (castle links as "castle").</summary>
        public static List<(string slot, SlotRequirement requires)> Build(IReadOnlyList<ChainNode> selection, float castleX, float castleZ, ChainOptions o)
        {
            var nodes = selection.GroupBy(n => n.Ref).Select(g => g.First()).ToList();
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);   // null = the castle
            switch (o.Mode)
            {
                case ChainMode.SelectionOrder:
                    for (int i = 0; i < nodes.Count; i++) parent[nodes[i].Ref] = i == 0 ? null : nodes[i - 1].Ref;
                    break;
                case ChainMode.NearestNeighbour:
                {
                    var left = new List<ChainNode>(nodes);
                    float lx = castleX, lz = castleZ;
                    string last = null;
                    while (left.Count > 0)
                    {
                        var next = left.OrderBy(n => Dist2(n.X, n.Z, lx, lz)).ThenBy(n => n.Ref, StringComparer.Ordinal).First();
                        parent[next.Ref] = last;
                        left.Remove(next);
                        last = next.Ref;
                        lx = next.X;
                        lz = next.Z;
                    }
                    break;
                }
                default:
                {
                    // Prim's: grow the tree from the castle, always adding the cheapest edge out of it.
                    var best = nodes.ToDictionary(n => n.Ref, n => (d: Dist2(n.X, n.Z, castleX, castleZ), from: (string)null), StringComparer.Ordinal);
                    var left = new List<ChainNode>(nodes);
                    while (left.Count > 0)
                    {
                        var next = left.OrderBy(n => best[n.Ref].d).ThenBy(n => n.Ref, StringComparer.Ordinal).First();
                        left.Remove(next);
                        parent[next.Ref] = best[next.Ref].from;
                        foreach (var n in left)
                        {
                            float d = Dist2(n.X, n.Z, next.X, next.Z);
                            if (d < best[n.Ref].d) best[n.Ref] = (d, next.Ref);
                        }
                    }
                    break;
                }
            }
            return nodes.Select(n => (n.Ref, parent[n.Ref] == null
                ? LevelRequirements.CastleLevel(Math.Max(1, o.CastleLevel))
                : new SlotRequirement { Slot = parent[n.Ref], Level = Math.Max(0, o.LinkLevel) })).ToList();
        }

        /// <summary>
        /// What a newly placed slot needs when auto-linking: a house needs the nearest existing house within range
        /// (else castle level 1); other kinds need castle level 1.
        /// </summary>
        public static SlotRequirement AutoLink(bool isHouse, float x, float z, IEnumerable<ChainNode> houses, float range = 25f)
        {
            if (isHouse)
            {
                var near = houses.Where(h => Dist2(h.X, h.Z, x, z) <= range * range).OrderBy(h => Dist2(h.X, h.Z, x, z)).ThenBy(h => h.Ref, StringComparer.Ordinal).ToList();
                if (near.Count > 0) return new SlotRequirement { Slot = near[0].Ref, Level = 0 };
            }
            return LevelRequirements.CastleLevel(1);
        }

        private static float Dist2(float ax, float az, float bx, float bz) => (ax - bx) * (ax - bx) + (az - bz) * (az - bz);
    }
}
