using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    Dev command for reading the game's UI at runtime, so UI work can be based on what's actually on screen instead of
    guessed GameObject paths:

        dev.py cmd "uidump pause 4"      the in-match pause frame, 4 levels deep
        dev.py cmd "uidump active"       whatever menu is open
        dev.py cmd "uidump hud 2"        the in-game HUD container
        dev.py cmd "uidump tooltip"      the in-world interaction tooltip
        dev.py cmd "uidump <name>"       any GameObject (active or not) with that exact name

    Each line shows the object, whether it's active, its rect, and the interesting parts of its components (sprite,
    colour, font, text).
*/
namespace PalaceMod
{
    public class UiInspector : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("uidump", "uidump <pause|active|hud|tooltip|name> [depth]   print a UI hierarchy with components", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: uidump <pause|active|hud|tooltip|name> [depth]");
                int depth = args.Length > 1 && int.TryParse(args[args.Length - 1], out int d) ? d : 3;
                string query = string.Join(" ", args.Length > 1 && int.TryParse(args[args.Length - 1], out _) ? args.Take(args.Length - 1) : args);
                var root = Resolve(query) ?? throw new InvalidOperationException($"nothing called \"{query}\"");
                var text = new StringBuilder("\n");
                Dump(root, 0, depth, text);
                return text.ToString();
            });
        }

        private static Transform Resolve(string query)
        {
            var frames = UIFrameManager.instance;
            switch (query.ToLowerInvariant())
            {
                case "pause":
                    return frames == null ? null : Traverse.Create(frames).Field("inMatchPauseMenuFrame").GetValue<UIFrame>()?.transform;
                case "active":
                    return frames?.ActiveFrame?.transform;
                case "hud":
                    return frames?.InGameUIContainer;
                case "tooltip":
                    return TooltipManager.instance?.targetTooltip?.transform;
                case "canvas":
                    return frames?.transform;
            }
            return Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(t => t.name == query && t.gameObject.scene.IsValid());
        }

        private static void Dump(Transform t, int level, int maxDepth, StringBuilder text)
        {
            text.Append(' ', level * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)");
            if (t is RectTransform r)
            {
                text.Append($" [{r.rect.width:0}x{r.rect.height:0} @{r.anchoredPosition.x:0},{r.anchoredPosition.y:0} a{r.anchorMin.x:0.##},{r.anchorMin.y:0.##}-{r.anchorMax.x:0.##},{r.anchorMax.y:0.##} p{r.pivot.x:0.##},{r.pivot.y:0.##}]");
            }
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform || c is CanvasRenderer) continue;
                text.Append(' ').Append(Describe(c));
            }
            text.Append('\n');
            if (level >= maxDepth)
            {
                if (t.childCount > 0) text.Append(' ', level * 2 + 2).Append($"... {t.childCount} children\n");
                return;
            }
            foreach (Transform child in t) Dump(child, level + 1, maxDepth, text);
        }

        private static string Describe(Component c)
        {
            static string Col(Color col) => "#" + ColorUtility.ToHtmlStringRGBA(col);
            switch (c)
            {
                case TextMeshProUGUI tmp:
                    string s = tmp.text ?? "";
                    if (s.Length > 30) s = s.Substring(0, 30) + "...";
                    return $"TMP(\"{s.Replace("\n", "\\n")}\" {tmp.font?.name} mat={tmp.fontSharedMaterial?.name} {tmp.fontSize:0.#}pt {Col(tmp.color)} {tmp.alignment})";
                case MPUIKIT.MPImageBasic mp:
                    return $"MPImageBasic({mp.sprite?.name ?? "none"} {Col(mp.color)} mat={mp.material?.name})";
                case MPUIKIT.MPImage mpi:
                    return $"MPImage({mpi.sprite?.name ?? "none"} {Col(mpi.color)} {mpi.DrawShape} r={mpi.Rectangle.CornerRadius} stroke={mpi.StrokeWidth} outline={mpi.OutlineWidth} {Col(mpi.OutlineColor)} falloff={mpi.FalloffDistance})";
                case Image img:
                    return $"Image({img.sprite?.name ?? "none"} {img.type} {Col(img.color)} mat={img.material?.name})";
                case LayoutGroup lg:
                    return $"{lg.GetType().Name}(pad {lg.padding.left},{lg.padding.right},{lg.padding.top},{lg.padding.bottom}{(lg is HorizontalOrVerticalLayoutGroup h ? $" sp {h.spacing}" : "")})";
                case TFUITextButton b:
                    return $"TFUITextButton(nav u={b.topNav?.name} d={b.botNav?.name} l={b.leftNav?.name} r={b.rightNav?.name}, onApply {b.onApply.GetPersistentEventCount()})";
                case ThronefallUIElement e:
                    return $"{e.GetType().Name}(nav u={e.topNav?.name} d={e.botNav?.name})";
                case Canvas cv:
                    return $"Canvas({cv.renderMode} order {cv.sortingOrder} override {cv.overrideSorting})";
                case CanvasScaler cs:
                    return $"CanvasScaler({cs.uiScaleMode} ref {cs.referenceResolution} match {cs.matchWidthOrHeight})";
                default:
                    return c.GetType().Name;
            }
        }
    }
}
