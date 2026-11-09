using UnityEngine;

/*
    Which way an entity faces (degrees, 0 = north / +Z, clockwise): the one source of truth for `.facing`, `ahead()`,
    parts that turn with their parent and formation slots. A unit's root never turns: the game turns its model
    (RotateForwardToRVOVelocity.transformToRotate, toward its walking direction or its attack target), and the king's
    model is PlayerMovement.meshParent. Everything else (Builder bodies, buildings) turns its own root.
*/
namespace PalaceMod.Builder.Logic.Bodies
{
    public static class Heading
    {
        public static float Yaw(LogicEntity e) => e?.Go() is { } go && go ? Yaw(go) : e?.Body?.Facing ?? 0f;

        public static float Yaw(GameObject go) => go == null ? 0f : Normalize(Turned(go).eulerAngles.y);

        /// <summary>The transform whose forward is the object's facing.</summary>
        public static Transform Turned(GameObject go)
        {
            var rot = go.GetComponentInChildren<RotateForwardToRVOVelocity>();
            if (rot != null && rot.transformToRotate != null) return rot.transformToRotate;
            var rot2 = go.GetComponentInChildren<RotateForwardToRVOVelocityRVO>();
            if (rot2 != null && rot2.transformToRotate != null) return rot2.transformToRotate;
            var pm = go.GetComponent<PlayerMovement>();
            if (pm != null && pm.meshParent != null) return pm.meshParent;
            return go.transform;
        }

        public static float Normalize(float deg) { deg %= 360f; return deg < 0 ? deg + 360f : deg; }

        /// <summary>The rotation that turns an offset in the entity's own frame (x right, y up, z forward) into the world.</summary>
        public static Quaternion Rotation(LogicEntity e) => Quaternion.Euler(0f, Yaw(e), 0f);
    }
}
