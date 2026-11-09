using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Studio.Enemies;
using UnityEngine;

/*
    `dev.py cmd "builder-logic …"`: drives the behaviour editor the way the mouse does (select, open the tab, open a rule,
    press a row by its text, place a logic object, draw a wire) and reports the JSON it wrote, for tests.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal static class LogicDev
    {
        private static LogicOwner owner;

        public static string Run(string[] args)
        {
            if (args.Length == 0) return State();
            string rest = string.Join(" ", args.Skip(1));
            switch (args[0])
            {
                case "state": return State();
                case "owner":
                    owner = rest == "level" ? LogicOwner.Level() : rest == "unit" ? LogicOwner.Unit(EnemyStudio.UnitId, EnemyStudio.UnitId) : LogicOwner.ForItem(rest) ?? throw new ArgumentException($"no item \"{rest}\"");
                    if (owner.StudioKey != null) Studio.Select([owner.StudioKey], SelectOp.Replace);
                    return $"owner {owner.Kind} {owner.Id} ({owner.Title})";
                case "tab":
                    if (EnemyStudio.Active) { EnemyDock.PanelTab = "behaviour"; EnemyDock.RefreshInspector(); return "enemy behaviour tab"; }
                    StudioDock.ShowPanel("panel:" + LogicStudio.Tab);
                    return "behaviour tab";
                case "rules":
                    return string.Join("\n", (Owner().Rules()?.OfType<JObject>() ?? []).Select((r, i) => $"{i}: {RuleText.Summary(r, Owner().Kind)}"));
                case "open":
                    LogicStudio.OpenRule(Owner(), int.Parse(rest));
                    return "open " + rest;
                case "new":
                    LogicStudio.NewRule(Owner(), string.IsNullOrEmpty(rest) ? null : (JToken)rest);
                    if (!string.IsNullOrEmpty(rest)) return "new rule: " + Summary();
                    return "new rule; event picker open";
                case "act":
                {
                    if (!RuleEditor.IsOpen) throw new InvalidOperationException("no rule open");
                    if (LogicCatalog.Action(rest) == null) throw new ArgumentException($"unknown action \"{rest}\"");
                    RuleEditor.Owner.EditRule(RuleEditor.Rule, "Add " + rest, r => LogicDoc.ActionList(r, [], true).Add(LogicDoc.NewAction(rest)));
                    return Summary();
                }
                case "set":
                {
                    // set <JSON path inside the rule, e.g. do[0].count> <json>
                    if (!RuleEditor.IsOpen) throw new InvalidOperationException("no rule open");
                    string path = args[1];
                    var value = JToken.Parse(string.Join(" ", args.Skip(2)));
                    RuleEditor.Owner.EditRule(RuleEditor.Rule, "Set " + path, r =>
                    {
                        int dot = path.LastIndexOf('.');
                        var parent = dot < 0 ? r : r.SelectToken(path.Substring(0, dot)) as JObject ?? throw new ArgumentException("no " + path);
                        parent[path.Substring(dot + 1)] = value;
                    });
                    return Summary();
                }
                case "press": return Press(rest);
                case "place":
                {
                    var all = LogicStudio.PaletteObjects();
                    var obj = all.FirstOrDefault(o => o.Id == args[1]) ?? throw new ArgumentException("objects: " + string.Join(", ", all.Select(o => o.Id)));
                    Vector3 at = args.Length >= 4 ? new Vector3(F(args[2]), 0f, F(args[3])) : Studio.CursorGround ?? Vector3.zero;
                    at.y = Studio.Ground(at);
                    string key = PlaceItems.Add(obj.Name, obj.IdPrefix, obj.Place, at, 0f);
                    Studio.Select([key], SelectOp.Replace);
                    owner = LogicOwner.ForItem(key);
                    return "placed " + key;
                }
                case "wire":
                {
                    // wire <fromKey> <event> <toKey> <input>
                    var from = LogicOwner.ForItem(args[1]) ?? throw new ArgumentException("no " + args[1]);
                    var to = LogicOwner.ForItem(args[3]) ?? throw new ArgumentException("no " + args[3]);
                    WireTool.Write(from, to, args[2], null, args[4]);
                    return string.Join("\n", Wires.Scan(LogicOwner.LevelLogic()).Select(w => w.ToString()));
                }
                case "unwire":
                {
                    var w = Wires.Scan(LogicOwner.LevelLogic()).FirstOrDefault(x => (x.FromId == args[1] || PlaceItems.Prefix + x.FromId == args[1]) && x.Signal == args[2]) ?? throw new ArgumentException("no such wire");
                    WireTool.Remove(w);
                    return "removed " + w;
                }
                case "wires": return string.Join("\n", Wires.Scan(LogicOwner.LevelLogic()).Select(w => w.ToString()));
                case "json":
                    return EnemyStudio.Active ? LogicDoc.UnitNode(EnemyStudio.Unit).ToString() : LogicOwner.LevelLogic()?.ToString() ?? "(no logic)";
                case "check":
                    var issues = ExprLint.Check(rest, Owner().Scope(RuleEditor.IsOpen ? LogicCatalog.Event(LogicDoc.EventOf(RuleEditor.Owner.Rules()?[RuleEditor.Rule] as JObject)) : null));
                    return issues.Count == 0 ? "ok" : string.Join("\n", issues);
                case "close": RuleEditor.Close(); return "closed";
                case "find":
                    return string.Join("\n", Studio.Items.Where(i => (i.Name + " " + i.TypeName).Contains(rest, StringComparison.OrdinalIgnoreCase)).Take(20).Select(i => $"{i.Key}  {i.TypeName}  ({i.Pos.x:0}, {i.Pos.z:0})"));
                case "rows": return Rows();
                default: throw new ArgumentException("unknown subcommand " + args[0]);
            }
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static LogicOwner Owner() => owner ?? (RuleEditor.IsOpen ? RuleEditor.Owner : LogicStudio.SelectedOwner());

        private static string Summary()
        {
            if (!RuleEditor.IsOpen) return "(no rule open)";
            var r = RuleEditor.Owner.Rules()?[RuleEditor.Rule] as JObject;
            return RuleText.Summary(r, RuleEditor.Owner.Kind) + "\n" + r?.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string State() =>
            $"mode {Studio.Mode}; owner {(Owner()?.Title ?? "-")}; editor {(RuleEditor.IsShown ? (RuleEditor.Owner == null ? "picker" : $"{RuleEditor.Owner.Title} rule {RuleEditor.Rule}") : "closed")}; " +
            $"places {LogicDoc.Places(LogicOwner.LevelLogic())?.Count ?? 0}; wires {Wires.Scan(LogicOwner.LevelLogic()).Count}; tool {Studio.ToolId}";

        private static IEnumerable<TFUITextButton> Buttons()
        {
            return Roots().SelectMany(r => r.GetComponentsInChildren<TFUITextButton>()).Distinct();
        }

        // The rule editor's panel first, then the docks (and an open popup).
        private static IEnumerable<Transform> Roots() =>
            new Transform[] { ModPopup.Current != null && ModPopup.Current.IsOpen ? ModPopupRoot() : null, RuleEditor.PanelRect, StudioDock.Dock?.Root, EnemyDock.Dock?.Root }.Where(r => r != null);

        private static Transform ModPopupRoot() => UnityEngine.Object.FindObjectsOfType<RectTransform>().FirstOrDefault(r => r.name.StartsWith("ModPopup") && r.gameObject.activeInHierarchy);

        private static string Plain(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", "").Trim();

        /// <summary>Clicks the first row or tile whose text starts with (else contains) the given text, rule editor first.</summary>
        private static string Press(string text)
        {
            foreach (bool start in new[] { true, false })
            {
                bool Match(string s) => start ? s.StartsWith(text, StringComparison.OrdinalIgnoreCase) : s.Contains(text, StringComparison.OrdinalIgnoreCase);
                foreach (var b in Buttons())
                {
                    var t = b.GetComponent<TMPro.TextMeshProUGUI>();
                    if (t == null || !b.gameObject.activeInHierarchy || !Match(Plain(t.text))) continue;
                    b.onApply?.Invoke();
                    return "pressed row " + Plain(t.text).Replace("\n", " / ");
                }
                foreach (var tile in Roots().SelectMany(r => r.GetComponentsInChildren<ModTile>()))
                {
                    if (!tile.gameObject.activeInHierarchy) continue;
                    string label = tile.Label != null ? Plain(tile.Label.text) : tile.name;
                    if (!Match(label)) continue;
                    tile.Press();
                    return "pressed tile " + label;
                }
            }
            throw new ArgumentException($"nothing showing \"{text}\"");
        }

        private static string Rows() => string.Join("\n", Buttons().Where(b => b.gameObject.activeInHierarchy).Select(b => Plain(b.GetComponent<TMPro.TextMeshProUGUI>()?.text).Replace("\n", " / ")).Take(80));
    }
}
