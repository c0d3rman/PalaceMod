using System.Collections.Generic;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio;

namespace PalaceMod.Tests
{
    public static class StudioTests
    {
        public static void CommonValues()
        {
            Assert.Equal(5, StudioMath.Common(new[] { 5, 5, 5 }, out bool mixed), "shared value");
            Assert.True(!mixed, "not mixed");
            StudioMath.Common(new[] { 5, 6, 5 }, out mixed);
            Assert.True(mixed, "mixed");
            StudioMath.Common(new int[0], out mixed);
            Assert.True(mixed, "nothing counts as mixed");
        }

        public static void LassoPolygon()
        {
            // An L shape: (0,0) (4,0) (4,1) (1,1) (1,4) (0,4)
            float[] x = [0, 4, 4, 1, 1, 0], y = [0, 0, 1, 1, 4, 4];
            bool In(float px, float py) => StudioMath.InPolygon(px, py, 6, i => x[i], i => y[i]);
            Assert.True(In(0.5f, 0.5f), "corner inside");
            Assert.True(In(3f, 0.5f), "arm inside");
            Assert.True(In(0.5f, 3f), "other arm inside");
            Assert.True(!In(3f, 3f), "notch outside");
            Assert.True(!In(-1f, 2f), "left outside");
        }

        public static void PruneDropsEmptyEditsButKeepsRequirements()
        {
            var lv = new LevelDef
            {
                BuildSlots = new BuildSlotOverlay
                {
                    Edit =
                    [
                        new BuildSlotEdit { Ref = "House 1" },
                        new BuildSlotEdit { Ref = "House 2", Requires = new SlotRequirement { Slot = "castle", Level = 0 } },
                        new BuildSlotEdit { Ref = "House 3", Upgrades = [null, new UpgradeEdit()] },
                    ],
                    Remove = [],
                },
                Props = [],
            };
            StudioMath.Prune(lv);
            Assert.Equal(1, lv.BuildSlots.Edit.Count, "only the edit with a requirement is left");
            Assert.Equal("House 2", lv.BuildSlots.Edit[0].Ref, "kept edit");
            Assert.Null(lv.BuildSlots.Remove, "empty remove list dropped");
            Assert.Null(lv.Props, "empty props dropped");
        }

        public static void GroupsLoseMissingMembersAndSmallGroups()
        {
            var lv = new LevelDef
            {
                BuildSlots = new BuildSlotOverlay { Add = [new BuildSlotAdd { Id = "slot-1" }, new BuildSlotAdd { Id = "slot-2" }] },
                Props = [new PropDef { Id = "prop-1" }],
                Groups =
                [
                    new GroupDef { Id = "g1", Members = ["add:slot-1", "add:slot-2", "prop-gone", "prop:prop-9"] },
                    new GroupDef { Id = "g2", Members = ["prop:prop-1", "prop:prop-7"] },
                ],
            };
            StudioMath.Prune(lv);
            Assert.Equal(1, lv.Groups.Count, "a group left with one member is dropped");
            Assert.Equal("g1", lv.Groups[0].Id, "kept group");
            Assert.True(!lv.Groups[0].Members.Contains("prop:prop-9"), "a deleted prop leaves its group");
            Assert.True(lv.Groups[0].Members.Contains("add:slot-2"), "present members stay");
        }

        public static void GroupingMovesMembersOutOfOldGroups()
        {
            var lv = new LevelDef
            {
                BuildSlots = new BuildSlotOverlay { Add = [new BuildSlotAdd { Id = "a" }, new BuildSlotAdd { Id = "b" }, new BuildSlotAdd { Id = "c" }] },
                Groups = [new GroupDef { Id = "g1", Name = "Old", Members = ["add:a", "add:b"] }],
            };
            StudioMath.Group(lv, new List<string> { "add:b", "add:c" }, "g2");
            Assert.Equal(1, lv.Groups.Count, "the old group fell below two members");
            Assert.Equal("g2", lv.Groups[0].Id, "new group");
            Assert.Equal(2, lv.Groups[0].Members.Count, "its members");
        }
    }
}
