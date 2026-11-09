using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

/*
    Savestates and retry-day for the logic world (docs/builder-engine.md, "Determinism, savestates and retry").
    Captured: time, the random source, level vars and HUD-relevant state, every entity's vars/labels/enabled and its
    rules' state (enabled, once, cooldown, timer phase), statuses (through IStatModifiers), and every live
    Builder-spawned entity, which is respawned on restore. Not captured: pending waits, delayed signals, projectiles
    in flight (docs: "not restored").

    Restore runs on a fresh world whose level has been set up again (places, attaches): state is matched by entity id.
*/
namespace PalaceMod.Builder.Logic
{
    public sealed partial class LogicWorld
    {
        public JObject Capture()
        {
            var o = new JObject
            {
                ["v"] = 1,
                ["time"] = Time,
                ["step"] = stepTime,
                ["rng"] = Random.State,
                ["counter"] = spawnCounter,
            };
            var ents = new JObject();
            var spawned = new JArray();
            // Vanilla objects are kept only when rules gave them vars or labels (their ids are scene paths, which
            // a reload reproduces for placed buildings but not for units).
            foreach (var e in byId.Values
                .Where(x => !x.Destroyed && x != Level && (!x.Id.StartsWith("vanilla:") || x.Vars.Count > 0 || x.Labels.Count > 0))
                .OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var s = EntityState(e);
                if (e.Spawned)
                {
                    if (e.Kind == "projectile") continue;   // in flight: not restored
                    s["template"] = e.Template;
                    s["team"] = e.Team;
                    s["kind"] = e.Kind;
                    s["owner"] = e.Owner?.Id;
                    var p = e.Position;
                    s["pos"] = new JArray(p.X, p.Y, p.Z);
                    s["rotY"] = e.Body?.Facing ?? 0;
                    if (e.Body != null) s["hp"] = e.Body.Hp;
                    spawned.Add(s);
                }
                else ents[e.Id] = s;
            }
            ents["level"] = EntityState(Level);
            o["entities"] = ents;
            o["spawned"] = spawned;
            var parts = new JObject();
            foreach (var part in LogicSaveState.Parts)
            {
                try { if (part.Capture(this) is { } tok) parts[part.Id] = tok; }
                catch (Exception ex) { TraceAdd("level", null, null, "savestate", TraceOutcome.Error, $"{part.Id}: {ex.Message}", 0, null); }
            }
            o["parts"] = parts;
            return o;
        }

        private JObject EntityState(LogicEntity e)
        {
            var s = new JObject { ["id"] = e.Id };
            if (e.Vars.Count > 0)
            {
                var vars = new JObject();
                foreach (var kv in e.Vars.OrderBy(k => k.Key, StringComparer.Ordinal)) vars[kv.Key] = Values.ToJson(kv.Value);
                s["vars"] = vars;
            }
            if (e.Labels.Count > 0) s["labels"] = new JArray(e.Labels.OrderBy(l => l, StringComparer.Ordinal));
            if (!e.Enabled) s["enabled"] = false;
            if (e.Rules.Count > 0)
            {
                var rules = new JArray();
                foreach (var ri in e.Rules)
                    rules.Add(new JObject
                    {
                        ["path"] = ri.Rule.Path,
                        ["on"] = ri.Enabled,
                        ["done"] = ri.Done,
                        ["cd"] = ri.CooldownUntil > Time ? ri.CooldownUntil - Time : 0,
                        ["next"] = double.IsNaN(ri.NextTime) ? null : ri.NextTime - LocalTime(e),
                        ["ticks"] = ri.Ticks,
                        ["armed"] = ri.HpArmed,
                    });
                s["rules"] = rules;
            }
            if (LogicServices.Stats != null)
            {
                try { if (LogicServices.Stats.Capture(e) is { } st) s["stats"] = st; }
                catch (Exception ex) { TraceAdd(e.Id, null, null, "savestate", TraceOutcome.Error, $"stats: {ex.Message}", 0, null); }
            }
            return s;
        }

