using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PalaceMod.Builder.Model;
using UnityEngine;
using Object = UnityEngine.Object;

/*
    What the sandbox panel does that the debug menu doesn't: unlimited gold, spawning enemies at a point or a spawn
    line, and teleporting the king. Everything else (nights, speed, god mode, healing, building) calls DebugActions.

    Spawning goes through the game's own Spawn (the class a wave is made of): a one-off Spawn with the unit, count and
    elite flag is driven frame by frame like a wave drives it, with the night's difficulty. So enemies get the same
    hit points, damage, perk modifiers and elite look as in a wave, custom units come from IUnitFactory templates (the
    template-aware Spawn.Update patch instantiates them), and they path to their targets as wave enemies do. A point
    spawn uses a stand-in line with no points, which Spawn reads as "spawn at the line's position".
*/
namespace PalaceMod.Builder.Studio.Play
{
    internal static class SandboxActions
    {
        // ---- Gold -------------------------------------------------------------------------------------------------

        /// <summary>Unlimited gold in Studio play: spending never lowers the balance, and it's kept at least Floor.</summary>
        public static bool InfiniteGold = true;
        public const int Floor = 999;

        private static readonly AccessTools.FieldRef<PlayerInteraction, int> BalanceRef =
            AccessTools.FieldRefAccess<PlayerInteraction, int>("balance");

        internal static bool GoldActive => InfiniteGold && StudioPlay.InPlay;

        /// <summary>Per frame in play: tops the balance up (without counting it as income: networth stays).</summary>
        internal static void KeepGold()
        {
            var player = PlayerInteraction.instance;
            if (!GoldActive || player == null) return;
            if (player.Balance < Floor) BalanceRef(player) = Floor;
        }

        /// <summary>SpendCoins postfix: give the coin back.</summary>
        internal static void Refund(PlayerInteraction player, int amount)
        {
            if (GoldActive && player != null && amount > 0) BalanceRef(player) += amount;
        }

        // ---- Spawning ---------------------------------------------------------------------------------------------

        internal sealed class UnitChoice
        {
            public string Ref, Name;
            public Sprite Icon;
            public bool Custom;
        }

        /// <summary>The enemies the panel offers: the pack's own enemy units first, then the game's.</summary>
        public static List<UnitChoice> Units(DesignPack pack)
        {
            var list = new List<UnitChoice>();
            foreach (var u in pack?.Units ?? [])
                if (u?.Id != null && u.Side == UnitSide.Enemy)
                    list.Add(new UnitChoice { Ref = u.Id, Name = u.Name ?? u.Id, Custom = true, Icon = BuilderServices.Catalog.FindUnit(ContentRef.Parse(u.Base ?? "").Id)?.Icon });
            foreach (var u in BuilderServices.Catalog?.Units ?? [])
                if (u.Side == UnitSide.Enemy)
                    list.Add(new UnitChoice { Ref = ContentRef.Vanilla(u.Id), Name = u.DisplayName ?? u.Id, Icon = u.Icon });
            return list;
        }

        /// <summary>The spawner's lines that can take the unit (ground or flying), by name.</summary>
        public static List<EnemySpawnLine> Lines() =>
            EnemySpawner.instance == null ? [] : EnemySpawner.instance.GetComponentsInChildren<EnemySpawnLine>(true)
                .Where(l => l.canSpawnSmallGround || l.canSpawnBigGround || l.canSpawnFlying)
                .GroupBy(l => l.name).Select(g => g.First()).ToList();

        /// <summary>The night in progress, or the upcoming one during the day (its difficulty scales spawns).</summary>
        private static float Difficulty()
        {
            var spawner = EnemySpawner.instance;
            if (spawner?.waves == null || spawner.waves.Count == 0) return 1f;
            bool night = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;
            int i = Mathf.Clamp(spawner.Wavenumber + (night ? 0 : 1), 0, spawner.waves.Count - 1);
            return spawner.waves[i]?.difficultyMulti ?? 1f;
        }

