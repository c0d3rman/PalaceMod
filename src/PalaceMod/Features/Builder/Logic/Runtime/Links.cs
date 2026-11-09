using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/*
    Entity links (docs/builder-engine.md, "Links"): any entity can hang on another as a part. The relation is the
    core's (pure C#, tested with the fake world): `.parent`, `.children`, `.root`, `.group`, the `link`/`unlink`
    actions, the `linked`/`unlinked` events, what happens to the parts when the parent dies, and how a part's damage
    is shared. The game side (Logic/Bodies/Links) moves rigid parts with their parent every frame, applies the damage
    shares inside the game's own damage call, and builds a template's `parts`.

    Link fields (the `link` action's params, a `parts[]` entry's fields):
      offset [x, y, z]     where it sits in the parent's frame (x right, y up, z forward); default: where it is now
      rotY                 its yaw relative to the parent's; default: as it is now
      follow               rigid (moves and turns with the parent) | position (moves, keeps its own yaw) |
                           none (a relation only: it moves on its own, e.g. a `follow` movement)
      onParentDeath        die (killed with it) | drop (unlinked, set down on the ground) | stay (unlinked, left where it is)
      damageToParent       this much of every hit it takes is dealt to its parent too (2 = double; a weak point)
      damageToGroup        this fraction of every hit is shared evenly by its whole linked group, itself included
                           (1 = one shared pool)
      damageKept           the fraction it takes itself, before the group share (default 1)
*/
namespace PalaceMod.Builder.Logic
{
    public sealed class LinkSpec
    {
        public const string Rigid = "rigid", PositionOnly = "position", None = "none";
        public const string Die = "die", Drop = "drop", Stay = "stay";
        public static readonly string[] Follows = [Rigid, PositionOnly, None];
        public static readonly string[] DeathPolicies = [Die, Drop, Stay];

        public Vec Offset;
        public float RotY;
        public string Follow = Rigid;
        public string OnParentDeath = Die;
        public double ToParent, ToGroup, Kept = 1;
        /// <summary>Made by its parent template's `parts` (rebuilt with the parent, not saved by savestates).</summary>
        public bool FromParts;

        public bool Moves => Follow != None;
        public bool SharesDamage => ToParent > 0 || ToGroup > 0 || Kept != 1;

        public JObject ToJson() => new()
        {
            ["offset"] = new JArray(Offset.X, Offset.Y, Offset.Z), ["rotY"] = RotY, ["follow"] = Follow, ["onParentDeath"] = OnParentDeath,
            ["damageToParent"] = ToParent, ["damageToGroup"] = ToGroup, ["damageKept"] = Kept,
        };

        public static LinkSpec FromJson(JObject o)
        {
            var s = new LinkSpec();
            if (o == null) return s;
            if (o["offset"] is JArray a && a.Count == 3) s.Offset = new Vec((float)a[0], (float)a[1], (float)a[2]);
            s.RotY = (float?)o["rotY"] ?? 0;
            s.Follow = (string)o["follow"] ?? Rigid;
            s.OnParentDeath = (string)o["onParentDeath"] ?? Die;
            s.ToParent = (double?)o["damageToParent"] ?? 0;
            s.ToGroup = (double?)o["damageToGroup"] ?? 0;
            s.Kept = (double?)o["damageKept"] ?? 1;
            return s;
        }
    }

    /// <summary>Services the game side installs for links (null in tests: the core then only unlinks).</summary>
    public static class LinkServices
    {
        /// <summary>Kill a part whose parent died (onParentDeath: die), crediting the parent's killer if known.</summary>
        public static Action<LogicEntity, LogicEntity> Kill;
        /// <summary>A part was unlinked (drop: set it down on the ground; its own movement resumes).</summary>
        public static Action<LogicEntity, string> Released;
        /// <summary>The offset and relative yaw of <c>child</c> in <c>parent</c>'s frame now (for "keep where it is").</summary>
        public static Func<LogicEntity, LogicEntity, (Vec offset, float rotY)> Relative;
    }

    public sealed partial class LogicWorld
    {
        /// <summary>Every entity that has a parent, in link order (the game side places the rigid ones each frame).</summary>
        public readonly List<LogicEntity> Linked = [];

        /// <summary>Link <paramref name="child"/> to <paramref name="parent"/> (re-linking moves it). Throws on a cycle.</summary>
        public void LinkTo(LogicEntity child, LogicEntity parent, LinkSpec spec, int depth = 0)
        {
            if (child == null || parent == null) return;
            if (child == parent) throw new LogicError("link: an entity can't be linked to itself");
            if (child.Kind is "level" or "king" or "castle" or "building") throw new LogicError($"link: a {child.Kind} can't be a part");
            for (var p = parent; p != null; p = p.Parent)
                if (p == child) throw new LogicError($"link: {parent.Id} is already linked under {child.Id}");
            if (child.Parent != null) Unlink(child, null, depth);
            child.Parent = parent;
            child.Link = spec ?? new LinkSpec();
            parent.Children.Add(child);
            Linked.Add(child);
            Raise("linked", child, parent, 0, null, null, depth);
        }

        /// <summary>Unlink a part. <paramref name="release"/>: drop | stay (what the game side does with it), null = stay.</summary>
        public void Unlink(LogicEntity child, string release, int depth = 0)
        {
            var parent = child?.Parent;
            if (parent == null) return;
            parent.Children.Remove(child);
            Linked.Remove(child);
            child.Parent = null;
            child.Link = null;
            LinkServices.Released?.Invoke(child, release ?? LinkSpec.Stay);
            Raise("unlinked", child, parent, 0, null, null, depth);
        }

