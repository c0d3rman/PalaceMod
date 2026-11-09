using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PalaceMod.Builder.Model;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    The Enemy Studio's live test: N copies of the enemy against a few defenders, on a platform of its own outside the
    level (ArenaZone: its own pathfinding graph, and target searches that never cross its edge), in the Studio's edit
    scene (no reload, no night). The turntable is hidden while it runs and comes back when it's cleared.

    The game's attackers only act at night (AutoAttack.Update and its overrides return in the day), so while a fight
    runs, an attack script on a unit marked BuilderArenaUnit runs as if it were night: EnemyStudioManager's patch flips
    DayNightCycle's state around that one Update call and back. Nothing else in the scene wakes up. Units are made like
    the game's Spawn makes them (difficulty on hit points and damage, the elite material), with no coins.

    The fight is measured by polling hit points every frame: damage each side took, the first and last kill, and who
    is left. It ends when a side is down or after 90 s; Clear destroys every arena unit (and anything an arena enemy
    spawned on death).
*/
namespace PalaceMod.Builder.Studio.Enemies
{
    /// <summary>Marks a unit of the Enemy Studio's arena: its attacks run in the day.</summary>
    public sealed class BuilderArenaUnit : MonoBehaviour
    {
        public bool enemy;
    }

    internal static class EnemyArena
    {
        public const float TimeLimit = 90f, Distance = 16f;

        public sealed class Defender
        {
            public string Id, Name;
            public int Count;
        }

        public static int Count = 5;
        public static bool Elite;
        public static float Difficulty = 1f;
        /// <summary>Defender picks (catalog player unit ids) and counts.</summary>
        public static readonly List<Defender> Defenders = [];

        private sealed class Tracked
        {
            public Hp Hp;
            public bool Enemy;
            public float LastHp;
            public bool Down, Started;
        }

        private static readonly List<Tracked> tracked = [];
        private static HashSet<Hp> before = [];
        public static bool Running { get; private set; }
        public static bool HasResult { get; private set; }
        /// <summary>Bumped when a fight starts, ends or is cleared (the readout redraws).</summary>
        public static int Version { get; private set; }
        public static float Elapsed { get; private set; }
        public static float DamageToDefenders, DamageToEnemies;
        public static float? FirstKill, LastKill;
        public static string Result = "";
        private static Vector3 center;

        public static int EnemiesAlive => tracked.Count(t => t.Enemy && !t.Down);
        public static int EnemiesTotal => tracked.Count(t => t.Enemy);
        public static int DefendersStanding => tracked.Count(t => !t.Enemy && !t.Down);
        public static int DefendersTotal => tracked.Count(t => !t.Enemy);

        /// <summary>Whether attack scripts on this object should act now (the patch asks).</summary>
        public static bool Wakes(Component c) => Running && c != null && c.GetComponentInParent<BuilderArenaUnit>() != null;

        /// <summary>Defaults: three of the first melee troop and three of the first ranged one.</summary>
        public static void DefaultDefenders()
        {
            if (Defenders.Count > 0) return;
            var units = BuilderServices.Catalog?.Units?.Where(u => u.Side == UnitSide.Player && u.Prefab != null).ToList() ?? [];
            var melee = units.FirstOrDefault(u => u.Id == "P Melee") ?? units.FirstOrDefault(u => u.Tags.Contains("MeeleFighter"));
            var ranged = units.FirstOrDefault(u => u.Id == "P Archer") ?? units.FirstOrDefault(u => u.Tags.Contains("RangedFighter"));
            foreach (var u in new[] { melee, ranged }.Where(u => u != null).Distinct())
                Defenders.Add(new Defender { Id = u.Id, Name = u.DisplayName ?? u.Id, Count = 3 });
        }

