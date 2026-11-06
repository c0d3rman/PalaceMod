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
    /// <summary>A plan, or a branch of one: a branch shares its parent's steps before <see cref="ForkDay"/> (live, so
    /// editing them changes both) and has its own from then on.</summary>
    public sealed class PlannerScenario
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name;
        public List<EconAction> Actions = [];
        /// <summary>The plan this branches from, or null.</summary>
        public string Parent;
        public int ForkDay;
        /// <summary>Perks tried in this plan (key -> equipped), on top of the parent's; empty: the run's loadout.</summary>
        public Dictionary<string, bool> Perks = [];

        public PlannerScenario CopyAs(string name) => new()
        {
            Name = name, Actions = Actions.Select(a => a.Copy()).ToList(), Parent = Parent, ForkDay = ForkDay,
            Perks = new Dictionary<string, bool>(Perks ?? []),
        };
    }

    public sealed class PlannerFile
    {
        public int Version = 1;
        public string Level;
        public List<PlannerScenario> Scenarios = [];
        /// <summary>The plan shown, and the one it's compared with (null: doing nothing).</summary>
        public string Current, Compare;
        /// <summary>Just made (no file yet): the planner seeds its first plan.</summary>
        [JsonIgnore] public bool Fresh;

        [JsonIgnore] public PlannerScenario CurrentScenario => Scenarios.FirstOrDefault(s => s.Id == Current) ?? Scenarios.FirstOrDefault();
        [JsonIgnore] public PlannerScenario CompareScenario => Compare == null ? null : Scenarios.FirstOrDefault(s => s.Id == Compare && s.Id != CurrentScenario?.Id);

        public PlannerScenario Get(string id) => id == null ? null : Scenarios.FirstOrDefault(s => s.Id == id);

        /// <summary>The scenario's parent chain, nearest first (cycles cut).</summary>
        public List<PlannerScenario> Ancestors(PlannerScenario s)
        {
            var list = new List<PlannerScenario>();
            for (var p = Get(s?.Parent); p != null && !list.Contains(p) && p != s; p = Get(p.Parent)) list.Add(p);
            return list;
        }

        /// <summary>Every step the scenario runs: its parents' before its fork day, then its own.</summary>
        public List<EconAction> Effective(PlannerScenario s)
        {
            if (s == null) return [];
            var chain = Ancestors(s);
            var result = new List<EconAction>();
            // From the root down: each level keeps what's before the next branch's fork day.
            int cut = int.MaxValue;
            var levels = new List<(PlannerScenario plan, int before)>();
            var child = s;
            foreach (var p in chain)
            {
                cut = Math.Min(cut, child.ForkDay);
                levels.Add((p, cut));
                child = p;
            }
            for (int i = levels.Count - 1; i >= 0; i--)
                result.AddRange(levels[i].plan.Actions.Where(a => a.Day < levels[i].before));
            result.AddRange(s.Actions);
            return result;
        }

        /// <summary>The plan an action belongs to (for editing a branch's inherited steps).</summary>
        public PlannerScenario OwnerOf(EconAction a) => Scenarios.FirstOrDefault(s => s.Actions.Contains(a));

        /// <summary>The perks the scenario tries: its parents', then its own on top.</summary>
        public Dictionary<string, bool> EffectivePerks(PlannerScenario s)
        {
            var result = new Dictionary<string, bool>();
            if (s == null) return result;
            var chain = Ancestors(s);
            for (int i = chain.Count - 1; i >= 0; i--)
                foreach (var kv in chain[i].Perks ?? []) result[kv.Key] = kv.Value;
            foreach (var kv in s.Perks ?? []) result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>"Plan 1 › mill" for a branch.</summary>
        public string FullName(PlannerScenario s)
        {
            if (s == null) return "";
            var chain = Ancestors(s);
            return chain.Count == 0 ? s.Name : string.Join(" › ", chain.AsEnumerable().Reverse().Select(p => p.Name).Append(s.Name));
        }

        /// <summary>Deletes a plan; its branches keep what they had from it (copied in) and hang off its parent.</summary>
        public void Delete(PlannerScenario s)
        {
            foreach (var child in Scenarios.Where(c => c.Parent == s.Id).ToList())
            {
                var inherited = s.Actions.Where(a => a.Day < child.ForkDay).Select(a => a.Copy()).ToList();
                child.Actions.InsertRange(0, inherited);
                foreach (var kv in s.Perks ?? []) if (!child.Perks.ContainsKey(kv.Key)) child.Perks[kv.Key] = kv.Value;
                child.Parent = s.Parent;
                child.ForkDay = s.Parent == null ? 0 : Math.Min(child.ForkDay, s.ForkDay);
            }
            Scenarios.Remove(s);
            if (Compare == s.Id) Compare = null;
            if (Current == s.Id) Current = null;
            Normalize();
        }

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
                s.Actions.RemoveAll(a => a == null || (a.Kind != EconActionKind.Gold && string.IsNullOrEmpty(a.Slot)));
                s.Name = string.IsNullOrWhiteSpace(s.Name) ? FreeName("Plan") : s.Name;
                if (string.IsNullOrEmpty(s.Id)) s.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                s.Perks ??= [];
            }
            foreach (var s in Scenarios)
                if (s.Parent != null && (Get(s.Parent) == null || Ancestors(s).Contains(s) || s.Parent == s.Id)) s.Parent = null;
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
            if (file == null) file = new PlannerFile { Fresh = true };
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
