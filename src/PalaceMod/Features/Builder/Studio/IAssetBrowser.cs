using System;
using System.Collections.Generic;
using UnityEngine;

/*
    The palette's Library tab (docs/builder-ux.md, "Asset browser slot"). The Studio ships one implementation,
    HostSceneryBrowser (this map's scenery and the pack's asset: meshes); the cross-map library registers its own with
    StudioServices.AssetBrowser = ... . Placed items are saved as LevelDef.props entries whose base is BaseRef(item);
    resolving new base kinds at play time is the browser owner's job (LevelOverlay), the Studio stays agnostic.
*/
namespace PalaceMod.Builder.Studio
{
    internal interface IAssetBrowser
    {
        /// <summary>The tab label.</summary>
        string Title { get; }

        /// <summary>The category chips (left column / top row). May be empty.</summary>
        IReadOnlyList<AssetBrowserCategory> Categories { get; }

        /// <summary>The tiles for a category (null = all) and filter text (null or "" = none). May grow while loading;
        /// raise Changed to have the Studio query again.</summary>
        IReadOnlyList<AssetBrowserItem> Query(string category, string text);

        /// <summary>Raised when Query's results changed (loading finished, the library changed).</summary>
        event Action Changed;

        /// <summary>A ghost for the Place tool, parented under <paramref name="parent"/>: an inert copy (no colliders,
        /// no game scripts) at the origin with yaw 0. Null while the item isn't Ready (the tile shows a spinner badge).
        /// The Studio destroys it.</summary>
        GameObject MakeGhost(AssetBrowserItem item, Transform parent);

        /// <summary>What a placed item is saved as: a PropDef base string ("mesh:&lt;name&gt;", "asset:&lt;key&gt;",
        /// "lib:&lt;id&gt;" ...).</summary>
        string BaseRef(AssetBrowserItem item);

        /// <summary>The scale a newly placed item starts at (e.g. fit huge cliff rocks to about 6 m). 1 = as is.</summary>
        float StartScale(AssetBrowserItem item);
    }

    internal sealed class AssetBrowserCategory
    {
        public string Id, Name;
        public Sprite Icon;
    }

    internal sealed class AssetBrowserItem
    {
        public string Id, Name, Category;
        public Sprite Icon;
        /// <summary>A short corner badge (e.g. a count or the source map), or null.</summary>
        public string Badge;
        /// <summary>False while its mesh is still loading (MakeGhost returns null meanwhile).</summary>
        public bool Ready = true;
        /// <summary>Tooltip text, or null.</summary>
        public string Description;
    }
}
