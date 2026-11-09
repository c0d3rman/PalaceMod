using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;

namespace PalaceMod.Tests
{
    /// <summary>Entity links in the logic core (Runtime/Links.cs): the relation, its selectors, death and damage policy.</summary>
    public static class LinkTests
    {
        private static LogicWorld NewWorld(string logicJson = "{}")
        {
            var w = new LogicWorld();
            LogicWorld.Current = w;
            LogicServices.Factory = new FakeFactory();
            LogicServices.Game = new FakeGame();
            LogicServices.Spatial = null;
            LogicServices.Stats = null;
            LogicServices.Areas = null;
            LinkServices.Kill = null;
            LinkServices.Released = null;
            LinkServices.Relative = null;
            w.Load(JObject.Parse(logicJson), null, null, null, "levels[0]");
            return w;
        }

        private static LogicEntity Place(LogicWorld w, string id, Vec pos, string kind = "unit", string rules = null)
        {
            var place = JObject.Parse($$"""{ "id": "{{id}}", "kind": "{{kind}}", "team": "enemy" }""");
            if (rules != null) place["rules"] = JArray.Parse(rules);
            var e = w.Describe(null, place, id, "enemy", null);
            e.Kind = kind;
            e.Body = new FakeBody { Position = pos };
            w.Add(e);
            return e;
        }

        private static Value E(LogicWorld w, LogicEntity self, string expr) => ExprCompiler.Compile(expr, "test")(new LogicContext { World = w, Self = self });

        private static void Do(LogicWorld w, LogicEntity self, string actionJson)
        {
            var list = ActionCompiler.CompileList(JArray.Parse("[" + actionJson + "]"), "test");
            new LogicContext { World = w, Self = self }.Run(list);
        }

        public static void LinkActionBuildsTheTreeAndSelectorsSeeIt()
        {
            var w = NewWorld();
            var head = Place(w, "head", new Vec(0, 0, 0));
            var a = Place(w, "a", new Vec(2, 0, 0));
            var b = Place(w, "b", new Vec(4, 0, 0));
            Do(w, head, """{ "do": "link", "target": "@a", "to": "self", "offset": [2, 1, 0] }""");
            Do(w, head, """{ "do": "link", "target": "@b", "to": "@a", "follow": "none", "onParentDeath": "drop" }""");
            Assert.Equal(head, a.Parent, "a's parent");
            Assert.Equal(a, b.Parent, "b hangs on a");
            Assert.Equal(3.0, E(w, head, "self.group.len").Num, "the whole tree is one group");
            Assert.Equal(LinkSpec.None, b.Link.Follow, "follow none");
        }

        public static void Selectors()
        {
            var w = NewWorld();
            var head = Place(w, "head", new Vec(0, 0, 0));
            var a = Place(w, "a", new Vec(2, 0, 0));
            var b = Place(w, "b", new Vec(4, 0, 0));
            w.LinkTo(a, head, new LinkSpec());
            w.LinkTo(b, a, new LinkSpec { Follow = LinkSpec.None });
            Assert.Equal("a", E(w, b, "self.parent").AsEntity?.Id, ".parent");
            Assert.Equal(1.0, E(w, head, "self.children.len").Num, ".children");
            Assert.Equal("head", E(w, b, "self.root").AsEntity?.Id, ".root");
            Assert.Equal(3.0, E(w, b, "self.group.len").Num, ".group from a leaf");
            Assert.Equal("head", E(w, b, "self.group.first").AsEntity?.Id, "the group is root first");
            Assert.Equal(true, E(w, head, "self.parent").IsNone, "a root has no parent");
            Assert.Equal(2.0, E(w, head, "count('where(it.root == self)')").Num, "count the parts of a tree");
            Assert.Equal(2, w.Linked.Count, "two linked parts");
        }

