using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;
using UnityEngine;

/*
    The `attacks` component (docs/builder-engine.md): an automatic attack like the game's AutoAttack, but generic.
    One BuilderAttack per entry, on the unit's root (next to its Hp, so a knocked-out building's attacks stop too).

    Each cooldown it picks a target (its own `targeting`, else the unit's targeting override, else "foe"), within
    `range` and beyond `minRange` of the unit, and then: `launch` (a Builder projectile through Bodies' Launcher),
    or `weapon` ("vanilla:<weapon>" or "inherit": the unit's own weapon, with the game's damage, arrows and
    difficulty scaling), then raises `attack` (other = the target; text = the attack id) and runs `do`.
    `replace: true` switches the unit's vanilla attacks off. The base attack's onAttackTriggered still fires, so the
    attack animation and turn-to-target play.

    `Range` and `Cooldown` are public so stat modifiers (attackRange, attackCooldown) can scale them.
*/
namespace PalaceMod.Builder.Logic.Bodies.Steering
{
    public sealed class BuilderAttack : MonoBehaviour
    {
        public string Id;
        public float Cooldown = 1, Jitter, Range = 10, MinRange;
        public string When = "night";
        internal LogicEntity Entity;
        internal List<TargetRule> Targeting;
        internal string LaunchEntity;
        internal Vector3 LaunchFrom = new(0, 1, 0);
        internal JObject LaunchOverrides;
        internal Weapon Weapon;
        internal ActionList Do;
        internal string Source;
        private float cooldown = -1;
        private AutoAttack baseAttack;
        private TaggedObject tagged;
        private Hp hp;
        private PathfindMovement movement;
        private readonly List<TaggedObject> picks = [];

        private static readonly AccessTools.FieldRef<AutoAttack, Vector3> lastTarget = AccessTools.FieldRefAccess<AutoAttack, Vector3>("lastTargetPosition");

        public static readonly string[] Whens = ["night", "always", "day"];

        internal static void AddAll(LogicEntity e, GameObject go, JArray list, string owner)
        {
            for (int i = 0; i < list.Count; i++)
            {
                string path = $"{owner}.attacks[{i}]";
                if (list[i] is not JObject o) { SteerLog.Once(path, $"{path}: an attack must be an object"); continue; }
                try { Add(e, go, o, path); }
                catch (LogicError ex) { SteerLog.Once(path, $"{path}: {ex.Message}"); }
            }
        }

        internal static BuilderAttack Add(LogicEntity e, GameObject go, JObject o, string path)
        {
            var baseAttacks = go.GetComponents<AutoAttack>();
            var first = baseAttacks.FirstOrDefault();
            var a = go.AddComponent<BuilderAttack>();
            a.Entity = e;
            a.Source = path;
            a.Id = (string)o["id"] ?? $"attack{go.GetComponents<BuilderAttack>().Length - 1}";
            a.Cooldown = (float?)o["cooldown"] ?? first?.cooldownDuration ?? 1f;
            a.Jitter = Mathf.Clamp01((float?)o["jitter"] ?? 0);
            a.Range = (float?)o["range"] ?? (first != null ? LogicSteeringManager.AttackRange(first) : 10f);
            a.MinRange = (float?)o["minRange"] ?? 0;
            a.When = (string)o["when"] ?? "night";
            if (!Whens.Contains(a.When)) { Destroy(a); throw new LogicError($"when \"{a.When}\": expected night, always or day"); }
            if (o["targeting"] is { Type: not JTokenType.Null } t) a.Targeting = TargetRule.ParseList(t);
            if (o["launch"] is JObject launch)
            {
                a.LaunchEntity = (string)launch["entity"] ?? throw new LogicError("launch needs an entity");
                if (launch["from"] is JArray f && f.Count == 3) a.LaunchFrom = new Vector3((float)f[0], (float)f[1], (float)f[2]);
                var over = (JObject)launch.DeepClone();
                over.Remove("entity"); over.Remove("from");
                a.LaunchOverrides = over.Count > 0 ? over : null;
            }
            else if (o["weapon"] is { Type: JTokenType.String } w)
            {
                string ws = (string)w;
                if (ws == "inherit") a.Weapon = first?.weapon ?? throw new LogicError("weapon \"inherit\": the base unit has no attack");
                else
                {
                    var r = ContentRef.Parse(ws);
                    a.Weapon = (r.Kind == RefKind.Vanilla ? BuilderServices.Catalog?.FindWeapon(r.Id)?.Asset : null)
                        ?? throw new LogicError($"weapon \"{ws}\": no such vanilla weapon");
                }
            }
            if (o["do"] is JArray d) a.Do = SteerExpr.Actions(d, path + ".do");
            if ((bool?)o["replace"] == true)
                foreach (var b in baseAttacks) LogicSteeringManager.Suppress(b);
            return a;
        }

