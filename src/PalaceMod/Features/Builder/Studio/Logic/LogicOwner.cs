using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio.Enemies;

/*
    Who a rule belongs to, as the Studio edits it: the level script, an object the level has (attach), a placed logic
    entity (place) or the unit open in the Enemy Studio. Reading gives a copy of the owner's JSON node; editing is one
    undo step on the draft (the Level Studio's level, or the Enemy Studio's unit).
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class LogicOwner
    {
        public OwnerKind Kind;
        /// <summary>"level", the attach key, the place id, or the unit id.</summary>
        public string Id;
        /// <summary>The Studio item key it belongs to (for selection, framing and wires), or null.</summary>
        public string StudioKey;
        public string Title;

        public static LogicOwner Level() => new() { Kind = OwnerKind.Level, Id = "level", Title = "Level script" };

        public static LogicOwner Unit(string unitId, string title) => new() { Kind = OwnerKind.Unit, Id = unitId, Title = title };

        /// <summary>A non-unit template of the open draft (pack entities[]): the Entity Studio's thing.</summary>
        public static LogicOwner Entity(string templateId, string title = null) =>
            new() { Kind = OwnerKind.Entity, Id = templateId, Title = title ?? (string)LogicDoc.EntityNode(DraftPack, templateId)?["name"] ?? templateId };

        /// <summary>A status of the open draft (pack statuses[]).</summary>
        public static LogicOwner Status(string statusId) =>
            new() { Kind = OwnerKind.Status, Id = statusId, Title = (string)LogicDoc.StatusNode(DraftPack, statusId)?["name"] ?? statusId };

        /// <summary>The draft templates are edited in: the Enemy/Entity Studio's, else the Level Studio's.</summary>
        public static PackDraft Draft => EnemyStudio.Draft ?? Studio.Draft;
        public static DesignPack DraftPack => Draft?.Pack;

        /// <summary>The owner for a Studio item key: a place, or an attach on the level's own object.</summary>
        public static LogicOwner ForItem(string studioKey)
        {
            if (studioKey == null) return null;
            if (studioKey.StartsWith(PlaceItems.Prefix)) { string id = studioKey.Substring(PlaceItems.Prefix.Length); return new LogicOwner { Kind = OwnerKind.Place, Id = id, StudioKey = studioKey, Title = PlaceItems.Title(id) }; }
            var item = Studio.Find(studioKey);
            if (item == null || item.Kind == StudioItemKind.Group) return null;
            return new LogicOwner { Kind = OwnerKind.Attach, Id = LogicDoc.AttachKey(studioKey, item.IsCastle), StudioKey = studioKey, Title = item.Name };
        }

        /// <summary>The owner a trace or wire names: "level", an attach key, or a place id.</summary>
        public static LogicOwner FromId(string id)
        {
            if (id == null) return null;
            if (id == "level") return Level();
            if (id == "castle") return ForItem(Studio.Items.FirstOrDefault(i => i.IsCastle)?.Key);
            if (id.Contains(':')) return ForItem(id);
            return ForItem(PlaceItems.Prefix + id);
        }

        public bool IsUnit => Kind == OwnerKind.Unit;
        /// <summary>A template (a unit or a thing): edited outside any level, so no wires or @name.</summary>
        public bool IsTemplate => Kind is OwnerKind.Unit or OwnerKind.Entity or OwnerKind.Status;

        /// <summary>What the owner really is: a place's template merged under its own fields; else its node.</summary>
        /// <summary>Whether it is one of these kinds or has one of these parts (events only some things have: an
        /// area's enter, a mover's arrive).</summary>
        public bool Has(IEnumerable<string> kindsOrParts)
        {
            var node = Effective();
            string kind = Kind == OwnerKind.Place ? PlaceItems.KindOf(Read()) : (string)node?["kind"];
            return kindsOrParts.Any(k => k == kind || node?[k] is JObject);
        }

        public JObject Effective() => Kind == OwnerKind.Place ? LogicDoc.Effective(DraftPack, Read()) : Read();

        /// <summary>The owner's node now (a copy; null if it has none yet).</summary>
        public JObject Read()
        {
            if (IsUnit) return EnemyStudio.Unit?.Id == Id ? LogicDoc.UnitNode(EnemyStudio.Unit) : LogicDoc.UnitNode(Studio.Draft?.Pack.Units.FirstOrDefault(u => u?.Id == Id));
            if (Kind == OwnerKind.Entity) return LogicDoc.EntityNode(DraftPack, Id);
            if (Kind == OwnerKind.Status) return LogicDoc.StatusNode(DraftPack, Id);
            var logic = LogicDoc.Logic(Studio.Level);
            return LogicDoc.Node(logic, Kind, Id, false);
        }

        /// <summary>The level's logic block now (null in the Enemy Studio).</summary>
        public static JObject LevelLogic() => Studio.Level == null ? null : LogicDoc.Logic(Studio.Level);

        public JArray Rules() => LogicDoc.Rules(Read());

        /// <summary>One undo step that changes the owner's node (created if needed).</summary>
        public void Edit(string label, Action<JObject> change, string coalesce = null)
        {
            if (IsUnit)
            {
                if (EnemyStudio.Unit?.Id == Id) EnemyStudio.Edit(label, u => LogicDoc.EditUnit(u, change), structural: true, coalesce: coalesce);
                else
                {
                    var draft = Studio.Draft;
                    draft?.Edit(label, p => { var u = p.Units.FirstOrDefault(x => x?.Id == Id); if (u != null) LogicDoc.EditUnit(u, change); }, structural: true, coalesce: coalesce);
                }
                LogicStudio.Changed();
                return;
            }
            if (Kind is OwnerKind.Entity or OwnerKind.Status)
            {
                var d = Draft ?? throw new InvalidOperationException("no draft open");
                string eid = Id;
                bool status = Kind == OwnerKind.Status;
                d.Edit(label, p => { if (status) LogicDoc.EditStatus(p, eid, change); else LogicDoc.EditEntity(p, eid, change); }, structural: true, coalesce: coalesce);
                LogicStudio.Changed();
                return;
            }
            var kind = Kind;
            var id = Id;
            Studio.Edit(label, lv => LogicDoc.EditLogic(lv, logic =>
            {
                var node = LogicDoc.Node(logic, kind, id, true);
                if (node == null) throw new InvalidOperationException($"no {kind} \"{id}\" to edit");
                change(node);
            }), coalesce);
            LogicStudio.Changed();
        }

        /// <summary>Edits one rule (by index).</summary>
        public void EditRule(int index, string label, Action<JObject> change, string coalesce = null) => Edit(label, node =>
        {
            var rules = LogicDoc.EnsureRules(node);
            if (index < 0 || index >= rules.Count || rules[index] is not JObject rule) throw new InvalidOperationException("that rule is gone");
            change(rule);
            LogicDoc.TidyWhen(rule);
        }, coalesce);

        /// <summary>The names expressions can check against here.</summary>
        public ExprScope Scope(EventUi ev, bool spawned = false)
        {
            var node = Read();
            var logic = LevelLogic();
            var scope = new ExprScope { Event = ev, IsLevelScript = Kind == OwnerKind.Level, HasSpawned = spawned };
            foreach (var p in (logic?["vars"] as JObject)?.Properties() ?? []) scope.LevelVars.Add(p.Name);
            foreach (var p in (node?["vars"] as JObject)?.Properties() ?? []) scope.SelfVars.Add(p.Name);
            if (Kind == OwnerKind.Level) foreach (var v in scope.LevelVars) scope.SelfVars.Add(v);
            if (logic != null) foreach (var n in LogicDoc.Names(logic)) scope.Names.Add(n);
            return scope;
        }

        /// <summary>The "@name" others use for it, giving it one if it has none (inside an edit of the level).</summary>
        public static string EnsureName(JObject logic, LogicOwner owner)
        {
            if (owner.Kind == OwnerKind.Place) return LogicDoc.RefName(OwnerKind.Place, owner.Id, LogicDoc.Place(logic, owner.Id));
            if (owner.Kind != OwnerKind.Attach) return null;
            var node = LogicDoc.Attach(logic, owner.Id, true);
            if ((string)node["name"] is string n) return n;
            n = LogicDoc.NameFor(owner.Title ?? owner.Id, LogicDoc.Names(logic));
            node["name"] = n;
            return n;
        }

        public override bool Equals(object obj) => obj is LogicOwner o && o.Kind == Kind && o.Id == Id;
        public override int GetHashCode() => (Kind, Id).GetHashCode();
    }
}
