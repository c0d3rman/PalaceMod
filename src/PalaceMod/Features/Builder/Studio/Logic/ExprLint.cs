using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model.Logic;

/*
    Checks an expression or filter as it's typed, for the error line under the field: the engine's own parser and type
    checker (Model/Logic/ExprChecker), given what this rule can see (the event's roles, `it`/`spawned` where bound, the
    level's and this thing's variables, the level's @names). Columns here are 1-based.
*/
namespace PalaceMod.Builder.Studio.Logic
{
    internal sealed class FieldIssue
    {
        public int Col;          // 1-based
        public string Message;
        public bool Warning;
        public override string ToString() => $"col {Col}: {Message}";
    }

    /// <summary>What names an expression may use where it is.</summary>
    internal sealed class ExprScope
    {
        public EventUi Event;
        public bool IsLevelScript;
        public ISet<string> LevelVars = new HashSet<string>();
        public ISet<string> SelfVars = new HashSet<string>();
        /// <summary>@names in the level (empty: unknown, not checked).</summary>
        public ISet<string> Names = new HashSet<string>();
        public bool HasSpawned, HasIt;
    }

    internal static class ExprLint
    {
        private static CheckScope ToCheck(ExprScope s)
        {
            if (s == null) return new CheckScope();
            return new CheckScope
            {
                Event = s.Event?.Name != null && LogicSchema.Events.TryGetValue(s.Event.Name, out var ev) ? ev : null,
                LevelScript = s.IsLevelScript,
                HasIt = s.HasIt,
                HasSpawned = s.HasSpawned,
                LevelVars = s.LevelVars,
                // Entity vars are checked only where the owner declares some (else other packs' templates may).
                SelfVars = s.SelfVars.Count > 0 ? s.SelfVars : null,
                Names = s.Names.Count > 0 ? s.Names : null,
            };
        }

        private static List<FieldIssue> Convert(List<ExprIssue> issues) =>
            issues.Select(i => new FieldIssue { Col = i.Col + 1, Message = i.Message, Warning = i.Warning }).ToList();

        public static List<FieldIssue> Check(string text, ExprScope scope = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return [];
            var issues = new List<ExprIssue>();
            ExprChecker.Check(text, ToCheck(scope), issues, out _);
            return Convert(issues);
        }

        public static List<FieldIssue> CheckFilter(string text, ExprScope scope = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return [];
            var issues = new List<ExprIssue>();
            ExprChecker.CheckFilter(text, ToCheck(scope), issues);
            return Convert(issues);
        }
    }
}
