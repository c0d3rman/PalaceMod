using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using PalaceMod.Builder.Studio;
using UnityEngine;
using UnityEngine.SceneManagement;

/*
    The Studio palette's Library tab (IAssetBrowser) backed by the cross-map scenery library.

    Chips: the palette's own "All" (the Studio's HostSceneryBrowser items, passed through untouched, then every library
    mesh, ordered by home map then kind), "Here" (the host browser's items plus the library meshes this map uses), then
    one chip per kind (Trees, Rocks, Walls, Towers, Graves, Other) across all maps. The text filter matches a mesh's name, kind and the maps that use it, so "Moorweg"
    lists Moorweg's. Tiles carry the home map's initials as a badge and a rendered picture (LibraryThumbs) once it's
    drawn. Placed library items are saved as "mesh:<name>", which LevelOverlay and the Studio resolve through
    SceneryLibrary.Resolve (the host's own mesh of that name first).
*/
namespace PalaceMod.Builder.Library
{
    internal sealed class LibraryBrowser : IAssetBrowser
    {
        public const string ThisMap = "here";
        private const string IdPrefix = "library:";

        private readonly IAssetBrowser host;
        private readonly Dictionary<string, AssetBrowserItem> items = new(StringComparer.Ordinal);
        private List<AssetBrowserCategory> categories;

        public LibraryBrowser(IAssetBrowser host)
        {
            this.host = host;
            if (host != null) host.Changed += RaiseChanged;
            LibraryThumbs.Rendered += RaiseChanged;
        }

        public string Title => "Library";

        public event Action Changed;

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception e) { Log.For<BuilderManager>().Caught("Scenery library: Changed handler failed", e); }
        }

        public IReadOnlyList<AssetBrowserCategory> Categories => categories ??= BuildCategories();

        private static List<AssetBrowserCategory> BuildCategories()
        {
            // The palette's own "All" chip (category null) is every map; chips are narrow, so one short word each.
            var list = new List<AssetBrowserCategory> { new() { Id = ThisMap, Name = "Here" } };
            foreach (var kind in SceneryLibrary.Catalog.Meshes.Select(m => m.Category).Where(c => c != null).Distinct().OrderBy(KindOrder))
                list.Add(new AssetBrowserCategory { Id = "kind:" + kind, Name = kind });
            return list;
        }

        private static int KindOrder(string kind) => kind switch
        {
            "Trees" => 0, "Rocks" => 1, "Walls" => 2, "Towers" => 3, "Graves" => 4, _ => 9,
        };

        public IReadOnlyList<AssetBrowserItem> Query(string category, string text)
        {
            var result = new List<AssetBrowserItem>();
            string filter = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            bool here = category == ThisMap;
            if ((category == null || here) && host != null)
                foreach (var item in host.Query(null, filter) ?? [])
                {
                    // The host's own tiles have no picture; the library's of the same mesh fits.
                    if (item.Icon == null && SceneryLibrary.Catalog.Find(host.BaseRef(item)) is SceneryEntry e && SceneryLibrary.MeshOf(e) != null)
                        item.Icon = LibraryThumbs.Get(e);
                    result.Add(item);
                }

            var taken = new HashSet<string>(result.Select(i => host?.BaseRef(i)).Where(r => r != null), StringComparer.Ordinal);
            string kind = category != null && category.StartsWith("kind:", StringComparison.Ordinal) ? category.Substring(5) : null;
            string scene = SceneManager.GetActiveScene().name;
            var maps = SceneryLibrary.Catalog.Maps;
            foreach (var e in SceneryLibrary.Available
                         .OrderBy(e => maps.IndexOf(e.Home)).ThenBy(e => KindOrder(e.Category)).ThenBy(e => e.Name, StringComparer.Ordinal))
            {
                if (kind != null && e.Category != kind) continue;
                // "Here" adds the library meshes this map uses that its live scene no longer shows as separate objects
                // (the game merges most scenery into combined meshes at load).
                if (here && !e.Maps.ContainsKey(scene)) continue;
                if (taken.Contains(e.Ref)) continue;   // the host's own copy is already listed
                if (filter != null && !Matches(e, filter)) continue;
                result.Add(ItemOf(e));
            }
            return result;
        }

        private static bool Matches(SceneryEntry e, string filter) =>
            Contains(e.Name, filter) || Contains(e.Category, filter) || e.Maps.Keys.Any(m => Contains(m, filter));

        private static bool Contains(string s, string part) => s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private AssetBrowserItem ItemOf(SceneryEntry e)
        {
            string id = IdPrefix + e.Name;
            if (!items.TryGetValue(id, out var item))
            {
                string maps = string.Join(", ", e.Maps.OrderByDescending(kv => kv.Value).Select(kv => $"{SceneryCatalog.DisplayMap(kv.Key)} ({kv.Value})"));
                items[id] = item = new AssetBrowserItem
                {
                    Id = id,
                    Name = e.Name.Trim(),
                    Category = "kind:" + e.Category,
                    Badge = Initials(SceneryCatalog.DisplayMap(e.Home)),
                    Description = $"{e.Category} · {maps}",
                };
            }
            item.Icon = LibraryThumbs.Get(e);
            item.Ready = SceneryLibrary.MeshOf(e) != null;
            return item;
        }

        private static string Initials(string map) => string.IsNullOrEmpty(map) ? null : map.Length <= 3 ? map : map.Substring(0, 3);

        private static SceneryEntry EntryOf(AssetBrowserItem item) =>
            item?.Id != null && item.Id.StartsWith(IdPrefix, StringComparison.Ordinal)
                ? SceneryLibrary.Catalog.Meshes.FirstOrDefault(m => m.Name == item.Id.Substring(IdPrefix.Length))
                : null;

        public GameObject MakeGhost(AssetBrowserItem item, Transform parent)
        {
            var e = EntryOf(item);
            if (e == null) return host?.MakeGhost(item, parent);
            var b = SceneryLibrary.BaseOf(e, out _);
            if (b == null) return null;
            var go = LayoutScene.MakeProp(SceneManager.GetActiveScene(), b, "ghost " + e.Name, Vector3.zero, 0f, 1f, false);
            go.transform.SetParent(parent, false);
            return go;
        }

        public string BaseRef(AssetBrowserItem item)
        {
            var e = EntryOf(item);
            return e != null ? e.Ref : host?.BaseRef(item);
        }

        /// <summary>Some scenery is huge (cliff rocks, ruin walls): new props start at most about 6 m across.</summary>
        public float StartScale(AssetBrowserItem item)
        {
            var e = EntryOf(item);
            if (e == null) return host?.StartScale(item) ?? 1f;
            var mesh = SceneryLibrary.MeshOf(e);
            if (mesh == null || e.Scale == null || e.Scale.Length != 3) return 1f;
            var size = Vector3.Scale(mesh.bounds.size, new Vector3(e.Scale[0], e.Scale[1], e.Scale[2]));
            float fit = Mathf.Min(1f, 6f / Mathf.Max(0.01f, Mathf.Max(size.x, size.y, size.z)));
            return Mathf.Clamp(Mathf.Round(fit * 10f) / 10f, 0.3f, 1f);
        }
    }
}
