using System;
using System.Collections.Generic;
using System.Linq;
using PalaceMod.Builder.Editor;
using PalaceMod.Builder.Model;
using UnityEngine;
using UnityEngine.UI;

/*
    The start popup (docs/builder-ux.md, "Starting a level"): a new level picks a map (thumbnail tiles of the main
    maps), what to start from on it (the map as it is, Cleared: its ground and castle only, or a Flat field once the
    terrain layer is there), a name and its nights, then opens in the Studio.
*/
namespace PalaceMod.Builder.Studio.Start
{
    internal static class StartPopup
    {
        /// <summary>Set by the terrain layer (W4) once a flat field can be generated.</summary>
        public static bool FlatFieldAvailable;

        private static readonly string[] BaseLabels = ["The map as it is", "Cleared", "Flat field"];
        private static readonly string[] BaseValues = [null, LevelBases.Cleared, LevelBases.Flat];

        private static PackDraft draft;
        private static string map = "Nordfels", name, waveSet;
        private static int baseIndex = 1;
        private static Action<string> created;

        /// <summary>The main maps (not their challenge variants), in the game's order.</summary>
        public static List<CatalogHost> Maps() =>
            EditorCatalog.Hosts.Where(h => h != null && !string.IsNullOrEmpty(h.Scene) && !h.Scene.Contains(' ')).ToList();

        /// <summary>Opens the popup for a pack being edited. created gets the new level's id (after it's opened).</summary>
        public static void Open(PackDraft d, Action<string> onCreated = null)
        {
            draft = d ?? throw new ArgumentNullException(nameof(d));
            created = onCreated;
            var maps = Maps();
            if (!maps.Any(h => h.Scene == map)) map = maps.FirstOrDefault()?.Scene;
            name = null;
            waveSet = null;
            if (!FlatFieldAvailable && baseIndex == 2) baseIndex = 1;
            ModPopup.Open("New level", Build, width: 920f);
        }

        private static void Build(ModPage page)
        {
            var maps = Maps();
            const float tileW = 160f, tileH = 128f, gap = 12f;
            int perRow = 5;
            int rows = (maps.Count + perRow - 1) / perRow;
            var area = page.Custom("maps", rows * (tileH + gap));
            for (int r = 0; r < rows; r++)
            {
                var row = new List<ThronefallUIElement>();
                for (int c = 0; c < perRow && r * perRow + c < maps.Count; c++)
                {
                    var host = maps[r * perRow + c];
                    var tile = Tile(area, host, new Vector2(c * (tileW + gap), -r * (tileH + gap)), new Vector2(tileW, tileH));
                    row.Add(tile);
                }
                page.AddElementRow(row);
            }

            var chosen = maps.FirstOrDefault(h => h.Scene == map);
            if (chosen != null)
                page.Row(chosen.DisplayName ?? chosen.Scene, $"{chosen.VanillaWaves?.Nights?.Count ?? 0} nights · {chosen.SpawnLines.Count} spawn lines · {chosen.StartingGold}◉");
            int shown = FlatFieldAvailable ? 3 : 2;
            page.Choice("Start from", BaseLabels.Take(shown).ToList(), () => Math.Min(baseIndex, shown - 1), i => { baseIndex = i; ModPopup.Current?.Refresh(); },
                tooltip: () => baseIndex switch
                {
                    0 => "Every build slot and all the scenery of the map",
                    1 => "The map's ground and its castle; no build slots, no scenery",
                    _ => "Flat generated ground with the map's castle, light and sky",
                });
            page.TextInput("Name", () => name ?? DefaultName(), v => name = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
            var sets = draft.Pack.WaveSets?.Where(w => w?.Host == map).ToList() ?? [];
            var nightLabels = new List<string> { "The map's own" };
            nightLabels.AddRange(sets.Select(w => w.Name ?? w.Id));
            page.Choice("Nights", nightLabels, () => Math.Max(0, sets.FindIndex(w => w.Id == waveSet) + 1),
                i => waveSet = i == 0 ? null : sets[i - 1].Id);
            page.Button("Create and open", () => Create());
        }

        private static ModTile Tile(RectTransform parent, CatalogHost host, Vector2 at, Vector2 size)
        {
            var tile = ModTile.Create(parent, "map " + host.Scene, size, null, host.DisplayName ?? host.Scene, 18f);
            tile.Key = host.Scene;
            tile.Place(at);
            var label = tile.Label.rectTransform;
            label.anchorMin = new Vector2(0f, 0f);
            label.anchorMax = new Vector2(1f, 0f);
            label.pivot = new Vector2(0.5f, 0f);
            label.anchoredPosition = new Vector2(0f, 4f);
            label.sizeDelta = new Vector2(-12f, 26f);
            tile.Label.alignment = TMPro.TextAlignmentOptions.Center;
            var thumb = Thumbnails.Get(host.Scene);
            if (thumb != null)
            {
                var img = new GameObject("Thumbnail", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
                img.transform.SetParent(tile.Icon.transform.parent, false);
                img.sprite = thumb;
                img.preserveAspect = true;
                img.raycastTarget = false;
                var rt = img.rectTransform;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.offsetMin = new Vector2(8f, 32f);
                rt.offsetMax = new Vector2(-8f, -8f);
            }
            tile.SetOn(host.Scene == map);
            tile.Applied += () =>
            {
                if (map == host.Scene) return;
                map = host.Scene;
                waveSet = null;
                ModPopup.Current?.Refresh();
            };
            return tile;
        }

        private static string DefaultName()
        {
            var host = EditorCatalog.Host(map);
            string mapName = host?.DisplayName ?? map;
            return baseIndex switch { 1 => $"{mapName} (cleared)", 2 => $"{mapName} field", _ => $"{mapName} Layout" };
        }

        /// <summary>Adds the level to the draft and opens it in the Studio (or the level page without one).</summary>
        public static string Create()
        {
            if (draft == null || map == null) return null;
            string levelName = name ?? DefaultName();
            string id = null;
            int index = draft.Pack.Levels?.Count ?? 0;
            string b = BaseValues[Math.Min(baseIndex, BaseValues.Length - 1)];
            draft.Edit("Add level", p =>
            {
                p.Levels ??= [];
                id = draft.IsLibrary
                    ? DesignLibrary.Store.UniqueLevelId(Ids.FromName(levelName))
                    : EditorCatalog.UniqueId(Ids.FromName(levelName), p.Levels.Select(l => l?.Id));
                p.Levels.Add(new LevelDef { Id = id, Name = levelName, Host = map, Base = b, WaveSet = waveSet });
            }, structural: true);
            ModPopup.Current?.Close();
            if (draft.IsLibrary)
            {
                // A library level is an item from the start: written now, then edited in its own workspace.
                if (!draft.Save(out string saveError)) Notify.Show(saveError, NotifyKind.Error, 5f, "builder-library");
                DesignLibrary.Adopt(draft);
            }
            if (!Studio.Open(draft, id, out string error))
            {
                Log.For<BuilderManager>().Info($"new level {id}: the Studio didn't open ({error}); showing its page");
                BuilderEditor.OpenLevel(index);
            }
            created?.Invoke(id);
            return id;
        }

        /// <summary>For the dev command: sets the choices without clicking.</summary>
        public static void Choose(string scene, int baseKind, string levelName)
        {
            map = scene;
            baseIndex = baseKind;
            name = levelName;
        }
    }
}
