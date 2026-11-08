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

    The strip (in the palette's place): the brushes as an icon row (1-8 pick them; the lit tile is the brush), the
    brush's one line of modifiers, its numbers, and for Material the paints as swatches in the colours the field draws.
    A ramp previews live while dragging (the ground follows the cursor; its incline shows in the status bar and the
    guide line turns red past the walkable slope).

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
        public float Strength = 3f;      // Raise: metres per second at the centre; Smooth: blend per second
        public float Softness = 0.6f;
        public float TargetHeight = 3f;        // SetHeight, relative to the field
        public float RampWidth = 8f;
        public GroundMaterial Paint = GroundMaterial.Earth;

        public static readonly string[] BrushNames = ["Raise", "Flatten", "Set height", "Smooth", "Ramp", "Material", "Block", "Water"];
        public static readonly string[] MaterialNames = ["Grass", "High grass", "Low grass", "Earth", "Sand", "Road", "Rock"];

        private static readonly string[] BrushHelp =
        [
            "Drag raises the ground under the brush. Shift lowers.",
            "Drag levels the ground to the height where you pressed.",
            "Drag levels the ground to the strip's height (Softness 0: a plateau with cliff sides). Alt-click picks up a height.",
            "Drag evens out bumps and edges.",
            "Drag from the top to the bottom: a straight incline the strip's width wide. Up to 30° is walkable.",
            "Drag paints the chosen ground colour, with smooth edges.",
            "Drag blocks the ground for enemies and the king (fliers ignore it). Shift unblocks.",
            "Drag floods the ground (the bed is dug 1 m below the water level). Shift dries it.",
        ];

        private static string Modifiers(TerrainBrush b) => b switch
        {
            TerrainBrush.Raise => "Shift: lower",
            TerrainBrush.SetHeight => "Alt-click: pick height",
            TerrainBrush.Block => "Shift: unblock",
            TerrainBrush.Water => "Shift: dry",
            TerrainBrush.Ramp => "Drag top → bottom",
            _ => null,
        };

        private static bool Flat => Studio.Level != null ? GroundLayer.IsFlat(Studio.Level) : GroundLayer.Flat;

        /// <summary>The brushes this level's base allows.</summary>
        public IReadOnlyList<TerrainBrush> Available => Flat ? (TerrainBrush[])Enum.GetValues(typeof(TerrainBrush)) : [TerrainBrush.Block];

        // ---- StudioTool -----------------------------------------------------------------------------------------

        public override string Id => "terrain";
        public override string Name => "Terrain";
        public override int Order => 700;
        public override Keybind Key => key;
        public override Sprite Icon => icon ??= TerrainIcons.Tool();
        private static Sprite icon;

        public override string Hint
        {
            get
            {
                if (stroking && stroke == TerrainBrush.Ramp && rampSlope is float deg)
                    return $"Ramp {deg:0}°{(deg > WalkSlope ? " · too steep to walk" : "")} · {rampLength:0.#} m · release to make it · Esc cancels";
                string size = Brush == TerrainBrush.Ramp ? $"width {RampWidth:0.#} m" : $"size {Radius:0.#} m";
                string mods = Modifiers(Brush);
                return $"{BrushNames[(int)Brush]}{(mods != null ? " · " + mods : "")} · [ ] or Alt+wheel {size} · 1-8 brushes";
            }
        }

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

        public override bool Digit(int index)
        {
            if (index >= BrushNames.Length) return false;
            SetBrush((TerrainBrush)index);
            return true;
        }

        public void SetBrush(TerrainBrush b)
        {
            if (stroking) return;
            if (!Available.Contains(b))
            {
                Notify.Show($"{BrushNames[(int)b]} needs a Flat field (Start popup)", NotifyKind.Warning, 3f, "studio-terrain");
                return;
            }
            Brush = b;
            Studio.RefreshPanels();
        }

        public override void Update(StudioPointer p)
        {
            if (bigger != null && bigger.Down) SetSize(+1);
            if (smaller != null && smaller.Down) SetSize(-1);
            if (p.Alt && !p.OverUI && !stroking)
            {
                float lines = ScrollInput.Lines;
                if (Mathf.Abs(lines) > 0.01f) SetSize(lines > 0 ? +1 : -1);
            }
            shiftHeld = p.Shift;
            cursor = p.OverUI ? null : p.Ground;
            if (stroking && !p.LeftHeld)
            {
                if (cursor is Vector3 last) ContinueStroke(last, Time.unscaledDeltaTime);
                EndStroke();
                return;
            }
            if (cursor is not Vector3 at) return;
            if (p.LeftDown) BeginStroke(at, p.Shift, p.Alt);
            else if (stroking) ContinueStroke(at, Time.unscaledDeltaTime);
        }

        private void SetSize(int dir)
        {
            if (Brush == TerrainBrush.Ramp)
                RampWidth = Mathf.Clamp(RampWidth + dir * 1f, 2f, 30f);
            else
                Radius = Mathf.Clamp(Mathf.Round((Radius + dir * (Radius < 5f || (Radius == 5f && dir < 0) ? 0.5f : 1f)) * 2f) / 2f, 0.5f, 30f);
            Studio.RefreshPanels();
        }

        private static float WalkSlope => GroundLayer.WalkSlopeDegrees;

        // ---- Strokes --------------------------------------------------------------------------------------------

        private bool stroking, invert, shiftHeld;
        private TerrainBrush stroke;   // the brush the stroke started with
        private Vector3 strokeStart, lastPoint;
        private float pressHeight;
        private Dirty strokeDirty;
        private bool stale;
        // Ramp preview: the heights before the stroke, the region the preview last changed, its incline.
        private float[] rampBefore;
        private Dirty rampDirty;
        private float rampTop;
        private float? rampSlope;
        private float rampLength;

        public void BeginStroke(Vector3 at, bool shift, bool alt)
        {
            if (alt && Brush is TerrainBrush.SetHeight or TerrainBrush.Flatten && GroundLayer.Grid is TerrainGrid sample)
            {
                TargetHeight = (float)Math.Round(sample.SurfaceAt(at.x, at.z) - sample.Y, 2);
                if (Brush == TerrainBrush.Flatten) Brush = TerrainBrush.SetHeight;
                Notify.Show($"Height {TargetHeight:0.##} m", NotifyKind.Info, 2f, "studio-terrain");
                Studio.RefreshPanels();
                return;
            }
            if (!EnsureGrid()) return;
            var g = GroundLayer.Grid;
            stroking = true;
            stroke = Brush;
            invert = shift;
            strokeStart = lastPoint = at;
            pressHeight = g.SurfaceAt(at.x, at.z) - g.Y;
            strokeDirty = Dirty.None;
            if (stroke == TerrainBrush.Ramp)
            {
                rampBefore = (float[])g.Heights.Clone();
                rampDirty = Dirty.None;
                rampTop = pressHeight;
                rampSlope = null;
                return;
            }
            Apply(at, 1f / 30f);
        }

        public void ContinueStroke(Vector3 at, float dt)
        {
            if (!stroking) return;
            lastPoint = at;
            if (stroke == TerrainBrush.Ramp) PreviewRamp(at);
            else Apply(at, dt);
        }

        /// <summary>Puts back what the last preview changed and lays the ramp to the cursor.</summary>
        private void PreviewRamp(Vector3 at)
        {
            var g = GroundLayer.Grid;
            if (g == null || rampBefore == null) return;
            var redraw = rampDirty;
            if (!rampDirty.Empty)
                for (int cz = rampDirty.Z0; cz <= rampDirty.Z1 + 1; cz++)
                    for (int cx = rampDirty.X0; cx <= rampDirty.X1 + 1; cx++)
                    {
                        int i = g.Corner(cx, cz);
                        g.Heights[i] = rampBefore[i];
                    }
            // The bottom's height is the ground there before the ramp.
            float bottom = BeforeHeight(g, at.x, at.z);
            rampLength = new Vector2(at.x - strokeStart.x, at.z - strokeStart.z).magnitude;
            rampSlope = rampLength < 0.5f ? null : TerrainBrushes.RampSlope(strokeStart.x, strokeStart.z, rampTop, at.x, at.z, bottom);
            rampDirty = rampLength < 0.5f ? Dirty.None : TerrainBrushes.Ramp(g, strokeStart.x, strokeStart.z, rampTop, at.x, at.z, bottom, RampWidth);
            redraw.Add(rampDirty);
            if (!redraw.Empty) GroundLayer.Rebuild(redraw);
        }

        private float BeforeHeight(TerrainGrid g, float x, float z)
        {
            var now = (float[])g.Heights.Clone();
            Array.Copy(rampBefore, g.Heights, now.Length);
            float h = g.SurfaceAt(x, z) - g.Y;
            Array.Copy(now, g.Heights, now.Length);
            return h;
        }

        public void EndStroke()
        {
            if (!stroking) return;
            stroking = false;
            var g = GroundLayer.Grid;
            if (g == null) return;
            if (stroke == TerrainBrush.Ramp)
            {
                strokeDirty = rampDirty;
                rampBefore = null;
                rampSlope = null;
            }
            if (strokeDirty.Empty) return;
            Commit(Label());
        }

        private string Label() => stroke switch
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
            Dirty d = stroke switch
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
                Notify.Show($"{BrushNames[(int)Brush]} needs a Flat field (Start popup)", NotifyKind.Warning, 3f, "studio-terrain");
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
            g.WaterLevel = Mathf.Clamp(level, TerrainGrid.MinHeight - g.Y + 1f, g.MaxWorld - g.Y);
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
        private LineRenderer ring, inner, rampLine, rampLeft, rampRight;
        private const int RingPoints = 48;

        private Color BrushColour(bool inverse) => Brush switch
        {
            TerrainBrush.Block => inverse ? new Color(0.55f, 0.9f, 0.55f) : new Color(0.95f, 0.3f, 0.25f),
            TerrainBrush.Water => inverse ? new Color(0.85f, 0.7f, 0.45f) : new Color(0.35f, 0.7f, 1f),
            TerrainBrush.Raise => inverse ? new Color(1f, 0.6f, 0.35f) : Color.white,
            TerrainBrush.Material => GroundLayer.PaintColour(Paint),
            _ => Color.white,
        };

        public void Draw()
        {
            if (Studio.ToolId != Id || cursor is not Vector3 at)
            {
                Hide();
                return;
            }
            ring ??= MakeLine("ring", RingPoints, loop: true);
            inner ??= MakeLine("core", RingPoints, loop: true);
            bool ramping = stroking && stroke == TerrainBrush.Ramp;
            ring.enabled = !ramping;
            bool inverse = stroking ? invert : shiftHeld;
            var color = BrushColour(inverse);
            ring.startColor = ring.endColor = color;
            float r = Brush == TerrainBrush.Ramp ? RampWidth / 2f : Radius;
            Circle(ring, at, r);
            // Soft brushes: a fainter inner ring where the full strength ends.
            bool soft = !ramping && Brush is TerrainBrush.Raise or TerrainBrush.Flatten or TerrainBrush.SetHeight or TerrainBrush.Smooth && Softness > 0.05f;
            inner.enabled = soft;
            if (soft)
            {
                inner.startColor = inner.endColor = new Color(color.r, color.g, color.b, 0.45f);
                Circle(inner, at, Radius * (1f - Softness));
            }
            rampLine ??= MakeLine("ramp", 2, loop: false);
            rampLeft ??= MakeLine("ramp left", 2, loop: false);
            rampRight ??= MakeLine("ramp right", 2, loop: false);
            rampLine.enabled = rampLeft.enabled = rampRight.enabled = ramping;
            if (ramping)
            {
                var c = rampSlope is float deg && deg > WalkSlope ? new Color(0.95f, 0.3f, 0.25f) : new Color(0.55f, 0.95f, 0.6f);
                rampLine.startColor = rampLine.endColor = c;
                rampLeft.startColor = rampLeft.endColor = rampRight.startColor = rampRight.endColor = new Color(c.r, c.g, c.b, 0.6f);
                Segment(rampLine, strokeStart, at, 0f);
                var dir = new Vector3(at.x - strokeStart.x, 0f, at.z - strokeStart.z);
                var side = dir.sqrMagnitude > 0.01f ? Vector3.Cross(Vector3.up, dir.normalized) * (RampWidth / 2f) : Vector3.zero;
                Segment(rampLeft, strokeStart + side, at + side, 0f);
                Segment(rampRight, strokeStart - side, at - side, 0f);
            }
        }

        private static void Circle(LineRenderer lr, Vector3 at, float r)
        {
            for (int i = 0; i < RingPoints; i++)
            {
                float a = i * Mathf.PI * 2f / RingPoints;
                var p = at + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                lr.SetPosition(i, new Vector3(p.x, GroundY(p) + 0.2f, p.z));
            }
        }

        private static void Segment(LineRenderer lr, Vector3 a, Vector3 b, float lift)
        {
            lr.SetPosition(0, new Vector3(a.x, GroundY(a) + 0.25f + lift, a.z));
            lr.SetPosition(1, new Vector3(b.x, GroundY(b) + 0.25f + lift, b.z));
        }

        public void Hide()
        {
            foreach (var lr in new[] { ring, inner, rampLine, rampLeft, rampRight })
                if (lr != null) lr.enabled = false;
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

        // ---- The brush strip ----------------------------------------------------------------------------------------

        public override bool HasPanel => true;

        private const float TileGap = 8f;

        public override void BuildPanel(ModPage page)
        {
            float width = page.Size.x > 1f ? page.Size.x : 270f;
            // The brushes: two rows of four icon tiles, the number key in the corner.
            int perRow = 4;
            float tile = Mathf.Floor((width - TileGap * (perRow - 1)) / perRow);
            var area = page.Custom("Brushes", tile * 2 + TileGap);
            var available = Available;
            for (int i = 0; i < BrushNames.Length; i++)
            {
                var b = (TerrainBrush)i;
                var t = ModTile.Create(area, "Brush " + BrushNames[i], new Vector2(tile, tile), TerrainIcons.Brush(b));
                t.Key = "brush:" + i;
                t.Place(new Vector2((i % perRow) * (tile + TileGap), -(i / perRow) * (tile + TileGap)));
                t.SetBadge((i + 1).ToString());
                t.SetOn(b == Brush);
                bool ok = available.Contains(b);
                t.SetDim(!ok);
                t.Applied += () => SetBrush(b);
                ModTooltip.Attach(t, $"{BrushNames[i]} ({i + 1})", () => BrushHelp[(int)b] + (ok ? "" : "\nNeeds a Flat field (Start popup)."));
                page.AddElement(t);
            }
            string mods = Modifiers(Brush);
            page.Text($"<b>{BrushNames[(int)Brush]}</b>{(mods != null ? "  ·  " + mods : "")}");

            if (Brush == TerrainBrush.Ramp)
                page.Number("Width", () => RampWidth, v => RampWidth = v, 2f, 30f, 1f, v => $"{v:0.#} m", tooltip: () => "The walkable width of the incline; its sides are walls.");
            else
                page.Number("Size", () => Radius, v => Radius = v, 0.5f, 30f, 0.5f, v => $"{v:0.#} m", tooltip: () => "The brush's radius. [ and ], or Alt+wheel.");
            switch (Brush)
            {
                case TerrainBrush.Raise:
                    page.Number("Strength", () => Strength, v => Strength = v, 0.5f, 10f, 0.5f, v => $"{v:0.#} m/s", tooltip: () => "How fast the centre rises while you drag.");
                    Soft(page);
                    break;
                case TerrainBrush.Flatten:
                    Soft(page);
                    break;
                case TerrainBrush.SetHeight:
                    var g = GroundLayer.Grid;
                    float lo = g != null ? TerrainGrid.MinHeight - g.Y : TerrainGrid.MinHeight, hi = g != null ? g.MaxWorld - g.Y : TerrainGrid.MaxAbove;
                    page.Number("Height", () => TargetHeight, v => TargetHeight = v, lo, hi, 0.5f, v => $"{v:0.##} m", tooltip: () => "Above the castle's ground (0). Alt-click the ground to pick one up.");
                    Soft(page);
                    break;
                case TerrainBrush.Smooth:
                    page.Number("Strength", () => Strength, v => Strength = v, 0.5f, 10f, 0.5f, v => $"{v:0.#}", tooltip: () => "How quickly bumps even out.");
                    break;
                case TerrainBrush.Material:
                    Swatches(page, width);
                    break;
                case TerrainBrush.Water:
                    page.Number("Water level", () => GroundLayer.Grid?.WaterLevel ?? -1f, SetWaterLevel, -6f, 10f, 0.5f, v => $"{v:0.##} m", tooltip: () => "The whole field's water surface, above the castle's ground (0).");
                    break;
            }
            page.Row("Paths", stale ? "Out of date" : "Up to date", "Enemy paths are recomputed when you play; click to update them now.", () =>
            {
                UpdatePaths();
                Studio.RefreshPanels();
            });
        }

        private void Soft(ModPage page) =>
            page.Number("Softness", () => Softness * 100f, v => Softness = v / 100f, 0f, 100f, 10f, v => $"{v:0}%", tooltip: () => "0: a hard edge (cliffs). 100: a smooth hill. The inner ring shows where full strength ends.");

        /// <summary>The paints as swatches in the colours the field draws them.</summary>
        private void Swatches(ModPage page, float width)
        {
            int perRow = 4;
            float tile = Mathf.Floor((width - TileGap * (perRow - 1)) / perRow), h = Mathf.Min(tile, 52f);
            int rows = (MaterialNames.Length + perRow - 1) / perRow;
            var area = page.Custom("Paints", rows * h + (rows - 1) * TileGap);
            for (int i = 0; i < MaterialNames.Length; i++)
            {
                var m = (GroundMaterial)i;
                var t = ModTile.Create(area, "Paint " + MaterialNames[i], new Vector2(tile, h));
                t.Key = "paint:" + i;
                t.Place(new Vector2((i % perRow) * (tile + TileGap), -(i / perRow) * (h + TileGap)));
                t.SetFill(GroundLayer.PaintColour(m));
                t.SetOn(m == Paint);
                t.SetMarker(m == Paint);
                t.Applied += () =>
                {
                    Paint = m;
                    Studio.RefreshPanels();
                };
                ModTooltip.Attach(t, MaterialNames[i], () => "Paint with it. Plain grass is the field's own ground.");
                page.AddElement(t);
            }
            page.Text($"Paint: <b>{MaterialNames[(int)Paint]}</b>");
        }
    }
}
