using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    Shared plumbing for the World and Show actions (LogicWorldVerbsManager): the session, game checks, per-session
    state that resets whenever a new LogicWorld is installed, and small parsers.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal static class WorldCommon
    {
        public static readonly Log log = Log.For<LogicWorldVerbsManager>();

        public static BuilderSessionInfo Session => BuilderServices.Sessions.Current;
        public static PackEntry PackEntry => Session?.Pack?.Id is string id ? BuilderServices.Packs.Find(id) : null;

        public static bool InMatch =>
            DayNightCycle.Instance != null && EnemySpawner.instance != null && PlayerInteraction.instance != null && TagManager.instance != null &&
            LocalGamestate.Instance != null && LocalGamestate.Instance.CurrentState == LocalGamestate.State.InMatch;

        public static void RequireMatch(string action)
        {
            if (!InMatch) throw new LogicError($"{action}: only during a level");
        }

        public static bool IsNight => DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;

        public static readonly AccessTools.FieldRef<EnemySpawner, int> WavenumberRef = AccessTools.FieldRefAccess<EnemySpawner, int>("wavenumber");
        public static readonly AccessTools.FieldRef<EnemySpawner, bool> SpawningRef = AccessTools.FieldRefAccess<EnemySpawner, bool>("spawningInProgress");
        public static readonly AccessTools.FieldRef<Spawn, bool> SpawnFinishedRef = AccessTools.FieldRefAccess<Spawn, bool>("finished");

        /// <summary>Where an action happens: `at` (entity or position), else the context's self, else null.</summary>
        public static Vector3? At(LogicContext ctx, ActionParams p, string name = "at")
        {
            if (p.Has(name))
            {
                var v = p.Position(name) ?? throw new LogicError($"\"{name}\" is empty (no entity or position)");
                return v.ToVector3();
            }
            var self = p.Target ?? ctx.Self;
            return self != null && self.Kind != "level" && (self.Body != null || self.Game != null) ? self.Position.ToVector3() : null;
        }

        public static Color ParseColor(string text, string param) =>
            TryColor(text, out var c) ? c : throw new LogicError($"\"{param}\": \"{text}\" isn't a colour (use {Model.Logic.ParamForms.ColorHelp})");

        /// <summary>A colour field or param: the one parser the validator checks with (Model.Logic.ParamForms).</summary>
        public static bool TryColor(string text, out Color c)
        {
            bool ok = Model.Logic.ParamForms.TryParseColor(text, out float r, out float g, out float b, out float a);
            c = new Color(r, g, b, a);
            return ok;
        }

        public static string Near(IEnumerable<string> names, string wanted)
        {
            var close = names.Where(n => n.IndexOf(wanted ?? "", StringComparison.OrdinalIgnoreCase) >= 0 || (wanted ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).Take(5).ToList();
            return close.Count > 0 ? $" (did you mean {string.Join(", ", close.Select(n => $"\"{n}\""))}?)" : "";
        }

        public static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Per-session state of the World and Show actions. Reset whenever <see cref="LogicWorld.Current"/> changes, so
    /// nothing leaks from one session (or dev world) into the next.
    /// </summary>
    internal static class WorldSession
    {
        private static LogicWorld current;
        public static event Action Reset;

        /// <summary>Called on every action and every frame: notices a new (or no) world and resets the state.</summary>
        public static void Sync()
        {
            var w = LogicWorld.Current;
            if (ReferenceEquals(w, current)) return;
            current = w;
            try { Reset?.Invoke(); }
            catch (Exception e) { WorldCommon.log.Error("Resetting world-verb state failed", e); }
        }

        public static bool Active => LogicWorld.Current != null;
    }
}
