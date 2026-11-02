using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    The savestate menu (pause menu > Savestates, or its key): the level's states with thumbnails, a detail page per
    state with Load / Overwrite / Delete, and a confirmation step for anything destructive.
*/
namespace PalaceMod
{
    internal sealed class SaveStateBrowser
    {
        private readonly ModFrame list;
        private readonly ModFrame detail;
        private SaveStateInfo selected;
        private string confirming;   // null, "load", "overwrite" or "delete"

        public SaveStateBrowser()
        {
            list = new ModFrame("Savestates")
                .AddTab("Saved", BuildSaved)
                .AddTab("Automatic", BuildAuto);
            detail = new ModFrame("Savestate").SetContent(BuildDetail);
            SaveStates.Changed += () => { if (list.IsOpen) list.Refresh(); };
        }

        public bool IsOpen => list.IsOpen || detail.IsOpen;

        public void Open()
        {
            if (SaveStates.CurrentLevel == null) return;
            list.Open();
        }

        public void Close()
        {
            if (detail.IsOpen) detail.Close();
            if (list.IsOpen) list.Close();
        }

        // ---- Pages ----------------------------------------------------------------------------------------------

        private static string LevelName()
        {
            string scene = SaveStates.CurrentLevel;
            var info = LevelProgressManager.instance?.GetLevelInfoFromCurrentSceneName();
            return info != null && !string.IsNullOrEmpty(info.displayName) ? info.displayName : scene;
        }

        private void BuildSaved(ModPage page)
        {
            page.Header(LevelName());
            bool can = SaveStates.CanCapture(out string reason);
            page.Button(can ? "Save the current moment" : $"Can't save now: {reason}", () =>
            {
                try
                {
                    var state = SaveStates.Capture();
                    Notify.Show($"Saved \"{state.Name}\"", NotifyKind.Success, key: "savestates");
                }
                catch (InvalidOperationException e) { Notify.Show(e.Message, NotifyKind.Warning, key: "savestates"); }
            }, "Saves everything about this moment as a new state: buildings, gold, units, the king, and mid-night the enemies too.");
            var states = SaveStates.List().Where(s => s.Kind is SaveStateKind.Manual or SaveStateKind.Quick).ToList();
            if (states.Count == 0)
            {
                page.Space(10f);
                page.Text($"No savestates for this level yet. {Keys()}");
                return;
            }
            page.Space(10f);
            foreach (var state in states) StateRow(page, state);
        }

        private void BuildAuto(ModPage page)
        {
            page.Header(LevelName());
            var manager = Feature<SaveStatesManager>.Instance;
            page.Text(manager.AutoMode == AutoStateMode.Off
                ? "Automatic states are off (Settings > Mods > Savestates). An undo state is still kept before every load."
                : $"Taken {AutoWhen(manager.AutoMode)}; the newest {manager.AutoHistory} are kept. An undo state is kept before every load.");
            var states = SaveStates.List().Where(s => s.Kind is SaveStateKind.AutoDawn or SaveStateKind.AutoDusk or SaveStateKind.BeforeLoad).ToList();
            if (states.Count == 0)
            {
                page.Text("None yet.");
                return;
            }
            page.Space(10f);
            foreach (var state in states) StateRow(page, state);
        }

        private static string AutoWhen(AutoStateMode mode) => mode switch
        {
            AutoStateMode.Dawn => "shortly after every sunrise",
            AutoStateMode.Dusk => "right before every night",
            _ => "shortly after every sunrise and right before every night",
        };

        private static string Keys()
        {
            var manager = Feature<SaveStatesManager>.Instance;
            string save = manager.QuickSaveKey.Shortcut.MainKey != KeyCode.None ? manager.QuickSaveKey.Shortcut.ToString() : null;
            string load = manager.QuickLoadKey.Shortcut.MainKey != KeyCode.None ? manager.QuickLoadKey.Shortcut.ToString() : null;
            if (save == null || load == null) return "";
            return $"{save} quick-saves and {load} quick-loads at any time.";
        }

        private void StateRow(ModPage page, SaveStateInfo state)
        {
            page.Row(state.Name, $"{state.Moment}  ·  {state.Gold}g  ·  {Ago(state.CreatedUtc)}", () => Details(state),
                () => { selected = state; confirming = null; detail.Open(); }, Thumbnails.Sprite(state));
        }

        private static string Details(SaveStateInfo s)
        {
            string when = s.IsNight ? $"During night {s.NightNumber} of {s.WaveCount}" : $"Day before night {s.NightNumber} of {s.WaveCount}";
            return $"{when}\n{s.Gold} gold, score {s.Score}\n{s.UnitsAlive} units alive" +
                (s.IsNight ? $", {s.EnemiesAlive} enemies" : "") + $"\nSaved {s.CreatedUtc.ToLocalTime():MMM d, HH:mm}";
        }