        /// <summary>Spawns <paramref name="count"/> of a unit at a ground point or along a spawn line (by name).</summary>
        public static string Spawn(DesignPack pack, string unitRef, int count, bool elite, Vector3? at, string lineName)
        {
            if (!DebugActions.InLevel) throw new InvalidOperationException("Only during a level");
            if (PerkManager.instance == null) throw new InvalidOperationException("Not ready yet");
            var prefab = BuilderServices.Units.Resolve(pack, unitRef, out string error) ?? throw new InvalidOperationException(error);
            count = Mathf.Clamp(count, 1, 200);
            Transform line;
            GameObject temp = null;
            if (at is Vector3 point)
            {
                temp = new GameObject("PalaceBuilder sandbox spawn");
                temp.transform.position = point;
                line = temp.transform;
            }
            else
            {
                line = Lines().FirstOrDefault(l => l.name == lineName)?.transform ?? throw new InvalidOperationException($"No spawn line \"{lineName}\"");
            }
            var spawn = new Spawn(prefab, line, count, 0.12f, 0, 0f, elite);
            spawn.Reset(_resetGold: false);
            Plugin.Instance.StartCoroutine(Drive(spawn, Difficulty(), temp));
            GameplayImpact.Mark("Builder: sandbox spawn");
            return $"spawning {count}x {unitRef}{(elite ? " (elite)" : "")} {(at != null ? $"at {at.Value}" : $"on {lineName}")}";
        }

        private static IEnumerator Drive(Spawn spawn, float difficulty, GameObject temp)
        {
            float until = Time.unscaledTime + 60f;
            try
            {
                while (!spawn.Finished && DebugActions.InLevel && Time.unscaledTime < until)
                {
                    spawn.Update(difficulty);
                    yield return null;
                }
            }
            finally
            {
                if (temp != null) Object.Destroy(temp);
            }
        }

        // ---- Teleport ---------------------------------------------------------------------------------------------

        /// <summary>Moves the king to a ground point; with followers, the units following him keep their place around
        /// him, snapped to their navmesh (as the Teleporters feature does).</summary>
        public static string Teleport(Vector3 point, bool followers)
        {
            var king = PlayerInteraction.instance != null ? PlayerInteraction.instance.GetComponent<PlayerMovement>() : null;
            if (king == null) throw new InvalidOperationException("No king");
            Vector3 from = king.transform.position;
            king.TeleportTo(point + Vector3.up * 0.1f);
            int moved = 0;
            if (followers && CommandUnits.instance != null)
            {
                foreach (var unit in CommandUnits.instance.PlayerUnitsCommanding)
                {
                    if (unit == null || !unit.FollowingPlayer || unit.hp == null || !unit.hp.Alive) continue;
                    Vector3 relative = unit.transform.position - from;
                    relative.y = 0f;
                    if (relative.magnitude > 8f) relative = relative.normalized * 8f;
                    unit.transform.position = unit.GetNearestGroundPosition(point + relative);
                    unit.ClearCurrentPath();
                    moved++;
                }
            }
            GameplayImpact.Mark("Builder: sandbox teleport");
            return $"king at {point}" + (moved > 0 ? $" with {moved} units" : "");
        }

        // ---- Pointer ----------------------------------------------------------------------------------------------

        /// <summary>Dev stand-in for the mouse in play mode.</summary>
        public static Vector3? DevCursor;

        /// <summary>The ground under the mouse in play mode: a ray on the ground layers, else the king's height.</summary>
        public static bool CursorGround(out Vector3 point)
        {
            point = default;
            if (DevCursor is Vector3 dev) { point = Level.LayoutScene.Ground(dev); return true; }
            var cam = Camera.main;
            if (cam == null) return false;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 500f, GroundMask, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                return true;
            }
            float height = PlayerInteraction.instance != null ? PlayerInteraction.instance.transform.position.y : 0f;
            if (!new Plane(Vector3.up, new Vector3(0f, height, 0f)).Raycast(ray, out float d)) return false;
            point = Level.LayoutScene.Ground(ray.GetPoint(d));
            return true;
        }

        private static int GroundMask
        {
            get
            {
                int ground = LayerMask.NameToLayer("Ground");
                return ground >= 0 ? 1 << ground : Physics.DefaultRaycastLayers;
            }
        }
    }
}
