using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.Rendering;

namespace FS2Hercules
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        // KR-67 Ifrit: closest stock airframe (twin engine, 19 m, heavy multirole). Its physics,
        // colliders, cockpit, gear and networking are reused; only the look and stats change.
        const string BaseKey = "Multirole1";
        const string Key = "FS2_GTF_Hercules";

        // Fixed-wing atmospheric-refit propulsion. The Hercules has four genuinely independent engine cores, one
        // for each physical exhaust mouth. Total installed thrust is intentionally unchanged from the previous
        // two-core/four-outlet build; dividing it across four cores changes failure granularity, not performance.
        const float HerculesDryThrustPerEngine = 35400f;   // N; 4 x 35.4 = 141.6 kN total dry
        const float HerculesWetThrustPerEngine = 63600f;   // N; 4 x 63.6 = 254.4 kN total with reheat
        const float HerculesGLimit = 6.5f;
        const float HerculesUltimateStructuralG = HerculesGLimit * 1.5f; // normal-airframe ultimate load margin
        const float HerculesRollRate = 120f * Mathf.Deg2Rad; // faster fixed-wing roll response: 3.0 s per complete roll
        const float HerculesRollTightnessMultiplier = 1.35f; // strengthen FBW roll-rate tracking without touching yaw
        const float HerculesAlphaLimiter = 22f;        // low-AR lifting body still produces useful lift into the low-20s;
        // enough transient margin to recover from a steep bank without turning the aircraft into a high-alpha fighter
        const float HerculesAlphaLimiterStrength = 0.3f; // stock default 0.05 (max 50% cut); fully zeroes by +7 deg over
        const float HerculesMaxBankAngle = 65f; // canon "poor maneuverability": no sustained flight past 90 deg of
        // bank, where the wing's lift vector stops opposing gravity at all (past 180 deg it reverses and adds to
        // it) -- confirmed live as the AI banking to 180 deg and falling out of the sky. 65 deg keeps the load
        // factor needed to hold a turn (1/cos(65 deg) = 2.37 g) well inside HerculesGLimit with margin, unlike the
        // stock AI combat call's unconditional 180 deg (full aerobatic freedom, tuned for a nimble fighter).
        const float DurabilityRatio = 850f / 665f;  // $Hitpoints + $Shields
        const float AccelRatio = 3.15f / 3.0f;      // $Forward accel (time to max speed), peers / Herc

        // Line the Herc's POF eye point (0, 1.19, 6.04) up with the Ifrit pilot's head (0, 1.10, 6.30).
        static readonly Vector3 VisualOffset = new Vector3(0f, 1.10f - 1.19f, 6.30f - 6.04f);

        // Centres of the Herc's four nozzle mouths in aircraft space (VisualOffset included), just inside where the
        // hull's nozzle openings end (z -7.55), and their width. Each position now has its own independent
        // Turbojet + JetNozzle core rather than sharing one controller with its sibling on the same pod.
        static readonly Vector3[] NozzleExits =
        {
            new Vector3(-3.905f, 1.655f, -7.45f), new Vector3(-3.905f, -1.59f, -7.45f),
            new Vector3(3.905f, 1.655f, -7.45f), new Vector3(3.905f, -1.59f, -7.45f),
        };
        const float NozzleWidth = 1.5f;

        // The Herc's engine pods and ventral fin hang 7.5 m below the aircraft origin; the Ifrit's gear holds it
        // only 2.3 m up. The gear keeps its physics but touches down lower, so the Herc rests this high with about
        // 1.1 m under the fin (room for suspension compression and takeoff rotation); the mains move out under the
        // pods so the taller stance stays stable.
        const float RestHeight = 8.6f;
        const float PadFriction = 0.3f;
        // Conventional turbojet fuel model retained from the atmospheric-refit tuning.
        const float MainDryTsfc = 0.80f * 0.4536f / 4.448f / 3600f;
        const float MainWetTsfc = 2.20f * 0.4536f / 4.448f / 3600f;
        // FS2 primary banks (POF gun points, aircraft space). Lasers: 450 m/s, 2 s lifetime, energy weapons.
        static readonly Vector3[] SubachMuzzles =
        {
            new Vector3(4.83f, 0.62f, 4.15f), new Vector3(-4.85f, 0.64f, 4.15f),
            new Vector3(4.83f, -0.67f, 3.59f), new Vector3(-4.85f, -0.67f, 3.59f),
        };
        static readonly Vector3[] PrometheusMuzzles = { new Vector3(0.48f, -2.05f, 3.96f), new Vector3(-0.47f, -2.03f, 3.96f) };
        // Missile launch apertures are the symmetric features immediately beside the cockpit on the Hercules mesh.
        // Mesh inspection puts their outer edge at x ~= +/-1.12 and z ~= 4.9; place the invisible launch mounts just
        // outside the skin so missiles emerge cleanly from the left/right cockpit shoulders. Missile store renderers
        // are hidden at spawn time; the weapons and ammunition still exist and fire normally from these positions.
        static readonly Vector3 MissileLaunchRight = new Vector3(2.68f, -1.85f, 4.92f);
        static readonly Vector3 MissileLaunchLeft  = new Vector3(-2.68f, -1.85f, 4.92f);
        // Refit external pylons under the pod floors (y -3.5): inner and outer stations either side of each pod.
        static readonly Vector3 InnerPylonRight = new Vector3(3.0f, -3.6f, -0.6f), OuterPylonRight = new Vector3(5.0f, -3.6f, -0.6f);
        const float LaserSpeed = 650f, LaserLifetime = 5f;
        const int LaserAmmo = 2400;
        // Energy weapons: shots come from the power plant, not a magazine, so the charge counter has no mass. The
        // bank's gun, capacitor and cooling hardware is a refit estimate carried as the mount's empty mass.
        const float SubachBankMass = 4 * 45f, PrometheusBankMass = 2 * 70f;
        // Non-stealth: comparable to the stock non-stealth A-19 Brawler (0.6) of similar planform size; the Hercules
        // has twice its frontal area and flat pod walls, so it is not reduced below that.
        const float HerculesRadarSize = 0.6f;
        const int HerculesCountermeasures = 25;
        // FS2 hull damage per bolt ($Damage x $Armor Factor) and the Subach bank's hull DPS (4 guns, 0.2 s).
        const float SubachHull = 15f * 0.9f, PrometheusHull = 18f * 1.1f, SubachBankHullDps = 4f * SubachHull / 0.2f;

        const string Description =
            "A veteran Terran heavy-assault design adapted for atmospheric operations. The Hercules trades speed and agility for heavy armor and concentrated firepower; its pilots must plan turns early and respect the airframe's high mass.\n\n" +
            "Atmospheric refit (not a FreeSpace 2 variant) - Heavy Assault - Han-Ronald Corp. 20.3 m, two primary banks with 6 guns, two missile banks. Conventional fixed-wing configuration with four independent turbojet cores arranged as upper/lower pairs on the two engine pods. Each physical engine has its own RPM, fuel draw, afterburner, thrust point and damage/failure state. Subsonic: the slab-sided hull limits it to high subsonic speed. 6.5 g limit. Maneuverability: Poor. Armor: Heavy.";

        static ManualLogSource Log;
        static string assetDir;
        static AircraftDefinition herc;
        static WeaponMount[] lasers;

        void Awake()
        {
            Log = Logger;
            assetDir = Path.Combine(Path.GetDirectoryName(Info.Location), "assets");
            Harmony.CreateAndPatchAll(typeof(Plugin));
        }

        // Prefix so the Hercules and its lasers are in the lists before AfterLoad builds Lookup/IndexLookup,
        // and before NetworkManagerNuclearOption registers the aircraft prefabs.
        [HarmonyPrefix, HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[0])]
        static void AddHercules(Encyclopedia __instance)
        {
            if (__instance.aircraft.Any(a => a != null && a.jsonKey == Key)) return;
            if (!GeneratedAssetsPresent()) return;
            try
            {
                herc ??= Build(__instance);
                __instance.aircraft.Add(herc);
                __instance.weaponMounts.AddRange(lasers);
            }
            catch (Exception e)
            {
                Log.LogError("Could not add GTF Hercules: " + e);
            }
        }

        static bool GeneratedAssetsPresent()
        {
            string[] required =
            {
                "hercules.nomesh",
                "HercPBR.png",
                "HercPBR-normal.png",
                "HercPBR-glow.png"
            };
            var missing = required
                .Where(file => !File.Exists(Path.Combine(assetDir, file)))
                .ToArray();
            if (missing.Length == 0) return true;

            Log.LogError(
                "GTF Hercules was not loaded because generated assets are missing: " +
                string.Join(", ", missing) +
                ". Double-click Extract-FS2Hercules.cmd and select a user-owned FreeSpace 2 installation.");
            return false;
        }

        // Loadout stores spawn after HideRenderers has run. Keep their gameplay objects, ammunition, colliders and
        // launcher logic, but Hercules missile stores are internal and must not be drawn externally. Their spawned
        // launcher object is moved to the cockpit-side aperture on the corresponding side, so the projectile origin
        // follows the Hercules model rather than the inherited Ifrit rack position.
        [HarmonyPostfix, HarmonyPatch(typeof(Hardpoint), nameof(Hardpoint.SpawnMount))]
        static void ConfigureHerculesStore(Hardpoint __instance, Aircraft aircraft, WeaponMount weaponMount, GameObject __result)
        {
            if (__result == null || aircraft?.definition?.jsonKey != Key) return;

            // Stores may overlap Hercules part hitboxes because several inherited hardpoints are now internal.
            var storeColliders = __result.GetComponentsInChildren<Collider>(true);
            if (storeColliders.Length > 0)
                foreach (var part in aircraft.partLookup)
                    if (part != null)
                        foreach (var hull in part.GetComponents<Collider>())
                            foreach (var store in storeColliders)
                                Physics.IgnoreCollision(hull, store);

            if (!IsMissileMount(weaponMount)) return;

            var root = aircraft.transform;
            float x = __instance?.transform != null ? root.InverseTransformPoint(__instance.transform.position).x : 0f;
            // Centreline/unknown inherited mounts alternate deterministically by hardpoint index.
            if (Mathf.Abs(x) < 0.05f) x = (__instance != null && (__instance.HardpointIndex & 1) != 0) ? -1f : 1f;
            var launch = x < 0f ? MissileLaunchLeft : MissileLaunchRight;
            __result.transform.position = root.TransformPoint(launch);

            foreach (var r in __result.GetComponentsInChildren<Renderer>(true))
                r.forceRenderingOff = true;
        }

        static bool IsMissileMount(WeaponMount mount)
        {
            if (mount == null || mount.tailHook) return false;
            string text = ((mount.name ?? "") + " " + (mount.info?.name ?? "")).ToLowerInvariant();
            if (text.Contains("missile") || text.Contains("aam") || text.Contains("agm") || text.Contains("irm") || text.Contains("arm"))
                return true;
            // Fallback for modded/renamed mounts: stock missile launchers expose missile-named components.
            return mount.prefab != null && mount.prefab.GetComponentsInChildren<Component>(true)
                .Any(c => c != null && c.GetType().Name.IndexOf("missile", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // A raised Hercules can remain in Retracting when one of the tall, invisible gear animations does not finish.
        // Weapon safety may use the replicated up/down command in that case, while still retaining ground safety.
        [HarmonyPostfix, HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.SafetyIsOn))]
        static void HerculesWeaponSafety(WeaponStation __instance, Aircraft aircraft, ref bool __result)
        {
            if (!__result || aircraft?.definition?.jsonKey != Key || aircraft.gearDeployed) return;
            bool groundSafety = Traverse.Create(__instance).Field("groundSafety").GetValue<bool>();
            __result = groundSafety && aircraft.radarAlt < 0.5f;
        }

        // Hercules AI protection is applied before the stock control filter reshapes the command.
        // In the fixed-wing build there is no powered-lift authority or nozzle scheduler: the stock
        // runway/taxi/takeoff/landing state machine remains in charge, while these safeguards account
        // for the Hercules' deliberately lower pitch/roll authority in combat.
        [HarmonyPrefix, HarmonyPatch(typeof(Aircraft), nameof(Aircraft.FilterInputs))]
        static void ProtectHerculesAi(Aircraft __instance)
        {
            if (__instance.definition?.jsonKey != Key) return;
            ProtectHerculesAiFromCFIT(__instance);
            ProtectHerculesAiFromExcessBank(__instance);
            ProtectHerculesAiFromTargetCollision(__instance);
        }

        // Ground-proximity protection for the AI. The Hercules has deliberately lower pitch authority
        // than the stock multirole airframe, so begin a pull-up early enough to arrest the current sink
        // rather than relying only on combat-state breakoff timing tuned for a more agile fighter.
        const float CfitReactionMargin = 20f;

        static void ProtectHerculesAiFromCFIT(Aircraft aircraft)
        {
            if (aircraft.Player != null || aircraft.radarAlt > 500f) return;
            var inputs = aircraft.GetInputs();
            float vy = aircraft.rb.velocity.y;
            if (inputs == null || vy >= 0f) return;
            float sink = -vy;
            float horizontalSpeed = new Vector2(aircraft.rb.velocity.x, aircraft.rb.velocity.z).magnitude;
            float pitchRate = Traverse.Create(aircraft.GetControlsFilter().GetFlyByWire()).Field("maxPitchAngularVel").GetValue<float>();
            float flightPathAngle = Mathf.Atan2(sink, Mathf.Max(horizontalSpeed, 1f));
            float recoverTime = flightPathAngle / Mathf.Max(pitchRate, 0.05f);
            float altitudeNeeded = 0.5f * sink * recoverTime + CfitReactionMargin;
            // ControlInputs.pitch is positive nose-down in wingborne flight; pull-up is therefore -1.
            if (aircraft.radarAlt < altitudeNeeded) inputs.pitch = -1f;
        }

        // Fixed-wing availability follows conventional hangar compatibility instead of VTOL capability.
        // The Hercules is cloned from the Ifrit, so a hangar that already offers the Ifrit has the runway/
        // fixed-wing infrastructure expected by this airframe. Deliberately do not inject it merely because
        // an airbase exposes verticalLandingPoints; that was what made the VTOL build appear on naval pads.
        [HarmonyPrefix, HarmonyPatch(typeof(Hangar), nameof(Hangar.GetAvailableAircraft))]
        static void OfferHerculesInList(Hangar __instance) => AddHerculesToConventionalHangar(__instance);

        [HarmonyPrefix, HarmonyPatch(typeof(Hangar), nameof(Hangar.CanSpawnAircraft))]
        static void OfferHerculesForSpawn(Hangar __instance) => AddHerculesToConventionalHangar(__instance);

        static void AddHerculesToConventionalHangar(Hangar hangar)
        {
            if (herc == null || hangar == null) return;
            var field = Traverse.Create(hangar).Field("availableAircraft");
            var list = field.GetValue<AircraftDefinition[]>();
            if (list == null || Array.IndexOf(list, herc) >= 0) return;

            // Compatibility is inherited from the stock fixed-wing base aircraft only. No VTOL-airbase fallback.
            bool hangarHasIfrit = list.Any(a => a != null && a.jsonKey == BaseKey);
            if (!hangarHasIfrit) return;
            field.SetValue(list.Append(herc).ToArray());
        }

        // Fixed-wing AI intentionally uses the stock taxi -> runway -> takeoff and runway-landing
        // states. The old direct-to-takeoff/vertical-departure overrides are removed.

        // AI combat states deliberately turn off the stock terrain/collision safety net during an attack run (e.g.
        // AIPilotCombatModes.UseFixedGuns sets ignoreCollisions=true whenever it is lined up on a target), trusting
        // the aircraft's own pitch authority to break off in time. That break-off timing is a fixed 1-2 second
        // time-to-impact threshold, not scaled to the airframe. Live AI testing confirmed the result: a strafing
        // Hercules pitched to -61 deg and hit the ground at radarAlt 0 with no warning. Rather than reverse-
        // engineer every attack mode's own break-off math, keep this aircraft's one real terrain safety net
        // (AutopilotPlane.AutoAim's raycast-based TerrainWarningSystem, which already scales its own lookahead by
        // 9/aircraftGLimit -- i.e. it is already more conservative for a lower-G aircraft) switched on regardless
        // of what the attack mode asked for. It only ever acts when an actual raycast ahead detects closing
        // terrain, so it has no effect at altitude or in the open air.
        //
        // (A first attempt also clamped the bankAllowed parameter passed to this same call -- stock
        // AIPilotCombatModes always passes 180 deg, full aerobatic freedom -- to stop the AI banking the Hercules
        // past 90 deg, where lift no longer opposes gravity at all. Measured worse: bankAllowed < 180 switches
        // AutoAim onto an entirely different roll-error formula, not just a smaller version of the same one, and
        // two live re-tests with it active both showed more crashes than the one run without it. Reverted; see
        // ProtectHerculesAiFromExcessBank below for the replacement that doesn't touch AutoAim's own control law.)
        [HarmonyPrefix, HarmonyPatch(typeof(AutopilotPlane), nameof(AutopilotPlane.AutoAim))]
        static void KeepHerculesTerrainAvoidanceOn(AutopilotPlane __instance, ref bool ignoreCollisions)
        {
            if (__instance.aircraft?.definition?.jsonKey == Key && __instance.aircraft.Player == null) ignoreCollisions = false;
        }

        // Replacement for the reverted bankAllowed clamp above: instead of changing which formula AutoAim uses to
        // compute its own roll command, this leaves that alone and, like ProtectHerculesAiFromCFIT, overrides the
        // raw roll stick directly -- the same input a player would give -- whenever the aircraft is already banked
        // past HerculesMaxBankAngle, using the same hook point and the same bounded FBW roll law
        // (ControlsFilter.Filter's maxRollAngularVel/rollTightness, already reduced for this airframe in
        // TuneFlightControls) as any other roll command.
        static void ProtectHerculesAiFromExcessBank(Aircraft aircraft)
        {
            if (aircraft.Player != null) return;
            var inputs = aircraft.GetInputs();
            if (inputs == null) return;
            float bank = Mathf.Atan2(-aircraft.transform.right.y, aircraft.transform.up.y) * Mathf.Rad2Deg;
            if (Mathf.Abs(bank) > HerculesMaxBankAngle) inputs.roll = -Mathf.Sign(bank);
        }

        // AIPilotCombatModes.UseFixedGuns (a private nested method/enum, reached only via reflection) breaks off a
        // gun run when the predicted time to closest approach on the target drops below a fixed 1-2 second
        // threshold (1 s head-on, 2 s same-direction or against a target under 30 m/s) -- the same kind of
        // stock-timing assumption that caused the terrain CFITs, just aimed at the target instead of the ground.
        // A clean zero-combat live test (air-started Hercules vs. an unarmed target, so nothing else could have
        // caused it) showed exactly this: impact damage at 800+ m altitude with only 3.1 g recorded, consistent
        // with flying into the target itself. KeepHerculesTerrainAvoidanceOn does not cover this case:
        // TerrainWarningSystem's raycast uses a statics/exclusion-zone layer mask that excludes other aircraft.
        //
        // Rather than override raw stick input again, this reruns the same geometry check UseFixedGuns itself
        // uses (targetVector/targetDist/targetAngle, already computed for this tick by the time FilterInputs
        // runs) and, if it would trigger stock's own break-off, triggers it early -- scaled by the same
        // 9/aircraftGLimit ratio TerrainWarningSystem already uses for a lower-G aircraft's longer lookahead --
        // by calling the real AttackMode.BreakOffAttack transition through reflection, so the aircraft breaks off
        // exactly the way the game already knows how to (climbing toward the nearest airbase), just sooner.
        static readonly Type AttackModeType = AccessTools.Inner(typeof(AIPilotCombatModes), "AttackMode");
        static readonly object BreakOffAttackValue = Enum.Parse(AttackModeType, "BreakOffAttack");
        static readonly MethodInfo SetCombatModeMethod = AccessTools.Method(typeof(AIPilotCombatModes), "SetCombatMode", new[] { AttackModeType });

        static void ProtectHerculesAiFromTargetCollision(Aircraft aircraft)
        {
            if (aircraft.Player != null) return;
            if (!(aircraft.pilots?.FirstOrDefault()?.currentState is AIPilotCombatModes combat)) return;
            var t = Traverse.Create(combat);
            var target = t.Field("currentTarget").GetValue<Unit>();
            if (target == null || target.rb == null) return;
            float targetAngle = t.Field("targetAngle").GetValue<float>();
            if (targetAngle >= 15f) return;
            Vector3 targetVector = t.Field("targetVector").GetValue<Vector3>();
            float targetDist = t.Field("targetDist").GetValue<float>();
            if (targetDist <= 0f) return;
            float closing = Vector3.Dot(-targetVector.normalized, target.rb.velocity - aircraft.rb.velocity);
            float timeToClosestApproach = targetDist / Mathf.Max(closing, 0.1f);
            bool sameDirection = Vector3.Dot(target.transform.forward, aircraft.transform.forward) > 0f;
            float stockThreshold = sameDirection ? 2f : 1f;
            if (target.speed < 30f) stockThreshold = 2f;
            if (timeToClosestApproach >= stockThreshold * (9f / HerculesGLimit)) return;
            t.Field("breakOffTimer").SetValue(target.speed < 30f ? 10f : 2f);
            t.Field("aimTargetVel").SetValue(Vector3.zero);
            t.Field("allowTargetAssessment").SetValue(true);
            SetCombatModeMethod.Invoke(combat, new object[] { BreakOffAttackValue });
        }

        // At full size the Herc is ~20 m tall standing on its hover pads, far above shelter (5.9 m) and hangar (7.7 m)
        // ceilings, so spawning inside crushes it. Hangars place the new aircraft at their spawnTransform: for the Herc
        // that point is moved, for this call only, onto the ground just outside the doors, facing out. The hangar's
        // door-close check measures distance from the restored point, so its door sequence is unchanged.
        const float HangarExitMargin = 8f;

        [HarmonyPrefix, HarmonyPatch(typeof(Hangar), "SpawnAircraft")]
        static void SpawnOutsideHangar(Hangar __instance, AircraftDefinition definition, out Pose? __state)
        {
            __state = null;
            var hangar = Traverse.Create(__instance);
            if (definition != herc || hangar.Field("waitForOpenBeforeSpawn").GetValue<bool>()) return;
            var spawn = hangar.Field("spawnTransform").GetValue<Transform>();
            if (spawn == null) return;

            var doors = hangar.Field("doors").GetValue<HangarDoor[]>()?.Where(d => d?.transform != null).ToArray();
            Vector3 toDoors = doors != null && doors.Length > 0
                ? doors.Aggregate(Vector3.zero, (sum, d) => sum + d.transform.position) / doors.Length - spawn.position
                : spawn.forward;
            toDoors.y = 0f;
            if (toDoors.sqrMagnitude < 1f) toDoors = Vector3.ProjectOnPlane(spawn.forward, Vector3.up) * 10f;
            var exit = spawn.position + toDoors.normalized * (toDoors.magnitude + herc.length / 2f + HangarExitMargin);
            if (!Physics.Raycast(exit + Vector3.up * 60f, Vector3.down, out var ground, 200f, PhysicsLayers.StaticsMask)) return;

            __state = new Pose(spawn.position, spawn.rotation);
            spawn.SetPositionAndRotation(ground.point, Quaternion.LookRotation(toDoors.normalized, Vector3.up));
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(Hangar), "SpawnAircraft")]
        static Exception RestoreHangarSpawnPoint(Hangar __instance, Pose? __state, Exception __exception)
        {
            if (__state == null) return __exception;
            Pose original = (Pose)__state;
            Traverse.Create(__instance).Field("spawnTransform").GetValue<Transform>().SetPositionAndRotation(original.position, original.rotation);
            return __exception;
        }

        static AircraftDefinition Build(Encyclopedia enc)
        {
            var baseDef = enc.aircraft.First(a => a.jsonKey == BaseKey);

            // Inactive, persistent parent: templates never run Awake, but copies made from them do.
            var holder = new GameObject("FS2Hercules_Templates");
            holder.SetActive(false);
            DontDestroyOnLoad(holder);
            var prefab = Instantiate(baseDef.unitPrefab, holder.transform);
            prefab.name = Key;
            var aircraft = prefab.GetComponent<Aircraft>();

            var mounts = enc.weaponMounts.Where(w => w != null).GroupBy(w => w.name).ToDictionary(g => g.Key, g => g.First());
            RemoveTailHookHardpoint(aircraft);
            lasers = AddLasers(mounts["gun_27mm_internal"], holder.transform, aircraft);
            AddSecondPrimaryBank(aircraft, lasers[1]);

            var p = Instantiate(baseDef.aircraftParameters);
            p.name = Key;
            p.aircraftName = "GTF Hercules";
            p.rankRequired = 2;
            p.aircraftGLimit = HerculesGLimit;
            p.verticalLanding = false;
            TuneFlightControls(aircraft);



            var def = Instantiate(baseDef);
            def.name = Key;
            def.jsonKey = Key;
            def.unitName = "GTF Hercules";
            def.bogeyName = "Hercules";
            def.code = "GTF";
            def.description = Description;
            def.value = baseDef.value * 0.8f;
            def.radarSize = HerculesRadarSize;
            def.length = 20.3f;
            def.width = 13.7f;
            def.height = 11.6f;
            def.aircraftParameters = p;
            def.unitPrefab = prefab;

            ((Unit)aircraft).definition = def;
            prefab.GetComponent<NetworkIdentity>().PrefabHash = StableHash(Key);

            // Hercules-aligned parts, colliders, masses, lift/drag areas and fuel tanks before anything that is
            // positioned relative to them (nozzle exits, gear contacts, hardpoints).
            HerculesAirframe.Fit(prefab, p);
            FitStructuralJoints(prefab);

            // Convert the inherited two-engine Ifrit propulsion into four independent Hercules cores before
            // applying engine ratings. The tuning loop below therefore sees four engines at one-quarter total thrust.
            FitEngines(prefab);

            foreach (var jet in prefab.GetComponentsInChildren<Turbojet>(true))
            {
                var t = Traverse.Create(jet);
                float inheritedDry = jet.maxThrust;
                float inheritedWet = jet.GetMaxThrust();

                // Do not derive Hercules thrust from the Ifrit's speed. The Hercules has a separate aerodynamic
                // model, so these are direct engine ratings. Top speed now emerges from the stock turbojet's
                // normal thrust-speed behaviour acting against HerculesAirframe drag rather than a speed-ratio
                // shortcut. Keep the inherited engine maxSpeed curve intact instead of scaling it to an FS2 ratio.
                float newDry = HerculesDryThrustPerEngine;
                float newTotal = HerculesWetThrustPerEngine;
                float newReheat = newTotal - newDry;
                jet.maxThrust = newDry;

                // Fuel flow from thrust-specific fuel consumption: dry flow at full RPM, idle about 7.5% thrust,
                // and reheat flow chosen so total full-afterburner flow matches the wet TSFC.
                t.Field("fuelConsumptionMax").SetValue(MainDryTsfc * newDry);
                t.Field("fuelConsumptionMin").SetValue(MainDryTsfc * 1.3f * 0.075f * newDry);
                float reheatFlow = MainWetTsfc * newTotal - MainDryTsfc * newDry;

                foreach (var nozzle in t.Field("nozzles").GetValue<JetNozzle[]>())
                {
                    var burners = Traverse.Create(nozzle).Field("afterburners").GetValue<Array>();
                    if (burners.Length == 0) continue;
                    float inheritedBurnerTotal = 0f;
                    foreach (var ab in burners)
                        inheritedBurnerTotal += Traverse.Create(ab).Field("thrust").GetValue<float>();

                    foreach (var ab in burners)
                    {
                        var a = Traverse.Create(ab);
                        float oldThrust = a.Field("thrust").GetValue<float>();
                        // Preserve the inherited split between burner entries, while making their sum equal the
                        // explicit per-core Hercules reheat increment. Each of the four cores owns exactly one
                        // physical nozzle, so this burner belongs only to this engine.
                        float share = inheritedBurnerTotal > 0f ? oldThrust / inheritedBurnerTotal : 1f / burners.Length;
                        a.Field("thrust").SetValue(newReheat * share);
                        a.Field("fuelConsumption").SetValue(reheatFlow * share);
                    }
                }

                t.Field("spoolRate").SetValue(t.Field("spoolRate").GetValue<float>() * AccelRatio);
                Log.LogInfo($"{jet.name}: explicit Hercules engine rating dry {inheritedDry:0} -> {newDry:0} N, wet {inheritedWet:0} -> {newTotal:0} N; inherited maxSpeed curve retained; fuel {MainDryTsfc * newDry:0.00} kg/s dry, {MainWetTsfc * newTotal:0.00} kg/s wet");
            }
            FitGearForFixedWing(prefab, def);
            FitWeaponBays(aircraft);
            FitCountermeasures(prefab);
            HerculesIrst.Install(prefab, aircraft, prefab.GetComponentsInChildren<UnitPart>(true).First(part => part.name == "nose"));
            FitLoadouts(p, aircraft, mounts, aircraft.weaponManager.hardpointSets.Length);
            foreach (var part in prefab.GetComponentsInChildren<UnitPart>(true))
            {
                var armor = Traverse.Create(part).Field("armorProperties").GetValue<ArmorProperties>();
                if (armor == null) continue;
                armor.pierceTolerance *= DurabilityRatio;
                armor.blastTolerance *= DurabilityRatio;
                armor.fireTolerance *= DurabilityRatio;
            }

            prefab.AddComponent<HideRenderers>();

            var template = prefab.GetComponentsInChildren<Renderer>(true).Select(r => r.sharedMaterial)
                .First(m => m != null && m.shader.name == "Universal Render Pipeline/Lit" && m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap") != null);
            var visual = new GameObject("hercules_visual") { layer = prefab.layer };
            visual.transform.SetParent(prefab.transform, false);
            visual.transform.localPosition = VisualOffset;
            // Hull only: the POF's thruster01-04 submodels are FS2's see-through engine glow effect, which drawn solid
            // becomes a blue shell sticking 1.7 m out of each nozzle.
            visual.AddComponent<MeshFilter>().sharedMesh = LoadMesh(Path.Combine(assetDir, "hercules.nomesh"), "HercPBR");
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = MakeMaterial(template, "HercPBR.png", "HercPBR-normal.png", "HercPBR-glow.png");

            // Exterior renderers are switched off in cockpit view, which keeps the hull out of the pilot's face.
            var exterior = Traverse.Create(aircraft).Field("exteriorRenderers");
            exterior.SetValue(exterior.GetValue<Renderer[]>().Append(renderer).ToArray());

            Log.LogInfo($"GTF Hercules added: key={Key}, rank {p.rankRequired}, g-limit {p.aircraftGLimit:0.0}, rest height {def.spawnOffset.y:0.0} m");
            return def;
        }


        // The Hercules keeps the Ifrit's part/joint wiring so stock damage, detachment and networking still work,
        // but HerculesAirframe replaces both the part masses and their positions. Inherited breakForce/breakTorque
        // values were therefore calibrated for a different mass distribution and can trip during ordinary maneuver
        // loads. Re-size each Unity joint to carry the mass hanging below it at the Hercules ultimate structural
        // load (1.5 x the 6.5 g operational limit). Existing stronger limits are preserved. This does NOT make the
        // aircraft indestructible: impacts can still exceed these loads, and ordinary weapon/part damage remains
        // untouched.
        static void FitStructuralJoints(GameObject prefab)
        {
            var root = prefab.transform;
            foreach (var joint in prefab.GetComponentsInChildren<Joint>(true))
            {
                var owner = joint.GetComponent<UnitPart>() ?? joint.GetComponentInParent<UnitPart>();
                if (owner == null) continue;

                // Approximate the supported branch by summing UnitParts under this transform. The hierarchy already
                // mirrors the inherited damage/joint tree, making this a better load estimate than owner.mass alone.
                var branch = owner.GetComponentsInChildren<UnitPart>(true);
                float supportedMass = branch.Where(p => p != null).Sum(p => Mathf.Max(0f, p.mass));
                if (supportedMass <= 0f) supportedMass = Mathf.Max(owner.mass, 1f);

                float requiredForce = supportedMass * 9.81f * HerculesUltimateStructuralG;

                // Estimate bending moment from the supported branch COM about this part. Clamp the arm so tiny
                // numerical separations do not create a near-zero torque limit, and so long branches remain bounded.
                Vector3 weighted = Vector3.zero;
                float massSum = 0f;
                foreach (var part in branch)
                {
                    if (part == null || part.mass <= 0f) continue;
                    weighted += root.InverseTransformPoint(part.transform.position) * part.mass;
                    massSum += part.mass;
                }
                Vector3 branchCom = massSum > 0f ? weighted / massSum : root.InverseTransformPoint(owner.transform.position);
                float arm = Mathf.Clamp(Vector3.Distance(branchCom, root.InverseTransformPoint(owner.transform.position)), 1f, 10f);
                float requiredTorque = requiredForce * arm;

                float oldForce = joint.breakForce;
                float oldTorque = joint.breakTorque;
                if (float.IsInfinity(oldForce) || float.IsInfinity(oldTorque)) continue;
                joint.breakForce = Mathf.Max(oldForce, requiredForce);
                joint.breakTorque = Mathf.Max(oldTorque, requiredTorque);
                Log.LogInfo($"{owner.name}: structural joint ultimate {HerculesUltimateStructuralG:0.00} g, branch {supportedMass:0} kg, breakForce {oldForce:0} -> {joint.breakForce:0} N, breakTorque {oldTorque:0} -> {joint.breakTorque:0} Nm");
            }
        }

        static void TuneFlightControls(Aircraft aircraft)
        {
            var flyByWire = Traverse.Create(aircraft.GetControlsFilter().GetFlyByWire());
            float pitchRate = flyByWire.Field("maxPitchAngularVel").GetValue<float>();
            flyByWire.Field("gLimitPositive").SetValue(HerculesGLimit);
            const float HerculesPitchRateRatio = 0.85f;
            flyByWire.Field("maxPitchAngularVel").SetValue(pitchRate * HerculesPitchRateRatio);
            flyByWire.Field("maxRollAngularVel").SetValue(HerculesRollRate);
            float stockRollTightness = flyByWire.Field("rollTightness").GetValue<float>();
            flyByWire.Field("rollTightness").SetValue(stockRollTightness * HerculesRollTightnessMultiplier);
            // Recovery tuning for the fixed-wing Hercules. G limit and pitch-rate limit are deliberately independent:
            // structural load factor says how much normal acceleration the aircraft may command, while pitch rate says
            // how quickly it can rotate to establish/reduce AoA. The earlier 6.5/9 coupling left too little nose-rate
            // authority during a steep-bank recovery. 0.85x stock keeps the heavy response while preserving usable
            // transient recovery authority. The low-aspect-ratio body lift curve remains approximately linear through
            // 25 deg in HerculesAirframe.BodyLift, so a 22 deg AoA limiter is inside the modeled pre-break region.
            flyByWire.Field("alphaLimiter").SetValue(HerculesAlphaLimiter);
            flyByWire.Field("alphaLimiterStrength").SetValue(HerculesAlphaLimiterStrength);
            Log.LogInfo($"Hercules fly-by-wire: {HerculesGLimit:0.0} g, pitch-rate x{HerculesPitchRateRatio:0.00}, roll-rate {HerculesRollRate * Mathf.Rad2Deg:0} deg/s, roll-tightness x{HerculesRollTightnessMultiplier:0.00}, alpha limiter {HerculesAlphaLimiter:0} deg @ strength {HerculesAlphaLimiterStrength:0.00}");
        }

        // Fixed-wing build: no DuctedThrustSystem, hover controller, nozzle-vectoring HUD,
        // or lift-jet flight-control layer is installed. The inherited turbojets/nozzles provide
        // conventional forward thrust and afterburner through the stock aircraft path.

        // Four independent engines. Nuclear Option's Turbojet already owns exactly the state we need: RPM,
        // condition, operable/failure state, fuel flow, afterburner drive and registration in Aircraft.engineStates.
        // The stock Ifrit contributes two Turbojets. For each side we retain the stock core/nozzle for the upper
        // Hercules engine and clone only serialized configuration into a new inactive lower-core object. Because the
        // whole Hercules template lives under an inactive holder, Awake does not run until all references are valid.
        //
        // Damage ownership is independent as well: the two already-separate inherited damage parts on each pod are
        // assigned one core each (engine_L/R for the upper core, nozzle_L/R for the lower core). Their proven
        // structural geometry is left untouched so this engine conversion cannot change aircraft inertia/handling.
        // A hit can therefore degrade or kill one core without automatically killing its sibling; catastrophic pod
        // detachment can still take both with it.
        static void FitEngines(GameObject prefab)
        {
            var root = prefab.transform;
            var memberwiseClone = AccessTools.Method(typeof(object), "MemberwiseClone");
            var stockJets = prefab.GetComponentsInChildren<Turbojet>(true).ToArray();
            if (stockJets.Length != 2)
                throw new InvalidOperationException($"Expected exactly two inherited Ifrit Turbojets before Hercules four-engine conversion, found {stockJets.Length}.");

            foreach (var upperJet in stockJets)
            {
                var upperJetConfig = Traverse.Create(upperJet);
                var inheritedNozzles = upperJetConfig.Field("nozzles").GetValue<JetNozzle[]>();
                if (inheritedNozzles == null || inheritedNozzles.Length != 1)
                    throw new InvalidOperationException($"{upperJet.name}: expected one inherited JetNozzle, found {inheritedNozzles?.Length ?? 0}.");

                var upperNozzle = inheritedNozzles[0];
                var upperNozzleConfig = Traverse.Create(upperNozzle);
                var inheritedThrustTransform = upperNozzleConfig.Field("thrustTransform").GetValue<Transform>();
                float side = Mathf.Sign(root.InverseTransformPoint(inheritedThrustTransform.position).x);
                if (side == 0f) throw new InvalidOperationException($"{upperJet.name}: cannot determine engine side from inherited nozzle.");

                string suffix = side < 0f ? "L" : "R";
                string sideName = side < 0f ? "LEFT" : "RIGHT";
                var exits = NozzleExits.Where(e => Mathf.Sign(e.x) == side).OrderByDescending(e => e.y).ToArray();
                if (exits.Length != 2) throw new InvalidOperationException($"{sideName}: expected upper/lower Hercules nozzle exits.");

                var upperPart = prefab.GetComponentsInChildren<UnitPart>(true).FirstOrDefault(p => p.name == $"engine_{suffix}");
                var lowerPart = prefab.GetComponentsInChildren<UnitPart>(true).FirstOrDefault(p => p.name == $"nozzle_{suffix}");
                if (upperPart == null || lowerPart == null)
                    throw new InvalidOperationException($"{sideName}: missing engine/nozzle UnitPart damage zones.");

                // Make a second Turbojet + JetNozzle while the object is explicitly inactive so Awake cannot observe
                // partially populated private fields. Only serialized/public configuration is copied; runtime state
                // (RPM, condition, operable, events, aircraft pointer, fuel timers) starts clean and independent.
                var lowerCoreObject = new GameObject($"herc_{sideName.ToLowerInvariant()}_lower_engine_core");
                lowerCoreObject.SetActive(false);
                lowerCoreObject.transform.SetParent(root, false);
                lowerCoreObject.transform.SetPositionAndRotation(root.TransformPoint(exits[1]), root.rotation);

                var lowerJet = lowerCoreObject.AddComponent<Turbojet>();
                CopySerializedConfiguration(upperJet, lowerJet);
                var lowerNozzle = lowerCoreObject.AddComponent<JetNozzle>();
                CopySerializedConfiguration(upperNozzle, lowerNozzle);

                // Clone the burner's serializable object before modifying the upper nozzle. The lower engine gets its
                // own burner state and its own flame/glow renderers. Burner audio may remain shared by the pod; audio
                // aggregation is cosmetic and does not couple engine physics or failures.
                var upperBurners = upperNozzleConfig.Field("afterburners").GetValue<Array>();
                if (upperBurners == null || upperBurners.Length < 1)
                    throw new InvalidOperationException($"{upperJet.name}: inherited nozzle has no afterburner entry.");
                var upperBurner = upperBurners.GetValue(0);
                var lowerBurner = memberwiseClone.Invoke(upperBurner, null);
                var upperBurnerConfig = Traverse.Create(upperBurner);

                var upperFlame = upperBurnerConfig.Field("flameRenderer").GetValue<Renderer>();
                var upperGlow = upperBurnerConfig.Field("nozzleGlowRenderer").GetValue<Renderer>();
                var lowerFlame = Duplicate(upperFlame);
                var lowerGlow = Duplicate(upperGlow);
                float glowWidth = Width(upperGlow);
                float scale = NozzleWidth / glowWidth;

                // Each physical engine gets its own force transform at the real upper/lower nozzle mouth.
                var upperThrust = NewEngineThrustTransform(root, $"herc_{sideName.ToLowerInvariant()}_upper_thrust", exits[0]);
                var lowerThrust = NewEngineThrustTransform(root, $"herc_{sideName.ToLowerInvariant()}_lower_thrust", exits[1]);

                // Preserve the aircraft's original IR signature despite doubling engine count: each new core carries
                // half of the inherited side-nozzle baseline and burner IR. Thrust/fuel are set later by the common
                // four-core tuning loop.
                float inheritedIRMin = upperNozzleConfig.Field("IRMin").GetValue<float>();
                float inheritedIRMax = upperNozzleConfig.Field("IRMax").GetValue<float>();
                float inheritedPriority = upperNozzleConfig.Field("thrustProportion").GetValue<float>();
                float inheritedBurnerIR = upperBurnerConfig.Field("IRIntensity").GetValue<float>();

                ConfigureIndependentNozzle(upperNozzle, upperJet, upperPart, upperThrust, upperBurner,
                    upperFlame, upperGlow, exits[0], scale, inheritedIRMin, inheritedIRMax, inheritedPriority, inheritedBurnerIR);
                ConfigureIndependentNozzle(lowerNozzle, lowerJet, lowerPart, lowerThrust, lowerBurner,
                    lowerFlame, lowerGlow, exits[1], scale, inheritedIRMin, inheritedIRMax, inheritedPriority, inheritedBurnerIR);

                upperJetConfig.Field("criticalParts").SetValue(new[] { upperPart });
                upperJetConfig.Field("nozzles").SetValue(new[] { upperNozzle });
                upperJetConfig.Field("failureMessage").SetValue($"{sideName} UPPER ENGINE FAILURE");

                var lowerJetConfig = Traverse.Create(lowerJet);
                lowerJetConfig.Field("criticalParts").SetValue(new[] { lowerPart });
                lowerJetConfig.Field("nozzles").SetValue(new[] { lowerNozzle });
                lowerJetConfig.Field("failureMessage").SetValue($"{sideName} LOWER ENGINE FAILURE");

                lowerCoreObject.SetActive(true); // parent template remains inactive; Awake still waits for spawn.
                Log.LogInfo($"{sideName}: split inherited engine into independent UPPER ({upperPart.name}) and LOWER ({lowerPart.name}) cores at {exits[0]} / {exits[1]}.");
            }

            int engineCount = prefab.GetComponentsInChildren<Turbojet>(true).Length;
            int nozzleCount = prefab.GetComponentsInChildren<JetNozzle>(true).Length;
            if (engineCount != 4 || nozzleCount != 4)
                throw new InvalidOperationException($"Hercules four-engine invariant failed: {engineCount} Turbojets, {nozzleCount} JetNozzles.");
            Log.LogInfo("Hercules propulsion: four independent Turbojet cores installed; total thrust is preserved by per-core ratings.");
        }

        static Transform NewEngineThrustTransform(Transform root, string name, Vector3 aircraftPoint)
        {
            var thrust = new GameObject(name).transform;
            thrust.SetParent(root, false);
            thrust.SetPositionAndRotation(root.TransformPoint(aircraftPoint), root.rotation);
            return thrust;
        }

        static void ConfigureIndependentNozzle(JetNozzle nozzle, Turbojet jet, UnitPart part, Transform thrust,
            object burner, Renderer flame, Renderer glow, Vector3 exit, float scale,
            float inheritedIRMin, float inheritedIRMax, float inheritedPriority, float inheritedBurnerIR)
        {
            var n = Traverse.Create(nozzle);
            n.Field("part").SetValue(part);
            n.Field("turbojet").SetValue(jet);
            // Avoid a second generic IReportDamage subscription; this nozzle's failure ownership is the assigned
            // Turbojet, which already listens to its dedicated critical UnitPart.
            n.Field("engine").SetValue(null);
            n.Field("thrustTransform").SetValue(thrust);
            n.Field("IRMin").SetValue(inheritedIRMin * 0.5f);
            n.Field("IRMax").SetValue(inheritedIRMax * 0.5f);
            n.Field("thrustProportion").SetValue(Mathf.Max(inheritedPriority * 0.5f, 0.001f));

            var burners = Array.CreateInstance(burner.GetType(), 1);
            burners.SetValue(burner, 0);
            n.Field("afterburners").SetValue(burners);

            var a = Traverse.Create(burner);
            a.Field("flameRenderer").SetValue(flame);
            a.Field("nozzleGlowRenderer").SetValue(glow);
            a.Field("IRIntensity").SetValue(inheritedBurnerIR * 0.5f);
            // thrustDirection is not used by the current JetNozzle force calculation, but point it at the correct
            // physical outlet anyway so future game changes do not silently inherit the old Ifrit location.
            a.Field("thrustDirection").SetValue(thrust);
            Mount(flame.transform, thrust.parent, exit, scale);
            Mount(glow.transform, thrust.parent, exit, scale);
        }

        // Copy only configuration Unity would serialize. Deliberately do not copy private runtime fields or event
        // delegates: each cloned engine must begin with independent operability, RPM, condition and subscriptions.
        static void CopySerializedConfiguration(Component source, Component destination)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in source.GetType().GetFields(flags))
            {
                if (field.IsStatic || field.IsInitOnly) continue;
                bool serialized = field.IsPublic || field.GetCustomAttributes(typeof(SerializeField), true).Length > 0;
                if (!serialized) continue;
                field.SetValue(destination, field.GetValue(source));
            }
        }

        // Moves each gear ground ray origin onto the Hercules contact geometry while keeping the stock
        // Ifrit landing-gear mechanism, steering, suspension and runway state machine. The tall stance is
        // required for ventral-fin clearance, so braking friction stays conservative to avoid nose-over.
        static void FitGearForFixedWing(GameObject prefab, AircraftDefinition def)
        {
            var root = prefab.transform;
            foreach (var gear in prefab.GetComponentsInChildren<LandingGear>(true))
            {
                var g = Traverse.Create(gear);
                var cast = g.Field("castPoint").GetValue<Transform>();
                float side = root.InverseTransformPoint(cast.position).x;
                var contact = Mathf.Abs(side) < 0.5f ? HerculesAirframe.NoseGearContact
                    : Vector3.Scale(HerculesAirframe.MainGearContactR, new Vector3(Mathf.Sign(side), 1f, 1f));
                cast.position = root.TransformPoint(contact);
                // Keep a reachable overload limit so a hard landing can still collapse the gear.
                g.Field("maxCompression").SetValue(g.Field("suspensionTravel").GetValue<float>() * 0.95f);
                // The centre of mass is ~8.7 m above the contacts. A high-grip tyre setup can nose-over this
                // geometry under braking, so retain the conservative 0.3 coefficient while restoring stock
                // differential braking/steering behavior for runway taxi.
                g.Field("frictionCoef").SetValue(PadFriction);
                g.Field("contactArea").SetValue(Mathf.Abs(side) < 0.5f ? 0.8f : 1.2f);
            }
            def.spawnOffset = new Vector3(def.spawnOffset.x, RestHeight, def.spawnOffset.z);
        }

        // The Ifrit's missile bays/racks do not match the Hercules mesh. Dedicated missile banks and side bays are
        // moved to the two cockpit-side launch apertures. The spawned missile stores are hidden by ConfigureHerculesStore,
        // so only the projectile emerging from the correct left/right shoulder is visible. Bomb/rocket/refit pylons remain
        // under the engine pods because those are genuinely external stores.
        static void FitWeaponBays(Aircraft aircraft)
        {
            var root = aircraft.transform;
            var parts = aircraft.GetComponentsInChildren<UnitPart>(true).ToDictionary(p => p.name);
            foreach (var set in aircraft.weaponManager.hardpointSets)
                foreach (var hardpoint in set.hardpoints)
                {
                    var hp = hardpoint.transform;
                    float side = Mathf.Sign(root.InverseTransformPoint(hp.position).x);
                    var mirror = new Vector3(side, 1f, 1f);
                    Vector3? target = set.name == "Forward Weapon Bay" ? MissileLaunchRight
                        : set.name == "Rear Weapon Bay" ? MissileLaunchLeft
                        : set.name == "Side Weapon Bays" ? (side < 0 ? MissileLaunchLeft : MissileLaunchRight)
                        : set.name == "Inner Wing Pylons" ? Vector3.Scale(InnerPylonRight, mirror)
                        : set.name == "Outer Wing Pylons" ? Vector3.Scale(OuterPylonRight, mirror)
                        : (Vector3?)null;
                    if (!target.HasValue) continue;

                    bool cockpitMissileBank = set.name == "Forward Weapon Bay" || set.name == "Rear Weapon Bay" || set.name == "Side Weapon Bays";
                    var part = cockpitMissileBank ? parts["fuselage_F"] : parts[target.Value.x < 0 ? "gearbay_L" : "gearbay_R"];
                    hp.SetParent(part.transform, true);
                    hp.position = root.TransformPoint(target.Value);
                    if (cockpitMissileBank) hp.rotation = root.rotation;
                    hardpoint.part = part;
                }
        }

        // The inherited Ifrit carrier hook is not part of the Hercules fixed-wing conversion. Remove the complete
        // hardpoint set before loadouts are constructed so there is no hook option, spawned hook object or carrier store.
        static void RemoveTailHookHardpoint(Aircraft aircraft)
        {
            var sets = aircraft.weaponManager.hardpointSets;
            var remove = sets.Where(set => set != null &&
                ((set.name ?? "").IndexOf("tail hook", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (set.weaponOptions != null && set.weaponOptions.Any(w => w != null && w.tailHook)))).ToArray();
            if (remove.Length == 0) return;

            foreach (var set in remove)
                if (set.hardpoints != null)
                    foreach (var hp in set.hardpoints)
                        if (hp?.transform != null) DestroyImmediate(hp.transform.gameObject);

            aircraft.weaponManager.hardpointSets = sets.Except(remove).ToArray();
            Log.LogInfo($"Removed {remove.Length} inherited tail-hook hardpoint set(s) from Hercules");
        }

        // Loadouts after tail-hook removal: 0 Subach, 1 missile bank R, 2 missile bank L, 3 side missile bays,
        // 4 inner pod pylons, 5 outer pod pylons, 6 Prometheus.
        // Default is the FreeSpace standard fit: 4 Subach + 2 Prometheus guns and the two internal missile banks.
        static void FitLoadouts(AircraftParameters p, Aircraft aircraft, Dictionary<string, WeaponMount> mounts, int sets)
        {
            foreach (var set in aircraft.weaponManager.hardpointSets)
                set.weaponOptions.RemoveAll(w => w != null && w.info != null && w.info.nuclear);
            var renames = new Dictionary<string, string>
            {
                { "Forward Weapon Bay", "Missile Bank 1 (right cockpit)" }, { "Rear Weapon Bay", "Missile Bank 2 (left cockpit)" },
                { "Side Weapon Bays", "Cockpit Side Missile Bays (refit)" }, { "Inner Wing Pylons", "Inner Pod Pylons (refit)" },
                { "Outer Wing Pylons", "Outer Pod Pylons (refit)" },
            };
            foreach (var set in aircraft.weaponManager.hardpointSets)
                if (renames.TryGetValue(set.name, out var name)) set.name = name;

            List<WeaponMount> Fit(params string[] keys)
            {
                var list = keys.Select(k => k == null ? null : k == "subach" ? lasers[0] : k == "prometheus" ? lasers[1] : mounts[k]).ToList();
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && !aircraft.weaponManager.hardpointSets[i].weaponOptions.Contains(list[i]))
                        throw new InvalidOperationException($"{list[i].name} is not an option on {aircraft.weaponManager.hardpointSets[i].name}");
                return list;
            }
            StandardLoadout Role(string name, float fuel, params string[] keys) =>
                new StandardLoadout { Name = name, FuelRatio = fuel, loadout = new Loadout { weapons = Fit(keys) } };

            var canon = Fit("subach", "AAM2_triple_internal", "AAM3_quad_internal", null, null, null, "prometheus");
            p.loadouts = new List<Loadout> { new Loadout { weapons = Enumerable.Repeat<WeaponMount>(null, sets).ToList() }, new Loadout { weapons = canon } };
            p.StandardLoadouts = new[]
            {
                new StandardLoadout { Name = "FS2 standard (heavy assault)", FuelRatio = 1f, loadout = new Loadout { weapons = canon } },
                Role("Refit interceptor", 0.85f, "subach", "AAM4_triple_internal", "AAM2_triple_internal", "AAM3_single_internal", "AAM2_double", null, "prometheus"),
                Role("Refit close air support", 0.7f, "subach", "AGM_heavy_internalx2", "bomb_cluster1_dual_internal", "AAM3_single_internal", "Rocket2_4Pod", "AGM_heavy_double", "prometheus"),
                Role("Refit SEAD", 0.75f, "subach", "AGM_heavy_internalx2", "AAM2_triple_internal", "AAM3_single_internal", "ARM1_single", "ARM1_single", "prometheus"),
                Role("Refit heavy assault", 0.7f, "subach", "bomb_500_internalx2", "AAM2_triple_internal", "AAM3_single_internal", "bomb_500_single", "AGM_heavy_double", "prometheus"),
            };
        }

        static void AddSecondPrimaryBank(Aircraft aircraft, WeaponMount prometheus)
        {
            var first = aircraft.weaponManager.hardpointSets[0];
            first.name = "Primary Bank 1 (Subach)";
            first.weaponOptions = new List<WeaponMount> { null, lasers[0] };

            var source = first.hardpoints[0];
            var point = new GameObject("prometheus_primary_bank").transform;
            point.SetParent(source.transform.parent, false);
            point.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            var secondHardpoint = new Hardpoint
            {
                transform = point,
                part = source.part,
                bayDoors = Array.Empty<BayDoor>(),
                BuiltInWeapons = Array.Empty<Weapon>(),
                BuiltInTurrets = Array.Empty<Turret>(),
                HardpointIndex = -1
            };
            Traverse.Create(secondHardpoint).Field("pylonOptions")
                .SetValue(Array.CreateInstance(AccessTools.Inner(typeof(Hardpoint), "HardpointPylon"), 0));
            var second = new HardpointSet
            {
                name = "Primary Bank 2 (Prometheus)",
                precludingHardpointSets = new List<byte>(),
                weaponOptions = new List<WeaponMount> { null, prometheus },
                hardpoints = new List<Hardpoint> { secondHardpoint }
            };
            aircraft.weaponManager.hardpointSets = aircraft.weaponManager.hardpointSets.Append(second).ToArray();
        }

        static void FitCountermeasures(GameObject prefab)
        {
            // FS2's 25 countermeasures are expendable decoys: all go to the flare/chaff dispenser. The inherited radar
            // jammer is powered by the aircraft, not ammunition, and keeps its stock setting.
            foreach (var dispenser in prefab.GetComponentsInChildren<FlareEjector>(true))
                dispenser.ammo = HerculesCountermeasures;
        }

        // FS2 primary banks, built from the 27mm autocannon mount and installed on independent stations so the
        // player can select either bank or link all six guns as on the source Hercules.
        static WeaponMount[] AddLasers(WeaponMount cannonMount, Transform holder, Aircraft aircraft)
        {
            var slot = aircraft.weaponManager.hardpointSets[0];
            var hardpoint = slot.hardpoints[0].transform;
            var cannonGun = cannonMount.prefab.GetComponentInChildren<Gun>(true);
            // The Subach HL-7 bank, FS2's standard-issue gun, gets the 27mm's sustained damage; other lasers keep
            // their FS2 hull-damage ratio to it.
            float cannonDps = Traverse.Create(cannonGun).Field("fireRate").GetValue<float>() / 60f * cannonMount.info.pierceDamage;

            var subach = MakeLaser(cannonMount, holder, hardpoint, aircraft.transform, "fs2_subach_hl7_x4",
                "GTW Subach HL-7 laser x4 (energy)", "GTW Subach HL-7", "SUBACH HL-7",
                "Standard-issue Terran laser. Four guns, fast firing. Level 3 hull damage, level 2 shield damage.",
                SubachMuzzles, 0.2f, cannonDps * SubachHull / SubachBankHullDps, new Color(8f, 0.3f, 0.3f), SubachBankMass);
            var prometheus = MakeLaser(cannonMount, holder, hardpoint, aircraft.transform, "fs2_prometheus_r_x2",
                "GTW-5a Prometheus R cannon x2 (energy)", "GTW-5a Prometheus R", "PROMETHEUS R",
                "Retrofit Prometheus cannon. Two guns, slower but heavier bolts. Level 4 hull damage, level 1 shield damage.",
                PrometheusMuzzles, 0.45f, cannonDps * PrometheusHull / SubachBankHullDps, new Color(0.6f, 5f, 0.6f), PrometheusBankMass);

            return new[] { subach, prometheus };
        }

        static WeaponMount MakeLaser(WeaponMount cannonMount, Transform holder, Transform hardpoint, Transform aircraftRoot,
            string key, string mountName, string weaponName, string shortName, string description,
            Vector3[] muzzlePoints, float fireWait, float pierce, Color tracer, float bankMass)
        {
            var info = Instantiate(cannonMount.info);
            info.name = key;
            info.weaponName = weaponName;
            info.shortName = shortName;
            info.description = description;
            info.muzzleVelocity = LaserSpeed;
            info.dragCoef = 0f;
            info.gravMult = 0f;
            info.pierceDamage = pierce;
            info.blastDamage = 0f;
            // The 27mm template carries 0.45 kg per round; an energy bank's charge counter has no mass.
            info.massPerRound = 0f;
            var requirements = info.targetRequirements;
            requirements.maxRange = LaserSpeed * LaserLifetime;
            info.targetRequirements = requirements;

            var prefab = Instantiate(cannonMount.prefab, holder);
            prefab.name = key;
            prefab.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var gun = prefab.GetComponentInChildren<Gun>(true);
            gun.info = info;
            var g = Traverse.Create(gun);

            // Lasers have no muzzle smoke or flash, and the Herc model carries its own gun barrels: remove the
            // 27mm's particles and barrel mesh before the muzzle is copied. SetGlobalParticles also lists those
            // systems and would throw in Start on the destroyed entries, so prune its lists too.
            foreach (var ps in prefab.GetComponentsInChildren<ParticleSystem>(true))
                DestroyImmediate(ps.gameObject);
            g.Field("muzzleParticles").SetValue(new ParticleSystem[0]);
            foreach (var global in prefab.GetComponentsInChildren<SetGlobalParticles>(true))
                Traverse.Create(global).Field("systems").GetValue<List<ParticleSystem>>().RemoveAll(ps => ps == null);
            foreach (var mesh in prefab.GetComponentsInChildren<MeshRenderer>(true))
                mesh.enabled = false;

            // Barrels at the FS2 gun points, expressed relative to the hardpoint the mount is spawned on.
            var template = g.Field("muzzles").GetValue<Transform[]>()[0];
            var muzzles = new Transform[muzzlePoints.Length];
            for (int i = 0; i < muzzles.Length; i++)
            {
                muzzles[i] = i == 0 ? template : Instantiate(template.gameObject, template.parent).transform;
                muzzles[i].position = prefab.transform.TransformPoint(hardpoint.InverseTransformPoint(aircraftRoot.TransformPoint(muzzlePoints[i])));
            }
            g.Field("muzzles").SetValue(muzzles);
            g.Field("fireRate").SetValue(60f / fireWait);
            g.Field("tracerRatio").SetValue(0);
            g.Field("tracerSize").SetValue(4f);
            g.Field("tracerColor").SetValue(tracer);
            g.Field("bulletSelfDestruct").SetValue(LaserLifetime);
            g.Field("bulletSpread").SetValue(0.1f);
            g.Field("magazineCapacity").SetValue(LaserAmmo);

            var mount = Instantiate(cannonMount);
            mount.name = key;
            mount.jsonKey = key;
            mount.mountName = mountName;
            mount.info = info;
            mount.prefab = prefab;
            mount.ammo = LaserAmmo;
            mount.mass = bankMass;
            mount.emptyMass = bankMass;
            Log.LogInfo($"{weaponName}: {muzzles.Length} guns, {60f / fireWait:0} rpm, {pierce:0} pierce per bolt, {LaserSpeed} m/s, {bankMass:0} kg hardware");
            return mount;
        }

        static float Width(Renderer r)
        {
            var size = Vector3.Scale(r.GetComponent<MeshFilter>().sharedMesh.bounds.size, r.transform.lossyScale);
            return Mathf.Max(size.x, size.y);
        }

        // Copies a flame or glow without its heat-haze particles and audio, which the nozzle drives only on the original.
        static Renderer Duplicate(Renderer r)
        {
            var copy = Instantiate(r.gameObject, r.transform.parent);
            copy.name = r.name + "_2";
            foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true)) DestroyImmediate(ps.gameObject);
            foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true)) DestroyImmediate(audio);
            return copy.GetComponent<Renderer>();
        }

        // Afterburner.Run overwrites the flame's localScale every frame, so size it through a parent.
        static void Mount(Transform part, Transform root, Vector3 exit, float scale)
        {
            var mount = new GameObject(part.name + "_mount").transform;
            mount.SetParent(part.parent, false);
            mount.SetPositionAndRotation(root.TransformPoint(exit), part.rotation);
            mount.localScale *= scale;
            part.SetParent(mount, false);
            part.localPosition = Vector3.zero;
            part.localRotation = Quaternion.identity;
        }

        static Material MakeMaterial(Material template, string albedoFile, string normalFile, string glowFile)
        {
            var albedo = LoadTexture(albedoFile, linear: false);
            var mat = new Material(template) { name = Path.GetFileNameWithoutExtension(albedoFile) };
            mat.SetColor("_BaseColor", Color.white);
            mat.SetTexture("_BaseMap", albedo);
            mat.SetTexture("_MainTex", albedo);
            mat.SetTexture("_BumpMap", normalFile == null ? null : LoadTexture(normalFile, linear: true));
            if (normalFile == null) mat.DisableKeyword("_NORMALMAP"); else mat.EnableKeyword("_NORMALMAP");
            if (glowFile != null)
            {
                mat.SetTexture("_EmissionMap", LoadTexture(glowFile, linear: false));
                mat.SetColor("_EmissionColor", Color.white);
                mat.EnableKeyword("_EMISSION");
            }
            mat.SetTexture("_MetallicGlossMap", null);
            mat.DisableKeyword("_METALLICSPECGLOSSMAP");
            mat.SetTexture("_OcclusionMap", null);
            mat.DisableKeyword("_OCCLUSIONMAP");
            mat.SetFloat("_Metallic", 0.35f);
            mat.SetFloat("_Smoothness", 0.45f);
            return mat;
        }

        static Texture2D LoadTexture(string file, bool linear)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = file };
            if (!tex.LoadImage(File.ReadAllBytes(Path.Combine(assetDir, file))))
                throw new InvalidDataException("Could not decode " + file);
            tex.Compress(true);
            tex.Apply(true, true);
            return tex;
        }

        // Reads the binary mesh written by the asset tool (per submesh: material name, pos/normal/uv vertices, indices)
        // and builds one mesh with a submesh per requested material, in the order given.
        static Mesh LoadMesh(string path, params string[] materials)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshes = new Dictionary<string, int[]>();
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                int count = br.ReadInt32();
                for (int s = 0; s < count; s++)
                {
                    string name = Encoding.ASCII.GetString(br.ReadBytes(br.ReadInt32()));
                    int vertexCount = br.ReadInt32();
                    bool keep = materials.Contains(name);
                    int baseIndex = positions.Count;
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        var nrm = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        var uv = new Vector2(br.ReadSingle(), br.ReadSingle());
                        if (keep) { positions.Add(pos); normals.Add(nrm); uvs.Add(uv); }
                    }
                    var indices = new int[br.ReadInt32()];
                    for (int i = 0; i < indices.Length; i++) indices[i] = br.ReadInt32() + baseIndex;
                    if (keep) submeshes[name] = indices;
                }
            }
            var missing = materials.Where(m => !submeshes.ContainsKey(m)).ToArray();
            if (missing.Length > 0) throw new InvalidDataException($"No '{string.Join("', '", missing)}' submesh in {path}");

            var mesh = new Mesh { name = "GTF_Hercules", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = materials.Length;
            for (int i = 0; i < materials.Length; i++) mesh.SetTriangles(submeshes[materials[i]], i);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            mesh.UploadMeshData(true);
            return mesh;
        }

        // Mirage needs a unique, non-zero prefab hash that is the same on every run.
        static int StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return h == 0 ? 1 : (int)h;
        }
    }

    // Runs on every spawned copy in Awake, while all parts are still children of the root (the game re-parents
    // them after spawning). Hides the Ifrit's skin, and its gear struts, wheels, ladder and chocks, which would
    // float under the raised Herc; cockpit interior, pilot, lights and effects stay.
    public class HideRenderers : MonoBehaviour
    {
        static bool logged;

        void Awake()
        {
            var hide = new HashSet<Renderer>();
            foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
            {
                var shader = r.sharedMaterial != null ? r.sharedMaterial.shader.name : "";
                if (shader == "Shader Graphs/AircraftSkin" || shader == "Shader Graphs/LOD" || shader == "Shader Graphs/glass"
                    || r.name.StartsWith("ladder") || r.name.StartsWith("chocks") || r.name.StartsWith("pylon_"))
                    hide.Add(r);
            }
            foreach (var gear in GetComponentsInChildren<LandingGear>(true))
                foreach (var r in Traverse.Create(gear).Field("gearHinge").GetValue<Transform>().GetComponentsInChildren<MeshRenderer>(true))
                    hide.Add(r);
            foreach (var r in hide) r.forceRenderingOff = true;
            if (!logged)
            {
                logged = true;
                Debug.Log($"[FS2Hercules] Hid {hide.Count} Ifrit skin and gear renderers on {name}");
            }
        }
    }
}
