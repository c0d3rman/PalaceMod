using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

/*
    Undo upgrade: take back a misclicked build, upgrade or branch choice during the same day.

    How it works. When the player pays for a build or upgrade (BuildSlot.TryToBuildOrUpgradeAndPay with a player),
    the action's scope is captured: the building and every building upgraded with it (ActivatorUpgradesThis
    children), the objects the next upgrade can switch on or off, the building's units, and the slots it can unlock.
    For each GameObject in scope we keep its active flag and placement; for each game component, every field
    (ObjectState). The upgrade itself runs in one call (or two when a branch choice is shown: the children are
    upgraded at payment and the building itself when the choice completes), and each call is wrapped in a "window":
    state just before vs. right after, in the same frame. Those diffs are exactly what the upgrade changed on things
    that already existed, including OnEnable/listener side effects (TowerUpgrade's tower stats, UpgradeUnitsOnEnable's
    unit stats, CaslteUpReciever's discount, EliteTowers' damage). Newly switched-on objects aren't diffed (their
    Awake/OnEnable initialise themselves); undo switches them back off and restores their placement, so switching
    them on again replays the same first-activation path.

    An action is only kept as undoable when that is exact. It isn't when a newly activated component changes state
    outside the scope (Upgrade*, temple, blacksmith, royal forge, mill improvements, summoning circle, outpost...),
    when another building changes during it, when something that existed before is destroyed, or when the building
    was knocked out. Such an action is recorded as a barrier: everything before it is no longer undoable, and
    pressing undo says why. Components that destroy themselves after applying an effect (TowerUpgrade) are recreated
    from their captured state on undo, so re-upgrading applies them again. Destroyed objects inside newly activated
    parts are fine: they destroyed themselves on first activation, as they would again.

    Undo is LIFO and limited to the current day (the stack is cleared when night starts or the level changes). It
    refunds exactly the coins paid (counted from the coin slots), without counting them as new income (networth).
*/
namespace PalaceMod
{
    public class UndoUpgradeManager : BaseManager
    {
        public override string FeatureSection => "Undo upgrade";
        public override string FeatureDescription => "Undo a misclicked build, upgrade, branch choice (castle upgrades included) or teleporter during the same day: press the undo key near it to get the gold back.";

        private const float NearDistance = 14f;
        private const int SettleFrames = 6;

        private Keybind undoKey;
        private ConfigEntry<bool> hintShown;

        // ---- Reflection ---------------------------------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<BuildSlot, int> slotLevel = AccessTools.FieldRefAccess<BuildSlot, int>("level");
        private static readonly AccessTools.FieldRef<BuildSlot, List<int>> slotApplied = AccessTools.FieldRefAccess<BuildSlot, List<int>>("appliedUpgrades");
        private static readonly AccessTools.FieldRef<BuildSlot, int> slotIncome = AccessTools.FieldRefAccess<BuildSlot, int>("goldIncome");
        private static readonly AccessTools.FieldRef<BuildSlot, bool> slotActivated = AccessTools.FieldRefAccess<BuildSlot, bool>("gotActivated");
        private static readonly AccessTools.FieldRef<BuildSlot, List<BuildSlot>> slotIsRootOf = AccessTools.FieldRefAccess<BuildSlot, List<BuildSlot>>("isRootOf");
        private static readonly AccessTools.FieldRef<BuildSlot, List<BuildSlot>> slotIsActivatorOf = AccessTools.FieldRefAccess<BuildSlot, List<BuildSlot>>("isActivatorOf");
        private static readonly AccessTools.FieldRef<BuildingInteractor, bool> harvestedToday = AccessTools.FieldRefAccess<BuildingInteractor, bool>("harvestedToday");
        private static readonly AccessTools.FieldRef<CostDisplay, int> filledCoins = AccessTools.FieldRefAccess<CostDisplay, int>("currentlyFilledCoins");
        private static readonly AccessTools.FieldRef<Hp, float> hpValue = AccessTools.FieldRefAccess<Hp, float>("hp");
        private static readonly AccessTools.FieldRef<PlayerInteraction, int> balance = AccessTools.FieldRefAccess<PlayerInteraction, int>("balance");
        private static readonly AccessTools.FieldRef<DayNightCycle, List<DayNightCycle.IDaytimeSensitive>> daytimeObjects =
            AccessTools.FieldRefAccess<DayNightCycle, List<DayNightCycle.IDaytimeSensitive>>("daytimeSensitiveObjects");
        private static readonly AccessTools.FieldRef<CommandUnits, List<AutoAttack>> attacksToEnable = AccessTools.FieldRefAccess<CommandUnits, List<AutoAttack>>("autoAttacksToEnable");
        private static readonly FieldInfo towerUpgradeTower = AccessTools.Field(typeof(TowerUpgrade), "autoAttackTower");
        private static readonly FieldInfo upgradeUnitsUnits = AccessTools.Field(typeof(UpgradeUnitsOnEnable), "units");

        // ---- What can be undone -----------------------------------------------------------------------------------

        // Newly activated components of these types change state outside what undo restores (player, global upgrade
        // managers, other buildings, waves, the upgrade definitions themselves) or start multi-day processes.
        private static readonly HashSet<string> unsafeTypes =
        [
            "BlacksmithUpgrade", "RoyalForgeUpgrade", "TempleUpgrade", "FishingHarbourUpgrade",
            "Mill_ImprovedPlow", "Mill_ImprovementWindSpirits", "Mill_ImprovementExplosiveTrap", "Mill_Improvement_Scarecrows",
            "SummoningCircle", "DestroyOrDisableOnEnable", "EnableObjects",
        ];

        // Why each unsafe type can't be taken back, shown when undo refuses.
        private static readonly Dictionary<string, string> unsafeReasons = new()
        {
            ["BlacksmithUpgrade"] = "its research has started, and the game keeps research progress in the run's save",
            ["RoyalForgeUpgrade"] = "its research has started, and the game keeps research progress in the run's save",
            ["TempleUpgrade"] = "the temple's blessing was cast when you bought it and can only be cast once",
            ["FishingHarbourUpgrade"] = "it doubled the harbour's income and then removed itself, so there is nothing left to switch off",
            ["Mill_ImprovedPlow"] = "it rewrote the mill's later upgrades and then removed itself",
            ["Mill_ImprovementWindSpirits"] = "it changed the mill's later upgrades and then removed itself",
            ["Mill_ImprovementExplosiveTrap"] = "it changed the mill's later upgrades and then removed itself",
            ["Mill_Improvement_Scarecrows"] = "it changed the mill's later upgrades and then removed itself",
            ["SummoningCircle"] = "it already added its enemies to the next wave",
            ["DestroyOrDisableOnEnable"] = "it destroyed or switched off other parts of the level",
            ["EnableObjects"] = "it switched on other parts of the level",
        };

        // Upgrades (mostly the castle's) whose effects reach the king, his weapon, the blacksmith multipliers, towers,
        // wall and tower prices or units. Undo handles them: their targets join the action's scope as "globals" (diffed in
        // every window like the building itself), their daytime registrations and static instances are rolled back, their
        // own fields are reset to the pre-activation state, and the commander's per-unit buffs are reversed by hand.
        private static readonly HashSet<string> globalEffectTypes =
        [
            "UpgradeAssassinsTraining", "UpgradeBuildersGuild", "UpgradeCastleUp", "UpgradeCommander", "UpgradeGodlyCurse",
            "UpgradeMagicArmor", "UpgradePlayerDmg", "UpgradePlayerHp", "EmpireUpgrade", "OutpostPerk",
        ];

        // These build or upgrade other buildings for free a few frames after they switch on (Builder's Guild: the nearest
        // level-1 house; Outpost perk: up to two towers). Those builds are recorded into the same action.
        private static readonly HashSet<string> freeBuildTypes = ["UpgradeBuildersGuild", "OutpostPerk"];

        // Upgrade components with a static "instance" that outlives switching them off (UpgradeCastleUp's makes every
        // later wall or tower build cheaper while it's set).
        private static readonly Type[] instanceTypes =
        [
            typeof(UpgradeAssassinsTraining), typeof(UpgradeBuildersGuild), typeof(UpgradeCastleUp), typeof(UpgradeCommander),
            typeof(UpgradeGodlyCurse), typeof(UpgradeMagicArmor),
        ];

