using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PalaceMod.Builder.Model
{
    public enum Severity { Warning, Error }

    /// <summary>One problem in a pack. Path is a JSON-ish locator such as "units[0].stats.maxHp".</summary>
    public sealed class ValidationIssue
    {
        public Severity Severity { get; }
        public string Path { get; }
        public string Message { get; }

        public ValidationIssue(Severity severity, string path, string message) { Severity = severity; Path = path; Message = message; }

        public override string ToString() => $"{(Severity == Severity.Error ? "error" : "warning")} {Path}: {Message}";
    }

    /// <summary>What the validator needs to know about the game; ICatalog implements it. Null skips game checks.</summary>
    public interface IValidationContext
    {
        /// <summary>Whether "vanilla:&lt;id&gt;" names a known unit/enemy of that side (side null = either).</summary>
        bool HasVanillaUnit(string id, string side);
        bool HasVanillaWeapon(string id);
        /// <summary>Whether the scene is a playable host map.</summary>
        bool HasHost(string scene);
        /// <summary>Spawn line names on the host, or null if unknown.</summary>
        IReadOnlyCollection<string> SpawnLines(string scene);
        /// <summary>Whether another installed pack provides the unit.</summary>
        bool HasPackUnit(string packId, string unitId);
    }

    public static class Validator
    {
        // TagManager.ETag names. Changing Flying or LargeUnit would also need a pathfinding graph change, which v1 doesn't do.
        private static readonly HashSet<string> TagNames = new(Enum.GetNames(typeof(TagManager.ETag)));
        private static readonly HashSet<string> MovementTags = ["Flying", "LargeUnit"];

        public static bool IsTag(string name) => TagNames.Contains(name);

        public static List<ValidationIssue> Validate(DesignPack pack, IValidationContext game = null)
        {
            var issues = new List<ValidationIssue>();
            void Error(string path, string msg) => issues.Add(new ValidationIssue(Severity.Error, path, msg));
            void Warn(string path, string msg) => issues.Add(new ValidationIssue(Severity.Warning, path, msg));

            if (pack == null) { Error("", "empty pack"); return issues; }
            if (pack.Format != DesignPack.FormatName) Error("format", $"must be \"{DesignPack.FormatName}\"");
            if (pack.Version != DesignPack.CurrentVersion) Error("version", $"is {pack.Version}; this PalaceMod reads version {DesignPack.CurrentVersion}");
            if (!Ids.IsValid(pack.Id)) Error("id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
            if (string.IsNullOrWhiteSpace(pack.Name)) Warn("name", "has no name");

            foreach (var kv in pack.Assets ?? [])
            {
                string p = $"assets.{kv.Key}";
                if (!Ids.IsValid(kv.Key)) Error(p, "asset keys use a-z, 0-9, '.', '_' or '-'");
                if (kv.Value == null) { Error(p, "is empty"); continue; }
                if (string.IsNullOrEmpty(kv.Value.Path) && string.IsNullOrEmpty(kv.Value.Url)) Error(p, "needs a path or a url");
                if (!string.IsNullOrEmpty(kv.Value.Path) && (kv.Value.Path.Contains("..") || System.IO.Path.IsPathRooted(kv.Value.Path)))
                    Error(p + ".path", "must be a relative path inside the pack folder");
                if (!string.IsNullOrEmpty(kv.Value.Url) && !IsSha256(kv.Value.Sha256)) Error(p + ".sha256", "a url needs the file's sha256");
            }

            var unitIds = new HashSet<string>();
            for (int i = 0; i < (pack.Units?.Count ?? 0); i++)
            {
                var u = pack.Units[i];
                string p = $"units[{i}]";
                if (u == null) { Error(p, "is empty"); continue; }
                if (!Ids.IsValid(u.Id)) Error(p + ".id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                else if (!unitIds.Add(u.Id)) Error(p + ".id", $"\"{u.Id}\" is used twice");
                if (u.Side != UnitSide.Enemy && u.Side != UnitSide.Player) Error(p + ".side", "must be \"enemy\" or \"player\"");
                var b = ContentRef.Parse(u.Base);
                if (b.Kind != RefKind.Vanilla) Error(p + ".base", "must be \"vanilla:<id>\" (`builder dump` lists the ids)");
                else if (game != null && !game.HasVanillaUnit(b.Id, u.Side)) Error(p + ".base", $"no vanilla {u.Side} \"{b.Id}\"");
                if (string.IsNullOrWhiteSpace(u.Name)) Warn(p + ".name", "has no name; the base unit's name is shown");

                var v = u.Visuals;
                if (v != null)
                {
                    if (v.Tint != null && !TryParseColor(v.Tint, out _)) Error(p + ".visuals.tint", "must be \"#rrggbb\"");
                    if (v.Scale is float s && (s < 0.1f || s > 10f)) Error(p + ".visuals.scale", "must be between 0.1 and 10");
                    CheckAsset(v.Icon, p + ".visuals.icon");
                    CheckAsset(v.Mesh, p + ".visuals.mesh");
                }
                var st = u.Stats;
                if (st != null)
                {
                    Range(st.MaxHp, 1, 100000, p + ".stats.maxHp");
                    Range(st.MoveSpeed, 0, 100, p + ".stats.moveSpeed");
                    Range(st.AttackCooldown, 0.05f, 120, p + ".stats.attackCooldown");
                    Range(st.AttackRange, 0, 200, p + ".stats.attackRange");
                    Range(st.KeepDistance, 0, 200, p + ".stats.keepDistance");
                    Range(st.AggroTime, 0, 600, p + ".stats.aggroTime");
                }
                var w = u.Weapon;
                if (w != null)
                {
                    if (w.Base != null && w.Base != "inherit")
                    {
                        var wb = ContentRef.Parse(w.Base);
                        if (wb.Kind != RefKind.Vanilla) Error(p + ".weapon.base", "must be \"inherit\" or \"vanilla:<weapon>\"");
                        else if (game != null && !game.HasVanillaWeapon(wb.Id)) Error(p + ".weapon.base", $"no vanilla weapon \"{wb.Id}\"");
                    }
                    CheckDamage(w.Damage, p + ".weapon.damage");
                    CheckDamage(w.Splash, p + ".weapon.splash");
                    Range(w.DamageMult, 0, 1000, p + ".weapon.damageMult");
                    Range(w.ProjectileSpeed, 0.1f, 1000, p + ".weapon.projectileSpeed");
                }
                if (u.Tags != null)
                {
                    foreach (var t in (u.Tags.Add ?? []).Concat(u.Tags.Remove ?? []))
                    {
                        if (!IsTag(t)) Error(p + ".tags", $"unknown tag \"{t}\"");
                        else if (MovementTags.Contains(t)) Error(p + ".tags", $"\"{t}\" changes how the unit paths and can't be added or removed yet; pick a base that has it");
                    }
                }
                for (int j = 0; j < (u.Behaviours?.Count ?? 0); j++)
                {
                    var bh = u.Behaviours[j];
                    string bp = $"{p}.behaviours[{j}]";
                    if (bh == null || !BehaviourTypes.All.Contains(bh.Type)) { Error(bp + ".type", $"unknown behaviour \"{bh?.Type}\" (known: {string.Join(", ", BehaviourTypes.All)})"); continue; }
                    if (bh.Type == BehaviourTypes.SpawnOnDeath)
                    {
                        string unit = bh.GetString("unit");
                        if (unit == null) Error(bp + ".unit", "needs a unit ref");
                        else if (unit == u.Id || unit == "unit:" + u.Id) Error(bp + ".unit", "a unit can't spawn itself on death");
                        else CheckUnitRef(unit, bp + ".unit", UnitSide.Enemy);
                    }
                }
            }

            var waveIds = new HashSet<string>();
            for (int i = 0; i < (pack.WaveSets?.Count ?? 0); i++)
            {
                var ws = pack.WaveSets[i];
                string p = $"waveSets[{i}]";
                if (ws == null) { Error(p, "is empty"); continue; }
                if (!Ids.IsValid(ws.Id)) Error(p + ".id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                else if (!waveIds.Add(ws.Id)) Error(p + ".id", $"\"{ws.Id}\" is used twice");
                bool hostKnown = !string.IsNullOrEmpty(ws.Host) && (game == null || game.HasHost(ws.Host));
                if (!hostKnown) Error(p + ".host", $"unknown host map \"{ws.Host}\" (`builder dump` lists them)");
                var lines = hostKnown ? game?.SpawnLines(ws.Host) : null;
                if (ws.StartingGold is int g && (g < 0 || g > 100000)) Error(p + ".startingGold", "must be between 0 and 100000");
                if (ws.Nights == null || ws.Nights.Count == 0) Error(p + ".nights", "needs at least one night");
                for (int n = 0; n < (ws.Nights?.Count ?? 0); n++)
                {
                    var night = ws.Nights[n];
                    string np = $"{p}.nights[{n}]";
                    if (night == null) { Error(np, "is empty"); continue; }
                    if (night.Difficulty < 0.1f || night.Difficulty > 100f) Error(np + ".difficulty", "must be between 0.1 and 100");
                    if (night.Spawns == null || night.Spawns.Count == 0) Error(np + ".spawns", "needs at least one spawn");
                    for (int s = 0; s < (night.Spawns?.Count ?? 0); s++)
                    {
                        var sp = night.Spawns[s];
                        string sp_ = $"{np}.spawns[{s}]";
                        if (sp == null) { Error(sp_, "is empty"); continue; }
                        CheckUnitRef(sp.Unit, sp_ + ".unit", UnitSide.Enemy);
                        if (sp.Count < 1 || sp.Count > 1000) Error(sp_ + ".count", "must be between 1 and 1000");
                        if (sp.Interval < 0 || sp.Interval > 600) Error(sp_ + ".interval", "must be between 0 and 600");
                        if (sp.Delay < 0 || sp.Delay > 3600) Error(sp_ + ".delay", "must be between 0 and 3600");
                        if (sp.Gold < 0 || sp.Gold > 10000) Error(sp_ + ".gold", "must be between 0 and 10000");
                        if (string.IsNullOrEmpty(sp.Line)) Error(sp_ + ".line", "needs a spawn line name");
                        else if (lines != null && !lines.Contains(sp.Line)) Error(sp_ + ".line", $"no spawn line \"{sp.Line}\" on {ws.Host} (has: {string.Join(", ", lines)})");
                    }
                }
            }

            var levelIds = new HashSet<string>();
            for (int i = 0; i < (pack.Levels?.Count ?? 0); i++)
            {
                var l = pack.Levels[i];
                string p = $"levels[{i}]";
                if (l == null) { Error(p, "is empty"); continue; }
                if (!Ids.IsValid(l.Id)) Error(p + ".id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                else if (!levelIds.Add(l.Id)) Error(p + ".id", $"\"{l.Id}\" is used twice");
                if (string.IsNullOrEmpty(l.Host) || (game != null && !game.HasHost(l.Host))) Error(p + ".host", $"unknown host map \"{l.Host}\"");
                if (l.WaveSet != null)
                {
                    var ws = pack.FindWaveSet(l.WaveSet);
                    if (ws == null) Error(p + ".waveSet", $"no wave set \"{l.WaveSet}\" in this pack");
                    else if (ws.Host != l.Host) Error(p + ".waveSet", $"wave set is for {ws.Host}, level is on {l.Host}");
                }
                if (l.BuildSlots != null || l.SpawnLines != null || (l.Props?.Count ?? 0) > 0)
                    Warn(p, "build slot, spawn line and prop edits aren't applied yet (stage 2)");
            }
            return issues;

            void Range(float? value, float min, float max, string path)
            {
                if (value is float f && (float.IsNaN(f) || f < min || f > max)) Error(path, $"must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}");
            }

            void CheckAsset(string reference, string path)
            {
                if (reference == null) return;
                var r = ContentRef.Parse(reference);
                if (r.Kind != RefKind.Asset) Error(path, "must be \"asset:<key>\"");
                else if (pack.Assets == null || !pack.Assets.ContainsKey(r.Id)) Error(path, $"no asset \"{r.Id}\" in this pack's \"assets\"");
            }

            void CheckDamage(List<DamageModDef> mods, string path)
            {
                for (int j = 0; j < (mods?.Count ?? 0); j++)
                {
                    var m = mods[j];
                    if (m == null) { Error($"{path}[{j}]", "is empty"); continue; }
                    foreach (var t in m.Tags ?? []) if (!IsTag(t)) Error($"{path}[{j}].tags", $"unknown tag \"{t}\"");
                    if (float.IsNaN(m.Add) || m.Add < -100000 || m.Add > 100000) Error($"{path}[{j}].add", "out of range");
                    if (float.IsNaN(m.Mult) || m.Mult < 0 || m.Mult > 1000) Error($"{path}[{j}].mult", "must be between 0 and 1000");
                }
            }

            void CheckUnitRef(string reference, string path, string side)
            {
                var r = ContentRef.Parse(reference);
                switch (r.Kind)
                {
                    case RefKind.Vanilla:
                        if (game != null && !game.HasVanillaUnit(r.Id, side)) Error(path, $"no vanilla {side} \"{r.Id}\"");
                        break;
                    case RefKind.LocalUnit:
                        var u = pack.FindUnit(r.Id);
                        if (u == null) Error(path, $"no unit \"{r.Id}\" in this pack");
                        else if (u.Side != side) Error(path, $"\"{r.Id}\" is a {u.Side} unit");
                        break;
                    case RefKind.PackUnit:
                        if (r.Pack == pack.Id) goto case RefKind.LocalUnit;
                        if (game != null && !game.HasPackUnit(r.Pack, r.Id)) Error(path, $"pack \"{r.Pack}\" with unit \"{r.Id}\" isn't installed");
                        break;
                    default:
                        Error(path, $"\"{reference}\" isn't a unit reference (use a unit id, \"vanilla:<id>\" or \"<pack>/<unit>\")");
                        break;
                }
            }
        }

        public static bool IsSha256(string s) => s != null && s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        /// <summary>Parses "#rrggbb" (or "rrggbb") into 0-1 RGB.</summary>
        public static bool TryParseColor(string hex, out (float r, float g, float b) rgb)
        {
            rgb = default;
            if (hex == null) return false;
            hex = hex.TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            rgb = (((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
            return true;
        }
    }
}
