using System.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;

/*
    Aura rings (docs/builder-engine.md, "Aura rings"; Bodies/World/AuraRings.cs): wires the rings to the logic world's
    events and owns the dev command `logic-rings`. Infrastructure: no settings; acts only in Builder sessions.
*/
namespace PalaceMod
{
    public class AuraRingsManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            LogicWorld.RulesChanged += AuraRings.Changed;
            LogicWorld.Started += AuraRings.Started;
            LogicWorld.Ending += _ => AuraRings.Reset();
            LogicWorld.Settled += AuraRings.Settle;
            LogicWorld.Polled += AuraRings.Poll;
            DevCommands.Register("logic-rings", "logic-rings [entity|template] | prefabs   aura rings: owner, radius, world radius, position, visibility; prefabs: the game's range-ring prefabs", args =>
                args.FirstOrDefault() == "prefabs" ? AuraRings.Prefabs() : AuraRings.Status(args.FirstOrDefault()));
        }
    }
}
