using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Model.Logic;
using PalaceMod.Builder.Studio.Enemies;
using PalaceMod.Builder.Studio.Logic;
using UnityEngine;

/*
    The Entity Studio's panels, drawn into the Enemy Studio's dock (EnemyDock hands over in thing mode):

    - palette "Things": the draft's things as tiles grouped by kind, "+ New" (the kind picker), Duplicate, Delete;
    - inspector: the kind picker for a new thing, else Look (the model's fit and tint), Parts (the behaviour editor's
      PartsPanel: kind, name, side, lifetime and every component) and Behaviour (variables and rules);
    - bottom left: the test's settings (distance, height, a moving target) and its button;
    - readout: what the thing is in numbers, who uses it, and the last test's result (hits, flight, damage, rules fired).
*/
namespace PalaceMod.Builder.Studio.Entities
{
    internal static class EntityPanels
    {
        private static string Dim(string s) => LogicFields.Dim(s);
        private static string Esc(string s) => LogicFields.Esc(s);
        private static string Gold(string s) => $"<color=#{ModStyle.Hex(ModStyle.Gold)}>{s}</color>";

        public static LogicOwner Owner()
        {
            var e = EntityStudio.Entity;
            return e == null ? null : LogicOwner.Entity(e.Id, string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name);
        }

        // ---- Palette ------------------------------------------------------------------------------------------------

        private static float deleteArmed = -10f;

        public static void Palette(ModPage page)
        {
            var all = EntityStudio.All.ToList();
            var cur = EntityStudio.Entity;
            foreach (var group in all.GroupBy(ThingKinds.Of).OrderBy(g => Array.FindIndex(ThingKinds.All, k => k.Id == g.Key) is int i && i >= 0 ? i : 99))
            {
                StudioDock.SmallHeader(page, ThingKinds.Label(group.Key) + (group.Count() > 1 ? "s" : ""));
                EnemyDock.Grid(page, "Thing", group.ToList(), 82f, e => (e.Id, EntityStudio.Portrait(e.Id), EnemyDock.Short(e.Name ?? e.Id), null, e == cur && cur != null, e.Name ?? e.Id,
                    $"{ThingKinds.Label(ThingKinds.Of(e))}{(e.Visual?.Mesh != null ? " · " + e.Visual.Mesh : "")}\nId: {e.Id}"), e =>
                    {
                        if (EntityStudio.Active) EntityStudio.Select(e.Id);
                        else EntityStudio.Enter(e.Id);
                    });
            }
            if (all.Count == 0) page.Text(Dim("No things yet: projectiles, props, pickups, areas and logic objects your enemies and levels use."));
            EnemyDock.Grid(page, "Thing", new[] { "new" }, 82f, _ => ("new", null, "+ New", null, EntityStudio.Active && cur == null, "New thing", "A projectile, prop, pickup, area or logic object."),
                _ => { if (EntityStudio.Active) EntityStudio.Select(null); else EntityStudio.Enter(null); });
            if (cur == null) return;
            page.Button("Duplicate", EntityStudio.Duplicate, "Makes a copy of this thing to vary.");
            ModRow row = null;
            var users = ThingKinds.UsedBy(EntityStudio.Draft?.Pack, cur.Id);
            row = page.Row("Delete", null, users.Count == 0 ? "Deletes this thing." : "Deletes this thing. Used by: " + string.Join(", ", users) + " (they show a problem until changed).", () =>
            {
                if (Time.unscaledTime - deleteArmed > 3f) { deleteArmed = Time.unscaledTime; row.SetLabel("Delete: press again"); return; }
                deleteArmed = -10f;
                EntityStudio.Delete();
            });
        }

        // ---- Inspector ----------------------------------------------------------------------------------------------