        private void Start()
        {
            baseAttack = GetComponent<AutoAttack>();
            tagged = GetComponent<TaggedObject>();
            hp = GetComponent<Hp>();
            movement = GetComponent<PathfindMovement>();
            cooldown = baseAttack != null ? Mathf.Max(baseAttack.cooldownAfterSpawn, 0) : 0;
        }

        private void Update()
        {
            var world = LogicWorld.Current;
            if (world == null || Entity == null) return;
            var cycle = DayNightCycle.Instance;
            bool day = cycle == null || cycle.CurrentTimestate == DayNightCycle.Timestate.Day;
            if ((When == "night" && day) || (When == "day" && !day)) return;
            if (hp != null && hp.KnockedOut) return;
            if (movement != null && !movement.enabled) return;   // knocked back, or the game froze it
            if (StatusFlags.Has(gameObject, "stunned") || StatusFlags.Has(gameObject, "disarmed")) return;
            cooldown -= Time.deltaTime;
            if (cooldown > 0) return;

            var st = LogicSteeringManager.Get(gameObject);
            // the attack's own rules, else the unit's override (then "foe" where it defers to the game), else "foe"
            var who = st ?? Self();
            if (Targeting != null) TargetChooser.Choose(who, transform.position, Range, MinRange, 1, picks, Targeting);
            else if (st?.Targeting == null || !TargetChooser.Choose(st, transform.position, Range, MinRange, 1, picks))
                TargetChooser.Choose(who, transform.position, Range, MinRange, 1, picks, DefaultRules);
            if (picks.Count == 0) { cooldown += 0.5f; return; }
            cooldown += Cooldown * (1f + (1f - 2f * Random.value) * Jitter);
            Fire(picks[0]);
        }

        private static readonly List<TargetRule> DefaultRules = [new TargetRule { Filter = "foe" }];

        private SteerState self;
        private SteerState Self() => self ??= new SteerState { Entity = Entity, Go = gameObject };

        private void Fire(TaggedObject target)
        {
            var targetEntity = LogicGame.EntityOf(target.gameObject);
            var origin = transform.position + transform.rotation * LaunchFrom;
            if (LaunchEntity != null)
            {
                var (_, error) = Launcher.Launch(new LaunchRequest { Template = LaunchEntity, Owner = Entity, From = origin, Target = targetEntity, Overrides = LaunchOverrides });
                if (error != null) SteerLog.Once(Source + " launch", $"{Source}: launch {LaunchEntity}: {error}");
            }
            else if (Weapon != null)
            {
                float dmg = baseAttack != null ? baseAttack.DamageMultiplyer : 1f, speed = baseAttack != null ? baseAttack.ProjectileSpeedMultiplyer : 1f;
                Weapon.Attack(origin, target.Hp, Vector3.zero, tagged, dmg, speed);
            }
            if (baseAttack != null)
            {
                lastTarget(baseAttack) = target.transform.position;
                baseAttack.onCooldown = true;
                baseAttack.onAttackTriggered.Invoke();
            }
            LogicEvents.Raise("attack", Entity, targetEntity, 0, transform.position.ToVec(), Id);
            if (Do != null)
            {
                var ctx = new LogicContext { World = LogicWorld.Current, Self = Entity, Subject = Entity, Other = targetEntity, RuleLabel = Source + ".do",
                    Event = new LogicEvent { Name = "attack", Subject = Entity, Other = targetEntity, Text = Id } };
                SteerExpr.Run(Do, ctx);
            }
        }
    }
}
