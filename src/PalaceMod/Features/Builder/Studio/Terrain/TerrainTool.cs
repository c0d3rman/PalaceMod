using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The Studio's Terrain tool (docs/builder-ux.md, "Terrain"): brushes on the level's ground layer. A stroke edits the
    live grid and re-meshes the touched chunks every frame; on release it becomes one undo step (Studio.Edit writing
    LevelDef.terrain) and the graphs are marked stale until the next path check or F5 (IStudioTerrain.UpdatePaths).

    On a flat field every brush works. On a map's own ground only Block does: the grid then only carries the mask.
    The level's terrain is the truth: after any edit, undo or redo (Studio.ItemsChanged) the shown ground is rebuilt
    from it if it differs.
*/
namespace PalaceMod.Builder.Studio.Terrain
{
    internal enum TerrainBrush { Raise, Flatten, SetHeight, Smooth, Ramp, Material, Block, Water }

    internal sealed class TerrainTool : StudioTool, IStudioOverlay, IStudioTerrain
    {
        public static TerrainTool Instance { get; private set; }
        private static Keybind key, bigger, smaller;

        public static void Register()
        {
            const string s = "Builder";
            key = Keybinds.Register(s, "studio-terrain", "Studio: terrain", new KeyboardShortcut(KeyCode.T),
                "In the level studio: the terrain brushes.", KeybindFlags.AllowInMenus, StudioContexts.Edit);
            bigger = Keybinds.Register(s, "studio-brush-bigger", "Studio: bigger brush", new KeyboardShortcut(KeyCode.RightBracket),
                "In the level studio's terrain tool: makes the brush bigger.", KeybindFlags.AllowInMenus, StudioContexts.Edit);
            smaller = Keybinds.Register(s, "studio-brush-smaller", "Studio: smaller brush", new KeyboardShortcut(KeyCode.LeftBracket),
                "In the level studio's terrain tool: makes the brush smaller.", KeybindFlags.AllowInMenus, StudioContexts.Edit);
            Instance = new TerrainTool();
            Studio.AddTool(Instance);
            Studio.AddOverlay(Instance);
            StudioServices.Terrain = Instance;
            Studio.ItemsChanged += Instance.Sync;
            Studio.ModeChanged += Instance.OnMode;
        }

        // ---- Brush settings -------------------------------------------------------------------------------------

        public TerrainBrush Brush = TerrainBrush.Raise;
        public float Radius = 5f;
        public float Strength = 3f;      // Raise: metres per second at the centre; Smooth: blend per application
        public float Softness = 0.6f;
        public float TargetHeight = 3f;        // SetHeight, relative to the field
        public float RampWidth = 8f;
        public GroundMaterial Paint = GroundMaterial.Earth;

        public static readonly string[] BrushNames = ["Raise", "Flatten", "Set height", "Smooth", "Ramp", "Material", "Block", "Water"];
        public static readonly string[] MaterialNames = ["Grass", "High grass", "Low grass", "Earth", "Sand", "Road", "Rock"];

        private static bool Flat => Studio.Level != null ? GroundLayer.IsFlat(Studio.Level) : GroundLayer.Flat;

        /// <summary>The brushes this level's base allows.</summary>
        public IReadOnlyList<TerrainBrush> Available => Flat ? (TerrainBrush[])Enum.GetValues(typeof(TerrainBrush)) : [TerrainBrush.Block];

        // ---- StudioTool -----------------------------------------------------------------------------------------

        public override string Id => "terrain";
        public override string Name => "Terrain";
        public override int Order => 700;
        public override Keybind Key => key;
        public override Sprite Icon => icon ??= MakeIcon();
        private static Sprite icon;

        public override string Hint => Brush switch
        {
            TerrainBrush.Raise => "Drag raise · Shift lower · [ ] size",
            TerrainBrush.Flatten => "Drag level to the height under the press · [ ] size",
            TerrainBrush.SetHeight => "Drag set height · Alt-click sample · [ ] size",
            TerrainBrush.Smooth => "Drag smooth · [ ] size",
            TerrainBrush.Ramp => "Drag from the top to the bottom",
            TerrainBrush.Material => "Drag paint · [ ] size",
            TerrainBrush.Block => "Drag block · Shift unblock · [ ] size",
            TerrainBrush.Water => "Drag water · Shift dry · [ ] size",
            _ => "",
        };

        public override void Activate()
        {
            if (!Available.Contains(Brush)) Brush = Available[0];
        }

        public override void Deactivate()
        {
            if (stroking) EndStroke();
            Hide();
        }

