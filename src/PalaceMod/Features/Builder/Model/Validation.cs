using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PalaceMod.Builder.Model.Logic;

namespace PalaceMod.Builder.Model
{
    public enum Severity { Warning, Error }

    /// <summary>One problem in a pack. Path is a JSON-ish locator such as "units[0].stats.maxHp".</summary>
    public sealed class ValidationIssue
    {
        public Severity Severity { get; }
        public string Path { get; }
        public string Message { get; }
        /// <summary>For a library item: the file it comes from, relative to the library ("entities/fire.json").</summary>
        public string File { get; set; }
        /// <summary>For a shared item checked in a level's context (LevelScope): that level's id.</summary>
        public string Level { get; set; }

        public ValidationIssue(Severity severity, string path, string message) { Severity = severity; Path = path; Message = message; }

        public override string ToString() => $"{(Severity == Severity.Error ? "error" : "warning")} {(File != null ? File + " " : "")}{Path}: {Message}{(Level != null ? $" (in level {Level})" : "")}";
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

    /// <summary>Optional game context: the spawn lines a host's own (vanilla) nights use; ICatalog implements it.</summary>
    public interface IHostWavesContext
    {
        /// <summary>Spawn line names the host's vanilla nights spawn from, or null if unknown.</summary>
        IReadOnlyCollection<string> VanillaWaveLines(string scene);
    }

    /// <summary>Optional extra game context for troop replacements; ICatalog implements it.</summary>
    public interface ITroopValidationContext
    {
        /// <summary>Building names that train the vanilla player troop, empty if no building does, null if unknown.</summary>
        IReadOnlyCollection<string> TroopBuildings(string unitId);
    }

    public static class Validator
    {
        // TagManager.ETag names. Changing Flying or LargeUnit would also need a pathfinding graph change, which v1 doesn't do.
        private static readonly HashSet<string> TagNames = LogicSchema.Tags; // TagManager.ETag (pure C#, so tests can load it)
        private static readonly HashSet<string> MovementTags = ["Flying", "LargeUnit"];

        public static bool IsTag(string name) => TagNames.Contains(name);

