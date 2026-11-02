using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

/*
    Adds a minigun to the loadout screen's weapons.

    The minigun is a runtime EquippableWeapon added to PerkManager.allEquippables, so the loadout screen, tooltips,
    the randomizer and the in-match loadout view pick it up like any native weapon. In a level it reuses the king's
    Long Bow rig (see MinigunGun): the bow's WeaponEquipper is pointed at the minigun before its Start, so the game
    equips it through its normal path.

    Saves stay vanilla-safe:
    - Mid-match saves store the loadout by display name. When the minigun is equipped we write the Long Bow's name
      *and* the minigun's; vanilla skips the unknown name and loads the bow, and with the mod on we drop the bow again.
    - Minigun victories aren't added to the level's "beaten with" records, so they don't count toward vanilla's
      "beat it with/without X" quests (and vanilla's save would write a name it can't resolve).
    - Eternal Trials and the mini-modes never offer it, because their run saves can't fall back.
    Turning the feature off removes it from the list, swaps an equipped minigun for the Long Bow, and reverts a live
    minigun back to the bow mid-level.
*/
namespace PalaceMod
{
    public class MinigunManager : BaseManager
    {
        public override string FeatureSection => "Minigun";
        public override string FeatureDescription => "Adds a minigun to the loadout's weapons: it spins up, sprays fast weak bullets and slows you while firing. Using it counts as a modded run.";

        internal const string DisplayName = "PalaceMod Minigun";
        private const string BowDisplayName = "Long Bow";
        private const string NameKey = "Equippable/" + DisplayName;
        private const string DescriptionKey = NameKey + " Description";
        private const string Title = "Minigun";
        private const string Description =
            "PASSIVE: Spins up while there's something to shoot, then sprays fast, weak and slightly inaccurate bullets. " +
            "You move slower while it's spinning.\n\n" +
            "ACTIVE: Overdrive. Instantly spins up and doubles the fire rate for a few seconds, with no slowdown.";

        private static ConfigEntry<float> fireRate, damagePercent, spinUp, accuracy, moveSpeed, volume;

        internal static float FireRate => fireRate?.Value ?? 15f;
        internal static float DamagePerBullet => (damagePercent?.Value ?? 12f) / 100f;
        internal static float SpinUpTime => spinUp?.Value ?? 1.5f;
        internal static float Accuracy => (accuracy?.Value ?? 80f) / 100f;
        internal static float MoveSpeedWhileFiring => (moveSpeed?.Value ?? 60f) / 100f;
        internal static float ModelScale = 1.4f;
        internal static float Volume => (volume?.Value ?? 50f) / 100f;

        private static EquippableWeapon minigun;
        private static bool hintShown;

        internal static EquippableWeapon Equippable => minigun;
        private static bool IsMinigun(Equippable e) => minigun != null && e == minigun;

        public override void Awake(Plugin plugin)
        {
            base.Awake(plugin);
            var config = plugin.Config;
            fireRate = config.Bind(FeatureSection, "Fire rate", 15f, new ConfigDescription(
                "Shots per second once fully spun up.", new AcceptableValueRange<float>(5f, 30f), new SettingTags { Step = 1f, Unit = "/s" }));
            damagePercent = config.Bind(FeatureSection, "Bullet damage", 12f, new ConfigDescription(
                "Damage of each bullet, as a percentage of a Long Bow arrow.", new AcceptableValueRange<float>(5f, 50f), new SettingTags { Step = 1f, Unit = "%" }));
            spinUp = config.Bind(FeatureSection, "Spin-up time", 1.5f, new ConfigDescription(
                "Seconds of firing it takes to reach full speed.", new AcceptableValueRange<float>(0.25f, 4f), new SettingTags { Step = 0.25f, Unit = " s" }));
            accuracy = config.Bind(FeatureSection, "Accuracy", 75f, new ConfigDescription(
                "Chance that a bullet hits its target; the rest spray past it.", new AcceptableValueRange<float>(30f, 100f), new SettingTags { Step = 5f, Unit = "%" }));
            moveSpeed = config.Bind(FeatureSection, "Move speed while firing", 60f, new ConfigDescription(
                "Your movement speed while the barrels are spinning.", new AcceptableValueRange<float>(30f, 100f), new SettingTags { Step = 5f, Unit = "%" }));
            volume = config.Bind(FeatureSection, "Volume", 50f, new ConfigDescription(
                "Loudness of the minigun's shots.", new AcceptableValueRange<float>(0f, 100f), new SettingTags { Step = 5f, Unit = "%", ZeroLabel = "Off" }));
            damagePercent.SettingChanged += (_, _) => MinigunGun.Active?.ApplyDamage();

            DevCommands.Register("minigun", "minigun equip|bow|status|loadout [scene]|pick [bow]|play|goto [dist]|overdrive|zoom <x>|scale <x>|dump|icons <dir>   equip the minigun or bow (in the menu, before 'level'), show the live gun, dump weapon rigs", DevCommand);
        }