        private void BuildDetail(ModPage page)
        {
            var state = selected;
            if (state == null || !File.Exists(state.JsonPath))
            {
                page.Text("This state no longer exists.");
                return;
            }
            if (confirming != null)
            {
                BuildConfirm(page, state);
                return;
            }
            var header = page.Header(state.Name);
            Thumbnail(header.transform.parent, state);
            page.Row("When", state.IsNight ? $"Night {state.NightNumber} of {state.WaveCount}" : $"Day before night {state.NightNumber}");
            page.Row("Gold", state.Gold.ToString());
            page.Row("Score", state.Score.ToString());
            page.Row("Units alive", state.UnitsAlive.ToString());
            if (state.IsNight) page.Row("Enemies alive", state.EnemiesAlive.ToString());
            page.Row("Saved", $"{state.CreatedUtc.ToLocalTime():MMM d, HH:mm} ({Ago(state.CreatedUtc)})");
            page.Space(10f);
            bool canLoad = SaveStates.CanRestore(state, out string reason);
            page.Button(canLoad ? "Load" : $"Can't load: {reason}", () =>
            {
                if (!SaveStates.CanRestore(state, out string why)) { Notify.Show(why, NotifyKind.Warning, key: "savestates"); return; }
                if (Feature<SaveStatesManager>.Instance.ConfirmLoad) Confirm("load");
                else Load(state);
            });
            if (state.Kind is SaveStateKind.Manual or SaveStateKind.Quick)
                page.Button("Overwrite with the current moment", () => Confirm("overwrite"));
            page.Button("Delete", () => Confirm("delete"));
        }

        private void BuildConfirm(ModPage page, SaveStateInfo state)
        {
            switch (confirming)
            {
                case "load":
                    page.Header($"Load \"{state.Name}\"?");
                    page.Text("Everything since that moment is replaced. An undo state (\"Before last load\") is kept on the Automatic tab. " +
                        "Loading a state marks this run as modded.");
                    page.Button("Load", () => Load(state));
                    break;
                case "overwrite":
                    page.Header($"Overwrite \"{state.Name}\"?");
                    page.Text("The state is replaced with the current moment.");
                    page.Button("Overwrite", () =>
                    {
                        try
                        {
                            selected = SaveStates.Overwrite(state);
                            Notify.Show($"Saved \"{selected.Name}\"", NotifyKind.Success, key: "savestates");
                        }
                        catch (InvalidOperationException e) { Notify.Show(e.Message, NotifyKind.Warning, key: "savestates"); }
                        confirming = null;
                        // Back to the list, rebuilt: its rows pointed at the replaced state.
                        detail.Close();
                        list.Refresh();
                    });
                    break;
                case "delete":
                    page.Header($"Delete \"{state.Name}\"?");
                    page.Text("This can't be undone.");
                    page.Button("Delete", () =>
                    {
                        SaveStates.Delete(state);
                        selected = null;
                        confirming = null;
                        detail.Close();
                        list.Refresh();
                    });
                    break;
            }
            page.Button("Cancel", () => { confirming = null; detail.Refresh(); });
        }

        private void Confirm(string action)
        {
            confirming = action;
            detail.Refresh();
        }

        private void Load(SaveStateInfo state)
        {
            confirming = null;
            try { SaveStates.Restore(state); }
            catch (InvalidOperationException e) { Notify.Show(e.Message, NotifyKind.Warning, key: "savestates"); }
        }

        private static void Thumbnail(Transform content, SaveStateInfo state)
        {
            var texture = Thumbnails.Texture(state);
            if (texture == null) return;
            const float height = 216f;
            var holder = new GameObject("Thumbnail", typeof(RectTransform), typeof(LayoutElement));
            holder.transform.SetParent(content, false);
            holder.GetComponent<LayoutElement>().preferredHeight = height + 12f;
            var image = new GameObject("Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            image.transform.SetParent(holder.transform, false);
            var raw = image.GetComponent<RawImage>();
            raw.texture = texture;
            raw.raycastTarget = false;
            var rect = (RectTransform)image.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -6f);
            rect.sizeDelta = new Vector2(height * texture.width / texture.height, height);
        }

        internal static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
            return $"{(int)span.TotalDays} d ago";
        }
    }

    /// <summary>Loads savestate thumbnails from disk once and keeps them while their file is unchanged.</summary>
    internal static class Thumbnails
    {
        private sealed class Entry
        {
            public DateTime Stamp;
            public Texture2D Texture;
            public Sprite Sprite;
        }

        private static readonly Dictionary<string, Entry> cache = [];

        public static Texture2D Texture(SaveStateInfo state) => Get(state.ThumbnailPath)?.Texture;

        public static Sprite Sprite(SaveStateInfo state)
        {
            var entry = Get(state.ThumbnailPath);
            if (entry == null) return null;
            if (entry.Sprite == null)
                entry.Sprite = UnityEngine.Sprite.Create(entry.Texture, new Rect(0, 0, entry.Texture.width, entry.Texture.height), new Vector2(0.5f, 0.5f));
            return entry.Sprite;
        }

        private static Entry Get(string path)
        {
            try
            {
                if (!File.Exists(path)) { ForgetPath(path); return null; }
                var stamp = File.GetLastWriteTimeUtc(path);
                if (cache.TryGetValue(path, out var hit) && hit.Stamp == stamp && hit.Texture != null) return hit;
                ForgetPath(path);
                var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "PalaceMod savestate thumbnail" };
                if (!texture.LoadImage(File.ReadAllBytes(path))) { Object.Destroy(texture); return null; }
                var entry = new Entry { Stamp = stamp, Texture = texture };
                cache[path] = entry;
                return entry;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Savestates: thumbnail {path} unreadable: {e.Message}");
                return null;
            }
        }

        public static void Forget(SaveStateInfo state) => ForgetPath(state.ThumbnailPath);

        public static void ForgetPath(string path)
        {
            if (!cache.TryGetValue(path, out var entry)) return;
            cache.Remove(path);
            if (entry.Sprite != null) Object.Destroy(entry.Sprite);
            if (entry.Texture != null) Object.Destroy(entry.Texture);
        }
    }
}
