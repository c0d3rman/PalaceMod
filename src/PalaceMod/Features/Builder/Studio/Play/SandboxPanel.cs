using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    The sandbox panel: docked on the right in Studio play (Studio.AddPanel, mode Play). Terse rows, each backed by
    SandboxActions or DebugActions. "Spawn" and "Teleport" arm a click on the map (the status is shown on the row;
    right click cancels); spawning on a spawn line happens at once.
*/
namespace PalaceMod.Builder.Studio.Play
{
    internal static class SandboxPanel
    {
        public const string Id = "sandbox";

        internal enum Armed { None, Teleport, Spawn }

        public static Armed Arm { get; private set; }

        // Spawn settings (kept for the game session, so a designer can spawn the same thing again after a restart).
        public static string UnitRef;
        public static int Count = 10;
        public static bool Elite;
        /// <summary>Null: a click on the map; else a spawn line's name.</summary>
        public static string Line;
        public static bool BringFollowers;

        private static readonly float[] Speeds = [0.5f, 1f, 2f, 4f, 8f];

        public static void Register() => Studio.AddPanel(Id, StudioMode.Play, () => StudioPlay.InPlay, Build);

        public static void Refresh() => Studio.RefreshPanels();

        public static void Disarm()
        {
            if (Arm == Armed.None) return;
            Arm = Armed.None;
            Refresh();
        }

        private static void Build(ModPage page)
        {
            if (!DebugActions.InLevel) { page.Header("Sandbox"); return; }
            var play = StudioPlay.Instance;

            page.Header("Gold");
            page.Toggle("Unlimited", () => SandboxActions.InfiniteGold, on => { SandboxActions.InfiniteGold = on; Refresh(); });
            page.Number("Gold", () => DebugActions.Gold, v =>
            {
                if (v < SandboxActions.Floor) SandboxActions.InfiniteGold = false;
                Run(() => DebugActions.SetGold(v));
            }, 0, 99999, 10);

            page.Header(DebugActions.IsNight ? $"Night {DebugActions.CurrentNight} of {DebugActions.NightCount}" : $"Day · next night {Math.Min(DebugActions.CurrentNight, DebugActions.NightCount)} of {DebugActions.NightCount}");
            if (DebugActions.IsNight) page.Button("End night", () => Run(DebugActions.EndNight));
            else
            {
                page.Button($"Start night [{PlayKeys.Label(PlayKeys.StartNight)}]", () => Run(DebugActions.StartNight));
                page.Number("Jump to night", () => Math.Min(DebugActions.CurrentNight, DebugActions.NightCount), v => Run(() => DebugActions.JumpToNight(v)), 1, Math.Max(1, DebugActions.NightCount));
            }
            page.Choice($"Speed [{PlayKeys.Label(PlayKeys.Slower)} {PlayKeys.Label(PlayKeys.Faster)}]", Speeds.Select(s => $"x{s:0.##}").ToList(),
                () => Array.FindIndex(Speeds, s => Mathf.Approximately(s, DebugActions.Speed)), i => SetSpeed(Speeds[i]));

            page.Header("King");
            page.Toggle("God mode", () => DebugActions.GodMode, on => { play.Touched(); DebugActions.SetGodMode(on); });
            page.Button("Revive", () => Run(DebugActions.ReviveKing));
            page.Button(Arm == Armed.Teleport ? "Teleport: click the map" : $"Teleport [{PlayKeys.Label(PlayKeys.Teleport)}]", () => Toggle(Armed.Teleport));
            page.Toggle("Bring followers", () => BringFollowers, on => BringFollowers = on);

            page.Header("Kingdom");
            page.Toggle("Invulnerable", () => DebugActions.InvulnerableKingdom, on => { play.Touched(); DebugActions.SetInvulnerableKingdom(on); });
            page.Button("Heal all", () => Run(DebugActions.HealAll));
            page.Button("Build all", () => Run(DebugActions.BuildAllBlueprints));
            page.Button("Max upgrade nearest", () => Run(() => DebugActions.MaxUpgrade(DebugActions.NearestBuilding() ?? throw new InvalidOperationException("No building nearby"))));

            page.Header("Enemies");
            page.Button($"Kill all [{PlayKeys.Label(PlayKeys.KillAll)}]", () => Run(DebugActions.KillEnemies));
            var units = SandboxActions.Units(play.DraftPack);
            if (UnitRef == null || units.All(u => u.Ref != UnitRef)) UnitRef = units.FirstOrDefault()?.Ref;
            var current = units.FirstOrDefault(u => u.Ref == UnitRef);
            page.Row("Unit", current?.Name ?? "-", (string)null, () => PickUnit(units), current?.Icon);
            page.Number("Count", () => Count, v => Count = v, 1, 50);
            page.Toggle("Elite", () => Elite, on => Elite = on);
            var lines = SandboxActions.Lines().Select(l => l.name).ToList();
            var at = new List<string> { "Click on the map" };
            at.AddRange(lines);
            page.Choice("At", at, () => Line == null ? 0 : Math.Max(0, lines.IndexOf(Line) + 1), i => Line = i == 0 ? null : lines[i - 1]);
            page.Button(Arm == Armed.Spawn ? "Spawn: click the map" : "Spawn", () =>
            {
                if (Line == null) Toggle(Armed.Spawn);
                else Run(() => SpawnNow(null));
            });

            page.Header("Play");
            page.Button($"Restart [{PlayKeys.Label(PlayKeys.Restart)}]", play.Restart);
            page.Button($"Back to the Studio [{PlayKeys.Label(PlayKeys.Back)}]", play.ReturnToEdit);
        }