        public override void Update()
        {
            var pm = PerkManager.instance;
            if (pm == null) return;
            if (minigun == null) Create();
            if (!pm.allEquippables.Contains(minigun))
            {
                // After the last native weapon, so it sits at the end of the weapon row.
                int index = pm.allEquippables.FindLastIndex(e => e is EquippableWeapon) + 1;
                pm.allEquippables.Insert(index, minigun);
                Log.For<MinigunManager>().Debug($"Added to the loadout at index {index}");
            }
        }

        protected override void OnFeatureDisabled()
        {
            var pm = PerkManager.instance;
            if (pm == null || minigun == null) return;
            pm.allEquippables.Remove(minigun);
            if (PerkManager.IsEquipped(minigun))
            {
                PerkManager.SetEquipped(minigun, false);
                var bow = FindBow();
                if (bow != null && !pm.CurrentlyEquipped.Any(e => e is EquippableWeapon)) PerkManager.SetEquipped(bow, true);
            }
            MinigunGun.Active?.Revert();
        }

        private static void Create()
        {
            minigun = ScriptableObject.CreateInstance<EquippableWeapon>();
            minigun.name = "PalaceMod_Minigun";
            minigun.displayName = DisplayName;
            minigun.description = Description;
            // CampaignProgress + DefaultUnlocked = always unlocked, sorting value 0 (MetaLevel would look it up in
            // PerkManager.metaLevelByPerk and throw).
            minigun.unlockRequirement = global::Equippable.UnlockRequirement.CampaignProgress;
            minigun.requiredBeatenLevel = global::Equippable.LevelName.DefaultUnlocked;
            minigun.icon = MinigunIcon.Create();
            minigun.hideFlags = HideFlags.DontUnloadUnusedAsset;
        }

        private static EquippableWeapon FindBow()
        {
            var weapons = PerkManager.instance?.allEquippables.OfType<EquippableWeapon>().Where(e => e != minigun).ToList();
            if (weapons == null || weapons.Count == 0) return null;
            return weapons.FirstOrDefault(e => e.displayName == BowDisplayName) ?? weapons[0];
        }

        private static bool IsBowRig(WeaponEquipper equipper)
        {
            if (equipper.activeWeapon == null || equipper.passiveWeapon == null) return false;
            var audio = equipper.activeWeapon.GetComponent<PlayerWeaponAudio>();
            if (audio != null) return audio.weaponType == PlayerWeaponAudio.WeaponType.Bow;
            return equipper.requiredWeapon != null && equipper.requiredWeapon.displayName == BowDisplayName;
        }

        // ---- In-level: turn the bow rig into the minigun ----

        [HarmonyPatch(typeof(WeaponEquipper), "Start")]
        private static class WeaponEquipper_Start_Patch
        {
            private static void Prefix(WeaponEquipper __instance)
            {
                if (!PerkManager.IsEquipped(minigun) || !IsBowRig(__instance)) return;
                try
                {
                    __instance.requiredWeapon = minigun;
                    var gun = MinigunGun.Install(__instance);
                    GameplayImpact.Mark("Minigun");
                    Log.For<MinigunManager>().Info($"Equipped ({gun.Describe()})");
                    if (!hintShown)
                    {
                        hintShown = true;
                        Notify.Show("Minigun: it spins up while firing and slows you down. Your ability triggers Overdrive.", NotifyKind.Info, 6f, "minigun.hint");
                    }
                }
                catch (Exception e)
                {
                    Log.For<MinigunManager>().Error($"Couldn't set up the minigun", e);
                }
            }
        }

        // Every ManualAttack.Attack implementation (the bow's stab overrides it), so both of the rig's attacks route here.
        [HarmonyPatch]
        private static class ManualAttack_Attack_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                AccessTools.GetTypesFromAssembly(typeof(ManualAttack).Assembly)
                    .Where(t => typeof(ManualAttack).IsAssignableFrom(t))
                    .Select(t => t.GetMethod("Attack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null))
                    .Where(m => m != null);

            private static bool Prefix(ManualAttack __instance)
            {
                var gun = MinigunGun.Active;
                if (gun == null || !gun.Owns(__instance)) return true;
                if (__instance.autoAttack) gun.PassiveAttack();
                else gun.ActiveAttack();
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerMovement), nameof(PlayerMovement.MoveScript))]
        private static class PlayerMovement_MoveScript_Patch
        {
            private static void Prefix(ref Vector2 inputVector)
            {
                var gun = MinigunGun.Active;
                if (gun != null) inputVector *= gun.MoveFactor;
            }
        }

        // ---- Names ----