        // Components with an OnEnable and no OnDisable whose OnEnable only touches state undo restores (or nothing).
        // Any other such type found in a newly activated part makes the action not undoable, so a game update that
        // adds a new one fails safe.
        private static readonly HashSet<string> safeOnEnableTypes =
        [
            "BuildSlot", "TowerUpgrade", "UpgradeUnitsOnEnable", "LastStandPerk", "BuildingDestructionHandler",
            "UpgradeAssassinsTraining", "UpgradeBuildersGuild", "UpgradeCastleUp", "UpgradeCommander", "UpgradeGodlyCurse",
            "UpgradeMagicArmor", "UpgradePlayerDmg", "UpgradePlayerHp", "EmpireUpgrade", "OutpostPerk",
            "SuspendPlayerCollisionUntilNoOverlap", "TimedUnparent", "FreezePositionOnEnable", "SimpleRotator",
            "ProjectileAudio", "SizeRectangleToMeshBounds", "TextBackgroundFitter", "ForceLayoutGroupRedraw",
            "AdjustLayoutGroupSpacingBasedOnUIScale", "AdjustSizeAndPositionBasedOnUIScale", "TFUIBlockoutElement",
        ];

        // Kept out of field diffs: interaction and UI state (the coin slots, the "waiting for a choice" flag; undo
        // refreshes these instead of rewinding them), presentation, and rendering/registry components whose state
        // has effects outside the object (mesh fusion, GPU instancing, the tag registry). Undo handles mesh fusion
        // through BuildingMeshTracker and the harvest flag explicitly.
        private static readonly HashSet<string> uncapturedTypes =
        [
            "BuildingInteractor", "CostDisplay", "Coinslot", "ProductionBar", "Healthbar", "BuildingFXProcessor",
            "BuildingRangeIndicator", "MaterialFlasherFX", "DamageIndicatorUIHandler", "OnDamageFeedbackHandler",
            "UIScaleHandlerWorld", "UnitAudio", "UnitAttackAnimator", "BillboardAlign", "ScreenMarkerIcon", "SelectUnitUI",
            "UnitProductionSelector", "UnitTypeDisplay", "UnitGroupIndicator", "NightLight", "MillRotor", "FromToAnimation",
            "PositionAnimation", "ScaleAnimation", "SpinAnimation", "SimpleWalk", "RotateForwardToRVOVelocity",
            "BuildingMeshTracker", "BuildingMeshesTracker", "GPUInstanced", "TaggedObject",
        ];

        private static readonly Dictionary<Type, string> classification = [];

        /// <summary>Why a newly activated component of this type makes an action not undoable, or null.</summary>
        private static string Unsafe(Type type)
        {
            if (classification.TryGetValue(type, out var cached)) return cached;
            string reason = null;
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                if (unsafeTypes.Contains(t.Name)) { reason = t.Name; break; }
            if (reason == null && !IsGameType(type)) reason = null;
            else if (reason == null)
            {
                bool safe = false, onEnable = false, onDisable = false;
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    safe |= safeOnEnableTypes.Contains(t.Name);
                    onEnable |= t.GetMethod("OnEnable", F, null, Type.EmptyTypes, null) != null;
                    onDisable |= t.GetMethod("OnDisable", F, null, Type.EmptyTypes, null) != null;
                }
                if (onEnable && !onDisable && !safe) reason = type.Name;
            }
            return classification[type] = reason;
        }

