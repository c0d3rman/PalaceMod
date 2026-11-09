using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/*
    `pickup` (docs/builder-engine.md, "Components"): `by` (a filter of who collects it; default the king), `radius`,
    `magnet` (it flies to a collector within this range). Fires `pickup` (other = the collector), then destroys
    itself unless `consume: false` (a rule decides, e.g. with `destroy`).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    internal sealed class BuilderPickup : MonoBehaviour
    {
        public LogicEntity Entity;
        public string By = "king";
        public float Radius = 1.5f, Magnet = 4f, MagnetSpeed = 10f;
        public bool Consume = true;
        private LogicEntity drawnTo, held;
        private bool taken;
        private readonly List<LogicEntity> candidates = [];

        public static readonly HashSet<BuilderPickup> All = [];

        public void Configure(LogicEntity e, JObject p)
        {
            Entity = e;
            By = (string)p["by"] ?? By;
            Radius = p.Value<float?>("radius") ?? Radius;
            Magnet = p.Value<float?>("magnet") ?? Magnet;
            Consume = p.Value<bool?>("consume") ?? true;
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        /// <summary>One 10 Hz step: find a collector in reach (or one to fly to).</summary>
        public void Poll()
        {
            if (taken || Entity == null || !Entity.Enabled) return;
            BodySpatial.Query(transform.position, Mathf.Max(Radius, Magnet), candidates);
            LogicEntity best = null;
            float bestD = float.MaxValue;
            var filter = BodyFilter.Compile(By);
            foreach (var c in candidates)
            {
                if (c == Entity || !c.Alive || !filter(Entity, c)) continue;
                float d = Vec.FlatDistance(c.Position, Entity.Position);
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null || bestD > Radius) held = null;
            if (best == null) { drawnTo = null; return; }
            if (bestD <= Radius) { if (best != held) Take(best); }
            else drawnTo = bestD <= Magnet ? best : null;
        }

        private void Update()
        {
            if (taken || drawnTo == null || !drawnTo.Exists) return;
            var target = drawnTo.Position.ToVector3() + Vector3.up * 0.5f;
            transform.position = Vector3.MoveTowards(transform.position, target, MagnetSpeed * Time.deltaTime);
            if (drawnTo != held && Vector3.Distance(transform.position, target) <= Mathf.Min(0.6f, Radius)) Take(drawnTo);
        }

        private void Take(LogicEntity by)
        {
            if (taken) return;
            BodyDev.Raise("pickup", Entity, by, 0, transform.position.ToVec());
            if (!Consume) { drawnTo = null; held = by; return; }   // again only after it has left
            taken = true;
            BodyEnd.Retire(gameObject);
        }
    }
}
