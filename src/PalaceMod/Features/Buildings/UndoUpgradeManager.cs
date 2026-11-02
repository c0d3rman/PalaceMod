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
        public override string FeatureDescription => "Undo a misclicked build, upgrade or branch choice during the same day: press the undo key near the building to get the gold back.";

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
        private static readonly FieldInfo towerUpgradeTower = AccessTools.Field(typeof(TowerUpgrade), "autoAttackTower");
        private static readonly FieldInfo upgradeUnitsUnits = AccessTools.Field(typeof(UpgradeUnitsOnEnable), "units");

        // ---- What can be undone -----------------------------------------------------------------------------------

        // Newly activated components of these types change state outside what undo restores (player, global upgrade
        // managers, other buildings, waves, the upgrade definitions themselves) or start multi-day processes.
        private static readonly HashSet<string> unsafeTypes =
        [
            "BlacksmithUpgrade", "RoyalForgeUpgrade", "TempleUpgrade", "EmpireUpgrade", "FishingHarbourUpgrade",
            "Mill_ImprovedPlow", "Mill_ImprovementWindSpirits", "Mill_ImprovementExplosiveTrap", "Mill_Improvement_Scarecrows",
            "OutpostPerk", "SummoningCircle", "DestroyOrDisableOnEnable", "EnableObjects", "UpgradeAssassinsTraining",
            "UpgradeBuildersGuild", "UpgradeCastleUp", "UpgradeCommander", "UpgradeGodlyCurse", "UpgradeMagicArmor",
            "UpgradePlayerDmg", "UpgradePlayerHp",
        ];

        // Components with an OnEnable and no OnDisable whose OnEnable only touches state undo restores (or nothing).
        // Any other such type found in a newly activated part makes the action not undoable, so a game update that
        // adds a new one fails safe.
        private static readonly HashSet<string> safeOnEnableTypes =
        [
            "BuildSlot", "TowerUpgrade", "UpgradeUnitsOnEnable", "LastStandPerk", "BuildingDestructionHandler",
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
            public readonly List<LastStandPerk> Registered = [];

            public bool Contains(BuildSlot s) => Slots.Any(x => x.Slot == s);
        }

        private sealed class Window
        {
            public UndoAction Action;
            public readonly List<ObjectState> States = [];
            public readonly List<TransformState> Transforms = [];
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
                "Takes back your last build, upgrade or branch choice today and refunds its gold. Stand near the building.");
            hintShown = plugin.Config.Bind(FeatureSection, "Hint shown", false,
                new ConfigDescription("Whether the one-time undo hint has been shown.", null, new SettingTags { Hidden = true }));
            RegisterDevCommands();
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
            if (undoKey.Down) Plugin.Logger.LogInfo("Undo upgrade: " + TryUndo(requireNear: true));
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

            var objects = ScopeObjects(action);
            foreach (var go in objects)
            {
                action.Transforms[go.transform] = new TransformState(go.transform);
                foreach (var c in go.GetComponents<MonoBehaviour>())
                    if (c != null && Capturable(c)) action.States[c] = (go, go.activeInHierarchy, ObjectState.Capture(c));
            }
            pending = action;
            OpenWindow(action, initial: true);
        }

        private static HashSet<GameObject> ScopeObjects(UndoAction action)
        {
            var objects = new HashSet<GameObject>();
            void AddTree(GameObject go)
            {
                if (go == null) return;
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) objects.Add(t.gameObject);
            }
            foreach (var st in action.Slots)
            {
                var s = st.Slot;
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
            }
            else
            {
                foreach (var kv in action.States)
                    if (kv.Key != null && kv.Key.gameObject.activeInHierarchy) w.States.Add(ObjectState.Capture(kv.Key));
                foreach (var t in action.Transforms.Keys)
                    if (t != null && t.gameObject.activeInHierarchy) w.Transforms.Add(new TransformState(t));
            }
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
        }

        private static void MarkUnrevertible(UndoAction action, string reason)
        {
            if (action.Unrevertible == null) action.Unrevertible = reason;
        }

        /// <summary>Some building changed outside a recorded action: nothing before this point can be undone safely.</summary>
        private static void Barrier(BuildSlot slot, string what)
        {
            if (stack.Count == 0 || stack[^1].Unrevertible != null) return;
            Plugin.Logger.LogInfo($"Undo upgrade: {BuildingName(slot)} {what} on its own; earlier actions can no longer be undone");
            stack.Clear();
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
            foreach (var go in action.NewObjects)
                foreach (var c in go.GetComponents<MonoBehaviour>())
                {
                    if (c == null) continue;
                    if (Unsafe(c.GetType()) is { } reason) MarkUnrevertible(action, $"its {reason} effect can't be taken back");
                    else if (c is TowerUpgrade tu && towerUpgradeTower.GetValue(tu) is Component tower && action.NewObjects.Contains(tower.gameObject))
                        MarkUnrevertible(action, "it upgrades a tower built in the same step");
                    else if (c is UpgradeUnitsOnEnable uu && upgradeUnitsUnits.GetValue(uu) is Hp[] hps && hps.Any(h => h != null && action.NewObjects.Contains(h.gameObject)))
                        MarkUnrevertible(action, "it upgrades units recruited in the same step");
                    else if (c is LastStandPerk lsp) action.Registered.Add(lsp);
                }

            action.SettleFrame = Time.frameCount + SettleFrames;
            stack.Add(action);
            Plugin.Logger.LogInfo($"Undo upgrade: recorded \"{action.Label}\" (cost {action.Cost}, {action.Changes.Count} field changes, " +
                $"{action.NewObjects.Count} objects switched on){(action.Unrevertible != null ? $"; not undoable: {action.Unrevertible}" : "")}");
        }

        // A few frames later: Start() has run on new objects and self-destroying effects are gone.
        private void Settle(UndoAction action)
        {
            action.Settled = true;
            // Destroyed since capture. Inside newly activated parts that's a first-activation effect: a destroyed
            // object would be destroyed again, and a component that removed itself (TowerUpgrade) is recreated on undo
            // so its OnEnable runs again. Anything that existed and was active before must survive.
            foreach (var kv in action.States)
            {
                if (kv.Key != null) continue;
                var (go, wasActive, state) = kv.Value;
                if (wasActive) MarkUnrevertible(action, "it removed something that already existed");
                else if (go != null) action.Recreate.Add((go, state.Target.GetType(), state));
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
                stack.Clear();
                stack.Add(action);
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
            if (stack.Count == 0) return Say("Nothing to undo today.", NotifyKind.Info);
            var action = stack[^1];
            if (action.Unrevertible != null) return Say($"Can't undo {Lower(action.Label)}: {action.Unrevertible}.", NotifyKind.Warning);
            if (!action.Settled) return "settling";
            var root = action.Root;
            if (root == null) { stack.Clear(); return Say("Nothing to undo today.", NotifyKind.Info); }
            var player = PlayerInteraction.instance;
            if (player == null) return "no player";
            if (requireNear && Vector3.Distance(Flat(player.transform.position), Flat(ClosestPoint(root, player.transform.position))) > NearDistance)
                return Say($"Walk back to the {action.Name} to undo {Lower(action.Label)}.", NotifyKind.Info);
            if (action.Slots.Any(s => s.Slot == null) || root.Level != action.LevelAfter)
            {
                stack.Clear();
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

        private static void Restore(UndoAction action, PlayerInteraction player)
        {
            // Units recruited by this action that the player has picked up.
            var command = CommandUnits.instance;
            if (command != null)
                foreach (var go in action.NewObjects)
                {
                    var p = go != null ? go.GetComponent<PathfindMovementPlayerunit>() : null;
                    if (p != null && command.PlayerUnitsCommanding.Remove(p)) command.OnUnitRemove(p);
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

            var cycle = DayNightCycle.Instance;
            if (cycle != null)
                foreach (var perk in action.Registered)
                    if (perk != null) daytimeObjects(cycle).Remove(perk);   // registered in OnEnable; switching on again re-registers

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

            if (action.Cost > 0)
            {
                // Like AddCoin, but a refund isn't new income, so networth (the end-of-run economy stat) stays put.
                balance(player) += action.Cost;
                player.onBalanceGain.Invoke(action.Cost);
            }
            Plugin.Logger.LogInfo($"Undo upgrade: restored {action.Label}: level {action.LevelAfter} -> {action.LevelBefore}, " +
                $"{action.Changes.Count} fields, {action.NewObjects.Count} objects off, {action.Recreate.Count} recreated, +{action.Cost} gold" +
                string.Concat(action.Changes.Values.Take(12).Select(c => "\n  " + c.Describe())));
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
                if (player == null || !Recording || !__instance.CanBeUpgraded || pending != null || window != null) return;
                if (ChoiceManager.instance != null && ChoiceManager.instance.ChoiceCoroutineRunning) return;
                try
                {
                    Begin(__instance, player);
                    __state = true;
                }
                catch (Exception e)
                {
                    pending = null;
                    window = null;
                    Plugin.Logger.LogError($"Undo upgrade: capture failed: {e}");
                }
            }

            private static void Postfix(bool __state)
            {
                if (!__state) return;
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
                        if (waiting.Changes.Count > 0) stack.Clear();
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
                        Plugin.Logger.LogError($"Undo upgrade: capture failed: {e}");
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
                if (action != null && !action.Contains(__instance)) MarkUnrevertible(action, $"it changed the price of the {BuildingName(__instance)}");
            }
        }

        // ---- Dev commands -----------------------------------------------------------------------------------------

        private void RegisterDevCommands()
        {
            DevCommands.Register("undo", "undo                   undo the last build/upgrade today (ignores distance)", _ => TryUndo(requireNear: false));
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
                $"gold {PlayerInteraction.instance?.Balance}";
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
            Plugin.Logger.LogInfo("undo-scan\n" + text);
            return text;
        }
    }
}
