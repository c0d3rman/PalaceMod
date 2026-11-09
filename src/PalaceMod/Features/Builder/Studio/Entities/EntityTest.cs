using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Logic;
using PalaceMod.Builder.Logic.Bodies;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Studio.Enemies;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    The Entity Studio's test (docs/builder-ux.md, "Entity Studio"): the thing runs through the real engine in a
    throwaway behaviour world (LogicManager.StartStudioWorld, the arena fight's kind of world, built from the draft
    plus a test dummy and an invisible launcher), on the stage, in the edit scene.

    - A projectile is launched (Launcher.Launch, as an attack's `launch` does) from the turntable at a dummy Distance
      metres away, which strolls back and forth across the line of fire when "Target moves" is on, so homing, arc,
      spin and speed show; its impact rules run (the dummy has 1000 hit points and shows the damage taken). The
      flight's path is drawn as it goes and stays until the next test.
    - Anything else is spawned on the turntable for a few seconds with the dummy strolling past it, so its spawn,
      area and lifetime rules run.

    It ends 1.5 s after the projectile is gone (or after 15 s); then the world goes and everything it made is removed.
*/
namespace PalaceMod.Builder.Studio.Entities
{
    internal static class EntityTest
    {
        public const string DummyId = "palacebuilder-test-dummy", LauncherId = "palacebuilder-test-launcher";
        public const float TimeLimit = 15f, DummyHp = 1000f;

        public static float Distance = 14f, Height = 1.5f;
        public static bool Moving = true;

        public static bool Running { get; private set; }
        public static bool HasResult { get; private set; }
        /// <summary>Bumped when a test starts, ends or is cleared (the readout redraws).</summary>
        public static int Version { get; private set; }
        public static float Elapsed { get; private set; }
        public static float? FlightTime;
        public static float Flown, DamageToDummy;
        public static readonly List<string> Impacts = [], Fired = [];
        public static string Result = "", Problem;

        private static LogicWorld world;
        private static LogicEntity dummy, launcher, thing;
        private static GameObject trail;
        private static LineRenderer trailLine;
        private static readonly List<Vector3> path = [];
        private static float goneAt = -1f;
        private static Vector3 lastPos;
        private static int traceSeen;

        // ---- Start --------------------------------------------------------------------------------------------------

        /// <summary>Starts the test for the thing being edited. Throws with a reason if it can't.</summary>
        public static string Start()
        {
            Clear();
            var e = EntityStudio.Entity ?? throw new InvalidOperationException("no thing to test");
            var draft = EntityStudio.Draft ?? throw new InvalidOperationException("no draft");
            if (!EnemyStage.Ready) throw new InvalidOperationException("no stage");
            if (EnemyArena.Running) EnemyArena.Clear();
            bool projectile = EntityStudio.IsProjectile;

            // Where: from the turntable, across the view (a little away from the camera) at the dummy.
            var stage = EnemyStage.Point;
            var right = Quaternion.Euler(0f, EnemyStage.Yaw + 35f, 0f) * Vector3.right;
            var side = Vector3.Cross(Vector3.up, right).normalized;
            var from = stage + Vector3.up * Mathf.Max(0.2f, Height);
            var target = EnemyStage.Snap(projectile ? stage + right * Mathf.Clamp(Distance, 2f, 60f) : stage + side * 3f);
            float stroll = projectile ? 3.5f : 6f;
            var strollDir = projectile ? side : right;

            // The thing's team decides the dummy's: a foe of whatever it is.
            string team = e.Team is "player" or "enemy" or "neutral" ? e.Team : "enemy";
            string dummyTeam = team == "player" ? "enemy" : "player";
            var dummyDef = new JObject
            {
                ["id"] = DummyId,
                ["kind"] = "prop",
                ["name"] = "Test dummy",
                ["visual"] = new JObject { ["mesh"] = ModelRef.Unit(dummyTeam == "player" ? "P Melee" : "E Racer") },
                ["body"] = new JObject { ["radius"] = 0.6, ["height"] = 2, ["hittable"] = true, ["hp"] = DummyHp },
            };
            if (Moving)
            {
                var a = EnemyStage.Snap(target - strollDir * stroll);
                var b = EnemyStage.Snap(target + strollDir * stroll);
                dummyDef["mover"] = new JObject
                {
                    ["path"] = new JArray(new JArray(a.x, a.y, a.z), new JArray(b.x, b.y, b.z)),
                    ["mode"] = "pingpong",
                    ["speed"] = projectile ? 2.5 : 3,
                };
            }
            var launcherDef = new JObject { ["id"] = LauncherId, ["kind"] = "logic", ["name"] = "Launcher" };

            Running = true;
            if (!LogicManager.StartStudioWorld(draft.Pack, [(DummyId, dummyDef), (LauncherId, launcherDef)], () => Running, out string error))
            {
                Running = false;
                throw new InvalidOperationException(error);
            }
            world = LogicWorld.Current;
            Elapsed = 0f;
            FlightTime = null;
            Flown = DamageToDummy = 0f;
            Impacts.Clear();
            Fired.Clear();
            Result = "";
            Problem = null;
            HasResult = false;
            goneAt = -1f;
            traceSeen = -1;
            try
            {
                launcher = world.Spawn(LauncherId, from.ToVec(), 0f, team, null);
                dummy = world.Spawn(DummyId, (Moving ? EnemyStage.Snap(target - strollDir * stroll) : target).ToVec(), 0f, dummyTeam, null);
                if (projectile)
                {
                    var (launched, why) = Launcher.Launch(new LaunchRequest { Template = e.Id, Owner = launcher, From = from, Target = dummy });
                    thing = launched ?? throw new InvalidOperationException(why ?? "it wasn't launched");
                }
                else thing = world.Spawn(e.Id, stage.ToVec(), EnemyStage.Spin, null, launcher);
            }
            catch (Exception ex) when (ex is LogicError || ex is InvalidOperationException)
            {
                Clear();
                throw new InvalidOperationException(ex.Message);
            }
            if (EnemyStage.Model != null) EnemyStage.Model.SetActive(false);
            lastPos = thing?.Go() != null ? thing.Go().transform.position : from;
            path.Clear();
            path.Add(lastPos);
            MakeTrail();
            EnemyStage.FrameArea((from + target) / 2f, Mathf.Max(6f, Vector3.Distance(from, target) * 0.75f + 2f));
            Version++;
            return $"{(projectile ? "launched" : "spawned")} {e.Id} ({thing?.Id}) at a {dummyTeam} dummy {Vector3.Distance(from, target):0.#} m away{(Moving ? ", moving" : "")}";
        }

        private static void MakeTrail()
        {
            trail = new GameObject("PalaceBuilder entity test trail");
            if (StudioScene.Scene.IsValid()) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(trail, StudioScene.Scene);
            trailLine = EntityLook.Line(trail, "Path", new Color(1f, 0.85f, 0.4f, 0.9f), path.ToArray(), false, 0.06f);
            trailLine.useWorldSpace = true;
        }

        // ---- Per frame ----------------------------------------------------------------------------------------------

        public static void Update()
        {
            if (!Running) return;
            if (LogicWorld.Current != world || world == null) { Finish("the test world ended"); return; }
            Elapsed += Time.deltaTime;
            var go = thing?.Go();
            bool gone = thing == null || thing.Destroyed || go == null || go.GetComponentsInChildren<Renderer>().All(r => !r.enabled);
            if (!gone)
            {
                var p = go.transform.position;
                if ((p - lastPos).sqrMagnitude > 0.04f)
                {
                    Flown += Vector3.Distance(p, lastPos);
                    lastPos = p;
                    path.Add(p);
                    if (trailLine != null) { trailLine.positionCount = path.Count; trailLine.SetPositions(path.ToArray()); }
                }
            }
            else if (goneAt < 0f) { goneAt = Elapsed; FlightTime = Elapsed; }
            if (thing != null)
                foreach (var s in BuilderProjectile.LastImpacts)
                    if (s.StartsWith(thing.Id + "->") && !Impacts.Contains(s)) Impacts.Add(s);
            // The trace is a ring of the last 100: take what's newer than the last frame read (the world ticks in
            // LateUpdate, so earlier frames are complete by now).
            int newest = traceSeen;
            foreach (var t in world.Trace)
            {
                if (t.Frame <= traceSeen) continue;
                newest = Math.Max(newest, t.Frame);
                if (t.Owner == null || t.Owner == "level") continue;
                Fired.Add($"{t.Time:0.0} s  {t.Owner}: {t.Event}{(t.Outcome != TraceOutcome.Fired ? $" ({t.Outcome}{(t.Message != null ? ": " + t.Message : "")})" : "")}");
                if (Fired.Count > 30) Fired.RemoveAt(0);
            }
            traceSeen = newest;
            var hp = dummy?.Go()?.GetComponent<Hp>();
            if (hp != null) DamageToDummy = Mathf.Max(0f, DummyHp - hp.HpValue);
            if (EntityStudio.IsProjectile ? goneAt >= 0f && Elapsed - goneAt > 1.5f : Elapsed > 6f) Finish(null);
            else if (Elapsed > TimeLimit) Finish("time limit");
        }

        private static void Finish(string why)
        {
            bool projectile = EntityStudio.IsProjectile;
            Result = projectile
                ? $"{(Impacts.Count > 0 ? $"Hit {Impacts.Count}×" : "No hit")} · flew {Flown:0.#} m in {(FlightTime ?? Elapsed):0.0} s · dummy took {DamageToDummy:0.#}"
                : $"Ran {Elapsed:0.0} s · {Fired.Count} rule firings · dummy took {DamageToDummy:0.#}";
            if (why != null && why != "time limit") Problem = why;
            if (why == "time limit") Result += $" · stopped at {TimeLimit:0} s";
            Log.For<BuilderManager>().Info($"Entity Studio test: {Result}; impacts [{string.Join(", ", Impacts)}]; rules [{string.Join(" | ", Fired.Take(8))}]");
            RemoveSpawned();
            Running = false;
            HasResult = true;
            Version++;
        }

        private static void RemoveSpawned()
        {
            if (world != null)
                foreach (var e in world.Entities.ToList())
                {
                    if (!e.Spawned) continue;
                    var go = e.Go();
                    if (go != null) Object.Destroy(go);
                }
            world = null;
            dummy = launcher = thing = null;
        }

        /// <summary>Stops a test and removes everything it made, the path too; the turntable model comes back.</summary>
        public static void Clear()
        {
            bool had = Running || HasResult || trail != null;
            if (Running) RemoveSpawned();
            Running = false;
            HasResult = false;
            world = null;
            if (trail != null) Object.Destroy(trail);
            trail = null;
            trailLine = null;
            path.Clear();
            if (EnemyStage.Model != null) EnemyStage.Model.SetActive(true);
            if (had) { if (EnemyStage.Ready) EnemyStage.FrameModel(); Version++; }
        }

        public static string Describe() =>
            Running ? $"running {Elapsed:0.0} s, flown {Flown:0.#} m, impacts {Impacts.Count}, dummy damage {DamageToDummy:0.#}, rules fired {Fired.Count}"
            : HasResult ? $"done: {Result}{(Problem != null ? " (" + Problem + ")" : "")}; impacts [{string.Join(", ", Impacts)}]; rules [{string.Join(" | ", Fired.Take(6))}]"
            : "idle";
    }
}
