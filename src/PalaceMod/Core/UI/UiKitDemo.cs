using System;
using System.Linq;

/*
    Dev-only demo of the UI kit (never shown to players): `dev.py cmd "uikit <what>"`.
        uikit frame          open a demo ModFrame (tabs, rows with values and tooltips, text, buttons, scrolling)
        uikit notify         show one notification of each kind, plus a long one
        uikit buttons <n>    put n demo buttons in the pause menu (0 removes them; past 3 they overflow into "More")
        uikit tooltip [off]  show the shared tooltip in the middle of the screen
        uikit mouse <object> [fx fy] | <x> <y> | off
                             move a simulated mouse to a point in the named object's rect (fractions from its
                             bottom-left, default the centre) or to screen pixels. The game sees it (Rewired and Unity
                             Input), so menus hover and raycast through their normal path; "off" gives the real mouse back
        uikit click [<object> [fx fy] | <x> <y>]
                             (move there, then) press and release the simulated left button
        uikit scroll <delta> [frames]
                             add delta to the scroll wheel on each of the next frames (default 1: one wheel notch;
                             many small deltas over many frames is a trackpad swipe)
        uikit shown          which tooltip is showing
        uikit numbers        open the demo frame on its Numbers tab (ModNumber fields: int, float, a huge range, clearable)
        uikit step <left|right> [n] [shift]
                             n left/right presses on the selected field in one go (a held key: the speed-up shows), with
                             Shift held if given; prints the selected row's value
        uikit numkey <min|max|next|prev>
                             Home / End / controller shoulder on the selected number field or settings slider
        uikit type <key|text> [...]
                             drive the field being typed into: text is typed; keys enter, esc, backspace, left, right,
                             home, end, selectall ('_' for a space in text); prints the typing state
*/
namespace PalaceMod
{
    public class UiKitDemo : BaseManager
    {
        private ModFrame demo;
        private int demoButtons;
        private int clicks;
        private int stepValue = 5;
        private int demoInt = 50, demoBig = 1;
        private float demoFloat = 1.5f;
        private int? demoCost;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("uikit", "uikit <frame|notify|buttons <n>|tooltip [off]|mouse <object> [fx fy]|mouse off|click [...]|scroll <d> [frames]|shown>   exercise the UI kit (Core/UI)", args =>
            {
                switch (args.FirstOrDefault()?.ToLowerInvariant())
                {
                    case "frame":
                        (demo ??= BuildDemo()).Open();
                        return demo.IsOpen ? "opened" : throw new InvalidOperationException("didn't open; see the log");
                    case "notify":
                        Notify.Show("Formation: Line", NotifyKind.Info, 4f, key: "demo-formation");
                        Notify.Show("Save state stored", NotifyKind.Success, 4f);
                        Notify.Show("Not enough gold to undo", NotifyKind.Warning, 4f);
                        Notify.Show("Couldn't load the replay file", NotifyKind.Error, 4f);
                        Notify.Show("A longer message wraps onto a second line once it gets past the panel's maximum width, so it never runs across the whole screen.", NotifyKind.Info, 6f);
                        return "";
                    case "buttons":
                        int n = args.Length > 1 ? int.Parse(args[1]) : 1;
                        for (int i = 1; i <= Math.Max(n, demoButtons); i++)
                        {
                            int index = i;
                            // Demo 1 opens the demo frame, the rest just say they were clicked.
                            if (i <= n) PauseMenu.AddButton($"demo{i}", $"Demo {i}", index == 1
                                ? () => (demo ??= BuildDemo()).Open()
                                : () => Notify.Show($"Demo {index} clicked", key: "demo-click"), order: i);
                            else PauseMenu.RemoveButton($"demo{i}");
                        }
                        demoButtons = n;
                        return $"{n} demo buttons";
                    case "tooltip":
                        if (args.Length > 1 && args[1] == "off") ModTooltip.Hide();
                        else ModTooltip.Show("Tooltip", "The shared tooltip: the game's selection tooltip with PalaceMod's edge.", null);
                        return ModTooltip.Visible ? "shown" : "hidden";
                    case "mouse":
                        return SimulateMouse(args.Skip(1).ToArray());
                    case "click":
                        string at = args.Length > 1 ? SimulateMouse(args.Skip(1).ToArray()) : "";
                        if (SimulatedPointer.Position == null) throw new InvalidOperationException("move the simulated mouse first (uikit mouse ...)");
                        SimulatedPointer.Click();
                        return $"clicked {at}".Trim();
                    case "scroll":
                        if (args.Length < 2) throw new ArgumentException("usage: uikit scroll <delta> [frames]");
                        int frames = args.Length > 2 ? int.Parse(args[2]) : 1;
                        SimulatedPointer.Scroll(float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), frames);
                        return $"scrolling {args[1]} x {frames} frames";
                    case "shown":
                        return ModTooltip.Describe() ?? "hidden";
                    case "numbers":
                        (demo ??= BuildDemo()).Open();
                        demo.ShowTab("Numbers");
                        return demo.IsOpen ? "opened on Numbers" : throw new InvalidOperationException("didn't open; see the log");
                    case "step":
                    {
                        var frame = UIFrameManager.instance?.ActiveFrame ?? throw new InvalidOperationException("no menu open");
                        if (frame.CurrentSelection == null) throw new InvalidOperationException("nothing selected");
                        if (args.Length < 2) throw new ArgumentException("usage: uikit step <left|right> [n] [shift]");
                        var direction = args[1] == "left" ? ThronefallUIElement.NavigationDirection.Left
                            : args[1] == "right" ? ThronefallUIElement.NavigationDirection.Right : throw new ArgumentException("left or right");
                        int count = args.Length > 2 && int.TryParse(args[2], out int c) ? c : 1;
                        ModNumber.DebugShift = args.Contains("shift");
                        try
                        {
                            // A step may rebuild the page; the frame re-selects the row by name.
                            for (int i = 0; i < count && frame.CurrentSelection != null; i++) frame.CurrentSelection.onEmptyNavigate.Invoke(direction);
                        }
                        finally { ModNumber.DebugShift = false; }
                        return SelectedValue(frame);
                    }
                    case "numkey":
                    {
                        var frame = UIFrameManager.instance?.ActiveFrame ?? throw new InvalidOperationException("no menu open");
                        var keys = frame.CurrentSelection?.GetComponent<NumberKeys>() ?? throw new InvalidOperationException("the selection isn't a number field");
                        keys.DebugKey(args.ElementAtOrDefault(1) ?? "");
                        return SelectedValue(frame);
                    }
                    case "type":
                        foreach (var k in args.Skip(1))
                        {
                            if (!ModTextInput.Active) break;
                            ModTextInput.DebugKey(k.Replace('_', ' '));
                        }
                        return ModTextInput.DebugState;
                    default:
                        throw new ArgumentException("usage: uikit <frame|notify|buttons <n>|tooltip [off]|mouse ...|shown>");
                }
            });
        }

        // The selected row's texts (rich text stripped), for checking steps from the command line.
        private static string SelectedValue(UIFrame frame)
        {
            var selected = frame.CurrentSelection;
            if (selected == null) return "nothing selected";
            var texts = selected.GetComponentsInChildren<TMPro.TextMeshProUGUI>()
                .Select(t => NumberMath.StripTags(t.text ?? "").Trim()).Where(t => t.Length > 0);
            return $"{selected.name}: {string.Join(" | ", texts)}";
        }

        private static string SimulateMouse(string[] args)
        {
            if (args.Length == 0) throw new ArgumentException("usage: uikit mouse <object> [fx fy] | <x> <y> | off");
            if (args[0] == "off")
            {
                SimulatedPointer.Release();
                return "real mouse";
            }
            UnityEngine.Vector2 point;
            if (args.Length == 2 && float.TryParse(args[0], out float x) && float.TryParse(args[1], out float y)) point = new UnityEngine.Vector2(x, y);
            else
            {
                bool fractions = args.Length >= 3 && float.TryParse(args[args.Length - 2], out _) && float.TryParse(args[args.Length - 1], out _);
                string name = string.Join(" ", fractions ? args.Take(args.Length - 2) : args);
                var target = UnityEngine.Object.FindObjectsOfType<UnityEngine.RectTransform>().FirstOrDefault(t => t.name == name)
                    ?? throw new InvalidOperationException($"no active object called \"{name}\"");
                float fx = fractions ? float.Parse(args[args.Length - 2]) : 0.5f, fy = fractions ? float.Parse(args[args.Length - 1]) : 0.5f;
                var corners = new UnityEngine.Vector3[4];
                target.GetWorldCorners(corners); // screen pixels on the game's overlay canvas
                point = new UnityEngine.Vector2(UnityEngine.Mathf.Lerp(corners[0].x, corners[2].x, fx), UnityEngine.Mathf.Lerp(corners[0].y, corners[2].y, fy));
            }
            SimulatedPointer.MoveTo(point);
            return $"mouse at {point.x:0},{point.y:0}";
        }

        private ModFrame BuildDemo() => new ModFrame("UI Kit Demo")
            .AddTab("Rows", page =>
            {
                page.Header("Label and value");
                for (int i = 1; i <= 24; i++)
                {
                    int n = i;
                    page.Row($"Row {i}", $"{i * 37 % 200}", () => $"Tooltip for row {n}, built when shown.");
                }
            })
            .AddTab("Text", page =>
            {
                page.Header("Paragraphs");
                page.Text("Body text wraps to the page width. It uses the game's Cormorant Garamond in the game's gold, slightly dimmed so rows stand out.");
                page.Text("A second paragraph, to show spacing.");
                page.Row("A row after text", "42", tooltip: "Rows and text mix freely.");
                var tall = page.Custom("Tall block", 1100f);
                GameWidgets.Text(tall, "Tall label", "A tall custom block: keyboard and controller scroll through it a page at a time.", ModStyle.BodyFont, 25f, ModStyle.Dim);
                var icon = PerkManager.instance?.allEquippables?.FirstOrDefault(e => e != null && e.icon != null)?.icon;
                if (icon != null) page.Row("A row with an icon", "7", tooltip: "Icons sit left of the label.", icon: icon);
            })
            .AddTab("Actions", page =>
            {
                page.Header("Buttons");
                page.Button("Info notification", () => Notify.Show("Something happened", NotifyKind.Info));
                page.Button("Success notification", () => Notify.Show("It worked", NotifyKind.Success));
                page.Button("Warning notification", () => Notify.Show("Careful", NotifyKind.Warning));
                page.Button("Error notification", () => Notify.Show("It failed", NotifyKind.Error));
                page.Stepper("Stepper", () => $"{stepValue}", d => stepValue = Math.Max(0, Math.Min(10, stepValue + d)),
                    () => Notify.Show($"Applied {stepValue}", key: "demo-stepper"), "Left/right or click the arrows; apply to use it.");
                page.Row("Clicks", clicks.ToString(), tooltip: "Rebuilt by Refresh", onClick: () =>
                {
                    clicks++;
                    demo.Refresh();
                });
            })
            .AddTab("Numbers", page =>
            {
                page.Header("Number fields");
                page.Text("Left/right step (hold to speed up, Shift or the shoulders x10), apply types a value, Home/End go to the ends.");
                page.Number("Whole number", () => demoInt, v => demoInt = v, 0, 100, tooltip: () => "0 to 100, step 1.");
                page.Number("Decimal", () => demoFloat, v => demoFloat = v, 0.5f, 5f, 0.1f, v => $"x{v:0.0}", () => "0.5 to 5, step 0.1, shown as x1.5.");
                page.Number("Huge range", () => demoBig, v => demoBig = v, 1, 1000000, format: v => $"{v:N0} gold", tooltip: () => "1 to 1,000,000; try 10k.");
                page.Number("Clearable", () => demoCost ?? 25, v => demoCost = v, 0, 999,
                    format: v => demoCost is int ? $"{v}" : $"<color=#{ModStyle.Hex(ModStyle.Dim)}>{v}  (default)</color>",
                    tooltip: () => "Type nothing to go back to the default.", onClear: () => demoCost = null);
                page.Number("Rebuilds the page", () => demoInt, v => { demoInt = v; demo.Refresh(); }, 0, 100,
                    tooltip: () => "Its setter refreshes the page, like the debug menu's: holding a key still speeds up.");
            });
    }
}
