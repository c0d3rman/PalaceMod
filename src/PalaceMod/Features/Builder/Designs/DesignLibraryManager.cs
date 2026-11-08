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
            DevCommands.Register("library", "library list [levels|enemies] | preview <level> | export <name> <level,...> [enemy,...] | code <level,...> | import <packId>   the design library", Command);
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
            string sub = args.ElementAtOrDefault(1) ?? "list";
            switch (sub)
            {
                case "list":
                {
                    var kinds = args.ElementAtOrDefault(2) switch { "levels" => new[] { DesignKind.Level }, "enemies" => new[] { DesignKind.Enemy }, _ => new[] { DesignKind.Level, DesignKind.Enemy } };
                    var lines = new List<string>();
                    foreach (var kind in kinds)
                        foreach (var item in DesignLibrary.Items(kind).OrderBy(i => i.Id))
                            lines.Add($"{(kind == DesignKind.Level ? "level" : "enemy")} {item.Id} \"{item.Name}\" {(kind == DesignKind.Level ? item.Host + $" nights={item.Nights}" : $"{item.Base} usedBy={item.Nights}")}{(item.Preview != null ? " [preview]" : "")}{(item.Origin != null ? " from " + item.Origin : "")}");
                    return lines.Count == 0 ? "library is empty" : string.Join("\n", lines);
                }
                case "preview":
                {
                    string id = args.ElementAtOrDefault(2) ?? Studio.LevelId ?? throw new ArgumentException("library preview <level> (with that level open in the Studio)");
                    if (Studio.LevelId != id) throw new InvalidOperationException("open the level in the Studio first");
                    return CaptureLevel(id) ? DesignLibrary.PreviewPath(DesignKind.Level, id) : throw new InvalidOperationException("capture failed (see the log)");
                }
                case "export":
                {
                    string name = args.ElementAtOrDefault(2) ?? throw new ArgumentException("library export <name> <level,...> [enemy,...]");
                    return DesignLibrary.ExportFile(Split(args.ElementAtOrDefault(3)), Split(args.ElementAtOrDefault(4)), name);
                }
                case "code":
                    return DesignLibrary.ShareCode(Split(args.ElementAtOrDefault(2)), Split(args.ElementAtOrDefault(3)), "Shared levels");
                case "import":
                {
                    var entry = BuilderServices.Packs.Find(args.ElementAtOrDefault(2)) ?? throw new ArgumentException("library import <installed pack id>");
                    var r = DesignLibrary.Import(entry.Pack, entry.Folder);
                    return $"imported {r.Summary()}; renamed: {string.Join(", ", r.LevelIds.Concat(r.UnitIds).Select(kv => kv.Key + "->" + kv.Value))}";
                }
                default:
                    throw new ArgumentException("library list | preview | export | code | import");
            }
        }

        private static IEnumerable<string> Split(string s) => string.IsNullOrEmpty(s) ? [] : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);
    }
}