        public static void Inspector(ModPage page, Action<IReadOnlyList<(string id, string label)>, Func<string>, Action<string>> tabs)
        {
            var e = EntityStudio.Entity;
            if (e == null) { KindPicker(page); return; }
            if (EntityStudio.PanelTabs.All(t => t.id != EnemyDock.PanelTab)) EnemyDock.PanelTab = "look";
            tabs(EntityStudio.PanelTabs, () => EnemyDock.PanelTab, id => { EnemyDock.PanelTab = id; EnemyDock.RefreshInspector(); });
            var owner = Owner();
            switch (EnemyDock.PanelTab)
            {
                case "parts": PartsPanel.Build(page, owner); break;
                case "behaviour":
                    page.FieldIndent = Math.Min(page.FieldIndent, 150f);
                    BehaviourPanel.Variables(page, owner, "Variables", "Numbers, yes/no, text or positions this thing keeps (self.$name).");
                    BehaviourPanel.Rules(page, owner, "Rules");
                    break;
                default: Look(page, e); break;
            }
        }

        private static void KindPicker(ModPage page)
        {
            StudioDock.SmallHeader(page, "New thing");
            page.Text(Dim("What kind? Each starts with the parts it needs; Parts adds or removes any of them later."));
            foreach (var k in ThingKinds.All)
            {
                var kind = k;
                page.Button(k.Label, () => EntityStudio.New(kind.Id), k.Description);
                page.Text(Dim(k.Description));
            }
        }