        [HarmonyPatch(typeof(TextTranslator), nameof(TextTranslator.Translate))]
        private static class TextTranslator_Translate_Patch
        {
            private static bool Prefix(string locaKey, ref string __result)
            {
                if (locaKey == NameKey) { __result = Title; return false; }
                if (locaKey == DescriptionKey) { __result = Description; return false; }
                return true;
            }
        }

        // ---- Keep it out of modes whose saves can't fall back ----

        // Classic loadout screens list it; Eternal Trials' screen doesn't.
        [HarmonyPatch(typeof(LoadoutUIHelper), nameof(LoadoutUIHelper.OnShow))]
        private static class LoadoutUIHelper_OnShow_Patch
        {
            private static void Prefix(LoadoutUIHelper __instance, out int __state)
            {
                __state = -1;
                if (__instance.mode == LocalGamestate.GameMode.Classic || minigun == null) return;
                __state = PerkManager.instance.allEquippables.IndexOf(minigun);
                if (__state >= 0) PerkManager.instance.allEquippables.RemoveAt(__state);
            }

            private static void Finalizer(int __state)
            {
                if (__state >= 0 && !PerkManager.instance.allEquippables.Contains(minigun)) PerkManager.instance.allEquippables.Insert(__state, minigun);
            }
        }

        [HarmonyPatch(typeof(MapChoice), nameof(MapChoice.GenerateNewMapChoice))]
        private static class MapChoice_Generate_Patch
        {
            private static void Prefix(out int __state)
            {
                __state = minigun == null ? -1 : PerkManager.instance.allEquippables.IndexOf(minigun);
                if (__state >= 0) PerkManager.instance.allEquippables.RemoveAt(__state);
            }

            private static void Finalizer(int __state)
            {
                if (__state >= 0 && !PerkManager.instance.allEquippables.Contains(minigun)) PerkManager.instance.allEquippables.Insert(__state, minigun);
            }
        }

        // ---- Saves ----

        [HarmonyPatch(typeof(MatchSave), nameof(MatchSave.ConvertObjectDataToStrings))]
        private static class MatchSave_Save_Patch
        {
            private static void Postfix(MatchSave __instance)
            {
                var names = Traverse.Create(__instance).Field<List<string>>("currentLoadoutAsString").Value;
                if (names == null || !names.Contains(DisplayName)) return;
                var bow = FindBow();
                if (bow != null && !names.Contains(bow.displayName)) names.Insert(names.IndexOf(DisplayName), bow.displayName);
            }
        }

        [HarmonyPatch(typeof(MatchSave), nameof(MatchSave.ConvertStringsToObjectData))]
        private static class MatchSave_Load_Patch
        {
            private static void Postfix(MatchSave __instance)
            {
                if (!__instance.currentLoadout.Contains(minigun)) return;
                __instance.currentLoadout.RemoveAll(e => e is EquippableWeapon && e != minigun);
                __instance.ApplyLoadout();
            }
        }

        [HarmonyPatch(typeof(LevelData), nameof(LevelData.SaveScoreAndStatsToBestIfBest))]
        private static class LevelData_Beaten_Patch
        {
            // A minigun victory isn't a vanilla victory, so it doesn't count toward "beat it with/without X" quests
            // (and vanilla's save can't resolve the minigun's name anyway).
            private static void Postfix(LevelData __instance)
            {
                if (minigun == null) return;
                __instance.levelHasBeenBeatenWith.RemoveAll(loadout => loadout.Contains(minigun));
            }
        }

        // ---- Dev ----

