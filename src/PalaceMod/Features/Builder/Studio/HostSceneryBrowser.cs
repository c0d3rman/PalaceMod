using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Level;
using UnityEngine;

/*
    The Library tab's default browser: the open map's scenery meshes (the same kinds as the Scenery tab), read from the
    live Studio scene on every query. The cross-map asset library replaces it (StudioServices.AssetBrowser) and may wrap
    one of these for "this map".
*/
namespace PalaceMod.Builder.Studio
{
    internal sealed class HostSceneryBrowser : IAssetBrowser
    {
        public HostSceneryBrowser() { }

        public string Title => "Library";

        private static readonly IReadOnlyList<AssetBrowserCategory> categories = [new AssetBrowserCategory { Id = "host", Name = "This map" }];
        public IReadOnlyList<AssetBrowserCategory> Categories => categories;

        public event Action Changed { add { } remove { } }

        public IReadOnlyList<AssetBrowserItem> Query(string category, string text)
        {
            if (category != null && category != "host") return [];
            string f = text?.Trim() ?? "";
            return StudioScene.PropBases
                .Where(b => f.Length == 0 || b.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(b => new AssetBrowserItem { Id = b.Id, Name = b.Name, Category = "host", Badge = "×" + b.Count, Description = $"{b.Count} on this map" })
                .ToList();
        }

        private static PropBase Base(AssetBrowserItem item) => StudioScene.PropBases.FirstOrDefault(b => b.Id == item?.Id);

        public GameObject MakeGhost(AssetBrowserItem item, Transform parent)
        {
            var b = Base(item);
            if (b == null) return null;
            var g = LayoutScene.MakeProp(StudioScene.Scene, b, "ghost", Vector3.zero, 0f, StudioEdits.StartScale(b), false);
            g.transform.SetParent(parent, true);
            return g;
        }

        public string BaseRef(AssetBrowserItem item) => Base(item)?.Id;

        public float StartScale(AssetBrowserItem item) => Base(item) is PropBase b ? StudioEdits.StartScale(b) : 1f;
    }
}