        // File types the asset loader (WP5) reads, by use. Url assets have no extension; the loader checks their bytes.
        public static readonly string[] ImageExtensions = [".png"];
        public static readonly string[] AudioExtensions = [".wav", ".ogg"];
        public static readonly string[] MeshExtensions = [".obj", ".glb", ".gltf"];

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
                    foreach (var kv in v.Colors ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) Error(p + ".visuals.colors", "a part name can't be empty");
                        else if (!TryParseColor(kv.Value, out _)) Error(p + $".visuals.colors.{kv.Key}", "must be \"#rrggbb\"");
                    }
                    if (v.Scale is float s && (s < 0.1f || s > 10f)) Error(p + ".visuals.scale", "must be between 0.1 and 10");
                    CheckAsset(v.Icon, p + ".visuals.icon", ImageExtensions);
                    switch (ModelRef.Kind(v.Mesh, out string modelId))
                    {
                        case ModelKind.Asset: CheckAsset(v.Mesh, p + ".visuals.mesh", MeshExtensions); break;
                        case ModelKind.Unit:
                            if (game != null && !game.HasVanillaUnit(modelId, null)) Error(p + ".visuals.mesh", $"no vanilla unit \"{modelId}\"");
                            break;
                        case ModelKind.Scenery: break; // resolved against the map and the scenery library when the unit is built
                        default:
                            if (v.Mesh != null) Error(p + ".visuals.mesh", "must be \"asset:<key>\", \"mesh:<scenery mesh>\" or \"vanilla:<unit id>\"");
                            break;
                    }
                    foreach (var (rot, key) in new[] { (v.MeshRotX, "meshRotX"), (v.MeshRotZ, "meshRotZ") })
                        if (rot is float r && (float.IsNaN(r) || r < -360f || r > 360f)) Error(p + ".visuals." + key, "must be between -360 and 360");
                    if (v.MeshOffset != null && (v.MeshOffset.Length != 3 || v.MeshOffset.Any(o => float.IsNaN(o) || o < -50f || o > 50f)))
                        Error(p + ".visuals.meshOffset", "must be [x, y, z], each between -50 and 50");
                    if (v.MeshLook != null && !ModelRef.Looks.Contains(v.MeshLook)) Error(p + ".visuals.meshLook", "must be \"own\" or \"unit\"");
                    if (v.Motion != null && !ModelRef.Motions.Contains(v.Motion)) Error(p + ".visuals.motion", "must be \"auto\", \"bob\" or \"none\"");
                    CheckAsset(v.Texture, p + ".visuals.texture", ImageExtensions);
                    if (v.MeshScale is float ms && (float.IsNaN(ms) || ms < 0.001f || ms > 1000f)) Error(p + ".visuals.meshScale", "must be between 0.001 and 1000");
                    if (v.MeshRotY is float mr && (float.IsNaN(mr) || mr < -360f || mr > 360f)) Error(p + ".visuals.meshRotY", "must be between -360 and 360");
                    if ((v.MeshScale != null || v.MeshRotY != null || v.MeshRotX != null || v.MeshRotZ != null || v.MeshOffset != null || v.MeshLook != null) && v.Mesh == null)
                        Warn(p + ".visuals.mesh", "meshScale, meshRot*, meshOffset and meshLook only apply with a mesh");
                    if (v.Sounds != null)
                    {
                        CheckAsset(v.Sounds.Attack, p + ".visuals.sounds.attack", AudioExtensions);
                        CheckAsset(v.Sounds.Hurt, p + ".visuals.sounds.hurt", AudioExtensions);
                        CheckAsset(v.Sounds.Death, p + ".visuals.sounds.death", AudioExtensions);
                    }
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
                if (u.Extra != null && u.Extra.ContainsKey("behaviours"))
                    Error(p + ".behaviours", "unknown behaviour (behaviours are rules since pack version 3; see docs/builder-engine.md)");
                // A unit's simple spawn-on-death rule runs on the game's own enemyToSpawnOnDeath (LogicLowering).
                int lowered = LogicLowering.LoweredRuleIndex(u, LogicLowering.IsUnitIn(pack));
                if (lowered >= 0) CheckUnitRef(LogicLowering.SpawnOnDeathUnit(u.Rules[lowered]), $"{p}.rules[{lowered}].do[0].entity", UnitSide.Enemy);
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
                // Lines the pack's levels add on this wave set's map count too (LevelOverlay makes them before the waves).
                if (lines != null)
                {
                    var added = (pack.Levels ?? []).Where(l => l?.WaveSet == ws.Id).SelectMany(l => l.SpawnLines?.Add ?? []).Select(a => a?.Id).Where(id => id != null).ToList();
                    if (added.Count > 0) lines = lines.Concat(added).Distinct().ToList();
                }
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
                CheckTroops(ws, p);
            }