        public override bool Cancel()
        {
            if (!stroking) return false;
            // Back to what the level holds.
            stroking = false;
            GroundLayer.SourceKey = null;
            Sync();
            return true;
        }

        public override void Update(StudioPointer p)
        {
            if (bigger != null && bigger.Down) SetRadius(Radius + (Radius < 5f ? 0.5f : 1f));
            if (smaller != null && smaller.Down) SetRadius(Radius - (Radius <= 5f ? 0.5f : 1f));
            cursor = p.OverUI ? null : p.Ground;
            if (stroking && !p.LeftHeld)
            {
                if (cursor is Vector3 last) lastPoint = last;
                EndStroke();
                return;
            }
            if (cursor is not Vector3 at) return;
            if (p.LeftDown) BeginStroke(at, p.Shift, p.Alt);
            else if (stroking) ContinueStroke(at, Time.unscaledDeltaTime);
        }

        private void SetRadius(float r)
        {
            Radius = Mathf.Clamp(Mathf.Round(r * 2f) / 2f, 0.5f, 30f);
            Studio.RefreshPanels();
        }

        // ---- Strokes --------------------------------------------------------------------------------------------

        private bool stroking, invert;
        private Vector3 strokeStart, lastPoint;
        private float pressHeight;
        private Dirty strokeDirty;
        private bool stale;

        public void BeginStroke(Vector3 at, bool shift, bool alt)
        {
            if (alt && Brush is TerrainBrush.SetHeight or TerrainBrush.Flatten && GroundLayer.Grid is TerrainGrid sample)
            {
                TargetHeight = (float)Math.Round(sample.HeightAt(at.x, at.z) - sample.Y, 2);
                Notify.Show($"Height {TargetHeight:0.##} m", NotifyKind.Info, 2f, "studio-terrain");
                Studio.RefreshPanels();
                return;
            }
            if (!EnsureGrid()) return;
            var g = GroundLayer.Grid;
            stroking = true;
            invert = shift;
            strokeStart = lastPoint = at;
            pressHeight = g.HeightAt(at.x, at.z) - g.Y;
            strokeDirty = Dirty.None;
            if (Brush != TerrainBrush.Ramp) Apply(at, 1f / 30f);
        }

        public void ContinueStroke(Vector3 at, float dt)
        {
            if (!stroking) return;
            lastPoint = at;
            if (Brush != TerrainBrush.Ramp) Apply(at, dt);
        }

        public void EndStroke()
        {
            if (!stroking) return;
            stroking = false;
            var g = GroundLayer.Grid;
            if (g == null) return;
            if (Brush == TerrainBrush.Ramp)
            {
                float top = g.HeightAt(strokeStart.x, strokeStart.z) - g.Y, bottom = g.HeightAt(lastPoint.x, lastPoint.z) - g.Y;
                var d = TerrainBrushes.Ramp(g, strokeStart.x, strokeStart.z, top, lastPoint.x, lastPoint.z, bottom, RampWidth);
                strokeDirty.Add(d);
                GroundLayer.Rebuild(d);
            }
            if (strokeDirty.Empty) return;
            Commit(Label());
        }

        private string Label() => Brush switch
        {
            TerrainBrush.Raise => invert ? "Lower ground" : "Raise ground",
            TerrainBrush.Flatten => "Flatten ground",
            TerrainBrush.SetHeight => "Set ground height",
            TerrainBrush.Smooth => "Smooth ground",
            TerrainBrush.Ramp => "Make a ramp",
            TerrainBrush.Material => "Paint " + MaterialNames[(int)Paint].ToLowerInvariant(),
            TerrainBrush.Block => invert ? "Unblock ground" : "Block ground",
            TerrainBrush.Water => invert ? "Remove water" : "Add water",
            _ => "Edit terrain",
        };

        private void Apply(Vector3 at, float dt)
        {
            var g = GroundLayer.Grid;
            if (g == null) return;
            dt = Mathf.Min(dt, 0.1f);
            Dirty d = Brush switch
            {
                TerrainBrush.Raise => TerrainBrushes.Raise(g, at.x, at.z, Radius, Softness, (invert ? -1f : 1f) * Strength * dt),
                TerrainBrush.Flatten => TerrainBrushes.Flatten(g, at.x, at.z, Radius, Softness, pressHeight),
                TerrainBrush.SetHeight => TerrainBrushes.Flatten(g, at.x, at.z, Radius, Softness, TargetHeight),
                TerrainBrush.Smooth => TerrainBrushes.Smooth(g, at.x, at.z, Radius, Softness, Mathf.Clamp01(Strength * dt)),
                TerrainBrush.Material => TerrainBrushes.Paint(g, at.x, at.z, Radius, CellLayer.Material, (byte)Paint),
                TerrainBrush.Block => TerrainBrushes.Paint(g, at.x, at.z, Radius, CellLayer.Blocked, (byte)(invert ? 0 : 1)),
                TerrainBrush.Water => TerrainBrushes.Water(g, at.x, at.z, Radius, !invert),
                _ => Dirty.None,
            };
            if (d.Empty) return;
            strokeDirty.Add(d);
            GroundLayer.Rebuild(d);
        }