        public static void LinkActionReadsItsParams()
        {
            var w = NewWorld();
            var head = Place(w, "head", new Vec(0, 0, 0));
            var a = Place(w, "a", new Vec(3, 0, 1));
            Do(w, head, """{ "do": "link", "target": "@a", "to": "self", "follow": "position", "onParentDeath": "stay", "damageToParent": 2, "damageToGroup": 0.5 }""");
            Assert.Equal(LinkSpec.PositionOnly, a.Link.Follow, "follow");
            Assert.Equal(LinkSpec.Stay, a.Link.OnParentDeath, "onParentDeath");
            Assert.Equal(2.0, a.Link.ToParent, "damageToParent");
            Assert.Equal(0.5, a.Link.ToGroup, "damageToGroup");
            Assert.Equal(new Vec(3, 0, 1), a.Link.Offset, "offset defaults to where it is now");
            Do(w, head, """{ "do": "link", "target": "@a", "to": "self", "offset": [1, 2, 3] }""");
            Assert.Equal(new Vec(1, 2, 3), a.Link.Offset, "explicit offset; re-linking replaces");
            Assert.Equal(1, head.Children.Count, "re-linking doesn't duplicate");
        }

        public static void CyclesAndBadPartsAreRefused()
        {
            var w = NewWorld();
            var head = Place(w, "head", new Vec(0, 0, 0));
            var a = Place(w, "a", new Vec(2, 0, 0));
            w.LinkTo(a, head, new LinkSpec());
            bool threw = false;
            try { w.LinkTo(head, a, new LinkSpec()); } catch (LogicError) { threw = true; }
            Assert.True(threw, "linking a parent under its own part is a cycle");
            threw = false;
            try { w.LinkTo(a, a, new LinkSpec()); } catch (LogicError) { threw = true; }
            Assert.True(threw, "an entity can't be its own part");
            var king = Place(w, "k", new Vec(0, 0, 0), kind: "king");
            threw = false;
            try { w.LinkTo(king, head, new LinkSpec()); } catch (LogicError) { threw = true; }
            Assert.True(threw, "the king can't be a part");
        }

        public static void UnlinkAndEvents()
        {
            var w = NewWorld("""{ "vars": { "log": [] } }""");
            var head = Place(w, "head", new Vec(0, 0, 0));
            var a = Place(w, "a", new Vec(2, 0, 0), rules: """[ { "when": "linked", "do": [ { "do": "listAdd", "name": "$log", "value": "'linked ' + other.id" } ] }, { "when": "unlinked", "do": [ { "do": "listAdd", "name": "$log", "value": "'unlinked ' + other.id" } ] } ]""");
            string released = null;
            LinkServices.Released = (e, how) => released = e.Id + " " + how;
            Do(w, head, """{ "do": "link", "target": "@a", "to": "self" }""");
            Do(w, head, """{ "do": "unlink", "target": "@a" }""");
            w.Tick(0.016);
            Assert.Equal(null, a.Parent, "unlinked");
            Assert.Equal("a drop", released, "unlink drops by default");
            Assert.Equal("linked head,unlinked head", string.Join(",", w.GetVar(w.Level, "log").AsList.Select(v => v.Str)), "events");
            Do(w, head, """{ "do": "link", "target": "@a", "to": "self" }""");
            Do(w, head, """{ "do": "unlink", "children": true, "release": "stay" }""");
            Assert.Equal(0, head.Children.Count, "children: true unlinks the parts");
            Assert.Equal("a stay", released, "release stay");
        }

        public static void ParentDeathPolicies()
        {
            var w = NewWorld();
            var head = Place(w, "head", new Vec(0, 0, 0));
            var dies = Place(w, "dies", new Vec(1, 0, 0));
            var drops = Place(w, "drops", new Vec(2, 0, 0));
            var stays = Place(w, "stays", new Vec(3, 0, 0));
            var under = Place(w, "under", new Vec(4, 0, 0));
            w.LinkTo(dies, head, new LinkSpec { OnParentDeath = LinkSpec.Die });
            w.LinkTo(drops, head, new LinkSpec { OnParentDeath = LinkSpec.Drop });
            w.LinkTo(stays, head, new LinkSpec { OnParentDeath = LinkSpec.Stay });
            w.LinkTo(under, drops, new LinkSpec());
            var killed = new System.Collections.Generic.List<string>();
            LinkServices.Kill = (e, _) => killed.Add(e.Id);
            w.ParentDied(head);
            Assert.Equal("dies", string.Join(",", killed), "die kills the part");
            Assert.True(drops.Parent == null && stays.Parent == null && dies.Parent == null, "every part is unlinked");
            Assert.Equal(drops, under.Parent, "a dropped part keeps its own parts (a split chain stays a chain)");
            // A gone parent (its object destroyed) counts as dead.
            LinkServices.Kill = null;
            w.LinkTo(dies, drops, new LinkSpec());
            w.Forget(drops);
            Assert.True(dies.Destroyed, "without a kill service the core despawns a die part");
            Assert.True(under.Destroyed, "...and the default policy is die");
        }