            var levelIds = new HashSet<string>();
            for (int i = 0; i < (pack.Levels?.Count ?? 0); i++)
            {
                var l = pack.Levels[i];
                string p = $"levels[{i}]";
                if (l == null) { Error(p, "is empty"); continue; }
                if (!Ids.IsValid(l.Id)) Error(p + ".id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                else if (!levelIds.Add(l.Id)) Error(p + ".id", $"\"{l.Id}\" is used twice");
                CheckColors(l.Colors, p + ".colors");
                if (string.IsNullOrEmpty(l.Host) || (game != null && !game.HasHost(l.Host))) Error(p + ".host", $"unknown host map \"{l.Host}\"");
                if (l.WaveSet != null)
                {
                    var ws = pack.FindWaveSet(l.WaveSet);
                    if (ws == null) Error(p + ".waveSet", $"no wave set \"{l.WaveSet}\" in this pack");
                    else if (ws.Host != l.Host) Error(p + ".waveSet", $"wave set is for {ws.Host}, level is on {l.Host}");
                }
                // Layout edits: shape only (names on the host are checked when the level loads).
                var placed = new HashSet<string>();
                for (int j = 0; j < (l.BuildSlots?.Edit?.Count ?? 0); j++)
                {
                    var e = l.BuildSlots.Edit[j];
                    string ep = $"{p}.buildSlots.edit[{j}]";
                    if (e == null || string.IsNullOrEmpty(e.Ref)) { Error(ep, "needs the name of a build slot on the map"); continue; }
                    CheckPos(e.Pos, ep + ".pos", optional: true);
                    CheckRequirement(e.Requires, e.UpgradeCap, ep, e.Ref);
                    if (e.Ref == SlotRefs.Castle) Error(ep + ".ref", "\"castle\" can't be edited this way; name the castle's build slot");
                    CheckCosts(e.Upgrades, ep + ".upgrades");
                }
                for (int j = 0; j < (l.BuildSlots?.Add?.Count ?? 0); j++)
                {
                    var a = l.BuildSlots.Add[j];
                    string ap = $"{p}.buildSlots.add[{j}]";
                    if (a == null) { Error(ap, "is empty"); continue; }
                    if (!Ids.IsValid(a.Id)) Error(ap + ".id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                    else if (!placed.Add(a.Id)) Error(ap + ".id", $"\"{a.Id}\" is used twice in this level");
                    var abase = ContentRef.Parse(a.Base ?? "");
                    if (abase.Kind == RefKind.Building)
                    {
                        if (BuildingRefs.PathOf(abase.Id) == null)
                            Error(ap + ".base", $"no building kind \"{abase.Id}\" (kinds: {string.Join(", ", BuildingRefs.Kinds.Select(k => k.Id))})");
                    }
                    else if (abase.Kind != RefKind.Scene) Error(ap + ".base", "must be \"scene:<build slot on the map>\" or \"building:<kind>\"");
                    CheckPos(a.Pos, ap + ".pos", optional: false);
                    CheckRequirement(a.Requires, a.UpgradeCap, ap, SlotRefs.Added(a.Id ?? ""));
                    CheckCosts(a.Upgrades, ap + ".upgrades");
                }
                for (int j = 0; j < (l.Props?.Count ?? 0); j++)
                {
                    var pr = l.Props[j];
                    string pp = $"{p}.props[{j}]";
                    if (pr == null) { Error(pp, "is empty"); continue; }
                    if (!Ids.IsValid(pr.Id)) Error(pp + ".id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                    else if (!placed.Add(pr.Id)) Error(pp + ".id", $"\"{pr.Id}\" is used twice in this level");
                    if (pr.Base == null || !pr.Base.StartsWith("mesh:") || pr.Base.Length == 5 || pr.Base[5] == '@')
                        Error(pp + ".base", "must be \"mesh:<scenery mesh>\" (the map's or the scenery library's), optionally ending \"@<Map>\"");
                    CheckPos(pr.Pos, pp + ".pos", optional: false);
                    Range(pr.Scale, 0.1f, 10f, pp + ".scale");
                    CheckColors(pr.Colors, pp + ".colors");
                }
                for (int j = 0; j < (l.SpawnLines?.Edit?.Count ?? 0); j++)
                {
                    var e = l.SpawnLines.Edit[j];
                    string ep = $"{p}.spawnLines.edit[{j}]";
                    if (e == null || string.IsNullOrEmpty(e.Ref)) { Error(ep, "needs the name of a spawn line on the map"); continue; }
                    CheckPos(e.Pos, ep + ".pos", optional: true);
                }
                var hostLines = game != null && !string.IsNullOrEmpty(l.Host) ? game.SpawnLines(l.Host) : null;
                for (int j = 0; j < (l.SpawnLines?.Add?.Count ?? 0); j++)
                {
                    var a = l.SpawnLines.Add[j];
                    string lp = $"{p}.spawnLines.add[{j}]";
                    if (a == null) { Error(lp, "is empty"); continue; }
                    if (!Ids.IsValid(a.Id)) Error(lp + ".id", "must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
                    else if (!placed.Add("line:" + a.Id)) Error(lp + ".id", $"\"{a.Id}\" is used twice in this level");
                    else if (hostLines != null && hostLines.Contains(a.Id)) Error(lp + ".id", $"{l.Host} already has a spawn line \"{a.Id}\"");
                    CheckPos(a.Pos, lp + ".pos", optional: false);
                    Range(a.Width, 1f, 200f, lp + ".width");
                    if (!a.Ground && !a.Large && !a.Flying) Error(lp, "spawns nothing: turn on ground, large or flying");
                }
                CheckRemovedLines(l, p, hostLines);
                if (!LevelBases.IsKnown(l.Base)) Error(p + ".base", $"must be \"{LevelBases.Cleared}\", \"{LevelBases.Flat}\" or absent");
                CheckLevelRequirements(l, p);
            }
            LogicValidator.Validate(pack, game, issues);
            return issues;

            // spawnLines.remove: host lines only, and no night of the level may spawn from one.
            void CheckRemovedLines(LevelDef l, string p, IReadOnlyCollection<string> hostLines)
            {
                var removed = l.SpawnLines?.Remove;
                if (removed == null) return;
                var added = new HashSet<string>((l.SpawnLines.Add ?? []).Select(a => a?.Id).Where(id => id != null), StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int j = 0; j < removed.Count; j++)
                {
                    string rp = $"{p}.spawnLines.remove[{j}]";
                    string name = removed[j];
                    if (string.IsNullOrEmpty(name)) { Error(rp, "needs the name of a spawn line on the map"); continue; }
                    if (!seen.Add(name)) Warn(rp, $"\"{name}\" is listed twice");
                    else if (added.Contains(name)) Error(rp, $"\"{name}\" is one of this level's added lines: delete it from spawnLines.add instead");
                    else if (hostLines != null && !hostLines.Contains(name)) Error(rp, $"no spawn line \"{name}\" on {l.Host} (has: {string.Join(", ", hostLines)})");
                }
                if (seen.Count == 0) return;
                var ws = l.WaveSet != null ? pack.FindWaveSet(l.WaveSet) : null;
                if (ws != null)
                {
                    int wi = pack.WaveSets.IndexOf(ws);
                    for (int n = 0; n < (ws.Nights?.Count ?? 0); n++)
                        for (int s = 0; s < (ws.Nights[n]?.Spawns?.Count ?? 0); s++)
                            if (ws.Nights[n].Spawns[s]?.Line is string line && seen.Contains(line) && !added.Contains(line))
                                Error($"waveSets[{wi}].nights[{n}].spawns[{s}].line", $"spawn line \"{line}\" is removed in level \"{l.Id}\" ({p}.spawnLines.remove)");
                }
                else if (l.WaveSet == null && game is IHostWavesContext waves && !string.IsNullOrEmpty(l.Host) && waves.VanillaWaveLines(l.Host) is { } used)
                {
                    foreach (var name in seen.Where(used.Contains))
                        Error($"{p}.spawnLines.remove", $"the map's own nights spawn from \"{name}\": give the level a wave set that doesn't, or keep the line");
                }
            }

            void CheckRequirement(SlotRequirement r, int? cap, string path, string self)
            {
                if (r != null)
                {
                    if (r.Level < 0 || r.Level > 10) Error(path + ".requires.level", "must be between 0 and 10");
                    if (r.Slot != null && r.Slot == self) Error(path + ".requires.slot", "a slot can't need itself");
                    if (r.Slot != null && r.Slot.Length == 0) Error(path + ".requires.slot", "is empty (leave it out for \"from the start\")");
                }
                if (cap is int c && c != -100 && (c < -10 || c > 10)) Error(path + ".upgradeCap", "must be between -10 and 10, or -100 for no cap");
            }

            // Prerequisites between the level's own slots: added slots must exist, removed ones can't unlock anything,
            // and no loops (host slots' own prerequisites aren't known here; LevelOverlay checks them at load).
            void CheckLevelRequirements(LevelDef l, string p)
            {
                var adds = new HashSet<string>((l.BuildSlots?.Add ?? []).Where(a => a?.Id != null).Select(a => SlotRefs.Added(a.Id)), StringComparer.Ordinal);
                var removed = new HashSet<string>(l.BuildSlots?.Remove ?? [], StringComparer.Ordinal);
                var edges = new Dictionary<string, SlotRequirement>(StringComparer.Ordinal);
                var paths = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int j = 0; j < (l.BuildSlots?.Edit?.Count ?? 0); j++)
                {
                    var e = l.BuildSlots.Edit[j];
                    if (e?.Ref == null || e.Requires == null) continue;
                    edges[e.Ref] = e.Requires;
                    paths[e.Ref] = $"{p}.buildSlots.edit[{j}].requires";
                }
                for (int j = 0; j < (l.BuildSlots?.Add?.Count ?? 0); j++)
                {
                    var a = l.BuildSlots.Add[j];
                    if (a?.Id == null || a.Requires == null) continue;
                    edges[SlotRefs.Added(a.Id)] = a.Requires;
                    paths[SlotRefs.Added(a.Id)] = $"{p}.buildSlots.add[{j}].requires";
                }
                foreach (var kv in edges)
                {
                    string target = kv.Value.Slot;
                    if (target == null || target == SlotRefs.Castle) continue;
                    if (SlotRefs.IsAdded(target) && !adds.Contains(target)) Error(paths[kv.Key] + ".slot", $"no added slot \"{SlotRefs.AddedId(target)}\" in this level");
                    else if (removed.Contains(target)) Warn(paths[kv.Key] + ".slot", $"\"{target}\" is removed from the map, so this slot never appears");
                    else if (l.Base != null && !SlotRefs.IsAdded(target)) Warn(paths[kv.Key] + ".slot", $"\"{target}\" is cleared away with the map, so this slot never appears");
                }
                var graph = edges.ToDictionary(kv => kv.Key, kv => kv.Value.Slot == null ? null : kv.Value, StringComparer.Ordinal);
                foreach (var loop in LevelRequirements.Cycles(graph))
                    Error(paths[loop[0]], $"requirement loop: {string.Join(" needs ", loop.Concat([loop[0]]))}");
            }