        /// <summary>Makes sure there's a grid to paint on: the level's, or a new one over the map.</summary>
        private bool EnsureGrid()
        {
            if (!Available.Contains(Brush))
            {
                Notify.Show("Flat field only", NotifyKind.Warning, 3f, "studio-terrain");
                return false;
            }
            if (GroundLayer.Active) return true;
            var level = Studio.Level;
            var scene = LayoutScene.Host(level?.Host);
            if (level == null || !scene.IsValid()) return false;
            GroundLayer.Show(scene, GroundLayer.NewFlat(scene), GroundLayer.IsFlat(level));
            GroundLayer.ShowMarks = true;
            return true;
        }

        /// <summary>Writes the grid into the level as one undo step.</summary>
        private void Commit(string label)
        {
            var def = GroundCodec.Encode(GroundLayer.Grid);
            var level = Studio.Level;
            stale = true;
            if (level == null) return;
            // The shown ground already is this; the sync after the edit must not rebuild it.
            GroundLayer.SourceKey = GroundLayer.KeyOf(level.Base, def);
            Studio.Edit(label, lv => lv.Terrain = def);
        }

        /// <summary>Sets the water level of the whole field (one undo step).</summary>
        public void SetWaterLevel(float level)
        {
            if (!EnsureGrid()) return;
            var g = GroundLayer.Grid;
            g.WaterLevel = Mathf.Clamp(level, TerrainGrid.MinHeight - g.Y + 1f, TerrainGrid.MaxHeight - g.Y);
            GroundLayer.Rebuild(new Dirty { X0 = 0, Z0 = 0, X1 = g.W - 1, Z1 = g.H - 1 });
            Commit("Set the water level");
        }

        // ---- Keeping the ground in step with the level ----------------------------------------------------------------

        private void OnMode(StudioMode mode)
        {
            GroundLayer.ShowMarks = mode == StudioMode.Edit;
            if (mode == StudioMode.Edit) Sync();
            else if (mode == StudioMode.Off) stroking = false;
        }

        /// <summary>Rebuilds the shown ground when the level's terrain differs from it (undo, redo, another edit).</summary>
        public void Sync()
        {
            if (Studio.Mode != StudioMode.Edit || stroking) return;
            var level = Studio.Level;
            if (level == null) return;
            string k = GroundLayer.KeyOf(level.Base, level.Terrain);
            if (k == GroundLayer.SourceKey && (GroundLayer.Active || !GroundLayer.Wants(level))) return;
            var scene = LayoutScene.Host(level.Host);
            if (!GroundLayer.Wants(level) || !scene.IsValid())
            {
                GroundLayer.Clear();
                GroundLayer.SourceKey = k;
                return;
            }
            try
            {
                GroundLayer.Show(scene, GroundLayer.Decode(level) ?? GroundLayer.NewFlat(scene), GroundLayer.IsFlat(level));
                GroundLayer.ShowMarks = true;
                if (GroundLayer.Flat) GroundLayer.SnapToGround(scene);
            }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            {
                Log.For<BuilderManager>().Warn($"terrain of {level.Id} can't be read: {e.Message}");
                GroundLayer.Clear();
            }
            GroundLayer.SourceKey = k;
            stale = true;
        }

        // ---- IStudioTerrain -------------------------------------------------------------------------------------

        public float? Height(Vector3 point) => GroundLayer.TryHeight(point, out float y) ? y : null;
        public bool PathsStale => stale;

        public void UpdatePaths()
        {
            long ms = GroundGraphs.Scan();
            stale = false;
            Log.For<BuilderManager>().Info($"terrain: paths updated in {ms} ms");
        }

        // ---- Brush ring (IStudioOverlay) ---------------------------------------------------------------------------

        private Vector3? cursor;
        private LineRenderer ring, rampLine;
        private const int RingPoints = 48;