        private static void Look(ModPage page, EntityDef e)
        {
            var v = e.Visual;
            StudioDock.SmallHeader(page, "Model");
            string mesh = v?.Mesh;
            page.Row("Source", mesh == null ? Dim("none (invisible)") : Esc(mesh), "Its model: a scenery mesh, a unit's model or an imported file. Pick one in the palette's Look tab.",
                () => { EnemyDock.PaletteTab = "look"; EnemyDock.RefreshAll(); });
            if (mesh == null) { page.Text(Dim("Pick a model in the palette's Look tab, or leave it invisible (areas and logic objects usually are).")); return; }
            page.Number("Scale", () => EntityStudio.Entity?.Visual?.MeshScale ?? 1f, x => EntityStudio.EditVisual("Set model scale", vis => vis.MeshScale = Mathf.Abs(x - 1f) < 1e-4f ? null : Mathf.Max(0.001f, x), coalesce: "meshScale"),
                0.01f, 100f, 0.05f, x => x.ToString("0.###") + "×", () => "The model's size (1: as the map draws it).");
            string[] axes = ["X", "Y", "Z"];
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                page.Number("Move " + axes[k], () => EntityStudio.Entity?.Visual?.MeshOffset is { Length: 3 } o ? o[k] : 0f,
                    x => EntityStudio.EditVisual("Move the model", vis =>
                    {
                        var o = vis.MeshOffset is { Length: 3 } cur ? (float[])cur.Clone() : new float[3];
                        o[k] = Mathf.Clamp(x, -50f, 50f);
                        vis.MeshOffset = o.All(c => Mathf.Abs(c) < 1e-4f) ? null : o;
                    }, coalesce: "meshOffset" + k), -50f, 50f, 0.05f, x => x.ToString("0.##") + " m",
                    () => k switch { 0 => "Right (+) or left, in metres.", 1 => "Up (+) or down, in metres (lift a projectile's model to its middle).", _ => "Forward (+) or back, in metres." });
            }
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                Func<VisualsDef, float?> get = k switch { 0 => vis => vis?.MeshRotX, 1 => vis => vis?.MeshRotY, _ => vis => vis?.MeshRotZ };
                page.Number("Turn " + axes[k], () => get(EntityStudio.Entity?.Visual) ?? 0f,
                    x => EntityStudio.EditVisual("Turn the model", vis =>
                    {
                        float? val = Mathf.Abs(x) < 1e-3f ? null : Mathf.Clamp(x, -360f, 360f);
                        if (k == 0) vis.MeshRotX = val; else if (k == 1) vis.MeshRotY = val; else vis.MeshRotZ = val;
                    }, coalesce: "meshRot" + k), -360f, 360f, 5f, x => x.ToString("0.#") + "°",
                    () => k switch { 0 => "Tilts it forward or back (a tree thrown flat: 90).", 1 => "Turns it to face its way of travel.", _ => "Tilts it sideways." });
            }
            string hex = v.Tint;
            page.Row("Tint", hex != null && ColorTools.TryParseHex(hex, out var tc) ? $"<color=#{ColorUtility.ToHtmlStringRGB(tc)}>■■</color>  {hex}" : Dim("none"),
                "Multiplies the model's colours.", () =>
                {
                    string cur = EntityStudio.Entity?.Visual?.Tint;
                    ModColorPicker.Open("Tint", cur != null && ColorTools.TryParseHex(cur, out var c) ? c : null,
                        picked => EntityStudio.EditVisual(picked == null ? "Remove tint" : "Set tint", vis => vis.Tint = picked is Color pc ? "#" + ColorUtility.ToHtmlStringRGB(pc).ToLowerInvariant() : null, coalesce: "tint"),
                        null, allowNone: true, noneLabel: "No tint");
                });
            if (e.Projectile?.Spin is { Length: 3 } spin && spin.Any(s => Mathf.Abs(s) > 0.01f))
                page.Text(Dim($"Spins {spin[0]:0}/{spin[1]:0}/{spin[2]:0}°/s in flight (Parts › Flight); Launch shows it."));
        }

        // ---- Test ---------------------------------------------------------------------------------------------------

        public static void Test()
        {
            if (EntityTest.Running) { EntityTest.Clear(); return; }
            try { Log.For<BuilderManager>().Info("Entity Studio: " + EntityTest.Start()); }
            catch (Exception e) { Notify.Show("Can't test: " + e.Message, NotifyKind.Warning, 4f, "builder-entity"); }
        }

        public static void Arena(ModPage page)
        {
            var e = EntityStudio.Entity;
            if (e == null) { StudioDock.SmallHeader(page, "Test"); page.Text(Dim("Pick a kind first.")); return; }
            bool projectile = EntityStudio.IsProjectile;
            StudioDock.SmallHeader(page, projectile ? "Launch test" : "Try it");
            if (projectile)
            {
                page.Number("Distance", () => EntityTest.Distance, x => EntityTest.Distance = x, 2f, 60f, 1f, x => x.ToString("0") + " m", () => "How far away the dummy stands.");
                page.Number("Height", () => EntityTest.Height, x => EntityTest.Height = x, 0.2f, 10f, 0.25f, x => x.ToString("0.##") + " m", () => "The height it's launched from (an attack's launch.from).");
            }
            page.Toggle("Target moves", () => EntityTest.Moving, x => EntityTest.Moving = x, () => projectile ? "The dummy strolls across the line of fire, so homing shows." : "A dummy strolls past it.");
            page.Button(EntityTest.Running ? "Stop" : projectile ? "Launch" : "Try", () => { Test(); EnemyDock.RefreshAll(); },
                projectile ? "Launches it at a dummy through the behaviour engine: flight, spin, hits and its impact rules (F5)." : "Spawns it on the stage for a few seconds with its rules running (F5).");
        }

        // ---- Readout ------------------------------------------------------------------------------------------------

        private static string N(LNum n, string unit = "") => n == null ? "-" : n.IsExpr ? "=" + Esc(n.Expr) : n.Value is double d ? d.ToString("0.##") + unit : "-";

        public static void Readout(ModPage page)
        {
            var e = EntityStudio.Entity;
            if (e == null) { StudioDock.SmallHeader(page, "New thing"); page.Text(Dim("Pick its kind in the panel on the right.")); return; }
            if (EntityTest.Running || EntityTest.HasResult)
            {
                StudioDock.SmallHeader(page, EntityTest.Running ? $"Testing · {EntityTest.Elapsed:0.0} s" : EntityTest.Result);
                var lines = new List<string>();
                if (EntityTest.Running)
                    lines.Add($"Flown {Gold(EntityTest.Flown.ToString("0.#") + " m")}   Hits {Gold(EntityTest.Impacts.Count.ToString())}   Dummy took {Gold(EntityTest.DamageToDummy.ToString("0.#"))}");
                if (EntityTest.Problem != null) lines.Add($"<color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Warning))}>{Esc(EntityTest.Problem)}</color>");
                if (EntityTest.Fired.Count > 0) lines.Add(Dim("Rules: ") + string.Join("\n", EntityTest.Fired.Skip(Math.Max(0, EntityTest.Fired.Count - 5)).Select(Esc)));
                else if (!EntityTest.Running && EntityStudio.IsProjectile && EntityTest.DamageToDummy <= 0f && (e.Rules == null || e.Rules.Count == 0))
                    lines.Add(Dim("No rules ran: give it an impact rule (Behaviour › + rule › When it hits) to make a hit do something."));
                if (lines.Count > 0) page.Text(string.Join("\n", lines));
            }
            StudioDock.SmallHeader(page, $"{Esc(e.Name ?? e.Id)} {Dim("· " + ThingKinds.Label(ThingKinds.Of(e)))}");
            var facts = new List<string>();
            if (e.Projectile is { } p)
                facts.Add($"Speed {Gold(N(p.Speed, " m/s"))}   Homing {Gold(N(p.Homing))}   Arc {Gold(N(p.Arc))}{(p.Gravity != null ? $"   Gravity {Gold(N(p.Gravity))}" : "")}" +
                    (p.Spin is { Length: 3 } s ? $"   Spin {Gold($"{s[0]:0}/{s[1]:0}/{s[2]:0}°/s")}" : "") + $"   Hits {Gold(Esc(p.Hits ?? "foe"))}");
            if (e.Body is { } b)
                facts.Add($"Body {Gold(b.Box is { Length: 2 } box ? $"{box[0]:0.##}×{box[1]:0.##} m" : "r " + N(b.Radius, " m"))} h {Gold(N(b.Height, " m"))}" +
                    string.Concat(new[] { b.Solid == true ? " · solid" : null, b.Walkable == true ? " · walkable" : null, b.Hittable == true ? $" · {N(b.Hp)} hp" : null, b.Flying == true ? " · flying" : null }.Where(x => x != null)));
            if (e.Area?.Shape is { } sh) facts.Add($"Area {Gold(sh.Circle != null ? "r " + N(sh.Circle.Radius, " m") : sh.Rect?.Size is { Length: 2 } sz ? $"{sz[0]:0.##}×{sz[1]:0.##} m" : "polygon")} watches {Gold(Esc(e.Area.Watch ?? "units and the king"))}");
            if (e.Pickup is { } pk) facts.Add($"Picked up by {Gold(Esc(pk.By ?? "king"))} within {Gold(N(pk.Radius, " m"))}");
            facts.Add($"Side {Gold(Esc(e.Team ?? "not set"))}   Lasts {Gold(e.Lifetime == null ? "forever" : N(e.Lifetime, " s"))}   Rules {Gold((e.Rules?.Count ?? 0).ToString())}" +
                (EntityStudio.ModelError != null ? $"\n<color=#{ModStyle.Hex(ModStyle.KindColor(NotifyKind.Error))}>{Esc(EntityStudio.ModelError)}</color>" : ""));
            page.Text(string.Join("\n", facts));
            var users = ThingKinds.UsedBy(EntityStudio.Draft?.Pack, e.Id);
            StudioDock.SmallHeader(page, "Used by");
            page.Text(users.Count == 0 ? Dim("Nothing yet: an enemy's attack (Moves › Launches) or a level's Logic palette.") : Esc(string.Join("\n", users)));
        }
    }
}