        /// <summary>Spawns the fight around the stage. Throws with a reason if it can't.</summary>
        public static string Start(DesignPack pack, string unitRef)
        {
            Clear();
            if (!EnemyStage.Ready) throw new InvalidOperationException("no stage");
            var template = BuilderServices.Units.Resolve(pack, unitRef, out string error) ?? throw new InvalidOperationException(error ?? "unit not found");
            before = new HashSet<Hp>(Object.FindObjectsOfType<Hp>());
            // Its own ground, outside the level (ArenaZone): no level geometry, no king, no buildings.
            ArenaZone.Create(StudioScene.Scene, EnemyStage.Point.y);
            center = ArenaZone.Center;
            EnemyStage.SetVisible(false);
            DamageToDefenders = DamageToEnemies = 0f;
            FirstKill = LastKill = null;
            Elapsed = 0f;
            Result = "";
            HasResult = false;

            // Defenders in an arc on one side of the platform; enemies come from 16 m away on the other.
            var away = Vector3.right;
            int total = Defenders.Sum(d => Mathf.Clamp(d.Count, 0, 20));
            int k = 0;
            foreach (var d in Defenders)
            {
                var unit = BuilderServices.Catalog?.FindUnit(d.Id);
                if (unit?.Prefab == null) continue;
                for (int i = 0; i < Mathf.Clamp(d.Count, 0, 20); i++, k++)
                {
                    float a = total <= 1 ? 0f : (k / (float)total - 0.5f) * 140f;
                    var pos = ArenaZone.At(-away * (Distance * 0.5f) + Quaternion.Euler(0f, a, 0f) * away * 3f);
                    var go = Object.Instantiate(unit.Prefab, pos, Quaternion.LookRotation(away));
                    go.name = "PalaceBuilder arena " + d.Id;
                    go.AddComponent<BuilderArenaUnit>();
                    ArenaZone.Bind(go);
                    if (!go.activeSelf) go.SetActive(true);
                    Track(go, enemy: false);
                }
            }
            var spawnAt = -away * (Distance * 0.5f) + away * Distance;
            var tagged = template.GetComponentInChildren<TaggedObject>(true);
            bool flying = tagged != null && tagged.Tags.Contains(TagManager.ETag.Flying);
            for (int i = 0; i < Mathf.Clamp(Count, 1, 50); i++)
            {
                var jitter = away * (i / 7 * 1.5f) + Vector3.Cross(Vector3.up, away) * ((i % 7) - 3) * 1.2f;
                var pos = ArenaZone.At(spawnAt + jitter);
                if (flying) pos += Vector3.up * 5f;
                var go = Object.Instantiate(template, pos, Quaternion.LookRotation(-away));
                go.name = "PalaceBuilder arena enemy";
                go.AddComponent<BuilderArenaUnit>().enemy = true;
                ArenaZone.Bind(go);
                if (!go.activeSelf) go.SetActive(true);
                Prepare(go);
                Track(go, enemy: true);
            }
            Running = true;
            Version++;
            EnemyStage.FrameArea(center, Distance * 0.9f);
            GameplayImpact.Mark("Builder: enemy studio arena");
            return $"fight: {EnemiesTotal}x {unitRef} vs {DefendersTotal} defenders";
        }

        /// <summary>The night's scaling and the elite look, as the game's Spawn does it; no coins.</summary>
        private static void Prepare(GameObject go)
        {
            var hp = go.GetComponentInChildren<Hp>();
            if (hp == null) return;
            hp.coinCount = 0;
            float hpMult = Difficulty, dmgMult = Mathf.Lerp(1f, Difficulty, 0.5f);
            if (Elite)
            {
                hpMult *= 4f;
                dmgMult *= 3f;
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                    if (r.sharedMaterial != null && r.sharedMaterial.name == "Enemy" && ColorAndLightManager.EliteEnemyMaterial != null) r.sharedMaterial = ColorAndLightManager.EliteEnemyMaterial;
                hp.Elite = true;
            }
            hp.HpOriginal = hp.maxHp;
            if (hpMult != 1f) { hp.maxHp *= hpMult; hp.SetHpToMaxHp(); }
            if (dmgMult != 1f) foreach (var a in go.GetComponentsInChildren<AutoAttack>()) a.DamageMultiplyer *= dmgMult;
        }

        private static void Track(GameObject go, bool enemy)
        {
            var hp = go.GetComponentInChildren<Hp>();
            if (hp == null) return;
            tracked.Add(new Tracked { Hp = hp, Enemy = enemy, LastHp = Mathf.Max(hp.HpValue, hp.maxHp) });
        }

