using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio;
using UnityEngine;

/*
    The design library's game-side wiring (Builder/Designs/DesignLibrary.cs): a level's preview is captured from the
    Studio every time its workspace is saved, and the `library` dev command lists, previews, exports and imports.
*/
namespace PalaceMod
{
    public class DesignLibraryManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            PackDraft.Saved += OnSaved;
            // A level that has no snapshot yet (made before previews, imported, migrated) gets one when it's first
            // opened in the Studio, a moment after its map is up.
            Studio.ModeChanged += mode =>
            {
                if (mode == StudioMode.Edit && Studio.Draft?.IsLibrary == true && Studio.LevelId is { } id && DesignLibrary.PreviewPath(DesignKind.Level, id) == null)
                    { pendingPreview = id; pendingAt = Time.unscaledTime + 1.5f; }
            };
            DevCommands.Register("library", "library list [levels|enemies] | home [enemies] | open <level> | enemy <id> | new-enemy <vanilla id> [name] | rename|duplicate|delete level|enemy <id> [name] | preview <level> | export <name> <level,...> [enemy,...] | code <level,...> | import <packId>   the design library", Command);
        }

        private static string pendingPreview;
        private static float pendingAt;

        public override void Update()
        {
            if (pendingPreview == null || Time.unscaledTime < pendingAt) return;
            string id = pendingPreview;
            pendingPreview = null;
            if (Studio.Mode == StudioMode.Edit && Studio.LevelId == id && CaptureLevel(id)) Log.Info($"Saved a first preview of {id}");
        }

        private static void OnSaved(PackDraft draft)
        {
            string level = draft.WorkspaceLevel;
            if (level == null || Studio.Mode != StudioMode.Edit || Studio.LevelId != level) return;
            CaptureLevel(level);
        }

        /// <summary>Pictures the open Studio level from above, framing its slots, lines and props, and saves it as the
        /// level's library preview.</summary>
        internal static bool CaptureLevel(string levelId)
        {
            var points = Studio.Items.Where(i => i.Kind != StudioItemKind.Group).Select(i => i.Pos).ToList();
            if (points.Count == 0) return false;
            float minX = points.Min(p => p.x), maxX = points.Max(p => p.x), minZ = points.Min(p => p.z), maxZ = points.Max(p => p.z);
            var center = new Vector3((minX + maxX) / 2f, points.Average(p => p.y), (minZ + maxZ) / 2f);
            float half = Mathf.Max(maxX - minX, maxZ - minZ) / 2f + 12f;
            half = Mathf.Clamp(half, 30f, 160f);
            LevelShot shot = null;
            try
            {
                shot = LevelCapture.Take(center, half * 1.25f, half, 480);
                if (shot?.Texture == null) return false;
                return DesignLibrary.SavePreview(DesignKind.Level, levelId, shot.Texture);
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Warn($"Couldn't capture the preview of {levelId}: {e.Message}");
                return false;
            }
            finally
            {
                shot?.Release();
            }
        }

        internal static string Command(string[] args)
        {
            string sub = args.ElementAtOrDefault(0) ?? "list";
            switch (sub)
            {
                case "list":
                {
                    var kinds = args.ElementAtOrDefault(1) switch { "levels" => new[] { DesignKind.Level }, "enemies" => new[] { DesignKind.Enemy }, _ => new[] { DesignKind.Level, DesignKind.Enemy } };
                    var lines = new List<string>();
                    foreach (var kind in kinds)
                        foreach (var item in DesignLibrary.Items(kind).OrderBy(i => i.Id))
                            lines.Add($"{(kind == DesignKind.Level ? "level" : "enemy")} {item.Id} \"{item.Name}\" {(kind == DesignKind.Level ? item.Host + $" nights={item.Nights}" : $"{item.Base} usedBy={item.Nights}")}{(item.Preview != null ? " [preview]" : "")}{(item.Origin != null ? " from " + item.Origin : "")}");
                    return lines.Count == 0 ? "library is empty" : string.Join("\n", lines);
                }
                case "preview":
                {
                    string id = args.ElementAtOrDefault(1) ?? Studio.LevelId ?? throw new ArgumentException("library preview <level> (with that level open in the Studio)");
                    if (Studio.LevelId != id) throw new InvalidOperationException("open the level in the Studio first");
                    return CaptureLevel(id) ? DesignLibrary.PreviewPath(DesignKind.Level, id) : throw new InvalidOperationException("capture failed (see the log)");
                }
                case "export":
                {
                    string name = args.ElementAtOrDefault(1) ?? throw new ArgumentException("library export <name> <level,...> [enemy,...]");
                    return DesignLibrary.ExportFile(Split(args.ElementAtOrDefault(2)), Split(args.ElementAtOrDefault(3)), name);
                }
                case "code":
                    return DesignLibrary.ShareCode(Split(args.ElementAtOrDefault(1)), Split(args.ElementAtOrDefault(2)), "Shared levels");
                case "home":
                    LibraryHome.ShowTab(args.ElementAtOrDefault(1) == "enemies" ? DesignKind.Enemy : DesignKind.Level);
                    return "home open";
                case "open":
                {
                    string id = args.ElementAtOrDefault(1) ?? throw new ArgumentException("library open <level>");
                    LibraryHome.OpenLevel(id);
                    return "opening " + id;
                }
                case "enemy":
                {
                    string id = args.ElementAtOrDefault(1) ?? throw new ArgumentException("library enemy <id>");
                    LibraryHome.OpenEnemy(id);
                    return "opening enemy " + id;
                }
                case "new-enemy":
                {
                    // library new-enemy <vanilla id> [name]: adds an enemy straight to the library (no editor).
                    string baseId = (args.ElementAtOrDefault(1) ?? throw new ArgumentException("library new-enemy <vanilla id> [name]")).Replace('_', ' ');
                    string name = args.Length > 2 ? string.Join(" ", args.Skip(2)) : null;
                    return DesignLibrary.NewEnemyOnDisk(new UnitDef { Id = Ids.FromName(name ?? baseId), Base = ContentRef.Vanilla(baseId), Name = name });
                }
                case "rename":
                    return DesignLibrary.Rename(Kind(args.ElementAtOrDefault(1)), args.ElementAtOrDefault(2), string.Join(" ", args.Skip(3))) ? "renamed" : throw new ArgumentException("library rename level|enemy <id> <name>");
                case "duplicate":
                    return DesignLibrary.Duplicate(Kind(args.ElementAtOrDefault(1)), args.ElementAtOrDefault(2)) ?? throw new ArgumentException("library duplicate level|enemy <id>");
                case "delete":
                    return DesignLibrary.Delete(Kind(args.ElementAtOrDefault(1)), args.ElementAtOrDefault(2)) ? "deleted" : throw new ArgumentException("library delete level|enemy <id>");
                case "import":
                {
                    var entry = BuilderServices.Packs.Find(args.ElementAtOrDefault(1)) ?? throw new ArgumentException("library import <installed pack id>");
                    var r = DesignLibrary.Import(entry.Pack, entry.Folder);
                    return $"imported {r.Summary()}; renamed: {string.Join(", ", r.LevelIds.Concat(r.UnitIds).Select(kv => kv.Key + "->" + kv.Value))}";
                }
                default:
                    throw new ArgumentException("library list | preview | export | code | import");
            }
        }

        private static DesignKind Kind(string s) => s == "enemy" || s == "enemies" ? DesignKind.Enemy : DesignKind.Level;

        private static IEnumerable<string> Split(string s) => string.IsNullOrEmpty(s) ? [] : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);
    }
}
