using System.Linq;
using System.Text;
using UnityEngine;

/*
    Pause-screen overview: an "Upgrades" button in the pause menu opens a frame listing every built building with
    its level and the choices it took; each row's tooltip has the full path and what's next.
*/
namespace PalaceMod
{
    public partial class UpgradesViewManager
    {
        private const string PauseButtonId = "upgrades-overview";
        private ModFrame overview;

        private void StartPauseOverview()
        {
            overview = new ModFrame("Upgrades").SetContent(BuildOverview);
            DevCommands.Register("upgv.overview", "upgv.overview          open the pause-screen upgrades overview", _ =>
            {
                overview.Open();
                return overview.IsOpen ? "open" : "not open";
            });
            RefreshPauseButton();
        }

        private void CloseOverview()
        {
            if (overview != null && overview.IsOpen) overview.Close();
        }

        private void RefreshPauseButton()
        {
            if (overview == null) return;
            PauseMenu.AddButton(PauseButtonId, "Upgrades", overview.Open, order: 20,
                visible: () => IsEnabled && PauseOverview.Value && PlayerInteraction.instance != null);
            PauseMenu.Refresh();
        }

        private void BuildOverview(ModPage page)
        {
            var slots = UpgradeInfo.AllBuilt();
            if (slots.Count == 0)
            {
                page.Text("Nothing built yet.");
                return;
            }
            foreach (var group in slots.GroupBy(UpgradeInfo.BuildingName))
            {
                int index = 0, count = group.Count();
                foreach (var slot in group)
                {
                    index++;
                    int maxLevel = UpgradeInfo.MaxLevel(slot);
                    var taken = UpgradeInfo.TakenChoices(slot);
                    string label = count > 1 ? $"{group.Key} {index}" : group.Key;
                    label += $"  <size=75%>Lv {slot.Level}/{maxLevel}</size>";
                    string value = taken.Count > 0 ? string.Join(", ", taken.Select(t => UpgradeInfo.ChoiceName(slot, t.choice))) : "";
                    // Rows without a choice get a blank icon so every label starts at the same place.
                    var icon = taken.Count > 0 ? UpgradeInfo.Icon(taken[taken.Count - 1].choice) : BlankSprite;
                    var captured = slot;
                    page.Row(label, value, () => Describe(captured), icon: icon);
                }
            }
        }

        private static Sprite blankSprite;

        private static Sprite BlankSprite
        {
            get
            {
                if (blankSprite != null) return blankSprite;
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, Color.clear);
                tex.Apply();
                return blankSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            }
        }

        /// <summary>Tooltip text: every step taken, then the next step's options and cost.</summary>
        private static string Describe(BuildSlot slot)
        {
            if (slot == null) return "";
            var sb = new StringBuilder();
            foreach (var step in UpgradeInfo.Taken(slot))
            {
                if (step.Level == 1 && !step.IsChoice) continue; // a plain build has nothing to report
                string text = Shorten(UpgradeInfo.StepText(slot, step));
                if (string.IsNullOrEmpty(text)) continue;
                sb.Append("Lv ").Append(step.Level).Append(": ").AppendLine(text);
            }
            var next = UpgradeInfo.Next(slot);
            if (next is { } n)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("Next (").Append(slot.NextUpgradeOrBuildCost).Append("<sprite name=\"coin\">): ");
                sb.Append(n.IsChoice
                    ? string.Join(" / ", n.Upgrade.upgradeBranches.Select(b => UpgradeInfo.IsLocked(b.choiceDetails) ? "Locked" : UpgradeInfo.ChoiceName(slot, b.choiceDetails)))
                    : Shorten(UpgradeInfo.StepText(slot, n)));
            }
            else if (UpgradeInfo.HasRemaining(slot))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("Next upgrade needs a higher castle level.");
            }
            else
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("Fully upgraded.");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
