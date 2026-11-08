using System;
using HarmonyLib;
using UnityEngine;

/*
    The minimap draws its background once, from graph 0's triangles, a frame after Start (MinimapRenderer
    PeriodicallyUpdateMinimap). After a rescan that changed the ground it has to be drawn again.
*/
namespace PalaceMod.Builder.Level
{
    internal static class MinimapRefresh
    {
        public static void Rebuild()
        {
            var mm = MinimapRenderer.instance;
            if (mm == null || AstarPath.active == null || AstarPath.active.graphs.Length == 0) return;
            try
            {
                var t = Traverse.Create(mm);
                // Before its coroutine's first pass the colours aren't set up yet; it draws the new graph itself then.
                if (t.Field("navMesh").GetValue<Mesh>() == null) return;
                var mesh = t.Method("GenerateGroundMesh").GetValue<Mesh>();
                if (mesh == null) return;
                var old = t.Field("navMesh").GetValue<Mesh>();
                t.Field("navMesh").SetValue(mesh);
                if (old != null && old != mesh) UnityEngine.Object.Destroy(old);
                t.Method("RenderBackground").GetValue();
                t.Method("ClearMinimap").GetValue();
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"couldn't redraw the minimap: {e.Message}");
            }
        }
    }
}