        /// <summary>A parent died or is gone: each part follows its onParentDeath.</summary>
        public void ParentDied(LogicEntity parent, LogicEntity killer = null)
        {
            if (parent == null || parent.Children.Count == 0) return;
            foreach (var c in parent.Children.ToArray())
            {
                string policy = c.Link?.OnParentDeath ?? LinkSpec.Die;
                if (policy == LinkSpec.Die)
                {
                    Unlink(c, LinkSpec.Stay);
                    if (LinkServices.Kill != null) LinkServices.Kill(c, killer);
                    else if (LogicServices.Factory != null && c.Exists) LogicServices.Factory.Despawn(c, true, false);
                }
                else Unlink(c, policy);
            }
        }

        /// <summary>The top of its link tree (itself when unlinked).</summary>
        public static LogicEntity RootOf(LogicEntity e)
        {
            int guard = 0;
            while (e?.Parent != null && guard++ < 64) e = e.Parent;
            return e;
        }

        /// <summary>Its whole linked group: the root and every descendant, root first (breadth-first), itself included.</summary>
        public static List<LogicEntity> GroupOf(LogicEntity e, bool aliveOnly = false)
        {
            var list = new List<LogicEntity>();
            var root = RootOf(e);
            if (root == null) return list;
            var queue = new Queue<LogicEntity>();
            queue.Enqueue(root);
            while (queue.Count > 0 && list.Count < 256)
            {
                var x = queue.Dequeue();
                if (!aliveOnly || x.Alive) list.Add(x);
                foreach (var c in x.Children) queue.Enqueue(c);
            }
            return list;
        }

        /// <summary>
        /// How one hit of <paramref name="amount"/> on <paramref name="victim"/> is shared: what it keeps, what its
        /// parent takes, and what each other member of its group takes. Pure, so the tests and the game agree.
        /// </summary>
        public static (double kept, double toParent, double perOther, List<LogicEntity> others) ShareDamage(LogicEntity victim, double amount)
        {
            var s = victim?.Link;
            if (s == null || amount <= 0) return (amount, 0, 0, null);
            double toParent = victim.Parent != null && victim.Parent.Alive ? amount * Math.Max(0, s.ToParent) : 0;
            double g = Math.Max(0, Math.Min(1, s.ToGroup));
            double kept = amount * Math.Max(0, s.Kept);
            if (g <= 0) return (kept, toParent, 0, null);
            var group = GroupOf(victim, aliveOnly: true);
            if (!group.Contains(victim)) group.Add(victim);
            double each = amount * g / group.Count;
            kept = kept * (1 - g) + each;
            group.Remove(victim);
            return (kept, toParent, each, group);
        }

        /// <summary>Parse the link fields of an action or a `parts[]` entry. "Keep where it is" needs the game side.</summary>
        public static LinkSpec ReadLinkSpec(ActionParams p, LogicEntity child, LogicEntity parent)
        {
            var s = new LinkSpec
            {
                Follow = p.Text("follow") ?? LinkSpec.Rigid,
                OnParentDeath = p.Text("onParentDeath") ?? LinkSpec.Die,
                ToParent = p.Number("damageToParent", 0),
                ToGroup = p.Number("damageToGroup", 0),
            };
            s.Kept = p.Number("damageKept", 1);
            if (Array.IndexOf(LinkSpec.Follows, s.Follow) < 0) throw new LogicError($"link: follow \"{s.Follow}\": expected rigid, position or none");
            if (Array.IndexOf(LinkSpec.DeathPolicies, s.OnParentDeath) < 0) throw new LogicError($"link: onParentDeath \"{s.OnParentDeath}\": expected die, drop or stay");
            var rel = LinkServices.Relative?.Invoke(child, parent) ?? (child.Position - parent.Position, 0f);
            s.Offset = p.Has("offset") ? Values.ToPosition(p.Get("offset")) ?? rel.offset : rel.offset;
            s.RotY = p.Has("rotY") ? (float)p.Number("rotY") : rel.rotY;
            return s;
        }
    }

    internal static class LinkActions
    {
        public static void Register()
        {
            LogicActions.Register("link", (ctx, p) =>
            {
                var parent = p.Entity("to") ?? throw new LogicError("link: `to` is missing or none");
                var child = p.Target;
                if (child == null || !child.Exists || !parent.Exists) return;
                ctx.World.LinkTo(child, parent, LogicWorld.ReadLinkSpec(p, child, parent), ctx.Depth + 1);
            }, ActionFlags.PerTarget);
            LogicActions.Register("unlink", (ctx, p) =>
            {
                string release = p.Text("release") ?? LinkSpec.Drop;
                if (release is not (LinkSpec.Drop or LinkSpec.Stay)) throw new LogicError($"unlink: release \"{release}\": expected drop or stay");
                var t = p.Target;
                if (p.Bool("children"))
                {
                    foreach (var c in t.Children.ToArray()) ctx.World.Unlink(c, release, ctx.Depth + 1);
                    return;
                }
                ctx.World.Unlink(t, release, ctx.Depth + 1);
            }, ActionFlags.PerTarget);
        }
    }
}
