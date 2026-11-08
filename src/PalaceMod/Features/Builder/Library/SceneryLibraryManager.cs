using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Library;
using PalaceMod.Builder.Studio;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    Host of the cross-map scenery library (Features/Builder/Library): registers the Library tab's browser and the
    Studio's prop resolver, draws tile pictures a few per frame, and the `builder-library` dev command. Infrastructure:
    the Builder's own switch gates everything that uses it.
*/
namespace PalaceMod
{
    public class SceneryLibraryManager : BaseManager
    {
        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            StudioServices.AssetBrowser = new LibraryBrowser(null);
            RegisterDevCommands();
        }

        public override void Update() => LibraryThumbs.Pump();

        private static readonly List<GameObject> placed = [];

        private static void RegisterDevCommands()
        {
            DevCommands.Register("builder-library",
                "builder-library status | query [here|all|kind:<Kind>|-] [text] | place <mesh> [x z] | gallery [spacing] | clear | thumb <mesh> | thumbs [reset]   scenery library", args =>
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
                string rest = string.Join(" ", args.Skip(1));
                switch (sub)
                {
                    case "status":
                    {
                        var ok = SceneryLibrary.Available.ToList();
                        var failed = SceneryLibrary.Failed;
                        return $"{ok.Count} of {SceneryLibrary.Catalog.Meshes.Count} meshes loaded (catalog for Unity {SceneryLibrary.Catalog.Unity}, game {Application.unityVersion})" +
                               (failed.Count == 0 ? "" : "\nfailed:\n" + string.Join("\n", failed.Select(kv => $"  {kv.Key}: {kv.Value}"))) +
                               "\n" + string.Join("\n", ok.GroupBy(e => e.Home).Select(g => $"  {g.Key}: {string.Join(", ", g.Select(e => e.Name))}"));
                    }
                    case "query":
                    {
                        var browser = StudioServices.AssetBrowser ?? throw new InvalidOperationException("no asset browser");
                        string cat = args.Length > 1 && args[1] != "-" ? args[1] : null;
                        string text = args.Length > 2 ? string.Join(" ", args.Skip(2)) : null;
                        var items = browser.Query(cat, text);
                        return $"{browser.Title}: chips {string.Join(" | ", browser.Categories.Select(c => c.Name))}\n{items.Count} items\n" +
                               string.Join("\n", items.Select(i => $"  [{i.Badge}] {i.Name} -> {browser.BaseRef(i)} ready={i.Ready} icon={(i.Icon != null ? "yes" : "no")} scale={browser.StartScale(i):0.0}  {i.Description}"));
                    }
                    case "place":
                    {
                        var parts = rest.Split(' ');
                        float x = 0, z = 0;
                        bool at = parts.Length >= 3 && float.TryParse(parts[^2], out x) && float.TryParse(parts[^1], out z);
                        string name = at ? string.Join(" ", parts.Take(parts.Length - 2)) : rest;
                        var scene = SceneManager.GetActiveScene();
                        var b = SceneryLibrary.Resolve(scene, "mesh:" + name, out var why) ?? throw new ArgumentException($"\"{name}\": {why}");
                        var pos = at ? new Vector3(x, 0f, z) : King() + new Vector3(8f, 0f, 0f);
                        pos = LayoutScene.Ground(pos + Vector3.up * 50f);
                        var go = LayoutScene.MakeProp(scene, b, "library " + name, pos, 0f, 1f, true);
                        placed.Add(go);
                        return $"placed {name} at {pos} (mesh readable={b.Mesh.isReadable}, from {(b.Mesh.isReadable ? "library" : "host")})";
                    }
                    case "gallery":
                    {
                        float spacing = float.TryParse(rest, out var sp) ? sp : 14f;
                        var scene = SceneManager.GetActiveScene();
                        var king = King();
                        var list = SceneryLibrary.Available.ToList();
                        int cols = Mathf.CeilToInt(Mathf.Sqrt(list.Count));
                        int n = 0;
                        foreach (var e in list)
                        {
                            var b = SceneryLibrary.BaseOf(e, out _);
                            if (b == null) continue;
                            var pos = king + new Vector3((n % cols - cols / 2f) * spacing, 0f, (n / cols + 1) * spacing);
                            var size = Vector3.Scale(b.Mesh.bounds.size, b.Scale);
                            float fit = Mathf.Min(1f, (spacing * 0.8f) / Mathf.Max(0.01f, Mathf.Max(size.x, size.y, size.z)));
                            placed.Add(LayoutScene.MakeProp(scene, b, "library " + e.Name, LayoutScene.Ground(pos), 0f, fit, false));
                            n++;
                        }
                        return $"placed {n} library meshes north of the king";
                    }
                    case "clear":
                    {
                        int n = placed.Count(g => g != null);
                        foreach (var g in placed) if (g != null) UnityEngine.Object.Destroy(g);
                        placed.Clear();
                        return $"removed {n}";
                    }
                    case "thumb":
                    {
                        var e = SceneryLibrary.Catalog.Find("mesh:" + rest) ?? throw new ArgumentException($"no library mesh \"{rest}\"");
                        var b = SceneryLibrary.BaseOf(e, out var why) ?? throw new InvalidOperationException(why);
                        var tex = LibraryThumbs.Render(b, 256) ?? throw new InvalidOperationException("render failed");
                        string file = Path.Combine(LibraryThumbs.Folder, "dev-" + e.Name.Trim().Replace(' ', '_') + ".png");
                        Directory.CreateDirectory(LibraryThumbs.Folder);
                        File.WriteAllBytes(file, tex.EncodeToPNG());
                        UnityEngine.Object.Destroy(tex);
                        return file;
                    }
                    case "thumbs":
                    {
                        if (rest == "reset") LibraryThumbs.Clear(files: true);
                        int ready = SceneryLibrary.Available.Count(e => LibraryThumbs.Get(e) != null);
                        return $"{ready} pictures ready for {SceneManager.GetActiveScene().name}; the rest are being drawn into {LibraryThumbs.Folder}";
                    }
                    default:
                        throw new ArgumentException("usage: builder-library status|query|place|gallery|clear|thumb|thumbs");
                }
            });
        }

        private static Vector3 King()
        {
            var p = PlayerInteraction.instance;
            return p != null ? p.transform.position : Vector3.zero;
        }
    }
}
