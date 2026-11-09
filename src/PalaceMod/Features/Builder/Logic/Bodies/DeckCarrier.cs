using System.Collections.Generic;
using UnityEngine;

/*
    Riding (docs/builder-engine.md, "Riding"): whatever stands on a walkable deck moves with it. After everything has
    moved for the frame (movers in Update, linked parts in their LateUpdate), each deck that moved since the last frame
    carries the king and the units standing on its top by the same move (translation and turn about its pivot; a deck
    that rises lifts them). So a deck on a mover path, a spinning platform, a teleported raft, or a walkable part linked
    to a walking colossus all carry their riders. Units walking the navmesh are carried too, but the graphs don't move
    with a gliding deck: a unit that paths off it snaps back to the ground graphs (riding troops should hold).
    A riding deck (a walkable part linked to a moving parent) isn't on the graphs at all, so the units on its top ride
    it: they don't walk (the game's movement would snap them to the ground) but still attack, and are carried with it.
    One that leaves it (teleported off, or the deck is gone) walks again from the ground under it.
    A rider on a riding deck (the king too) is taken out of the game's local avoidance (RVO) meanwhile: the avoidance
    is flat, so a rider standing over the walking parent would sit inside its agent and shove it about (the colossus
    milled round under the king instead of walking on).
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    [DefaultExecutionOrder(100)]
    internal sealed class DeckCarrier : MonoBehaviour
    {
        private static DeckCarrier instance;
        private readonly Dictionary<Transform, Matrix4x4> last = [];
        private readonly Collider[] overlap = new Collider[64];
        private readonly HashSet<Transform> riders = [];

        /// <summary>Total riders moved (dev status).</summary>
        public static int Carried;

        /// <summary>Units (game object ids) aboard a riding deck now: their movement Update is skipped.</summary>
        internal static readonly HashSet<int> Riding = [];
        private static readonly Dictionary<int, PathfindMovement> aboard = [];
        private readonly Dictionary<int, PathfindMovement> stillAboard = [];
        private static Pathfinding.RVO.RVOController kingAvoidanceOff;

        public static void Ensure()
        {
            if (instance != null) return;
            var go = new GameObject("PalaceBuilder deck carrier");
            instance = go.AddComponent<DeckCarrier>();
        }

        private void LateUpdate()
        {
            if (LogicWorld.Current == null) { last.Clear(); Riding.Clear(); aboard.Clear(); KingAvoidance(false); return; }
            stillAboard.Clear();
            bool kingAboard = false;
            var king = PlayerInteraction.instance != null ? PlayerInteraction.instance.transform : null;
            foreach (var (deck, offGraph) in Walkables.LiveDecks())
            {
                if (deck == null || !deck.gameObject.activeInHierarchy) continue;
                var now = deck.localToWorldMatrix;
                if (!last.TryGetValue(deck, out var before)) { last[deck] = now; before = now; }
                last[deck] = now;
                if (!Same(before, now)) Carry(deck, before, now);
                if (offGraph)
                {
                    Aboard(now);
                    if (king != null && OnTop(now.inverse, king.position)) kingAboard = true;
                }
            }
            KingAvoidance(kingAboard);
            // Units that left a riding deck walk again, from the ground under them.
            foreach (var kv in aboard)
                if (!stillAboard.ContainsKey(kv.Key) && kv.Value != null && kv.Value.isActiveAndEnabled) Land(kv.Value);
            aboard.Clear();
            Riding.Clear();
            foreach (var kv in stillAboard) { aboard[kv.Key] = kv.Value; Riding.Add(kv.Key); }
            if (last.Count > 64) { var dead = new List<Transform>(); foreach (var k in last.Keys) if (k == null) dead.Add(k); foreach (var k in dead) last.Remove(k); }
        }

        /// <summary>The units on a riding deck's top now (after this frame's carry).</summary>
        private void Aboard(Matrix4x4 now)
        {
            var inv = now.inverse;
            Vector3 c = now.MultiplyPoint3x4(new Vector3(0f, 0.5f, 0f));
            Vector3 half = new(now.GetColumn(0).magnitude / 2f + 0.5f, 1.5f, now.GetColumn(2).magnitude / 2f + 0.5f);
            var rot = Quaternion.LookRotation(now.GetColumn(2), now.GetColumn(1));
            int n = Physics.OverlapBoxNonAlloc(c + Vector3.up, half, overlap, rot, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var m = overlap[i].GetComponentInParent<PathfindMovement>();
                if (m == null || !OnTop(inv, m.transform.position)) continue;
                int id = m.gameObject.GetInstanceID();
                if (!stillAboard.ContainsKey(id) && !aboard.ContainsKey(id))
                {
                    m.ClearCurrentPath();
                    if (m.GetComponent<Pathfinding.RVO.RVOController>() is { } rvo) rvo.enabled = false;
                }
                stillAboard[id] = m;
            }
            // Riders found by where they stand, not only by their colliders (a unit's hit collider may be missing or off).
            foreach (var kv in aboard)
                if (kv.Value != null && !stillAboard.ContainsKey(kv.Key) && OnTop(inv, kv.Value.transform.position)) stillAboard[kv.Key] = kv.Value;
        }

        /// <summary>A unit put on a riding deck's top (teleport) rides from this frame on: its movement must not run
        /// before the carrier's next pass, or the game's navmesh snap drops it back to the ground.</summary>
        public static void Board(PathfindMovement m)
        {
            if (m == null) return;
            int id = m.gameObject.GetInstanceID();
            m.ClearCurrentPath();
            if (m.GetComponent<Pathfinding.RVO.RVOController>() is { } rvo) rvo.enabled = false;
            aboard[id] = m;
            Riding.Add(id);
        }

        private static void Land(PathfindMovement m)
        {
            var p = m.transform.position;
            try
            {
                m.transform.position = m is PathfindMovementEnemy en ? en.GetNearestGroundPosition(p)
                    : m is PathfindMovementPlayerunit pu ? pu.GetNearestGroundPosition(p) : p;
            }
            catch (System.NullReferenceException) { m.transform.position = BodyNav.UnitGround(p); }
            m.ClearCurrentPath();
            if (m.GetComponent<Pathfinding.RVO.RVOController>() is { } rvo) rvo.enabled = true;
        }

        /// <summary>The king's avoidance agent is off while he stands on a riding deck (and back on when he leaves it).</summary>
        private static void KingAvoidance(bool off)
        {
            if (off)
            {
                if (kingAvoidanceOff != null || PlayerInteraction.instance == null) return;
                var rvo = PlayerInteraction.instance.GetComponent<Pathfinding.RVO.RVOController>();
                if (rvo == null || !rvo.enabled) return;
                rvo.enabled = false;
                kingAvoidanceOff = rvo;
            }
            else if (kingAvoidanceOff != null)
            {
                kingAvoidanceOff.enabled = true;
                kingAvoidanceOff = null;
            }
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-5f) return false;
            return true;
        }

        private void Carry(Transform deck, Matrix4x4 before, Matrix4x4 now)
        {
            riders.Clear();
            var inv = before.inverse;
            // Who stood on the top where the deck was: the king, and units found round its old top.
            var king = PlayerInteraction.instance != null ? PlayerInteraction.instance.transform : null;
            if (king != null && OnTop(inv, king.position)) riders.Add(king);
            Vector3 c = before.MultiplyPoint3x4(new Vector3(0f, 0.5f, 0f));
            Vector3 half = new(before.GetColumn(0).magnitude / 2f + 0.5f, 1.5f, before.GetColumn(2).magnitude / 2f + 0.5f);
            var rot = Quaternion.LookRotation(before.GetColumn(2), before.GetColumn(1));
            int n = Physics.OverlapBoxNonAlloc(c + Vector3.up, half, overlap, rot, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var m = overlap[i].GetComponentInParent<PathfindMovement>();
                if (m != null && OnTop(inv, m.transform.position)) riders.Add(m.transform);
            }
            foreach (var kv in aboard)
                if (kv.Value != null && OnTop(inv, kv.Value.transform.position)) riders.Add(kv.Value.transform);
            var turn = Quaternion.LookRotation(now.GetColumn(2), now.GetColumn(1)) * Quaternion.Inverse(rot);
            foreach (var r in riders)
            {
                var local = inv.MultiplyPoint3x4(r.position);
                var to = now.MultiplyPoint3x4(local);
                // Riders end on the top (one that stood a little under it, on the ground below a low deck, steps up).
                float top = now.MultiplyPoint3x4(new Vector3(local.x, 0.5f, local.z)).y;
                if (to.y < top) to.y = top;
                var cc = r.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                var delta = to - r.position;
                r.position = to;
                Anchors(r, delta);
                float yaw = turn.eulerAngles.y;
                if (Mathf.Abs(Mathf.DeltaAngle(0f, yaw)) > 0.01f) r.rotation = Quaternion.Euler(0f, yaw, 0f) * r.rotation;
                if (cc != null) cc.enabled = true;
                Carried++;
            }
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<PathfindMovementPlayerunit, Vector3> P_home = HarmonyLib.AccessTools.FieldRefAccess<PathfindMovementPlayerunit, Vector3>("homePosition");

        /// <summary>A carried unit's anchors move with it: where it holds or returns to (the game's home, a Builder hold or
        /// wander point), so it doesn't walk back to where it stood.</summary>
        private static void Anchors(Transform r, Vector3 delta)
        {
            var pm = r.GetComponent<PathfindMovement>();
            if (pm == null) return;
            if (pm is PathfindMovementEnemy en) Steering.SteerMovement.E_home(en) += delta;
            else if (pm is PathfindMovementPlayerunit pu) { P_home(pu) += delta; Steering.SteerMovement.P_homeOriginal(pu) += delta; }
            if (LogicSteeringManager.Get(r.gameObject) is { } st)
            {
                if (st.HoldPoint is Vector3 h) st.HoldPoint = h + delta;
                if (st.WanderPoint is Vector3 w) st.WanderPoint = w + delta;
            }
        }

        /// <summary>A point stands on the deck's top: inside its footprint, at most 0.6 m under (a unit on the ground under a
        /// low deck the graphs don't have there, a king half stepped up) and 1.5 m over the top.</summary>
        private static bool OnTop(Matrix4x4 inv, Vector3 p)
        {
            var l = inv.MultiplyPoint3x4(p);
            if (Mathf.Abs(l.x) > 0.5f || Mathf.Abs(l.z) > 0.5f) return false;
            // The deck box is 0.3 m thick (local y -0.5..0.5); its top face is at local 0.5.
            var top = inv.inverse.MultiplyPoint3x4(new Vector3(l.x, 0.5f, l.z));
            float dy = p.y - top.y;
            return dy > -0.6f && dy < 1.5f;
        }
    }
}