        public static void DamageShares()
        {
            var w = NewWorld();
            var head = Place(w, "head", new Vec(0, 0, 0));
            var a = Place(w, "a", new Vec(1, 0, 0));
            var b = Place(w, "b", new Vec(2, 0, 0));
            var c = Place(w, "c", new Vec(3, 0, 0));
            // A weak point: the parent takes double, the part keeps its own hit.
            w.LinkTo(a, head, new LinkSpec { ToParent = 2 });
            var (kept, toParent, each, others) = LogicWorld.ShareDamage(a, 10);
            Assert.Equal(10.0, kept, "weak point keeps its hit");
            Assert.Equal(20.0, toParent, "parent takes 2x");
            Assert.True(others == null && each == 0, "no group share");
            // A pure proxy hitbox.
            a.Link = new LinkSpec { ToParent = 1, Kept = 0 };
            (kept, toParent, _, _) = LogicWorld.ShareDamage(a, 10);
            Assert.Equal(0.0, kept, "proxy keeps nothing");
            Assert.Equal(10.0, toParent, "proxy passes it all on");
            // A shared pool over a 4-member chain: everyone takes a quarter, the struck one included.
            a.Link = new LinkSpec { ToGroup = 1, Follow = LinkSpec.None };
            w.LinkTo(b, a, new LinkSpec { ToGroup = 1, Follow = LinkSpec.None });
            w.LinkTo(c, b, new LinkSpec { ToGroup = 1, Follow = LinkSpec.None });
            (kept, toParent, each, others) = LogicWorld.ShareDamage(b, 20);
            Assert.Equal(5.0, kept, "struck link takes its quarter");
            Assert.Equal(5.0, each, "every other link a quarter");
            Assert.Equal("head,a,c", string.Join(",", others.Select(o => o.Id)), "the rest of the group, root first");
            Assert.Equal(0.0, toParent, "no parent share");
            // Half shared: the struck one keeps half plus its share.
            b.Link.ToGroup = 0.5;
            (kept, _, each, _) = LogicWorld.ShareDamage(b, 20);
            Assert.Equal(12.5, kept, "10 kept + 2.5 share");
            Assert.Equal(2.5, each, "10 spread over 4");
            // Dead members don't share.
            ((FakeBody)c.Body).Hp = 0;
            (kept, _, each, others) = LogicWorld.ShareDamage(b, 30);
            Assert.Equal(2, others.Count, "the dead link is left out");
            Assert.Equal(5.0, each, "15 over 3 live members");
        }

        public static void SnapshotKeepsLinksButNotTemplateParts()
        {
            var w = NewWorld();
            var head = Place(w, "head", new Vec(0, 0, 0));
            var a = Place(w, "a", new Vec(1, 0, 0));
            var p = Place(w, "p", new Vec(2, 0, 0));
            w.LinkTo(a, head, new LinkSpec { ToParent = 2, Offset = new Vec(1, 2, 3) });
            w.LinkTo(p, head, new LinkSpec { FromParts = true });
            var snap = w.Capture();
            Assert.Equal("head", (string)snap["entities"]["a"]["parent"], "a link is saved");
            Assert.True(snap["entities"]["p"] == null, "a template part is not saved (rebuilt with its parent)");

            var w2 = NewWorld();
            var head2 = Place(w2, "head", new Vec(0, 0, 0));
            var a2 = Place(w2, "a", new Vec(1, 0, 0));
            w2.Restore(snap);
            Assert.Equal(head2, a2.Parent, "the link is restored");
            Assert.Equal(2.0, a2.Link.ToParent, "with its damage share");
            Assert.Equal(new Vec(1, 2, 3), a2.Link.Offset, "and its offset");
        }
    }
}