            void CheckColors(string map, string path)
            {
                if (map != null && !LevelColors.IsMap(map))
                    Error(path, $"must be a main map ({string.Join(", ", LevelColors.Maps)}) or left out for the level's own colours");
            }

            void Range(float? value, float min, float max, string path)
            {
                if (value is float f && (float.IsNaN(f) || f < min || f > max)) Error(path, $"must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}");
            }

            void CheckPos(float[] pos, string path, bool optional)
            {
                if (pos == null) { if (!optional) Error(path, "needs a position [x, y, z]"); return; }
                if (pos.Length != 3 || pos.Any(v => float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 5000f)) Error(path, "must be [x, y, z] on the map");
            }

            void CheckCosts(List<UpgradeEdit> list, string path)
            {
                for (int j = 0; j < (list?.Count ?? 0); j++)
                    if (list[j]?.Cost is int c && (c < 0 || c > 10000)) Error($"{path}[{j}].cost", "must be between 0 and 10000");
            }

            void CheckAsset(string reference, string path, string[] extensions)
            {
                if (reference == null) return;
                var r = ContentRef.Parse(reference);
                if (r.Kind != RefKind.Asset) Error(path, "must be \"asset:<key>\"");
                else if (pack.Assets == null || !pack.Assets.TryGetValue(r.Id, out var def)) Error(path, $"no asset \"{r.Id}\" in this pack's \"assets\"");
                else if (!string.IsNullOrEmpty(def?.Path) && !extensions.Contains(System.IO.Path.GetExtension(def.Path).ToLowerInvariant()))
                    Error(path, $"asset \"{r.Id}\" ({def.Path}) must be a {string.Join(" or ", extensions)} file");
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

            void CheckTroops(WaveSetDef ws, string p)
            {
                var troopGame = game as ITroopValidationContext;
                var seen = new HashSet<string>();
                for (int t = 0; t < (ws.Troops?.Count ?? 0); t++)
                {
                    var troop = ws.Troops[t];
                    string tp = $"{p}.troops[{t}]";
                    if (troop == null) { Error(tp, "is empty"); continue; }
                    var r = ContentRef.Parse(troop.Unit);
                    if (r.Kind == RefKind.Vanilla) { Error(tp + ".unit", "must be a custom player unit (its base is the troop it replaces)"); continue; }
                    CheckUnitRef(troop.Unit, tp + ".unit", UnitSide.Player);
                    // The base is only known for units in this pack; another pack's unit is checked when it loads.
                    var def = r.Kind == RefKind.LocalUnit || (r.Kind == RefKind.PackUnit && r.Pack == pack.Id) ? pack.FindUnit(r.Id) : null;
                    string baseId = def != null && def.Side == UnitSide.Player ? ContentRef.Parse(def.Base).Id : null;
                    if (baseId == null) continue;
                    var buildings = troopGame?.TroopBuildings(baseId);
                    if (buildings != null && buildings.Count == 0) Error(tp + ".unit", $"no building trains \"{baseId}\" (`builder dump` lists troops and their buildings)");
                    else if (troop.Building != null && buildings != null && !buildings.Any(b => string.Equals(b, troop.Building, StringComparison.OrdinalIgnoreCase)))
                        Error(tp + ".building", $"\"{troop.Building}\" doesn't train {baseId} (trained by: {string.Join(", ", buildings)})");
                    if (!seen.Add(baseId.ToLowerInvariant() + "|" + (troop.Building ?? "").ToLowerInvariant()))
                        Error(tp, $"{baseId}{(troop.Building == null ? "" : " in " + troop.Building)} is replaced twice");
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
