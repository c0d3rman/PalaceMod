using System;
using System.Linq;

/*
    Dev-only demo of the UI kit (never shown to players): `dev.py cmd "uikit <what>"`.
        uikit frame          open a demo ModFrame (tabs, rows with values and tooltips, text, buttons, scrolling)
        uikit notify         show one notification of each kind, plus a long one
        uikit buttons <n>    put n demo buttons in the pause menu (0 removes them; past 3 they overflow into "More")
        uikit tooltip [off]  show the shared tooltip in the middle of the screen
        uikit mouse <object> [fx fy] | <x> <y> | off
                             move a simulated mouse for hover tooltips (ModTooltip.AttachHover) to a point in the named
                             object's rect (fractions from its bottom-left, default the centre) or to screen pixels
        uikit shown          which tooltip is showing
*/
namespace PalaceMod
{
    public class UiKitDemo : BaseManager
    {
        private ModFrame demo;
        private int demoButtons;
        private int clicks;
        private int stepValue = 5;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            DevCommands.Register("uikit", "uikit <frame|notify|buttons <n>|tooltip [off]|mouse <object> [fx fy]|mouse off|shown>   exercise the UI kit (Core/UI)", args =>
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
                    case "shown":
                        return ModTooltip.Describe() ?? "hidden";
                    default:
                        throw new ArgumentException("usage: uikit <frame|notify|buttons <n>|tooltip [off]|mouse ...|shown>");
                }
            });
        }

        private static string SimulateMouse(string[] args)
        {
            if (args.Length == 0) throw new ArgumentException("usage: uikit mouse <object> [fx fy] | <x> <y> | off");
            if (args[0] == "off")
            {
                ModTooltip.HoverSource.SimulatedMouse = null;
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
            ModTooltip.HoverSource.SimulatedMouse = point;
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
            });
    }
}