        internal static void SetSpeed(float speed)
        {
            StudioPlay.Instance.Touched();
            Run(() => DebugActions.SetSpeed(speed));
        }

        internal static void StepSpeed(int delta)
        {
            int i = Array.FindIndex(Speeds, s => s >= DebugActions.Speed - 0.01f);
            if (i < 0) i = Speeds.Length - 1;
            SetSpeed(Speeds[Mathf.Clamp(i + delta, 0, Speeds.Length - 1)]);
        }

        internal static void Toggle(Armed what)
        {
            Arm = Arm == what ? Armed.None : what;
            Refresh();
        }

        internal static string SpawnNow(Vector3? at) =>
            SandboxActions.Spawn(StudioPlay.Instance.DraftPack, UnitRef ?? throw new InvalidOperationException("No unit picked"), Count, Elite, at, Line);

        /// <summary>A click on the map while armed (from StudioPlayManager).</summary>
        internal static void Clicked(Vector3 ground)
        {
            var what = Arm;
            if (what == Armed.Teleport) Run(() => SandboxActions.Teleport(ground, BringFollowers));
            else if (what == Armed.Spawn) Run(() => SpawnNow(ground));
            // Shift keeps it armed (spawn a few groups, hop around).
            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) Disarm();
        }

        private static void PickUnit(List<SandboxActions.UnitChoice> units)
        {
            ModPopup.Open("Unit", popup =>
            {
                foreach (var u in units)
                {
                    var choice = u;
                    popup.Row(choice.Name, choice.Custom ? "pack" : "", (string)null, () =>
                    {
                        UnitRef = choice.Ref;
                        ModPopup.Current?.Close();
                        Refresh();
                    }, choice.Icon);
                }
            }, width: 420f);
        }

        /// <summary>Runs an action and shows its result (or why it couldn't), then rebuilds the panel.</summary>
        internal static void Run(Func<string> action)
        {
            try
            {
                string result = action();
                Log.For<BuilderManager>().Debug($"Sandbox: {result}");
            }
            catch (InvalidOperationException e)
            {
                Notify.Show(e.Message, NotifyKind.Warning, 3f, "builder-sandbox");
            }
            catch (Exception e)
            {
                Log.For<BuilderManager>().Error("Sandbox action failed", e);
            }
            Refresh();
        }
    }
}