        /// <summary>Per frame while the Enemy Studio is up.</summary>
        public static void Update(float dt)
        {
            if (!Running) return;
            Elapsed += dt;
            // Enemies an arena enemy spawned on death join the fight.
            foreach (var hp in Object.FindObjectsOfType<Hp>())
            {
                if (before.Contains(hp) || hp.GetComponentInParent<BuilderArenaUnit>() != null) continue;
                var t = hp.TaggedObj ?? hp.GetComponent<TaggedObject>();
                if (t == null || !t.Tags.Contains(TagManager.ETag.EnemyOwned)) continue;
                if ((hp.transform.position - center).sqrMagnitude > 60f * 60f) continue;
                hp.gameObject.AddComponent<BuilderArenaUnit>().enemy = true;
                ArenaZone.Bind(hp.gameObject); // too late for its Start if it already ran; it was spawned on the arena anyway
                hp.coinCount = 0;
                tracked.Add(new Tracked { Hp = hp, Enemy = true, LastHp = hp.HpValue });
            }
            foreach (var t in tracked)
            {
                if (t.Down) continue;
                // Hit points are 0 until the unit's Start has run: count it from the first frame it's alive.
                if (!t.Started)
                {
                    if (t.Hp == null) { t.Down = true; continue; }
                    if (t.Hp.HpValue <= 0f) continue;
                    t.Started = true;
                    t.LastHp = t.Hp.HpValue;
                }
                float now = t.Hp == null ? 0f : Mathf.Max(0f, t.Hp.HpValue);
                float lost = t.LastHp - now;
                if (lost > 0f) { if (t.Enemy) DamageToEnemies += lost; else DamageToDefenders += lost; }
                t.LastHp = now;
                if (t.Hp == null || t.Hp.KnockedOut || !t.Hp.Alive)
                {
                    t.Down = true;
                    FirstKill ??= Elapsed;
                    LastKill = Elapsed;
                }
            }
            if (EnemiesAlive == 0 || DefendersStanding == 0 || Elapsed >= TimeLimit) Finish();
        }

        private static void Finish()
        {
            Running = false;
            HasResult = true;
            Version++;
            Result = EnemiesAlive == 0 && DefendersStanding == 0 ? "Both sides down"
                : EnemiesAlive == 0 ? $"Defenders won ({DefendersStanding}/{DefendersTotal} standing)"
                : DefendersStanding == 0 ? $"Enemies won ({EnemiesAlive}/{EnemiesTotal} left)"
                : $"Time up ({EnemiesAlive}/{EnemiesTotal} enemies, {DefendersStanding}/{DefendersTotal} defenders)";
            Log.For<BuilderManager>().Info($"Enemy Studio arena: {Result} in {Elapsed:0.0} s; damage to defenders {DamageToDefenders:0}, to enemies {DamageToEnemies:0}");
        }

        /// <summary>Destroys every arena unit and forgets the result.</summary>
        public static void Clear()
        {
            Running = false;
            foreach (var marker in Object.FindObjectsOfType<BuilderArenaUnit>(true))
                if (marker != null) Object.Destroy(marker.gameObject);
            bool hadZone = ArenaZone.Active;
            ArenaZone.Remove();
            if (hadZone)
            {
                // Back to the turntable.
                EnemyStage.SetVisible(true);
                EnemyStage.ResetView();
            }
            tracked.Clear();
            Version++;
            HasResult = false;
            Result = "";
        }

        public static string Describe() =>
            !Running && !HasResult ? "no fight"
            : $"{(Running ? "running" : Result)}; {Elapsed:0.0} s; enemies {EnemiesAlive}/{EnemiesTotal}; defenders {DefendersStanding}/{DefendersTotal}; " +
              $"damage to defenders {DamageToDefenders:0}, to enemies {DamageToEnemies:0}; first kill {(FirstKill is float f ? f.ToString("0.0") + " s" : "-")}";

        // ---- The day check (patched in EnemyStudioManager) ----------------------------------------------------------

        private static readonly AccessTools.FieldRef<DayNightCycle, DayNightCycle.Timestate> State =
            AccessTools.FieldRefAccess<DayNightCycle, DayNightCycle.Timestate>("currentTimestate");

        /// <summary>Before an arena unit's attack Update: pretend it's night. Returns whether to undo it after.</summary>
        internal static bool PretendNight(Component attack)
        {
            var cycle = DayNightCycle.Instance;
            if (cycle == null || cycle.CurrentTimestate != DayNightCycle.Timestate.Day || !Wakes(attack)) return false;
            State(cycle) = DayNightCycle.Timestate.Night;
            return true;
        }

        internal static void BackToDay()
        {
            var cycle = DayNightCycle.Instance;
            if (cycle != null) State(cycle) = DayNightCycle.Timestate.Day;
        }
    }
}