        public void Draw()
        {
            if (Studio.ToolId != Id || cursor is not Vector3 at)
            {
                Hide();
                return;
            }
            ring ??= MakeLine("ring", RingPoints, loop: true);
            ring.enabled = Brush != TerrainBrush.Ramp || !stroking;
            var color = Brush switch
            {
                TerrainBrush.Block => new Color(0.95f, 0.3f, 0.25f),
                TerrainBrush.Water => new Color(0.35f, 0.7f, 1f),
                _ => Color.white,
            };
            ring.startColor = ring.endColor = color;
            float r = Brush == TerrainBrush.Ramp ? RampWidth / 2f : Radius;
            for (int i = 0; i < RingPoints; i++)
            {
                float a = i * Mathf.PI * 2f / RingPoints;
                var p = at + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                ring.SetPosition(i, new Vector3(p.x, GroundY(p) + 0.2f, p.z));
            }
            rampLine ??= MakeLine("ramp", 2, loop: false);
            rampLine.enabled = stroking && Brush == TerrainBrush.Ramp;
            if (rampLine.enabled)
            {
                rampLine.startColor = rampLine.endColor = color;
                rampLine.SetPosition(0, new Vector3(strokeStart.x, GroundY(strokeStart) + 0.2f, strokeStart.z));
                rampLine.SetPosition(1, new Vector3(at.x, GroundY(at) + 0.2f, at.z));
            }
        }

        public void Hide()
        {
            if (ring != null) ring.enabled = false;
            if (rampLine != null) rampLine.enabled = false;
        }

        private static float GroundY(Vector3 p) => GroundLayer.TryHeight(p, out float y) ? y : Studio.Ground(p);

        private static LineRenderer MakeLine(string name, int points, bool loop)
        {
            var go = new GameObject(LayoutScene.AddedPrefix + "terrain " + name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = points;
            lr.loop = loop;
            lr.widthMultiplier = 0.18f;
            lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3100 };
            lr.enabled = false;
            return lr;
        }

        /// <summary>A small mountain glyph for the tool column.</summary>
        private static Sprite MakeIcon()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // Two peaks: |x - c| < (top - y) / slope.
                    bool big = Math.Abs(x - 26) < (52 - y) * 0.85f && y >= 10;
                    bool small = Math.Abs(x - 44) < (38 - y) * 0.9f && y >= 10;
                    px[y * n + x] = big || small ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        }

        // ---- The brush strip ----------------------------------------------------------------------------------------

        public override bool HasPanel => true;

        public override void BuildPanel(ModPage page)
        {
            page.Header("Terrain");
            var brushes = Available;
            page.Choice("Brush", brushes.Select(b => BrushNames[(int)b]).ToList(), () => brushes.ToList().IndexOf(Brush), i =>
            {
                Brush = brushes[i];
                Studio.RefreshPanels();
            });
            if (Brush == TerrainBrush.Ramp)
                page.Number("Width", () => RampWidth, v => RampWidth = v, 2f, 30f, 1f, v => $"{v:0.#} m");
            else
                page.Number("Size", () => Radius, v => Radius = v, 0.5f, 30f, 0.5f, v => $"{v:0.#} m");
            switch (Brush)
            {
                case TerrainBrush.Raise:
                    page.Number("Strength", () => Strength, v => Strength = v, 0.5f, 10f, 0.5f, v => $"{v:0.#} m/s");
                    page.Number("Softness", () => Softness * 100f, v => Softness = v / 100f, 0f, 100f, 10f, v => $"{v:0}%");
                    break;
                case TerrainBrush.Flatten:
                    page.Number("Softness", () => Softness * 100f, v => Softness = v / 100f, 0f, 100f, 10f, v => $"{v:0}%");
                    break;
                case TerrainBrush.SetHeight:
                    page.Number("Height", () => TargetHeight, v => TargetHeight = v, TerrainGrid.MinHeight, TerrainGrid.MaxHeight, 0.5f, v => $"{v:0.##} m");
                    page.Number("Softness", () => Softness * 100f, v => Softness = v / 100f, 0f, 100f, 10f, v => $"{v:0}%");
                    break;
                case TerrainBrush.Smooth:
                    page.Number("Strength", () => Strength, v => Strength = v, 0.5f, 10f, 0.5f, v => $"{v:0.#}");
                    break;
                case TerrainBrush.Material:
                    page.Choice("Material", MaterialNames, () => (int)Paint, i => Paint = (GroundMaterial)i);
                    break;
                case TerrainBrush.Water:
                    page.Number("Water level", () => GroundLayer.Grid?.WaterLevel ?? -1f, SetWaterLevel, -6f, 10f, 0.5f, v => $"{v:0.##} m");
                    break;
            }
            page.Row("Paths", stale ? "Out of date" : "Up to date", onClick: () =>
            {
                UpdatePaths();
                Studio.RefreshPanels();
            });
        }
    }
}
