using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

/*
    The economy planner's saved plans: one JSON file per level, BepInEx/PalaceMod/EconomyPlanner/<level>.json, never
    the game's save. A plan's steps name slots by their stable id (EconSlot.Id) and choices by key, so a plan made in
    one run works in the next run of the same map.
*/
namespace PalaceMod.Economy
{
    public sealed class PlannerScenario
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name;
        public List<EconAction> Actions = [];

        public PlannerScenario CopyAs(string name) => new() { Name = name, Actions = Actions.Select(a => a.Copy()).ToList() };
    }

    public sealed class PlannerFile
    {
        public int Version = 1;
        public string Level;
        public List<PlannerScenario> Scenarios = [];
        /// <summary>The plan shown, and the one it's compared with (null: doing nothing).</summary>
        public string Current, Compare;

        [JsonIgnore] public PlannerScenario CurrentScenario => Scenarios.FirstOrDefault(s => s.Id == Current) ?? Scenarios.FirstOrDefault();
        [JsonIgnore] public PlannerScenario CompareScenario => Compare == null ? null : Scenarios.FirstOrDefault(s => s.Id == Compare && s.Id != CurrentScenario?.Id);

        /// <summary>A plan name not taken yet, from <paramref name="stem"/>.</summary>
        public string FreeName(string stem)
        {
            if (Scenarios.All(s => s.Name != stem)) return stem;
            for (int i = 2; ; i++)
                if (Scenarios.All(s => s.Name != $"{stem} {i}")) return $"{stem} {i}";
        }

        /// <summary>Makes sure there's at least one plan and the references point at real ones.</summary>
        public void Normalize()
        {
            Scenarios ??= [];
            Scenarios.RemoveAll(s => s == null);
            foreach (var s in Scenarios)
            {
                s.Actions ??= [];
                s.Actions.RemoveAll(a => a == null || string.IsNullOrEmpty(a.Slot));
                s.Name = string.IsNullOrWhiteSpace(s.Name) ? FreeName("Plan") : s.Name;
                if (string.IsNullOrEmpty(s.Id)) s.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
            if (Scenarios.Count == 0) Scenarios.Add(new PlannerScenario { Name = "Plan 1" });
            if (Scenarios.All(s => s.Id != Current)) Current = Scenarios[0].Id;
            if (Compare != null && (Compare == Current || Scenarios.All(s => s.Id != Compare))) Compare = null;
        }
    }

    public static class PlannerStore
    {
        public static string FileFor(string folder, string level)
        {
            var name = new string((level ?? "level").Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c).ToArray());
            return Path.Combine(folder, name + ".json");
        }

        /// <summary>The level's plans; a fresh file (one empty plan) if there are none or the file is unreadable.
        /// <paramref name="error"/> says why it couldn't be read.</summary>
        public static PlannerFile Load(string folder, string level, out string error)
        {
            error = null;
            PlannerFile file = null;
            string path = FileFor(folder, level);
            try
            {
                if (File.Exists(path)) file = JsonConvert.DeserializeObject<PlannerFile>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                error = e.Message;
                // Keep the unreadable file for the player rather than overwriting it.
                try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception) { }
                file = null;
            }
            file ??= new PlannerFile();
            file.Level = level;
            file.Normalize();
            return file;
        }

        public static void Save(string folder, PlannerFile file)
        {
            Directory.CreateDirectory(folder);
            string path = FileFor(folder, file.Level);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(file, Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
