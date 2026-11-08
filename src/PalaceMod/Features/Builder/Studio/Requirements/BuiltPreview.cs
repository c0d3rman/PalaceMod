using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    Previews (docs/builder-ux.md, "What's what"):
    - Built preview (View ▾, or hold its key, Alt by default): every slot proxy shows its building at level 1 in the
      game's preview material; the inspector's Preview stepper shows one slot at any level (LayoutScene.ShowBuilt).
    - X-ray (View ▾ / X, StudioViewOptions.XRay): the map's scenery is drawn in the same see-through preview material,
      so slots, rings and labels behind trees and rocks show.
    Proxies are recreated on reconcile only for new items, so the wanted look is re-applied whenever a proxy changes.
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal sealed class BuiltPreview : IStudioOverlay
    {
        public static readonly BuiltPreview Instance = new();
        public static Keybind HoldKey;

        private static readonly Dictionary<string, int> levels = [];
        private readonly Dictionary<string, (int proxy, int level)> applied = [];

        public static bool On;

        public static int LevelOf(string key) => levels.TryGetValue(key, out int l) ? l : (On ? 1 : 0);

        public static void SetLevel(string key, int level)
        {
            levels[key] = level;
            Studio.RefreshPanels();
        }

        public void Draw()
        {
            bool hold = HoldKey?.Held ?? false;
            foreach (var item in Studio.Items)
            {
                if (item.Proxy == null || item.Kind is not (StudioItemKind.Slot or StudioItemKind.Added)) continue;
                int want = levels.TryGetValue(item.Key, out int l) ? l : (On || hold ? 1 : 0);
                int id = item.Proxy.GetInstanceID();
                if (applied.TryGetValue(item.Key, out var was) && was.proxy == id && was.level == want) continue;
                if (want == 0 && !applied.ContainsKey(item.Key)) { applied[item.Key] = (id, 0); continue; }
                LayoutScene.ShowBuilt(item.Proxy, want);
                applied[item.Key] = (id, want);
            }
            XRay.Update(StudioViewOptions.XRay);
        }

        public void Hide()
        {
            applied.Clear();
            levels.Clear();
            XRay.Update(false);
        }

        /// <summary>Scenery drawn see-through while X-ray is on.</summary>
        private static class XRay
        {
            private static readonly List<(Renderer r, Material[] mats)> faded = [];
            private static bool active;

            public static void Update(bool on)
            {
                if (on == active) return;
                active = on;
                if (!on)
                {
                    foreach (var (r, mats) in faded) if (r != null) r.sharedMaterials = mats;
                    faded.Clear();
                    return;
                }
                var level = Studio.Level;
                var preview = ProxyLook.PreviewMaterial();
                if (level == null || preview == null) return;
                foreach (var r in EmptyBase.Scenery(LayoutScene.Host(level.Host), out _))
                {
                    faded.Add((r, r.sharedMaterials));
                    r.sharedMaterials = Enumerable.Repeat(preview, r.sharedMaterials.Length).ToArray();
                }
            }
        }
    }
}
