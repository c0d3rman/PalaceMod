using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Requirement arrows (docs/builder-ux.md, "What's what"): curved ground arrows from each prerequisite to the slots that
    need it; verdigris for slot links, gold for castle-level links (thicker for higher castle levels). Which arrows show
    follows View ▾ Links (off / the selection's / all); all of them while the Link tool is active, where hovering a slot
    also brightens its whole chain to the castle. The chain preset popup draws its preview here too (Preview).
*/
namespace PalaceMod.Builder.Studio.Requirements
{
    internal sealed class RequirementArrows : IStudioOverlay
    {
        public static readonly RequirementArrows Instance = new();

        /// <summary>Edges to preview instead of the level's (dependent ref -> requirement), or null.</summary>
        public static List<(string slot, SlotRequirement requires)> Preview;

        private static readonly Color SlotLink = ModStyle.Accent;
        private static readonly Color CastleLink = new(0.98f, 0.80f, 0.30f);
        private const int Segments = 18;

        private GameObject root;
        private Material material;
        private readonly List<(LineRenderer arc, LineRenderer head)> pool = [];
        private int used;

        public void Draw()
        {
            used = 0;
            ChainPopup.Tick();
            try { DrawEdges(); }
            finally
            {
                for (int i = used; i < pool.Count; i++) { pool[i].arc.enabled = false; pool[i].head.enabled = false; }
            }
        }

        private void DrawEdges()
        {
            var level = Studio.Level;
            var host = RequirementState.Host;
            if (level == null || host == null) return;
            bool linkTool = Studio.ToolId == LinkTool.ToolId;
            var mode = StudioViewOptions.Links;
            if (Preview == null && !linkTool && mode == StudioViewOptions.LinkMode.Off) return;

            var eff = RequirementState.Effective;
            string hover = Studio.CursorGround is Vector3 g ? Studio.ItemAt(g)?.Key : null;
            var chain = new HashSet<string>(linkTool && hover != null ? LevelRequirements.ChainUp(eff, RequirementState.Ref(hover) ?? "") : [], StringComparer.Ordinal);
            var focus = new HashSet<string>(Studio.Selection.Select(RequirementState.Ref).Where(r => r != null), StringComparer.Ordinal);
            if (hover != null && RequirementState.Ref(hover) is string hr) focus.Add(hr);

            IEnumerable<(string dep, SlotRequirement req)> edges;
            if (Preview != null)
            {
                var previewed = new HashSet<string>(Preview.Select(p => p.slot), StringComparer.Ordinal);
                edges = Preview.Select(p => (p.slot, p.requires?.Slot == SlotRefs.Castle ? new SlotRequirement { Slot = host.Castle, Level = p.requires.Level } : p.requires))
                    .Concat(eff.Where(kv => !previewed.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value)));
            }
            else edges = eff.Select(kv => (kv.Key, kv.Value));

            foreach (var (dep, req) in edges)
            {
                if (req?.Slot == null || host.AddOns.Contains(dep)) continue;
                bool show = Preview != null || linkTool || mode == StudioViewOptions.LinkMode.All || focus.Contains(dep) || focus.Contains(req.Slot);
                if (!show) continue;
                var from = RequirementState.Pos(req.Slot);
                var to = RequirementState.Pos(dep);
                if (from == null || to == null) continue;
                bool castle = req.Slot == host.Castle;
                var color = castle ? CastleLink : SlotLink;
                bool bright = chain.Contains(dep) || focus.Contains(dep) || (Preview?.Any(p => p.slot == dep) ?? false);
                if (!bright && (linkTool || mode == StudioViewOptions.LinkMode.All) && (chain.Count > 0 || focus.Count > 0)) color.a = 0.45f;
                float width = (castle ? 0.22f + 0.08f * req.Level : 0.22f) * (bright ? 1.4f : 1f);
                Arrow(from.Value, to.Value, color, width);
            }
        }

        private void Arrow(Vector3 from, Vector3 to, Color color, float width)
        {
            var (arc, head) = Next();
            var flat = to - from;
            flat.y = 0f;
            float dist = flat.magnitude;
            if (dist < 0.5f) return;
            var dir = flat / dist;
            // Leave room for the slots' rings at both ends.
            var a = from + dir * Mathf.Min(2.5f, dist * 0.25f);
            var b = to - dir * Mathf.Min(2.5f, dist * 0.25f);
            a.y = Studio.Ground(a) + 0.4f;
            b.y = Studio.Ground(b) + 0.4f;
            var mid = (a + b) / 2f + Vector3.up * Mathf.Min(6f, dist * 0.18f);
            arc.positionCount = Segments + 1;
            for (int i = 0; i <= Segments; i++)
            {
                float t = i / (float)Segments;
                arc.SetPosition(i, (1 - t) * (1 - t) * a + 2 * (1 - t) * t * mid + t * t * b);
            }
            var tangent = (b - mid).normalized;
            var side = Vector3.Cross(Vector3.up, tangent).normalized;
            float size = Mathf.Clamp(width * 5f, 1f, 2f);
            head.positionCount = 3;
            head.SetPosition(0, b - tangent * size + side * size * 0.6f);
            head.SetPosition(1, b);
            head.SetPosition(2, b - tangent * size - side * size * 0.6f);
            foreach (var lr in new[] { arc, head })
            {
                lr.startWidth = lr.endWidth = width;
                lr.startColor = lr.endColor = color;
                lr.enabled = true;
            }
        }

        private (LineRenderer, LineRenderer) Next()
        {
            if (root == null)
            {
                pool.Clear();
                root = new GameObject(LayoutScene.AddedPrefix + "requirement arrows");
                var scene = LayoutScene.Host(Studio.Level?.Host);
                if (scene.IsValid()) SceneManager.MoveGameObjectToScene(root, scene);
                material ??= new Material(Shader.Find("Sprites/Default"));
            }
            if (used == pool.Count) pool.Add((Line("arc"), Line("head")));
            return pool[used++];

            LineRenderer Line(string name)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = material;
                lr.useWorldSpace = true;
                lr.numCapVertices = 2;
                lr.numCornerVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.enabled = false;
                return lr;
            }
        }

        public void Hide()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            pool.Clear();
            used = 0;
        }
    }
}
