using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

/*
    Labels over the map's objects: one pooled screen-space layer under the Studio's panels. Each shows the object's
    name (in its kind's colour), its cost chain and its requirement badge, as View ▾ Labels asks. Density follows the
    zoom (short names when zoomed out; full on hover and selection), and labels never overlap: lower-priority ones
    hide (selection > hover > castle > nearest).
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioLabels
    {
        /// <summary>Studio setting: pan when the pointer touches a screen edge.</summary>
        public static bool EdgePan;

        private static RectTransform layer;
        private static readonly List<TextMeshProUGUI> pool = [];
        private const int Max = 160;

        public static void Clear()
        {
            if (layer != null) Object.Destroy(layer.gameObject);
            layer = null;
            pool.Clear();
        }

        private static bool Ensure()
        {
            var root = StudioDock.Dock?.Root;
            if (root == null) return false;
            if (layer != null && layer.parent == root) return true;
            Clear();
            var go = new GameObject("Labels", typeof(RectTransform));
            go.transform.SetParent(root, false);
            go.transform.SetAsFirstSibling(); // under the panels
            layer = (RectTransform)go.transform;
            layer.anchorMin = Vector2.zero;
            layer.anchorMax = Vector2.one;
            layer.offsetMin = layer.offsetMax = Vector2.zero;
            return true;
        }

        private static TextMeshProUGUI Label(int i)
        {
            while (pool.Count <= i)
            {
                var t = GameWidgets.Text(layer, "Label", "", ModStyle.BoldFont, 20f, ModStyle.Gold, TextAlignmentOptions.Bottom);
                t.raycastTarget = false;
                t.enableWordWrapping = false;
                t.characterSpacing = 0f;
                t.outlineWidth = 0.18f;
                t.outlineColor = new Color32(20, 20, 22, 230);
                var r = t.rectTransform;
                r.anchorMin = r.anchorMax = Vector2.zero;
                r.pivot = new Vector2(0.5f, 0f);
                r.sizeDelta = new Vector2(260f, 48f);
                pool.Add(t);
            }
            return pool[i];
        }

        public static void Update()
        {
            if (!Ensure()) return;
            int used = 0;
            if (StudioViewOptions.Labels != StudioViewOptions.LabelMode.Off && StudioDock.Dock.Root.gameObject.activeInHierarchy)
            {
                var cam = Camera.main;
                var pivot = StudioCamera.Pivot;
                bool zoomedOut = StudioCamera.Zoom > 1.9f;
                var candidates = StudioScene.Pickable
                    .Select(i => (item: i, prio: StudioSelection.Contains(i.Key) ? 0 : StudioSelection.Hover == i.Key ? 1 : i.IsCastle ? 2 : 3, dist: (i.Pos - pivot).sqrMagnitude))
                    .OrderBy(c => c.prio).ThenBy(c => c.dist).Take(Max * 2);
                var placed = new List<Rect>();
                float scale = layer.lossyScale.x > 0f ? layer.lossyScale.x : 1f;
                foreach (var (item, prio, _) in candidates)
                {
                    if (used >= Max) break;
                    if (StudioShell.WorldToScreen(item.Pos + Vector3.up * 2.2f) is not Vector2 s) continue;
                    if (s.x < 0 || s.y < 0 || s.x > Screen.width || s.y > Screen.height) continue;
                    bool full = prio <= 1 || !zoomedOut;
                    string text = Text(item, full);
                    if (string.IsNullOrEmpty(text)) continue;
                    var label = Label(used);
                    label.text = text;
                    label.fontSize = prio <= 1 ? 22f : 19f;
                    var size = label.GetPreferredValues(text);
                    var local = (s - (Vector2)layer.position) / scale + layer.rect.size / 2f;
                    var rect = new Rect(local.x - size.x / 2f, local.y, size.x, size.y);
                    if (prio > 1 && placed.Any(r => r.Overlaps(rect))) continue;
                    placed.Add(rect);
                    label.rectTransform.anchoredPosition = local;
                    label.rectTransform.sizeDelta = new Vector2(size.x + 4f, size.y);
                    label.gameObject.SetActive(true);
                    used++;
                }
            }
            for (int i = used; i < pool.Count; i++) if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
        }

        private static string Text(StudioItem item, bool full)
        {
            var l = Studio.LabelFor(item.Key);
            var color = StudioSelection.Contains(item.Key) ? Level.LayoutMarkers.SelectedColor : item.Removed ? Level.LayoutMarkers.RemovedColor : l.Color ?? StudioScene.KindColor(item);
            string name = ModFields.TMPEscape(l.Name ?? (full ? item.Name : Economy.PlannerIcons.Short(item.TypeName)));
            string head = $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{name}</color>";
            if (!full) return head;
            string sub = StudioViewOptions.Labels switch
            {
                StudioViewOptions.LabelMode.Costs => l.Costs ?? (item.BaseCosts.Count > 0 ? string.Join("·", Enumerable.Range(0, item.BaseCosts.Count).Select(i => StudioEdits.Cost(item, i))) : null),
                StudioViewOptions.LabelMode.Requirements => l.Badge,
                _ => null,
            };
            return string.IsNullOrEmpty(sub) ? head : $"{head}\n<size=80%>{ModFields.TMPEscape(sub)}</size>";
        }
    }
}
