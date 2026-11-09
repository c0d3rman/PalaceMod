using System.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    Light, facing and shape at runtime (docs/builder-engine.md, "Light", "Turning and reshaping"): the `lighting`,
    `setLight`, `turn` and `setShape` actions, the `light` component, and the light level expressions read
    (LogicLighting). The work is in LightVerbs.cs and ShapeVerbs.cs; this registers them, ticks them, keeps them in
    savestates ("light") and owns the dev command `logic-light`. Infrastructure: no settings; acts only in Builder
    sessions (outside one the sun is the game's and nothing has an entity light).
*/
namespace PalaceMod
{
    public class LogicLightManager : BaseManager
    {
        private GameObject driver;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            LightVerbs.Register();
            ShapeVerbs.Register();
            LogicLighting.Service = LightLevels.Instance;
            LogicSaveState.Register("light", w => new Newtonsoft.Json.Linq.JObject
            {
                ["light"] = LightVerbs.Capture(w),
                ["shapes"] = ShapeVerbs.Capture(w),
            }, (w, state) =>
            {
                LightVerbs.Restore(w, state?["light"]);
                ShapeVerbs.Restore(w, state?["shapes"]);
            });
            DevCommands.Register("logic-light", "logic-light [status] | at x z | raw ...   lighting, entity lights, light levels, turns and reshapes; raw: renderer probe", args =>
            {
                WorldSession.Sync();
                var a = args.ToArray();
                string what = a.Length > 0 ? a[0] : "status";
                switch (what)
                {
                    case "raw": return LightProbe.Run(a.Skip(1).ToArray());
                    case "at":
                    {
                        float x = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), z = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                        return $"lightAt({x}, {z}) = {LightLevels.Instance.LightAt(new Vec(x, 0, z)):0.###} (daylight {LightLevels.Daylight:0.###})";
                    }
                    default: return LightVerbs.Status() + "\n" + ShapeVerbs.Status();
                }
            });
        }

        public override void Start()
        {
            driver = new GameObject("PalaceMod LightDriver");
            Object.DontDestroyOnLoad(driver);
            driver.hideFlags = HideFlags.HideAndDontSave;
            driver.AddComponent<LightDriver>();
        }

        public override void Update()
        {
            WorldSession.Sync();
            if (!WorldSession.Active) return;
            LightVerbs.Tick(Time.deltaTime);
            ShapeVerbs.Tick(Time.deltaTime);
        }

        public override void OnDestroy()
        {
            if (driver != null) Object.Destroy(driver);
            LightLevels.RestoreObjectLightLimit();
        }
    }
}
