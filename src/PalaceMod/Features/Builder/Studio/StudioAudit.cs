using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PalaceMod.Builder.Editor;
using UnityEngine;

/*
    `builder-studio audit`: places every palette kind on the open map (Slots, Scenery and every Library item), checks
    that what's created is what the ghost showed (the same meshes at the same size), then undoes it. Also checks that
    resizing one prop leaves other copies of the same mesh alone. Prints one line per failure and a summary.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioAudit
    {
        public static string Run()
        {
            if (!StudioScene.Ready || Studio.Draft == null) throw new InvalidOperationException("the Studio isn't open");
            var kinds = new List<(string tab, StudioPlaceable p)>();
            foreach (var type in StudioScene.Natives.Where(n => n.CanBase && n.Look != null).Select(n => n.Type).Distinct())
                if (StudioPalette.SlotKind(type) is { } s) kinds.Add(("Slots", s));
            foreach (var b in StudioScene.PropBases) kinds.Add(("Scenery", StudioPalette.PropKind(b)));
            var browser = StudioServices.AssetBrowser;
            if (browser != null)
                foreach (var it in browser.Query(null, null) ?? [])
                    if (it.Ready) kinds.Add(("Library", StudioPalette.LibraryKind(browser, it)));

            var at = StudioScene.Ground(StudioCamera.Pivot);
            var fails = new List<string>();
            int ok = 0;
            foreach (var (tab, p) in kinds)
            {
                string why = Check(p, at);
                if (why == null) ok++;
                else fails.Add($"{tab} {p.Name}: {why}");
            }
            fails.AddRange(ScaleCrosstalk(kinds.Where(k => k.tab != "Slots").Select(k => k.p), at));
            var sb = new StringBuilder($"placed {kinds.Count} kinds: {ok} match their ghost, {kinds.Count - ok} don't");
            foreach (var f in fails) sb.Append('\n').Append(f);
            return sb.ToString();
        }

        private static string Check(StudioPlaceable p, Vector3 at)
        {
            GameObject ghost = null;
            string key = null;
            try
            {
                ghost = p.MakeGhost?.Invoke(StudioScene.Root);
                if (ghost == null) return "no ghost";
                key = p.Place?.Invoke(at, 0f);
                if (key == null) return "nothing placed";
                var item = StudioScene.Find(key);
                if (item?.Proxy == null) return $"placed {key} but it has no object";
                ghost.transform.SetPositionAndRotation(item.Proxy.transform.position, item.Proxy.transform.rotation);
                var want = Meshes(ghost);
                var got = Meshes(item.Proxy);
                if (!want.SequenceEqual(got)) return $"ghost shows [{string.Join(", ", want)}], placed [{string.Join(", ", got)}]";
                var a = Size(ghost);
                var b = Size(item.Proxy);
                if ((a - b).magnitude > 0.02f * Mathf.Max(0.1f, a.magnitude)) return $"ghost is {a:F2}, placed is {b:F2}";
                return null;
            }
            catch (Exception e) { return "threw " + e.Message; }
            finally
            {
                if (ghost != null) UnityEngine.Object.DestroyImmediate(ghost);
                if (key != null) BuilderEditor.Undo();
            }
        }

        /// <summary>Resizing one prop must not change another copy of the same mesh.</summary>
        private static IEnumerable<string> ScaleCrosstalk(IEnumerable<StudioPlaceable> props, Vector3 at)
        {
            var fails = new List<string>();
            foreach (var p in props.GroupBy(x => x.Id).Select(g => g.First()))
            {
                string a = null, b = null;
                int edits = 0;
                try
                {
                    a = p.Place(at, 0f); if (a != null) edits++;
                    b = p.Place(at + new Vector3(6f, 0f, 0f), 0f); if (b != null) edits++;
                    var ia = StudioScene.Find(a);
                    var ib = StudioScene.Find(b);
                    if (ia?.Proxy == null || ib?.Proxy == null) { fails.Add($"Resize {p.Name}: couldn't place two"); continue; }
                    var before = ib.Proxy.transform.lossyScale;
                    var aBefore = ia.Proxy.transform.lossyScale;
                    float sizeA = StudioEdits.PropDef(ia)?.Scale ?? 1f;
                    StudioEdits.SetProps([ia], "Set size", d => d.Scale = sizeA * 2f);
                    edits++;
                    ia = StudioScene.Find(a);
                    ib = StudioScene.Find(b);
                    var after = ib.Proxy.transform.lossyScale;
                    var aAfter = ia.Proxy.transform.lossyScale;
                    if ((after - before).magnitude > 0.001f) fails.Add($"Resize {p.Name}: the other copy went {before:F2} → {after:F2}");
                    if ((aAfter - aBefore * 2f).magnitude > 0.01f * aBefore.magnitude) fails.Add($"Resize {p.Name}: doubled {aBefore:F2} → {aAfter:F2}");
                }
                catch (Exception e) { fails.Add($"Resize {p.Name}: threw {e.Message}"); }
                finally
                {
                    for (int i = 0; i < edits; i++) BuilderEditor.Undo();
                }
            }
            return fails;
        }

        private static List<string> Meshes(GameObject go) =>
            go.GetComponentsInChildren<MeshFilter>(false)
                .Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() is { enabled: true, forceRenderingOff: false })
                .Select(f => f.sharedMesh.name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        private static Vector3 Size(GameObject go)
        {
            Bounds? all = null;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!r.enabled || r.forceRenderingOff) continue;
                if (all is Bounds x) { x.Encapsulate(r.bounds); all = x; } else all = r.bounds;
            }
            return all?.size ?? Vector3.zero;
        }
    }
}