        /// <summary>
        /// Apply a captured state to this (freshly set-up) world. <paramref name="setHp"/> restores a respawned
        /// entity's hit points (the game side owns Hp). Returns a one-line summary.
        /// </summary>
        public string Restore(JObject o, Action<LogicEntity, float> setHp = null)
        {
            if (o == null) return "no logic state";
            Time = (double?)o["time"] ?? Time;
            stepTime = (double?)o["step"] ?? Time;
            if ((string)o["rng"] is { Length: 32 } rng) Random.State = rng;
            spawnCounter = Math.Max(spawnCounter, (int?)o["counter"] ?? 0);

            // Respawn spawned entities first, so entity vars may refer to them.
            int respawned = 0, failed = 0;
            var pendingStates = new List<(LogicEntity e, JObject s)>();
            if (o["spawned"] is JArray spawned)
                foreach (var s in spawned.OfType<JObject>())
                {
                    string id = (string)s["id"], template = (string)s["template"];
                    if (id == null || template == null) continue;
                    var e = Describe(template, null, id, (string)s["team"], null);
                    e.Kind = (string)s["kind"] ?? e.Kind;
                    e.Spawned = true;
                    var pos = s["pos"] is JArray pa && pa.Count == 3 ? new Vec((float)pa[0], (float)pa[1], (float)pa[2]) : Vec.Zero;
                    e.StaticPos = pos;
                    string error = "entity bodies aren't available";
                    if (LogicServices.Factory != null && LogicServices.Factory.Build(e, pos, (float?)s["rotY"] ?? 0, out error))
                    {
                        if (!byId.ContainsKey(e.Id)) Add(e);
                        respawned++;
                        if (s["hp"] != null) setHp?.Invoke(e, (float)s["hp"]);
                        pendingStates.Add((e, s));
                    }
                    else
                    {
                        pendingPlace.Remove(e);
                        failed++;
                        TraceAdd(id, null, null, "savestate", TraceOutcome.Error, $"couldn't respawn \"{template}\": {error}", 0, null);
                    }
                }
            foreach (var (e, s) in pendingStates)
                if (s["owner"] is JValue ow && ow.Type == JTokenType.String) e.Owner = Find((string)ow);

            int matched = 0, missing = 0;
            if (o["entities"] is JObject ents)
                foreach (var p in ents.Properties())
                {
                    var e = p.Name == "level" ? Level : Find(p.Name);
                    if (e == null) { missing++; continue; }
                    ApplyState(e, (JObject)p.Value);
                    matched++;
                }
            foreach (var (e, s) in pendingStates) ApplyState(e, s);

            // The respawn queued `spawn` events; a restore is not a spawn.
            var keep = queue.Where(ev => !(ev.Name == "spawn" && pendingStates.Any(ps => ps.e == ev.Subject))).ToList();
            queue.Clear();
            foreach (var ev in keep) queue.Enqueue(ev);

            if (o["parts"] is JObject parts)
                foreach (var part in LogicSaveState.Parts)
                    if (parts[part.Id] is { } tok)
                    {
                        try { part.Restore(this, tok); }
                        catch (Exception ex) { TraceAdd("level", null, null, "savestate", TraceOutcome.Error, $"{part.Id}: {ex.Message}", 0, null); }
                    }
            return $"logic: {matched} entities, {respawned} respawned" + (failed > 0 ? $", {failed} failed" : "") + (missing > 0 ? $", {missing} not found" : "");
        }

        private void ApplyState(LogicEntity e, JObject s)
        {
            if (s["vars"] is JObject vars)
            {
                e.Vars.Clear();
                foreach (var v in vars.Properties()) e.Vars[v.Name] = Values.FromJson(v.Value, Find);
            }
            if (s["labels"] is JArray labels) { e.Labels.Clear(); foreach (var l in labels) e.Labels.Add((string)l); }
            e.Enabled = (bool?)s["enabled"] ?? true;
            if (s["rules"] is JArray rules)
                foreach (var rs in rules.OfType<JObject>())
                {
                    var ri = e.Rules.FirstOrDefault(r => r.Rule.Path == (string)rs["path"]);
                    if (ri == null) continue;
                    ri.Enabled = (bool?)rs["on"] ?? ri.Enabled;
                    ri.Done = (bool?)rs["done"] ?? false;
                    double cd = (double?)rs["cd"] ?? 0;
                    ri.CooldownUntil = cd > 0 ? Time + cd : 0;
                    if (rs["next"] is JValue nv && nv.Type != JTokenType.Null)
                    {
                        ri.NextTime = LocalTime(e) + (double)nv;
                        if (!timers.Contains(ri)) timers.Add(ri);
                    }
                    else if (ri.Rule.Event == "after") { ri.NextTime = double.NaN; timers.Remove(ri); }
                    ri.Ticks = (int?)rs["ticks"] ?? 0;
                    ri.HpArmed = (bool?)rs["armed"] ?? true;
                }
            if (s["stats"] is { } st && LogicServices.Stats != null)
            {
                try { LogicServices.Stats.Restore(e, st); }
                catch (Exception ex) { TraceAdd(e.Id, null, null, "savestate", TraceOutcome.Error, $"stats: {ex.Message}", 0, null); }
            }
        }
    }
}