        private static bool HasOnEnable(Type type)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                if (t.GetMethod("OnEnable", F, null, Type.EmptyTypes, null) != null) return true;
            return false;
        }

        private static bool IsGameType(Type t) => t.Assembly.GetName().Name == "Assembly-CSharp";

        private static bool Capturable(MonoBehaviour c)
        {
            var t = c.GetType();
            return IsGameType(t) && string.IsNullOrEmpty(t.Namespace) && !uncapturedTypes.Contains(t.Name);
        }

        // ---- Recorded actions -------------------------------------------------------------------------------------

        private sealed class SlotState
        {
            public BuildSlot Slot;
            public int Level, Income;
            public List<int> Applied;
            public int[] Costs;
            public bool Activated, ActiveSelf;
            public Mesh Mesh;
            public Hp Hp;
            public float MaxHp, HpValue;
            public bool Harvested;

            public static SlotState Capture(BuildSlot s)
            {
                var hp = s.BuildingParent != null ? s.BuildingParent.GetComponent<Hp>() : null;
                return new SlotState
                {
                    Slot = s,
                    Level = slotLevel(s),
                    Income = slotIncome(s),
                    Applied = [.. slotApplied(s)],
                    Costs = s.OwnUpgrades.Select(u => u.cost).ToArray(),
                    Activated = slotActivated(s),
                    ActiveSelf = s.gameObject.activeSelf,
                    Mesh = s.MainMesh != null ? s.MainMesh.sharedMesh : null,
                    Hp = hp,
                    MaxHp = hp != null ? hp.maxHp : 0f,
                    HpValue = hp != null ? hpValue(hp) : 0f,
                    Harvested = s.Interactor != null && harvestedToday(s.Interactor),
                };
            }
        }

        private sealed class UndoAction
        {
            public BuildSlot Root;
            public string Name;
            public int Cost;
            public int LevelBefore, LevelAfter;
            public Choice Choice;
            public string Label;
            public string Unrevertible;
            public bool AwaitingChoice;
            public bool Committed;
            public int SettleFrame = -1;
            public bool Settled;

            public readonly List<SlotState> Slots = [];       // upgraded together
            public readonly List<SlotState> Dependents = [];  // may get unlocked by it
            public readonly Dictionary<Transform, TransformState> Transforms = [];
            public readonly Dictionary<MonoBehaviour, (GameObject go, bool wasActive, ObjectState state)> States = [];
            public readonly Dictionary<object, StateChange> Changes = [];
            public readonly Dictionary<Transform, TransformState> Moved = [];
            public readonly HashSet<GameObject> NewObjects = [];
            public readonly List<(GameObject go, Type type, ObjectState state)> Recreate = [];
            public readonly Dictionary<TaggedObject, List<TagManager.ETag>> UnitTags = [];
            public readonly List<DayNightCycle.IDaytimeSensitive> Registered = [];   // registered while switching on
            public readonly List<(Component owner, UnityEngine.Events.UnityEventBase evt, object[] calls)> Events = [];

            // Effects outside the building (castle upgrades): targets diffed in every window, static instances, and
            // the wall and tower prices UpgradeCastleUp lowered.
            public readonly List<object> GlobalTargets = [];
            public readonly List<ObjectState> Globals = [];
            public readonly List<(FieldInfo field, object value)> Statics = [];
            public readonly List<(BuildSlot slot, int index, int cost)> Prices = [];
            // Buildings a free-build effect (Builder's Guild, Outpost) upgraded as part of this action, with their level after.
            public readonly List<(BuildSlot slot, int level)> Extended = [];
            public bool FreeBuilds;

            // Another feature's action (a teleporter placement): undone by its callback, refunding Cost.
            public Action CustomUndo;
            public Func<bool> CustomAlive;
            public Vector3 At;
            public bool IsCustom => CustomUndo != null;

            public bool Contains(BuildSlot s) => Slots.Any(x => x.Slot == s);
        }

        private sealed class Window
        {
            public UndoAction Action;
            public readonly List<ObjectState> States = [];
            public readonly List<TransformState> Transforms = [];
            public int DaytimeCount;
            // An extension window (a free build attributed to Action): the slots and transforms it added.
            public bool Extension;
            public readonly List<BuildSlot> ExtensionSlots = [];
            public readonly List<SlotState> ExtensionDependents = [];
            public readonly List<Transform> ExtensionTransforms = [];
        }

        private static readonly List<UndoAction> stack = [];
        private static UndoAction pending;     // paid, waiting for its branch choice
        private static Window window;
        private static int payDepth;
        private static int? devPaid;
        private static DayNightCycle stackCycle;

        private static UndoAction Settling => stack.LastOrDefault(a => !a.Settled);

        // ---- Lifecycle --------------------------------------------------------------------------------------------

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            undoKey = Keybinds.Register(FeatureSection, "undo", "Undo build or upgrade", new KeyboardShortcut(KeyCode.Backspace),
                "Takes back your last build, upgrade, branch choice or teleporter today and refunds its gold. Stand near it.");
            hintShown = plugin.Config.Bind(FeatureSection, "Hint shown", false,
                new ConfigDescription("Whether the one-time undo hint has been shown.", null, new SettingTags { Hidden = true }));
            RegisterDevCommands();
            // A savestate load rebuilds the level: nothing recorded before it may be undone afterwards.
            SaveStates.Restoring += _ => Clear();
            SaveStates.Restored += _ => Clear();
        }

        protected override void OnFeatureDisabled() => Clear();

        public override void Update()
        {
            var cycle = DayNightCycle.Instance;
            if (cycle == null || cycle != stackCycle || cycle.CurrentTimestate == DayNightCycle.Timestate.Night)
            {
                if (stack.Count > 0 || pending != null) Clear();
                stackCycle = cycle;
                return;
            }
            foreach (var due in stack.Where(a => !a.Settled && Time.frameCount >= a.SettleFrame).ToList()) Settle(due);
            if (undoKey.Down) Log.For<UndoUpgradeManager>().Info(TryUndo(requireNear: true));
        }

        private static void Clear()
        {
            stack.Clear();
            pending = null;
            window = null;
            payDepth = 0;
        }

        // ---- Capture ----------------------------------------------------------------------------------------------

        private static bool Recording =>
            Feature<UndoUpgradeManager>.On && DayNightCycle.Instance != null &&
            DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Day;

        private static BuildSlot RootOf(BuildSlot s)
        {
            while (s.ActivatorUpgradesThis && s.ActivatorBuilding != null) s = s.ActivatorBuilding;
            return s;
        }

        private static void Begin(BuildSlot clicked, PlayerInteraction player)
        {
            var root = RootOf(clicked);
            int cost = devPaid ?? (clicked.Interactor != null && clicked.Interactor.costDisplay != null ? filledCoins(clicked.Interactor.costDisplay) : 0);
            devPaid = null;
            var action = new UndoAction { Root = root, Cost = cost, LevelBefore = root.Level, Name = BuildingName(root) };
            action.Slots.Add(SlotState.Capture(root));
            foreach (var s in root.BuiltSlotsThatRelyOnThisBuilding)
                if (s != null && s.ActivatorUpgradesThis && s.ActivatorBuilding == root) action.Slots.Add(SlotState.Capture(s));
            foreach (var s in root.BuiltSlotsThatRelyOnThisBuilding.Concat(slotIsActivatorOf(root)).Distinct())
                if (s != null && !action.Contains(s)) action.Dependents.Add(SlotState.Capture(s));
            if (clicked != root && clicked.Interactor != null && !action.Contains(clicked)) action.Slots.Add(SlotState.Capture(clicked));

            foreach (var go in ScopeObjects(action.Slots.Select(s => s.Slot)))
            {
                action.Transforms[go.transform] = new TransformState(go.transform);
                CaptureComponents(action, go, null);
            }
            CaptureGlobals(action, root);
            pending = action;
            OpenWindow(action, initial: true);
        }

        private static void CaptureComponents(UndoAction action, GameObject go, Window into)
        {
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                if (c == null || action.States.ContainsKey(c)) continue;
                if (Capturable(c))
                {
                    var state = ObjectState.Capture(c);
                    action.States[c] = (go, go.activeInHierarchy, state);
                    if (into != null && go.activeInHierarchy) into.States.Add(state);
                }
                if (go.activeInHierarchy && IsGameType(c.GetType()))
                    foreach (var f in EventHooks.EventsOf(c.GetType()))
                        if (f.GetValue(c) is UnityEngine.Events.UnityEventBase e) action.Events.Add((c, e, EventHooks.Calls(e)));
            }
        }

        /// <summary>When the next level can switch on an upgrade that reaches outside the building (the castle's), adds
        /// everything those upgrades can touch to the scope, and remembers their static instances.</summary>
        private static void CaptureGlobals(UndoAction action, BuildSlot root)
        {
            int level = slotLevel(root);
            if (level >= root.OwnUpgrades.Count) return;
            var types = new HashSet<string>();
            foreach (var branch in root.OwnUpgrades[level].upgradeBranches)
                foreach (var go in branch.objectsToActivate)
                    if (go != null)
                        foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                            if (c != null) types.Add(c.GetType().Name);
            if (level == 0 && root.BuildingParent != null)
                foreach (var c in root.BuildingParent.GetComponentsInChildren<MonoBehaviour>(true))
                    if (c != null) types.Add(c.GetType().Name);
            if (!types.Overlaps(globalEffectTypes)) return;

            var targets = action.GlobalTargets;
            void Add(object o) { if (o is UnityEngine.Object u ? u != null : o != null) targets.Add(o); }
            Add(PlayerUpgradeManager.instance);
            Add(BlacksmithUpgrades.instance);
            if (PlayerManager.Instance != null)
                foreach (var king in PlayerManager.Instance.RegisteredPlayers)
                {
                    if (king == null) continue;
                    Add(king.GetComponent<Hp>());
                    foreach (var attack in king.GetComponentsInChildren<ManualAttack>(true)) Add(attack);
                }
            if (types.Contains("EmpireUpgrade") && CastleCenter.instance != null && CastleCenter.instance.transform.parent?.parent is { } world)
            {
                foreach (var tower in world.GetComponentsInChildren<AutoAttackTower>(true)) Add(tower);
                foreach (var oil in world.GetComponentsInChildren<HotOilTower>(true)) Add(oil);
            }
            foreach (var t in targets.Distinct(RefEq.Instance).ToList())
                if (!(t is MonoBehaviour m && action.States.ContainsKey(m))) action.Globals.Add(ObjectState.Capture(t));
            foreach (var type in instanceTypes)
                if (AccessTools.Field(type, "instance") is { IsStatic: true } f) action.Statics.Add((f, f.GetValue(null)));
        }

        private static HashSet<GameObject> ScopeObjects(IEnumerable<BuildSlot> slots)
        {
            var objects = new HashSet<GameObject>();
            void AddTree(GameObject go)
            {
                if (go == null) return;
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) objects.Add(t.gameObject);
            }
            foreach (var s in slots)
            {
                AddTree(s.gameObject);
                int level = slotLevel(s);
                if (level < s.OwnUpgrades.Count)
                    foreach (var branch in s.OwnUpgrades[level].upgradeBranches)
                    {
                        foreach (var go in branch.objectsToActivate) AddTree(go);
                        foreach (var go in branch.objectsToDisable) AddTree(go);
                    }
            }
            // Units can be unparented from their building, and upgrades can target them by reference.
            foreach (var go in objects.ToList())
            {
                foreach (var r in go.GetComponents<UnitRespawnerForBuildings>())
                    foreach (var u in r.units) if (u != null) AddTree(u.gameObject);
                foreach (var u in go.GetComponents<UpgradeUnitsOnEnable>())
                    if (upgradeUnitsUnits.GetValue(u) is Hp[] hps) foreach (var h in hps) if (h != null) AddTree(h.gameObject);
            }
            return objects;
        }

        private static void OpenWindow(UndoAction action, bool initial)
        {
            var w = new Window { Action = action };
            if (initial)
            {
                foreach (var kv in action.States) if (kv.Value.wasActive) w.States.Add(kv.Value.state);
                w.Transforms.AddRange(action.Transforms.Values.Where(t => t.ActiveInHierarchy));
                w.States.AddRange(action.Globals);
            }
            else
            {
                foreach (var kv in action.States)
                    if (kv.Key != null && kv.Key.gameObject.activeInHierarchy) w.States.Add(ObjectState.Capture(kv.Key));
                foreach (var t in action.Transforms.Keys)
                    if (t != null && t.gameObject.activeInHierarchy) w.Transforms.Add(new TransformState(t));
                RecaptureGlobals(w);
            }
            StartWindow(w);
        }

        private static void RecaptureGlobals(Window w)
        {
            foreach (var g in w.Action.Globals)
                if (!(g.Target is UnityEngine.Object o && o == null)) w.States.Add(ObjectState.Capture(g.Target));
        }

        private static void StartWindow(Window w)
        {
            var cycle = DayNightCycle.Instance;
            w.DaytimeCount = cycle != null ? daytimeObjects(cycle).Count : 0;
            window = w;
        }

        private static void CloseWindow()
        {
            var w = window;
            window = null;
            if (w == null) return;
            foreach (var s in w.States) s.Diff(w.Action.Changes);
            foreach (var t in w.Transforms)
                if (t.Transform != null && t.PlacementDiffers(t.Transform) && !w.Action.Moved.ContainsKey(t.Transform))
                    w.Action.Moved[t.Transform] = t;
            // Daytime registrations made while switching on (LastStandPerk, UpgradeCastleUp, EmpireUpgrade...): they
            // have no OnDisable, so a switched-off copy would still act at dusk and switching it on again would register twice.
            var cycle = DayNightCycle.Instance;
            if (cycle != null)
            {
                var list = daytimeObjects(cycle);
                for (int i = w.DaytimeCount; i < list.Count; i++)
                    if (list[i] is Component c && c != null && !(c is BuildSlot)) w.Action.Registered.Add(list[i]);
            }
        }

        private static void MarkUnrevertible(UndoAction action, string reason)
        {
            if (action.Unrevertible == null) action.Unrevertible = reason;
        }

        /// <summary>Some building changed outside a recorded action: nothing before this point can be undone safely.</summary>
        private static void Barrier(BuildSlot slot, string what)
        {
            if (stack.Count == 0 || stack[^1].Unrevertible != null) return;
            Log.For<UndoUpgradeManager>().Info($"{BuildingName(slot)} {what} on its own; earlier actions can no longer be undone");
            DropBuilds();
        }

        private static void Commit(UndoAction action)
        {
            pending = null;
            var root = action.Root;
            action.LevelAfter = root.Level;
            action.Committed = true;
            int branches = action.LevelAfter - 1 < root.OwnUpgrades.Count ? root.OwnUpgrades[action.LevelAfter - 1].upgradeBranches.Count : 1;
            string choice = branches > 1 && action.Choice != null ? $" ({Translate(root.GET_LOCIDENTIFIER_CHOICENAME(action.Choice), action.Choice.name)})" : "";
            action.Label = action.LevelBefore == 0 ? $"Build {action.Name}{choice}" : $"Upgrade {action.Name} to level {action.LevelAfter}{choice}";

            // What got switched on.
            foreach (var kv in action.Transforms)
                if (kv.Key != null && !kv.Value.ActiveInHierarchy && kv.Key.gameObject.activeInHierarchy) action.NewObjects.Add(kv.Key.gameObject);
            foreach (var d in action.Dependents)
                if (!d.ActiveSelf && d.Slot.gameObject.activeSelf)
                    foreach (var t in d.Slot.GetComponentsInChildren<Transform>(false)) action.NewObjects.Add(t.gameObject);

            if (action.Slots[0].Hp != null && action.LevelBefore > 0 && action.Slots[0].HpValue <= 0f)
                MarkUnrevertible(action, "it was knocked out");
            CheckNewObjects(action, action.NewObjects);

            action.SettleFrame = Time.frameCount + SettleFrames;
            stack.Add(action);
            Log.For<UndoUpgradeManager>().Debug($"Recorded \"{action.Label}\" (cost {action.Cost}, {action.Changes.Count} field changes, " +
                $"{action.NewObjects.Count} objects switched on){(action.Unrevertible != null ? $"; not undoable: {action.Unrevertible}" : "")}");
        }

        private static bool OutpostOn => PerkManager.instance != null && PerkManager.instance.OutpostEquipped;

        private static void CheckNewObjects(UndoAction action, IEnumerable<GameObject> objects)
        {
            foreach (var go in objects)
                foreach (var c in go.GetComponents<MonoBehaviour>())
                {
                    if (c == null) continue;
                    var name = c.GetType().Name;
                    // Every tower slot carries one; it only acts (building free towers) with the Outpost perk.
                    if (c is OutpostPerk && !OutpostOn) continue;
                    if (Unsafe(c.GetType()) is { } reason)
                        MarkUnrevertible(action, unsafeReasons.TryGetValue(reason, out var why) ? why : $"its {reason} effect has no known way to be taken back");
                    else if (globalEffectTypes.Contains(name) && action.Globals.Count == 0 && action.GlobalTargets.Count == 0)
                        MarkUnrevertible(action, $"its {name} effect was switched on by something undo doesn't follow");
                    else if (c is TowerUpgrade tu && towerUpgradeTower.GetValue(tu) is Component tower && action.NewObjects.Contains(tower.gameObject))
                        MarkUnrevertible(action, "it upgrades a tower built in the same step");
                    else if (c is UpgradeUnitsOnEnable uu && upgradeUnitsUnits.GetValue(uu) is Hp[] hps && hps.Any(h => h != null && action.NewObjects.Contains(h.gameObject)))
                        MarkUnrevertible(action, "it upgrades units recruited in the same step");
                    // Applies a perk's cooldown bonus in Start, after the upgrade's window: fine on an attack switched on
                    // with it, not on one that already existed.
                    else if (c is PerkCooldownModifyer pcm && pcm.autoAttack != null && PerkManager.IsEquipped(pcm.requiredPerk) && !action.NewObjects.Contains(pcm.autoAttack.gameObject))
                        MarkUnrevertible(action, $"the {Translate(pcm.requiredPerk.LOCIDENTIFIER_NAME, pcm.requiredPerk.name)} perk sped up an attack that already existed");
                    if (freeBuildTypes.Contains(name)) action.FreeBuilds = true;
                }
        }

        // ---- Free builds (Builder's Guild, Outpost) ---------------------------------------------------------------

        /// <summary>The settling action whose upgrade can build or upgrade other buildings for free right now.</summary>
        private static UndoAction FreeBuildSource() =>
            stack.LastOrDefault(a => !a.IsCustom && !a.Settled && a.FreeBuilds && a.Unrevertible == null);

        /// <summary>Adds a building that a free-build effect is about to upgrade to the action, and opens a window on it.</summary>
        private static void Extend(UndoAction action, BuildSlot slot)
        {
            var w = new Window { Action = action, Extension = true };
            void AddSlot(BuildSlot s)
            {
                if (s == null || action.Contains(s)) return;
                action.Slots.Add(SlotState.Capture(s));
                w.ExtensionSlots.Add(s);
            }
            AddSlot(slot);
            foreach (var s in slot.BuiltSlotsThatRelyOnThisBuilding)
                if (s != null && s.ActivatorUpgradesThis && s.ActivatorBuilding == slot) AddSlot(s);
            foreach (var s in slot.BuiltSlotsThatRelyOnThisBuilding.Concat(slotIsActivatorOf(slot)).Distinct())
                if (s != null && !action.Contains(s) && !action.Dependents.Any(d => d.Slot == s))
                {
                    var d = SlotState.Capture(s);
                    action.Dependents.Add(d);
                    w.ExtensionDependents.Add(d);
                }
            foreach (var go in ScopeObjects(w.ExtensionSlots))
            {
                var t = go.transform;
                if (action.Transforms.ContainsKey(t)) continue;
                // A tower slot the Outpost just switched on is a dependent of the castle: undo switches it off as one.
                if (action.Dependents.Any(d => d.Slot != null && d.Slot.transform == t && !d.ActiveSelf)) continue;
                var ts = new TransformState(t);
                action.Transforms[t] = ts;
                w.ExtensionTransforms.Add(t);
                if (ts.ActiveInHierarchy) w.Transforms.Add(ts);
                CaptureComponents(action, go, w);
            }
            RecaptureGlobals(w);
            StartWindow(w);
        }

        private static void FinishExtension(Window w)
        {
            var action = w.Action;
            var added = new List<GameObject>();
            foreach (var t in w.ExtensionTransforms)
                if (t != null && !action.Transforms[t].ActiveInHierarchy && t.gameObject.activeInHierarchy && action.NewObjects.Add(t.gameObject)) added.Add(t.gameObject);
            foreach (var d in w.ExtensionDependents)
                if (!d.ActiveSelf && d.Slot != null && d.Slot.gameObject.activeSelf)
                    foreach (var t in d.Slot.GetComponentsInChildren<Transform>(false))
                        if (action.NewObjects.Add(t.gameObject)) added.Add(t.gameObject);
            CheckNewObjects(action, added);
            foreach (var s in w.ExtensionSlots)
                if (!s.ActivatorUpgradesThis) action.Extended.Add((s, s.Level));
            action.SettleFrame = Math.Max(action.SettleFrame, Time.frameCount + SettleFrames);
            Log.For<UndoUpgradeManager>().Debug($"\"{action.Label}\" also upgraded {string.Join(", ", w.ExtensionSlots.Select(s => s.buildingName))} for free");
        }

        // A few frames later: Start() has run on new objects and self-destroying effects are gone.
        private void Settle(UndoAction action)
        {
            action.Settled = true;
            // Destroyed since capture. Inside newly activated parts that's a first-activation effect. A component that
            // applied itself in OnEnable and removed itself (TowerUpgrade) is recreated on undo, since its target's
            // fields are rewound and switching it on again must reapply it. One that did so in Start (the Perk*
            // modifiers on units) applied itself to something undo leaves as is (a new unit), so it stays gone, as
            // it would after a first build. A destroyed object would be destroyed again. Anything that existed and
            // was active before must survive.
            foreach (var kv in action.States)
            {
                if (kv.Key != null) continue;
                var (go, wasActive, state) = kv.Value;
                if (wasActive) MarkUnrevertible(action, "it removed something that already existed");
                else if (go != null && HasOnEnable(state.Target.GetType())) action.Recreate.Add((go, state.Target.GetType(), state));
            }
            // A new component that subscribed (in Start) to an event of something that stays around after undo would keep
            // acting while switched off: the Armored Tower's shield listens to the tower's damage. The building's own
            // upgrade events are fine: they only fire when it's upgraded again, which switches the listener back on.
            foreach (var (owner, evt, before) in action.Events)
            {
                if (owner == null || owner is BuildSlot) continue;
                foreach (var call in EventHooks.Calls(evt))
                {
                    if (Array.IndexOf(before, call) >= 0) continue;
                    if (EventHooks.Target(call) is Component listener && listener != null && action.NewObjects.Contains(listener.gameObject))
                        MarkUnrevertible(action, $"its {listener.GetType().Name} stays hooked to the {owner.name}");
                }
            }
            foreach (var go in action.NewObjects)
            {
                if (go == null) continue;
                var p = go.GetComponent<PathfindMovementPlayerunit>();
                var tagged = go.GetComponent<TaggedObject>();
                if (p != null && tagged != null) action.UnitTags[tagged] = [.. tagged.Tags];
            }
            if (action.Unrevertible != null)
            {
                // A barrier: what came before can't be undone; what was recorded after it (a teleporter) still can.
                int at = stack.IndexOf(action);
                if (at > 0) stack.RemoveRange(0, at);
                return;
            }
            if (!hintShown.Value)
            {
                hintShown.Value = true;
                Notify.Show($"Misclicked? Press {undoKey.Shortcut} near the building to undo it today.", NotifyKind.Info, 5f, "undo-upgrade");
            }
        }

        // ---- Undo -------------------------------------------------------------------------------------------------

        private string TryUndo(bool requireNear)
        {
            if (DayNightCycle.Instance == null) return Say("Undo works during the day in a level.", NotifyKind.Warning);
            if (DayNightCycle.Instance.CurrentTimestate != DayNightCycle.Timestate.Day) return Say("Undo works only during the day.", NotifyKind.Warning);
            if (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning) return "choice open";
            // A teleporter picked up since it was placed was already refunded: nothing left to undo there.
            while (stack.Count > 0 && stack[^1].IsCustom && !Alive(stack[^1])) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) return Say("Nothing to undo today.", NotifyKind.Info);
            var action = stack[^1];
            if (action.Unrevertible != null) return Say($"Can't undo {Lower(action.Label)}: {action.Unrevertible}.", NotifyKind.Warning);
            if (!action.Settled) return "settling";
            var player = PlayerInteraction.instance;
            if (player == null) return "no player";
            if (action.IsCustom) return UndoCustom(action, player, requireNear);
            var root = action.Root;
            if (root == null) { DropBuilds(); return Say("Nothing to undo today.", NotifyKind.Info); }
            // Refreshing the cost displays would drop coins already put into another payment.
            if (TagManager.instance != null && TagManager.instance.playerBuildingInteractors.Any(i => i != null && i.costDisplay != null && filledCoins(i.costDisplay) > 0))
                return Say("Finish or cancel the payment first.", NotifyKind.Info);
            if (requireNear && Vector3.Distance(Flat(player.transform.position), Flat(ClosestPoint(root, player.transform.position))) > NearDistance)
                return Say($"Walk back to the {action.Name} to undo {Lower(action.Label)}.", NotifyKind.Info);
            if (action.Slots.Any(s => s.Slot == null) || root.Level != action.LevelAfter || action.Extended.Any(e => e.slot.Level != e.level))
            {
                DropBuilds();
                return Say($"Can't undo {Lower(action.Label)}: the building changed since.", NotifyKind.Warning);
            }
            foreach (var kv in action.UnitTags)
            {
                if (kv.Key == null) continue;
                var now = kv.Key.Tags.Where(t => t != TagManager.ETag.AUTO_Commanded).OrderBy(t => t);
                var then = kv.Value.Where(t => t != TagManager.ETag.AUTO_Commanded).OrderBy(t => t);
                if (!now.SequenceEqual(then)) return Say($"Can't undo {Lower(action.Label)}: its units have new orders or groups. Release them first.", NotifyKind.Warning);
            }

            Restore(action, player);
            stack.RemoveAt(stack.Count - 1);
            GameplayImpact.Mark("Undo upgrade");
            Notify.Show($"Undo: {action.Label} (+{action.Cost} gold)", NotifyKind.Success, 3f, "undo-upgrade");
            return $"undid {action.Label}, refunded {action.Cost}";
        }

        private static bool Alive(UndoAction a)
        {
            try { return a.CustomAlive == null || a.CustomAlive(); }
            catch (Exception) { return false; }
        }

        private static string UndoCustom(UndoAction action, PlayerInteraction player, bool requireNear)
        {
            if (requireNear && Vector3.Distance(Flat(player.transform.position), Flat(action.At)) > NearDistance)
                return Say($"Walk back to undo {Lower(action.Label)}.", NotifyKind.Info);
            stack.RemoveAt(stack.Count - 1);
            try { action.CustomUndo(); }
            catch (Exception e)
            {
                Log.For<UndoUpgradeManager>().Error($"Undoing \"{action.Label}\" failed", e);
                return Say($"Couldn't undo {Lower(action.Label)}.", NotifyKind.Warning);
            }
            Refund(player, action.Cost);
            GameplayImpact.Mark("Undo upgrade");
            Notify.Show(action.Cost > 0 ? $"Undo: {action.Label} (+{action.Cost} gold)" : $"Undo: {action.Label}", NotifyKind.Success, 3f, "undo-upgrade");
            Log.For<UndoUpgradeManager>().Info($"Undid {action.Label}, +{action.Cost} gold");
            return $"undid {action.Label}, refunded {action.Cost}";
        }

        // Like AddCoin, but a refund isn't new income, so networth (the end-of-run economy stat) stays put.
        private static void Refund(PlayerInteraction player, int amount)
        {
            if (amount <= 0) return;
            balance(player) += amount;
            player.onBalanceGain.Invoke(amount);
        }

        private static void DropBuilds() => stack.RemoveAll(a => !a.IsCustom);

        /// <summary>Records another feature's paid action so the undo key takes it back too, newest first along with
        /// builds and upgrades today. <paramref name="undo"/> removes it (without refunding: undo refunds
        /// <paramref name="cost"/>); <paramref name="alive"/> says whether there is still something to undo (a teleporter
        /// picked up since was already refunded). Does nothing while Undo upgrade is off or outside the day.</summary>
        public static void RecordCustom(string label, int cost, Vector3 at, Func<bool> alive, Action undo)
        {
            if (!Recording || undo == null) return;
            stack.Add(new UndoAction
            {
                Label = label, Name = label, Cost = Math.Max(0, cost), At = at, CustomAlive = alive, CustomUndo = undo,
                Committed = true, Settled = true,
            });
        }

        // Reverses the commander's per-unit effects, which depend on the flag at the time a unit is held, released,
        // picked up or put down: units held now lose the hold bonus while the flag is still on, and units following the
        // king lose the speed bonus they got when picked up. Then the flag goes off (also restored as a field) and the
        // holds are set again without the bonus.
        private static void RevertCommander()
        {
            var upgrades = PlayerUpgradeManager.instance;
            var command = CommandUnits.instance;
            if (upgrades == null || command == null || !upgrades.commander || TagManager.instance == null) return;
            var tagged = new List<TaggedObject>();
            TagManager.instance.FindAllTaggedObjectsWithTag(tagged, TagManager.ETag.PlayerUnit);
            var held = tagged.Select(t => t != null ? t.GetComponent<PathfindMovementPlayerunit>() : null).Where(p => p != null && p.HoldPosition).ToList();
            foreach (var p in held) p.HoldPosition = false;
            foreach (var p in command.PlayerUnitsCommanding) if (p != null) p.movementSpeed /= command.commanderUnitMoveSpeedMulti;
            upgrades.commander = false;
            foreach (var p in held) p.HoldPosition = true;
        }

        private static void Restore(UndoAction action, PlayerInteraction player)
        {
            if (action.NewObjects.Any(go => go != null && go.GetComponent<UpgradeCommander>() != null)) RevertCommander();

            // Units recruited by this action that the player has picked up.
            var command = CommandUnits.instance;
            if (command != null)
                foreach (var go in action.NewObjects)
                {
                    var p = go != null ? go.GetComponent<PathfindMovementPlayerunit>() : null;
                    if (p != null && command.PlayerUnitsCommanding.Remove(p))
                    {
                        // Selected recruits have their attacks switched off until they're placed; placing switches
                        // them back on, removal doesn't.
                        var pendingAttacks = attacksToEnable(command);
                        foreach (var attack in p.GetComponents<AutoAttack>())
                        {
                            pendingAttacks.Remove(attack);
                            attack.enabled = true;
                        }
                        command.OnUnitRemove(p);
                        if (command.PlayerUnitsCommanding.Count == 0) command.commanding = false;
                    }
                }

            // Let mesh fusion release the meshes before anything moves or switches off.
            foreach (var s in action.Slots) s.Slot.buildingMeshTracker?.Unfreeze();

            // Active flags: off first, then on.
            foreach (var kv in action.Transforms)
                if (kv.Key != null && kv.Key.gameObject.activeSelf && !kv.Value.ActiveSelf) kv.Key.gameObject.SetActive(false);
            foreach (var d in action.Dependents)
                if (!d.ActiveSelf && d.Slot != null && d.Slot.gameObject.activeSelf) d.Slot.gameObject.SetActive(false);
            foreach (var kv in action.Transforms)
                if (kv.Key != null && !kv.Key.gameObject.activeSelf && kv.Value.ActiveSelf) kv.Key.gameObject.SetActive(true);

            // Placement of what was switched on (units walked off, things unparented) and of what the upgrade moved.
            foreach (var kv in action.Transforms)
                if (kv.Key != null && !kv.Value.ActiveInHierarchy && kv.Value.PlacementDiffers(kv.Key)) kv.Value.RestorePlacement();
            foreach (var t in action.Moved.Values) t.RestorePlacement();

            // Recruits keep their home spot through deactivation; send it back to the original one so they line up
            // at the building as after a first build, not where they were ordered to.
            foreach (var go in action.NewObjects)
            {
                var p = go != null ? go.GetComponent<PathfindMovementPlayerunit>() : null;
                if (p == null) continue;
                p.HomePosition = p.HomePositionOriginal;
                p.HasReachedHomePositionAlready = false;
            }

            foreach (var (go, type, state) in action.Recreate)
                if (go != null) state.WriteTo(go.AddComponent(type));

            foreach (var change in action.Changes.Values) change.Restore();

            // The building's own state.
            foreach (var s in action.Slots)
            {
                var slot = s.Slot;
                slotLevel(slot) = s.Level;
                slotIncome(slot) = s.Income;
                var applied = slotApplied(slot);
                applied.Clear();
                applied.AddRange(s.Applied);
                for (int i = 0; i < s.Costs.Length && i < slot.OwnUpgrades.Count; i++) slot.OwnUpgrades[i].cost = s.Costs[i];
                slotActivated(slot) = s.Activated;
                if (slot.MainMesh != null && s.Mesh != null) slot.MainMesh.sharedMesh = s.Mesh;
                if (s.Hp != null)
                {
                    s.Hp.maxHp = s.MaxHp;
                    // A fresh building's Hp starts at 0 and its Start revives it; that has happened now, so leave it at
                    // full health for the next build.
                    hpValue(s.Hp) = s.HpValue > 0f ? Mathf.Min(s.HpValue, s.MaxHp) : s.MaxHp;
                    s.Hp.OnHpChange.Invoke();
                }
                if (slot.Interactor != null) harvestedToday(slot.Interactor) = s.Harvested;
            }
            foreach (var d in action.Dependents)
                if (d.Slot != null) slotActivated(d.Slot) = d.Activated;

            // Wall and tower prices the Castle Up discount lowered.
            for (int i = action.Prices.Count - 1; i >= 0; i--)
            {
                var (slot, index, cost) = action.Prices[i];
                if (slot != null && index < slot.OwnUpgrades.Count) slot.OwnUpgrades[index].cost = cost;
            }

            var cycle = DayNightCycle.Instance;
            if (cycle != null)
                foreach (var obj in action.Registered)
                    if (obj is Component c && c != null) daytimeObjects(cycle).Remove(obj);   // registered in OnEnable; switching on again re-registers

            // Upgrade components switched off again go back to how they were before they first switched on (Builder's
            // Guild's "already built a house", Castle Up's tag filter, Godly Curse's hit list), and the statics
            // pointing at them are put back, so switching them on again replays the first activation exactly.
            foreach (var go in action.NewObjects)
            {
                if (go == null || go.activeInHierarchy) continue;
                foreach (var c in go.GetComponents<MonoBehaviour>())
                    if (c != null && globalEffectTypes.Contains(c.GetType().Name) && action.States.TryGetValue(c, out var st) && !st.wasActive)
                        st.state.WriteTo(c);
            }
            foreach (var (field, value) in action.Statics)
                field.SetValue(null, value is UnityEngine.Object o && o == null ? null : value);

            // Health bars of anything whose health was rewound (the king after Royal Training-style upgrades).
            foreach (var hp in action.Changes.Values.OfType<FieldChange>().Select(c => c.Owner).OfType<Hp>().Distinct())
                if (hp != null) hp.OnHpChange?.Invoke();

            foreach (var s in action.Slots)
                if (s.Slot.isActiveAndEnabled && s.Slot.BuildingParent != null && s.Slot.BuildingParent.activeInHierarchy)
                    s.Slot.buildingMeshTracker?.FreezeMeshWithDelay();

            // Interaction cues (upgrade arrows, cost displays) of everything whose options depend on this building.
            var refresh = new HashSet<BuildingInteractor>();
            foreach (var s in action.Slots.Concat(action.Dependents))
                if (s.Slot != null && s.Slot.Interactor != null) refresh.Add(s.Slot.Interactor);
            foreach (var s in slotIsRootOf(action.Root))
                if (s != null) foreach (var i in s.GetComponentsInChildren<BuildingInteractor>()) refresh.Add(i);
            foreach (var i in refresh) if (i != null && i.gameObject.activeInHierarchy) i.UpdateInteractionState();
            TooltipManager.instance?.SetInteractorRefreshFlag();

            Refund(player, action.Cost);
            Log.For<UndoUpgradeManager>().Info($"Restored {action.Label}: level {action.LevelAfter} -> {action.LevelBefore}, " +
                $"{action.Changes.Count} fields, {action.NewObjects.Count} objects off, recreated [{string.Join(", ", action.Recreate.Select(r => r.type.Name))}], +{action.Cost} gold" +
                string.Concat(action.Changes.Values.Take(40).Select(c => "\n  " + c.Describe())));
        }

        // ---- Helpers ----------------------------------------------------------------------------------------------

        private static string Say(string text, NotifyKind kind)
        {
            Notify.Show(text, kind, 3f, "undo-upgrade");
            return text;
        }

        private static string Lower(string label) => label.Length > 0 ? char.ToLowerInvariant(label[0]) + label.Substring(1) : label;

        private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        private static Vector3 ClosestPoint(BuildSlot slot, Vector3 from)
        {
            var col = slot.Interactor != null ? slot.Interactor.GetComponent<Collider>() : null;
            return col != null && col.enabled && col.gameObject.activeInHierarchy ? col.ClosestPoint(from) : slot.transform.position;
        }

        private static string Translate(string key, string fallback)
        {
            try
            {
                var s = TextTranslator.Translate(key);
                return string.IsNullOrEmpty(s) || s == key ? fallback : s;
            }
            catch (Exception) { return fallback; }
        }

        private static string BuildingName(BuildSlot s) => Translate(s.LOCIDENTIFIER_NAME, s.buildingName);

        // ---- Patches ----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.TryToBuildOrUpgradeAndPay))]
        private static class TryToBuildOrUpgradeAndPay_Patch
        {
            private static void Prefix(BuildSlot __instance, PlayerInteraction player, ref bool __state)
            {
                if (payDepth++ > 0) return;
                if (!Recording || !__instance.CanBeUpgraded || pending != null || window != null) return;
                if (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning) return;
                // A free build by the Builder's Guild or the Outpost perk right after the upgrade that switched it on.
                if (player == null)
                {
                    if (FreeBuildSource() is not { } source) return;
                    try
                    {
                        Extend(source, RootOf(__instance));
                        __state = true;
                    }
                    catch (Exception e)
                    {
                        window = null;
                        MarkUnrevertible(source, "it built something for free that undo couldn't follow");
                        Log.For<UndoUpgradeManager>().Error($"Capture of a free build failed", e);
                    }
                    return;
                }
                try
                {
                    Begin(__instance, player);
                    __state = true;
                }
                catch (Exception e)
                {
                    pending = null;
                    window = null;
                    Log.For<UndoUpgradeManager>().Error($"Capture failed", e);
                }
            }

            private static void Postfix(bool __state)
            {
                if (!__state) return;
                if (window is { Extension: true } extension)
                {
                    CloseWindow();
                    FinishExtension(extension);
                    return;
                }
                var action = pending;
                CloseWindow();
                if (action == null) return;
                if (action.Root.Level > action.LevelBefore) Commit(action);
                else if (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning && ChoiceManager.instance.currentOriginBuildSlot == action.Root)
                    action.AwaitingChoice = true;
                else pending = null;
            }

            private static void Finalizer() => payDepth = Math.Max(0, payDepth - 1);
        }

        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.OnUpgradeChoiceComplete))]
        private static class OnUpgradeChoiceComplete_Patch
        {
            private static void Prefix(BuildSlot __instance, Choice _choiceMade, ref bool __state)
            {
                if (pending is { } action && __instance == action.Root) action.Choice = _choiceMade;
                if (pending is { AwaitingChoice: true } waiting && __instance == waiting.Root && window == null)
                {
                    if (_choiceMade == null || !Recording)
                    {
                        // Buildings upgraded along with it at payment stay upgraded when the choice is cancelled.
                        if (waiting.Changes.Count > 0) DropBuilds();
                        pending = null;
                        return;
                    }
                    try
                    {
                        OpenWindow(waiting, initial: false);
                        __state = true;
                    }
                    catch (Exception e)
                    {
                        pending = null;
                        window = null;
                        Log.For<UndoUpgradeManager>().Error($"Capture failed", e);
                    }
                    return;
                }
                if (_choiceMade == null) return;
                if (window != null && !window.Action.Contains(__instance)) MarkUnrevertible(window.Action, $"it also changed the {BuildingName(__instance)}");
                else if (window == null && Settling is { } settling && !settling.Contains(__instance)) MarkUnrevertible(settling, $"it also changed the {BuildingName(__instance)}");
                else if (window == null && pending == null) Barrier(__instance, "was upgraded");
            }

            private static void Postfix(bool __state)
            {
                if (!__state) return;
                var action = window?.Action;
                CloseWindow();
                if (action != null && action.Root.Level > action.LevelBefore) Commit(action);
                else pending = null;
            }
        }

        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.ForceManualUpgradeWithoutFeedbackEffects))]
        private static class ForceManualUpgrade_Patch
        {
            private static void Prefix(BuildSlot __instance)
            {
                if (window != null && !window.Action.Contains(__instance)) MarkUnrevertible(window.Action, $"it also changed the {BuildingName(__instance)}");
                else if (window == null) Barrier(__instance, "was upgraded");
            }
        }

        [HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.NextUpgradeOrBuildCost), MethodType.Setter)]
        private static class NextUpgradeOrBuildCost_Patch
        {
            private static void Prefix(BuildSlot __instance)
            {
                var action = window?.Action ?? Settling;
                if (action == null || action.Contains(__instance)) return;
                // Castle Up lowering every wall's and tower's next price: remembered and put back on undo.
                if (castleUpDiscount && window != null)
                {
                    if (!__instance.ActivatorUpgradesThis && __instance.CanBeUpgraded)
                        action.Prices.Add((__instance, slotLevel(__instance), __instance.NextUpgradeOrBuildCost));
                    return;
                }
                MarkUnrevertible(action, $"it changed the price of the {BuildingName(__instance)}");
            }
        }

        private static bool castleUpDiscount;

        [HarmonyPatch(typeof(UpgradeCastleUp), "DecreasePriceOfWallsAndTowers")]
        private static class CastleUpDiscount_Patch
        {
            private static void Prefix() => castleUpDiscount = true;
            private static void Finalizer() => castleUpDiscount = false;
        }

        // ---- Dev commands -----------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("undo", "undo                   undo the last build/upgrade today (ignores distance)", _ => TryUndo(requireNear: false));
            DevCommands.Register("undo-key", "undo-key               undo as the keybind does (needs the king near the building)", _ => TryUndo(requireNear: true));
            DevCommands.Register("goto", "goto <name> [dx dz]    teleport the king next to the nearest building matching name (plus an offset)", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: goto <name> [dx dz]");
                var parts = args.ToList();
                float dx = 0f, dz = -6f;
                if (parts.Count >= 3 && float.TryParse(parts[^2], out var x) && float.TryParse(parts[^1], out var z)) { dx = x; dz = z; parts.RemoveRange(parts.Count - 2, 2); }
                var slot = FindSlot(string.Join(" ", parts), buyable: false);
                var mover = PlayerMovement.instance ?? throw new InvalidOperationException("no player");
                mover.TeleportTo(slot.transform.position + new Vector3(dx, 0f, dz));
                return $"king at {mover.transform.position:F0} near {slot.buildingName} @{slot.transform.position:F0}";
            });
            DevCommands.Register("hold", "hold <name>            fill the coin slots of the nearest matching building like holding the interact key", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: hold <name>");
                var slot = FindSlot(string.Join(" ", args), buyable: true);
                var interactor = slot.Interactor ?? throw new InvalidOperationException("no interactor");
                var player = PlayerInteraction.instance ?? throw new InvalidOperationException("no player");
                Plugin.StartCoroutine(Hold(interactor, player));
                return $"holding interact on {slot.buildingName} (cost {slot.NextUpgradeOrBuildCost}, gold {player.Balance})";
            });
            DevCommands.Register("undo-status", "undo-status            list today's undoable actions, newest last", _ =>
                stack.Count == 0 ? "nothing to undo" : string.Join("\n", stack.Select(a =>
                    $"{a.Label}: +{a.Cost} gold, {a.Changes.Count} fields, {a.NewObjects.Count} new objects, {a.Recreate.Count} to recreate" +
                    (a.Unrevertible != null ? $" [not undoable: {a.Unrevertible}]" : a.Settled ? "" : " [settling]"))));
            DevCommands.Register("buy", "buy [name] [branch]    pay for and build/upgrade the nearest slot matching name (no name: list slots)", Buy);
            DevCommands.Register("slot", "slot <name>            level, branches, gold income, hp, units and cost of the nearest matching building", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: slot <name>");
                return Describe(FindSlot(string.Join(" ", args), buyable: false));
            });
            DevCommands.Register("undo-scan", "undo-scan              list game component types activated by builds/upgrades in this level", _ => Scan());
            DevCommands.Register("undo-branches", "undo-branches <name>   each level's choices of the nearest matching building and the game components they switch on", args =>
            {
                if (args.Length == 0) throw new ArgumentException("usage: undo-branches <name>");
                var slot = FindSlot(string.Join(" ", args), buyable: false);
                var lines = new List<string> { $"{slot.buildingName} level {slot.Level}" };
                for (int i = 0; i < slot.upgrades.Count; i++)
                    for (int b = 0; b < slot.upgrades[i].upgradeBranches.Count; b++)
                    {
                        var branch = slot.upgrades[i].upgradeBranches[b];
                        var types = branch.objectsToActivate.Where(go => go != null)
                            .SelectMany(go => go.GetComponentsInChildren<MonoBehaviour>(true)).Where(c => c != null && IsGameType(c.GetType()))
                            .Select(c => c.GetType().Name).Distinct();
                        string name = branch.choiceDetails != null ? Translate(slot.GET_LOCIDENTIFIER_CHOICENAME(branch.choiceDetails), branch.choiceDetails.name) : "-";
                        lines.Add($"  {i}.{b} {name} (cost {slot.upgrades[i].cost}, income {branch.goldIncomeChange:+0;-0;0}): {string.Join(" ", types)}");
                    }
                return string.Join("\n", lines);
            });
            DevCommands.Register("undo-snapshot", "undo-snapshot          everything castle upgrades and builds can change: king, weapon, blacksmith, buildings, prices, units, gold", _ => Snapshot());
        }

        private static string Snapshot()
        {
            var lines = new List<string>();
            var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
            lines.Add($"gold {player.Balance} networth {player.Networth}");
            var pum = PlayerUpgradeManager.instance;
            if (pum != null)
                lines.Add($"upgrades dmg x{pum.PlayerDamageMultiplyer:0.####} regen x{pum.PlayerHealthRegenerationMultiplyer:0.####} assassins {pum.assassinsTraining} " +
                    $"godlyCurse {pum.godlyCurse} magicArmor {pum.magicArmor} commander {pum.commander}");
            if (PlayerManager.Instance != null)
                foreach (var king in PlayerManager.Instance.RegisteredPlayers)
                {
                    var hp = king.GetComponent<Hp>();
                    lines.Add($"king hp {(hp != null ? $"{hpValue(hp):0.##}/{hp.maxHp:0.##}" : "-")}");
                }
            foreach (var m in UnityEngine.Object.FindObjectsOfType<ManualAttack>(true).OrderBy(m => m.name))
                lines.Add($"manual attack {m.name} on {(PlayerMovement.instance != null && m.transform.IsChildOf(PlayerMovement.instance.transform) ? "king" : m.transform.root.name)} cooldown {m.cooldownTime:0.####} auto {m.autoAttack}");
            var bs = BlacksmithUpgrades.instance;
            if (bs != null) lines.Add($"blacksmith melee {bs.meleeDamage:0.####} ranged {bs.rangedDamage:0.####} meleeRes {bs.meleeResistance:0.####} rangedRes {bs.rangedResistance:0.####} respawn {bs.unitRespawnSpeedMulti:0.####}");
            foreach (var type in instanceTypes)
                if (AccessTools.Field(type, "instance") is { IsStatic: true } f) lines.Add($"static {type.Name}.instance {(f.GetValue(null) is UnityEngine.Object o && o != null ? o.name : "null")}");
            var cycle = DayNightCycle.Instance;
            if (cycle != null) lines.Add($"daytime objects (not slots or interactors): {string.Join(",", daytimeObjects(cycle).OfType<Component>().Where(c => c != null && !(c is BuildSlot) && !(c is BuildingInteractor)).Select(c => c.GetType().Name).OrderBy(n => n))}");
            foreach (var s in UnityEngine.Object.FindObjectsOfType<BuildSlot>(true).OrderBy(s => s.buildingName).ThenBy(s => s.transform.position.x).ThenBy(s => s.transform.position.z))
            {
                var hp = s.BuildingParent != null ? s.BuildingParent.GetComponent<Hp>() : null;
                lines.Add($"slot {s.buildingName} @{s.transform.position.x:0},{s.transform.position.z:0} active {s.gameObject.activeInHierarchy} activated {slotActivated(s)} level {s.Level} " +
                    $"branches [{string.Join(",", s.AppliedUpgrades)}] income {s.GoldIncome} costs [{string.Join(",", s.OwnUpgrades.Select(u => u.cost))}] " +
                    $"hp {(hp != null ? $"{hpValue(hp):0.#}/{hp.maxHp:0.#}" : "-")}");
                foreach (var a in s.GetComponentsInChildren<AutoAttack>(true))
                    lines.Add($"  attack {a.name} active {a.gameObject.activeInHierarchy} cd {a.cooldownDuration:0.####} recheck {a.recheckTargetInterval:0.####} dmg x{a.DamageMultiplyer:0.####} " +
                        $"range [{string.Join(",", a.targetPriorities.Select(t => t.range.ToString("0.##")))}]" + (a is AutoAttackTower t ? $" projectiles {t.simultaneousProjectiles}" : ""));
                foreach (var oil in s.GetComponentsInChildren<HotOilTower>(true)) lines.Add($"  oil {oil.name} dmg x{oil.DamageMultiplyer:0.####}");
            }
            if (TagManager.instance != null)
            {
                var units = new List<TaggedObject>();
                TagManager.instance.FindAllTaggedObjectsWithTag(units, TagManager.ETag.PlayerUnit);
                foreach (var u in units.Where(u => u != null).OrderBy(u => u.name).ThenBy(u => u.GetInstanceID()))
                {
                    var p = u.GetComponent<PathfindMovementPlayerunit>();
                    var hp = u.GetComponent<Hp>();
                    lines.Add($"unit {u.name} hp {(hp != null ? $"{hpValue(hp):0.#}/{hp.maxHp:0.#}" : "-")} speed {(p != null ? p.movementSpeed.ToString("0.###") : "-")} hold {p?.HoldPosition} " +
                        $"dmg [{string.Join(",", u.GetComponents<AutoAttack>().Select(a => a.DamageMultiplyer.ToString("0.###")))}]");
                }
            }
            return string.Join("\n", lines);
        }

        private static IEnumerable<BuildSlot> Slots() =>
            UnityEngine.Object.FindObjectsOfType<BuildSlot>(false).Where(s => !s.ActivatorUpgradesThis);

        private static BuildSlot FindSlot(string name, bool buyable)
        {
            var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
            return Slots()
                .Where(s => s.buildingName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && (!buyable || s.CanBeUpgraded))
                .OrderBy(s => Vector3.Distance(s.transform.position, player.transform.position))
                .FirstOrDefault() ?? throw new InvalidOperationException($"no {(buyable ? "upgradable " : "")}building matching '{name}'");
        }

        private static string Describe(BuildSlot s)
        {
            var hp = s.BuildingParent != null ? s.BuildingParent.GetComponent<Hp>() : null;
            int units = s.GetComponentsInChildren<UnitRespawnerForBuildings>(true).SelectMany(r => r.units).Count(u => u != null && u.gameObject.activeInHierarchy);
            var mesh = s.MainMesh != null && s.MainMesh.sharedMesh != null ? s.MainMesh.sharedMesh.name : "-";
            return $"{s.buildingName} @{s.transform.position:F0}: level {s.Level}, branches [{string.Join(",", s.AppliedUpgrades)}], income {s.GoldIncome}, " +
                $"hp {(hp != null ? $"{hpValue(hp):0}/{hp.maxHp:0}" : "-")}, units {units}, mesh {mesh}, " +
                $"next cost {(s.CanBeUpgraded ? s.NextUpgradeOrBuildCost.ToString() : "-")}, harvested {(s.Interactor != null && harvestedToday(s.Interactor))}, " +
                $"gold {PlayerInteraction.instance?.Balance}" +
                string.Concat(s.GetComponentsInChildren<AutoAttack>(false).Select(a =>
                    $"\n  {a.GetType().Name} on {a.name}: cooldown {a.cooldownDuration:0.###}, dmg x{a.DamageMultiplyer:0.###}, weapon {(a.weapon != null ? a.weapon.name : "-")}, " +
                    $"range [{string.Join(",", a.targetPriorities.Select(t => t.range.ToString("0.#")))}]" +
                    (a is AutoAttackTower t ? $", projectiles {t.simultaneousProjectiles}" : ""))) +
                string.Concat(s.GetComponentsInChildren<UnitRespawnerForBuildings>(true).SelectMany(r => r.units).Where(u => u != null && u.gameObject.activeInHierarchy)
                    .Select(u => $"\n  unit {u.name} @{u.transform.position:F0} hp {hpValue(u):0}/{u.maxHp:0}"));
        }

        private static System.Collections.IEnumerator Hold(BuildingInteractor interactor, PlayerInteraction player)
        {
            interactor.Focus(player);
            interactor.InteractionBegin(player);
            float end = Time.time + 8f;
            int level = interactor.targetBuilding.Level;
            while (Time.time < end && interactor.targetBuilding.Level == level && !interactor.IsWaitingForChoice)
            {
                interactor.InteractionHold(player);
                yield return null;
            }
            interactor.InteractionEnd(player);
            Log.For<UndoUpgradeManager>().Info($"hold: {interactor.targetBuilding.buildingName} level {level} -> {interactor.targetBuilding.Level}, waiting for choice {interactor.IsWaitingForChoice}, gold {player.Balance}");
        }

        private static string Buy(string[] args)
        {
            var player = PlayerInteraction.instance ?? throw new InvalidOperationException("not in a level");
            if (args.Length == 0)
                return string.Join("\n", Slots().Where(s => s.CanBeUpgraded)
                    .OrderBy(s => Vector3.Distance(s.transform.position, player.transform.position))
                    .Take(25)
                    .Select(s => $"{s.buildingName} level {s.Level} cost {s.NextUpgradeOrBuildCost} " +
                        $"branches {s.Upgrades[s.Level].upgradeBranches.Count} dist {Vector3.Distance(s.transform.position, player.transform.position):0}"));
            int branch = -1;
            var nameParts = args.ToList();
            if (nameParts.Count > 1 && int.TryParse(nameParts[^1], out branch)) nameParts.RemoveAt(nameParts.Count - 1);
            else branch = -1;
            var slot = FindSlot(string.Join(" ", nameParts), buyable: true);
            int cost = slot.NextUpgradeOrBuildCost;
            if (player.Balance < cost) throw new InvalidOperationException($"need {cost} gold, have {player.Balance}");
            player.SpendCoins(cost);
            devPaid = cost;
            var upgrade = slot.Upgrades[slot.Level];
            if (branch >= 0 && upgrade.upgradeBranches.Count > 1)
            {
                if (branch >= upgrade.upgradeBranches.Count) throw new ArgumentException($"branch must be 0-{upgrade.upgradeBranches.Count - 1}");
                slot.TryToBuildOrUpgradeAndPay(player, _presentChoice: true);
                // Pick the branch the way the choice screen does; the choice coroutine completes on its next frame.
                ChoiceManager.instance.choiceToReturn = upgrade.upgradeBranches[branch].choiceDetails;
                UIFrameManager.instance.CloseActiveFrame();
                return $"paid {cost}; choosing branch {branch} ({upgrade.upgradeBranches[branch].choiceDetails.name}) on {slot.buildingName}";
            }
            slot.TryToBuildOrUpgradeAndPay(player, _presentChoice: false);
            devPaid = null;
            slot.Interactor?.MarkAsHarvested();   // as BuildComplete does after a real build
            return $"paid {cost}; " + Describe(slot);
        }

        private static string Scan()
        {
            var types = new Dictionary<Type, HashSet<string>>();
            void Add(GameObject go, string where)
            {
                if (go == null) return;
                foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (c == null || !IsGameType(c.GetType())) continue;
                    if (!types.TryGetValue(c.GetType(), out var set)) types[c.GetType()] = set = [];
                    set.Add(where);
                }
            }
            foreach (var slot in UnityEngine.Object.FindObjectsOfType<BuildSlot>(true))
            {
                Add(slot.BuildingParent, $"{slot.buildingName}:build");
                for (int i = 0; i < slot.upgrades.Count; i++)
                    for (int b = 0; b < slot.upgrades[i].upgradeBranches.Count; b++)
                        foreach (var go in slot.upgrades[i].upgradeBranches[b].objectsToActivate)
                            Add(go, $"{slot.buildingName}:{i}.{b}");
            }
            var text = string.Join("\n", types.OrderBy(kv => kv.Key.Name).Select(kv =>
                $"{kv.Key.Name} [{Unsafe(kv.Key) ?? "ok"}] {string.Join(" ", kv.Value.Take(6))}{(kv.Value.Count > 6 ? $" +{kv.Value.Count - 6}" : "")}"));
            Log.For<UndoUpgradeManager>().Info("undo-scan\n" + text);
            return text;
        }
    }
}