        private string DevCommand(string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            var pm = PerkManager.instance ?? throw new InvalidOperationException("PerkManager isn't loaded yet");
            switch (sub)
            {
                case "equip":
                case "bow":
                    foreach (var w in pm.CurrentlyEquipped.OfType<EquippableWeapon>().ToList()) PerkManager.SetEquipped(w, false);
                    var pick = sub == "equip" ? minigun : FindBow();
                    if (pick == null) throw new InvalidOperationException("minigun isn't registered (is the feature on?)");
                    PerkManager.SetEquipped(pick, true);
                    return $"equipped {pick.displayName}; loadout: {string.Join(", ", pm.CurrentlyEquipped.Select(e => e?.displayName ?? "null"))}";
                case "status":
                    return $"registered={pm.allEquippables.Contains(minigun)} equipped={PerkManager.IsEquipped(minigun)} " +
                           $"weapons=[{string.Join(", ", pm.allEquippables.OfType<EquippableWeapon>().Select(e => e.displayName))}] " +
                           $"gun={(MinigunGun.Active != null ? MinigunGun.Active.Describe() : "none")}";
                case "goto":
                    // Teleport the king next to the nearest living enemy, so tests don't wait for enemies to walk in.
                    var king = PlayerMovement.instance ?? throw new InvalidOperationException("not in a level");
                    float distance = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 12f;
                    var enemy = UnityEngine.Object.FindObjectsOfType<Hp>()
                        .Where(h => h.HpValue > 0f && h.TaggedObj != null && h.TaggedObj.Tags.Contains(TagManager.ETag.EnemyOwned))
                        .OrderBy(h => (h.transform.position - king.transform.position).sqrMagnitude).FirstOrDefault()
                        ?? throw new InvalidOperationException("no living enemy");
                    var away = king.transform.position - enemy.transform.position;
                    away.y = 0f;
                    var spot = enemy.transform.position + (away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward) * distance;
                    king.TeleportTo(spot);
                    return $"teleported to {spot} near {enemy.name}";
                case "loadout":
                    // From the level select: open a level's loadout screen, as walking up to its flag does.
                    string scene = args.Length > 1 ? args[1] : "Nordfels";
                    var interactor = UnityEngine.Object.FindObjectsOfType<LevelInteractor>().FirstOrDefault(l => l.levelInfo != null && l.levelInfo.sceneName == scene)
                        ?? throw new InvalidOperationException($"no level interactor for {scene} (are you in _LevelSelect?)");
                    interactor.InteractionBegin(null);
                    return $"opened the loadout for {scene}";
                case "pick":
                    // Pick the minigun (or "bow") on the open loadout screen through the screen's own select+apply path.
                    var helper = UnityEngine.Object.FindObjectsOfType<LoadoutUIHelper>().FirstOrDefault(h => h.weaponsParent != null && h.weaponsParent.gameObject.activeInHierarchy)
                        ?? throw new InvalidOperationException("no loadout screen open");
                    var want = args.Length > 1 && args[1] == "bow" ? FindBow() : minigun;
                    var button = helper.weaponsParent.GetComponentsInChildren<TFUIEquippable>().FirstOrDefault(b => b.Data == want)
                        ?? throw new InvalidOperationException($"{want?.displayName} isn't on the loadout screen");
                    helper.frame.Select(button);
                    helper.frame.Apply();
                    return $"picked {want.displayName}; loadout: {string.Join(", ", pm.CurrentlyEquipped.Select(e => e?.displayName ?? "null"))}";
                case "play":
                    var screen = UnityEngine.Object.FindObjectsOfType<LoadoutUIHelper>().FirstOrDefault(h => h.playButton != null && h.playButton.gameObject.activeInHierarchy)
                        ?? throw new InvalidOperationException("no loadout screen open");
                    screen.frame.Select(screen.playButton);
                    screen.frame.Apply();
                    return "play";
                case "zoom":
                    // Camera zoom as a multiple of the default view (0 restores it), for close-up screenshots.
                    var rig = UnityEngine.Object.FindObjectOfType<CameraRig>() ?? throw new InvalidOperationException("no camera rig");
                    float zoom = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
                    rig.overrideZoom = zoom > 0f ? zoom : -1f;
                    return $"zoom {zoom}";
                case "overdrive":
                    (MinigunGun.Active ?? throw new InvalidOperationException("no live minigun")).ActiveAttack();
                    return "overdrive";
                case "scale":
                    ModelScale = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
                    MinigunGun.Active?.SetModelScale(ModelScale);
                    return $"scale {ModelScale}";
                case "icons":
                    string dir = args.Length > 1 ? args[1] : throw new ArgumentException("usage: minigun icons <dir>");
                    foreach (var e in pm.allEquippables.OfType<EquippableWeapon>())
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, e.name + ".png"), ReadSprite(e.icon).EncodeToPNG());
                    return "ok";
                case "dump":
                    var sb = new StringBuilder();
                    foreach (var we in Resources.FindObjectsOfTypeAll<WeaponEquipper>())
                        sb.AppendLine($"EQ {we.name} req={we.requiredWeapon?.displayName} active={we.activeWeapon?.GetType().Name} passive={we.passiveWeapon?.GetType().Name}");
                    foreach (var ma in Resources.FindObjectsOfTypeAll<ManualAttack>())
                        sb.AppendLine($"MA {ma.GetType().Name} {ma.name} scene={ma.gameObject.scene.name} auto={ma.autoAttack} cd={ma.cooldownTime} w={ma.weapon?.name} " +
                                      $"dmg=[{string.Join(",", ma.weapon?.directDamage?.Select(d => $"{string.Join("+", d.requiredTags)}:{d.damageAdded}x{d.damageMultiplyer}") ?? new string[0])}] range={ma.targetPriorities.FirstOrDefault()?.range}");
                    return sb.ToString();
                default:
                    throw new ArgumentException("usage: minigun equip|bow|status|dump|icons <dir>");
            }
        }

        private static Texture2D ReadSprite(Sprite s)
        {
            var rt = RenderTexture.GetTemporary(s.texture.width, s.texture.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(s.texture, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var r = s.textureRect;
            var t = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return t;
        }
    }
}
