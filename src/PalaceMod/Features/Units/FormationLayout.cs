using System;
using System.Collections.Generic;
using UnityEngine;

/*
    Pure formation geometry: where each unit of a formation stands. No game state, so the preview and the placement
    patch compute exactly the same slots.

    Every formation is laid out around a center and a forward direction (where the formation faces). Slots come in
    rows, front row first; when a row is full the rest go into rows behind it (Line), or rings inside it (Arc, Circle).
*/
namespace PalaceMod
{
    public enum FormationType
    {
        /// <summary>The game's own loose cluster.</summary>
        Normal,
        /// <summary>Rows facing forward, up to 8 wide (or as wide as a mouse drag).</summary>
        Line,
        /// <summary>A half circle bulging forward, around the center.</summary>
        Arc,
        /// <summary>A ring around the center.</summary>
        Circle,
        /// <summary>Everyone on one spot.</summary>
        Dot,
    }

    public struct FormationSlot
    {
        public Vector3 Position;
        /// <summary>0 for the front row; higher rows are further back (or further in).</summary>
        public int Row;
        /// <summary>Sort key within the row (left to right, or angle around a ring), used to match units to slots.</summary>
        public float Order;
    }

    public static class FormationLayout
    {
        public const int DefaultLineWidth = 8;

        /// <summary>Fills <paramref name="slots"/> with <paramref name="count"/> positions.</summary>
        /// <param name="width">Width of the front row in world units (a mouse drag); 0 picks one from the count.</param>
        public static void Compute(FormationType type, int count, Vector3 center, Vector3 forward, float spacing, float width, List<FormationSlot> slots)
        {
            slots.Clear();
            if (count <= 0) return;
            spacing = Mathf.Max(0.25f, spacing);
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);

            switch (type)
            {
                case FormationType.Line: Line(count, center, forward, right, spacing, width, slots); break;
                case FormationType.Arc: Arc(count, center, forward, right, spacing, width, slots); break;
                case FormationType.Circle: Circle(count, center, forward, right, spacing, width, slots); break;
                case FormationType.Dot:
                    for (int i = 0; i < count; i++) slots.Add(new FormationSlot { Position = center, Order = i });
                    break;
                default: Cluster(count, center, forward, right, spacing, slots); break;
            }
        }

        private static void Line(int count, Vector3 center, Vector3 forward, Vector3 right, float spacing, float width, List<FormationSlot> slots)
        {
            int perRow = width > 0f ? Mathf.FloorToInt(width / spacing + 0.001f) + 1 : DefaultLineWidth;
            perRow = Mathf.Clamp(perRow, 1, count);
            // A dragged line keeps its dragged width when it has room to spare: spread the front row across it.
            float step = width > 0f && perRow > 1 ? Mathf.Max(spacing, width / (perRow - 1)) : spacing;
            for (int row = 0, placed = 0; placed < count; row++)
            {
                int n = Math.Min(perRow, count - placed);
                float rowStep = n == perRow ? step : spacing;
                for (int i = 0; i < n; i++, placed++)
                {
                    float x = (i - (n - 1) / 2f) * rowStep;
                    slots.Add(new FormationSlot { Position = center + right * x - forward * (row * spacing), Row = row, Order = x });
                }
            }
        }

        private static void Arc(int count, Vector3 center, Vector3 forward, Vector3 right, float spacing, float width, List<FormationSlot> slots)
        {
            // Auto radius: everyone fits on one half circle with `spacing` between neighbours.
            float radius = width > 0f ? width / 2f : (count - 1) * spacing / Mathf.PI;
            radius = Mathf.Max(spacing, radius);
            for (int ring = 0, placed = 0; placed < count; ring++)
            {
                float r = radius - ring * spacing;
                if (r < spacing * 0.5f)
                {
                    // No room for another ring: the rest stand at the center.
                    for (; placed < count; placed++) slots.Add(new FormationSlot { Position = center, Row = ring, Order = placed });
                    break;
                }
                int capacity = Mathf.FloorToInt(Mathf.PI * r / spacing + 0.001f) + 1;
                int n = Math.Min(capacity, count - placed);
                for (int i = 0; i < n; i++, placed++)
                {
                    float angle = n == 1 ? 0f : -Mathf.PI / 2f + Mathf.PI * i / (n - 1);
                    Vector3 offset = right * Mathf.Sin(angle) + forward * Mathf.Cos(angle);
                    slots.Add(new FormationSlot { Position = center + offset * r, Row = ring, Order = Mathf.Sin(angle) });
                }
            }
        }

        private static void Circle(int count, Vector3 center, Vector3 forward, Vector3 right, float spacing, float width, List<FormationSlot> slots)
        {
            float radius = width > 0f ? width / 2f : count * spacing / (2f * Mathf.PI);
            radius = Mathf.Max(spacing, radius);
            for (int ring = 0, placed = 0; placed < count; ring++)
            {
                float r = radius - ring * spacing;
                if (r < spacing * 0.5f)
                {
                    for (; placed < count; placed++) slots.Add(new FormationSlot { Position = center, Row = ring, Order = placed });
                    break;
                }
                int capacity = Math.Max(1, Mathf.FloorToInt(2f * Mathf.PI * r / spacing + 0.001f));
                int n = Math.Min(capacity, count - placed);
                for (int i = 0; i < n; i++, placed++)
                {
                    float angle = 2f * Mathf.PI * i / n;
                    Vector3 offset = right * Mathf.Sin(angle) + forward * Mathf.Cos(angle);
                    slots.Add(new FormationSlot { Position = center + offset * r, Row = ring, Order = angle });
                }
            }
        }

        // A tight, even blob (sunflower spiral): what Normal looks like when it's placed away from the king.
        private static void Cluster(int count, Vector3 center, Vector3 forward, Vector3 right, float spacing, List<FormationSlot> slots)
        {
            const float golden = 2.39996323f;
            float scale = spacing * 0.55f;
            for (int i = 0; i < count; i++)
            {
                float r = scale * Mathf.Sqrt(i + (count == 1 ? 0f : 0.5f));
                float angle = i * golden;
                Vector3 offset = right * Mathf.Sin(angle) + forward * Mathf.Cos(angle);
                slots.Add(new FormationSlot { Position = center + offset * r, Row = 0, Order = i });
            }
        }
    }
}
