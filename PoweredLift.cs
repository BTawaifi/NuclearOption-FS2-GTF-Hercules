using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FS2Hercules
{
    // Atmospheric-refit propulsion and low-speed flight control (not FreeSpace canon; see the audit report).
    //
    // Force producers, each applied where it physically sits:
    //  - two main engines (one per pod, 70.8 kN dry / 127.2 kN wet, inherited FS2-ratio ratings), each exhausting
    //    through a bifurcated swivel nozzle at the pod tail (the FS2 model's upper and lower thruster pair);
    //  - four lift jets, two in each pod nose, rated at the Yak-141's RD-41 (41.7 kN each), used only for powered lift.
    // Nothing multiplies thrust with nozzle angle: a main nozzle's force magnitude is its engine's thrust times a
    // turning loss; rotating it only changes direction. Lift-jet thrust is separate, costs its own fuel and exists only
    // while the jets run. Attitude in powered lift comes only from these six forces at their moment arms.
    public class HerculesPoweredLift : MonoBehaviour
    {
        // ---- design values (atmospheric-refit choices unless noted) ----
        public const float LiftJetRating = 41700f;            // N, RD-41 published rating
        public const float LiftJetTsfc = 1.25f * 0.4536f / 4.448f / 3600f;  // kg/(N s), 1.25 lb/(lbf h), assumption
        public const float MainDryTsfc = 0.80f * 0.4536f / 4.448f / 3600f;  // kg/(N s), low-bypass military power
        public const float MainWetTsfc = 2.20f * 0.4536f / 4.448f / 3600f;  // kg/(N s), on total afterburning thrust
        public const float LiftJetTimeConstant = 0.2f;        // s, thrust response once running
        public const float LiftJetStartTime = 3f;             // s, doors open and spin-up before thrust is available
        public const float NozzleTurnLoss = 0.03f;            // fraction of thrust lost at 90 degrees of swivel
        public const float SpoilLimit = 0.8f;                 // main nozzle can shed 20% quickly (area/bleed modulation)
        public const float MaxLateralVector = 12f;            // deg, yaw vectoring
        public const float AfterburnerMaxAngle = 15f;         // deg, reheat only with the nozzle near axial
        public const float LapseExponent = 0.8f;              // thrust ~ (rho/rho0)^0.8 below the tropopause
        public const float HgiMainLoss = 0.08f, HgiLiftLoss = 0.06f; // max hot-gas-ingestion thrust loss
        // Transonic drag rise: Lock's approximation dCD = 20 (M - Mcrit)^4 on the 138 m2 planform, with a critical
        // Mach of 0.70 for the thick, blunt hull. The game's own drag model adds at most 15% between Mach 0.8 and 1.2.
        public const float CriticalMach = 0.70f, DragRiseReferenceArea = 138f;
        static readonly Vector3 AlphaLimit = new Vector3(0.35f, 0.15f, 0.3f); // rad/s2 pitch, yaw, roll commands
        const float MaxAttitudeRate = 15f * Mathf.Deg2Rad;
        // Speed at which the hull carries 1 g at a moderate angle of attack (CL ~0.6 on 150 m2 at full weight).
        public const float WingborneSpeed = 90f;
        const float PitchEnvelope = 15f, RollEnvelope = 25f; // deg, attitude limits while powered lift is active

        public static readonly Vector3[] LiftJetPositions =
        {
            new Vector3(-3.6f, 0.2f, 3.9f), new Vector3(-3.6f, 0.2f, 3.3f),
            new Vector3(3.6f, 0.2f, 3.9f), new Vector3(3.6f, 0.2f, 3.3f),
        };

        // Serialized from Build.
        public Turbojet[] engines;          // L, R
        public JetNozzle[] nozzles;         // L, R
        public Transform[] exits;           // L, R swivel nozzle force points (bifurcated exhaust midpoints)
        public UnitPart[] engineParts;      // L, R, main thrust is applied to these
        public UnitPart[] liftJetParts;     // intake_L x2, intake_R x2
        public Transform[] flameMounts;     // visual exits, turned with the nozzle

        // ---- live state and telemetry (read by the test probe) ----
        public readonly float[] actuatorForce = new float[6];     // N applied this step: lift jets 0-3, main L/R 4-5
        public readonly float[] actuatorCapacity = new float[6];  // N available this step
        public readonly bool[] failed = new bool[6];
        public readonly float[] liftJetThrust = new float[4];
        public float[] engineThrottle = { 1f, 1f };
        public float hoverAuthority, nozzleAngle, commandedNozzle, nozzleFloor, lateralVector, lastFuelFlow, massEstimate;
        public float hgiMain, hgiLift, liftDoorOpen, liftAvailable;
        public Vector3 centerOfMassLocal, commandedMoment, achievedMoment;
        public float commandedLift, achievedLift, altitudeHold;
        public bool liftJetsRunning, autoHover;
        public string mode = "forward";

        Aircraft aircraft;
        DuctedThrustSystem duct;
        object autoHoverState;
        FieldInfo autoHoverActive, afterburnerAmount, afterburnerThrust, afterburnerFuel;
        static readonly AccessTools.FieldRef<Turbojet, float> JetThrustRatio = AccessTools.FieldRefAccess<Turbojet, float>("thrustRatio");
        ControlInputs inputs;
        float rawPitch, rawRoll, rawYaw, rawThrottle;
        bool rawCaptured;
        float liftJetsRunTime, fuelOwed, verticalIntegral, heading, holdPitch, holdRoll;
        bool holdAttitude, holdHeading, altitudeCaptured;
        readonly float[] flameoutTimer = new float[6], hotTimer = new float[6];
        IRSource[] liftJetIR;
        bool dragApplied;
        float[] slowSolution, fastSolution;
        Vector3 sustainedMoment;

        readonly System.Collections.Generic.HashSet<Rigidbody> bodies = new System.Collections.Generic.HashSet<Rigidbody>();

        void Awake()
        {
            aircraft = GetComponent<Aircraft>();
            duct = GetComponent<DuctedThrustSystem>();
            inputs = aircraft.GetInputs();
            autoHoverState = Traverse.Create(aircraft.GetControlsFilter()).Field("autoHover").GetValue();
            autoHoverActive = AccessTools.Field(autoHoverState.GetType(), "Active");
            var abType = AccessTools.Inner(typeof(JetNozzle), "Afterburner");
            afterburnerAmount = AccessTools.Field(abType, "afterburnerAmount");
            afterburnerThrust = AccessTools.Field(abType, "thrust");
            afterburnerFuel = AccessTools.Field(abType, "fuelConsumption");
            for (int i = 0; i < engines.Length; i++)
            {
                int index = 4 + i, engine = i;
                engines[i].OnEngineDisable += () => OnEngineDisabled(index, engine);
            }
            aircraft.onInitialize += () =>
            {
                liftJetIR = new IRSource[4];
                for (int j = 0; j < 4; j++)
                {
                    var point = new GameObject("herc_liftjet_" + j).transform;
                    point.SetParent(liftJetParts[j].transform, false);
                    point.position = transform.TransformPoint(LiftJetPositions[j]);
                    point.rotation = transform.rotation * Quaternion.LookRotation(Vector3.down);
                    liftJetIR[j] = new IRSource(point, 0f, flare: false);
                    aircraft.AddIRSource(liftJetIR[j]);
                }
                for (int j = 0; j < 4; j++)
                {
                    int index = j;
                    liftJetParts[j].onApplyDamage += e => OnLiftJetPartDamage(index, e);
                }
            };
        }

        // Pilot or autopilot command before the stock control filter reshapes it for the aerodynamic surfaces.
        public void CaptureCommand()
        {
            rawPitch = inputs.pitch; rawRoll = inputs.roll; rawYaw = inputs.yaw; rawThrottle = inputs.throttle;
            rawCaptured = true;
        }

        public bool IsAutoHoverActive => (bool)autoHoverActive.GetValue(autoHoverState);

        public float EngineThrottle(Turbojet jet, float stockThrottle)
        {
            int i = Array.IndexOf(engines, jet);
            if (i < 0) return stockThrottle;
            if (flameoutTimer[4 + i] > 0f) return 0f;
            return Mathf.Lerp(stockThrottle, engineThrottle[i], hoverAuthority);
        }

        public bool AfterburnerAllowed(bool stock) => nozzleAngle <= AfterburnerMaxAngle && hoverAuthority <= 0f;

        // Nozzle schedule (refit choice): the swivel may not go past 20 deg until the lift jets deliver thrust (so the
        // pitch moment of vectored main thrust can be balanced), and vectoring is limited at speed. The stock swivel
        // logic already keeps an airborne aircraft at 45 deg or more below 60 m/s. customAxis1 = 1 - angle / limit.
        public float NozzleLimit(float swivelLimit) =>
            Mathf.Min(Mathf.Lerp(swivelLimit, 20f, Mathf.InverseLerp(60f, 160f, aircraft.speed)), 5f + (swivelLimit - 5f) * liftAvailable);

        // Owns the swivel angle after the stock Swivel step (which would hold an airborne aircraft at 45 deg below
        // 60 m/s regardless of weight). The lever command is followed at the swivel rate, bounded by NozzleLimit and,
        // when airborne, by the angle the main nozzles must keep so that they, the lift jets (90%) and an estimate of
        // hull lift (CL 0.45 on 150 m2) still support the weight. The transition is therefore as fast as the lift
        // actually allows.
        public float ScheduleNozzleAngle(float previousAngle, float leverAngle, float swivelLimit, float dt)
        {
            commandedNozzle = leverAngle;
            float max = NozzleLimit(swivelLimit);
            nozzleFloor = 0f;
            if (aircraft.radarAlt > 2f && massEstimate > 0f)
            {
                float v = aircraft.speed;
                float hullLift = 0.5f * aircraft.airDensity * v * v * 150f * 0.45f;
                float liftJets = 0f, rear = 0f;
                for (int k = 0; k < 4; k++) liftJets += actuatorCapacity[k];
                for (int k = 4; k < 6; k++) rear += actuatorCapacity[k];
                float need = massEstimate * 9.81f - hullLift - 0.9f * liftJets;
                if (need > 0f && rear > 1f)
                    nozzleFloor = Mathf.Min(90f, Mathf.Asin(Mathf.Clamp01(need / rear)) * Mathf.Rad2Deg + 5f);
            }
            float desired = Mathf.Clamp(leverAngle, Mathf.Min(nozzleFloor, max), max);
            return Mathf.MoveTowards(previousAngle, desired, 40f * dt);
        }

        public void ForceFailure(int actuator)
        {
            if (actuator >= 4) Traverse.Create(engines[actuator - 4]).Method("KillEngine").GetValue();
            failed[actuator] = true;
        }

        // An engine destroyed by damage (not a flameout or a test failure injection on an intact pod) sets its pod's
        // saddle tank on fire through the game's networked fuel-fire model: fire damage, spreading, fireball.
        void OnEngineDisabled(int actuator, int engine)
        {
            failed[actuator] = true;
            var part = engineParts[engine];
            if (aircraft.IsServer && part != null && part.hitPoints < 30f && part.GetComponent<FuelTank>() != null)
            {
                aircraft.FuelTankStatus(part.id, ruptured: false, onFire: true);
                Report($"ENGINE FIRE {(engine == 0 ? "LEFT" : "RIGHT")}");
            }
        }

        void OnLiftJetPartDamage(int jet, UnitPart.OnApplyDamage e)
        {
            // The two jets in a pod nose are separate engines: the forward one fails first.
            float threshold = jet % 2 == 0 ? 45f : 20f;
            if (!failed[jet] && (e.detached || e.hitPoints < threshold))
            {
                failed[jet] = true;
                Report($"LIFT JET {(jet < 2 ? "L" : "R")}{jet % 2 + 1} FAILED");
            }
        }

        void Report(string text)
        {
            if (GameManager.IsLocalAircraft(aircraft)) SceneSingleton<AircraftActionsReport>.i?.ReportText(text, 5f);
        }

        // Called after DuctedThrustSystem has updated the nozzle angle and aimed the swivel transforms.
        public void Step()
        {
            if (aircraft == null || !aircraft.LocalSim || aircraft.rb == null || aircraft.disabled) return;
            float dt = Time.fixedDeltaTime;
            var root = transform;
            nozzleAngle = duct.GetNozzleAngle();
            autoHover = IsAutoHoverEnabled();
            if (!rawCaptured) CaptureCommand();
            rawCaptured = false;

            // Physical mass and centre of mass from the rigidbodies actually simulated (one in simple physics).
            float mass = 0f; Vector3 weighted = Vector3.zero;
            bodies.Clear();
            foreach (var part in aircraft.partLookup)
            {
                if (part == null || part.IsDetached() || part.rb == null || !bodies.Add(part.rb)) continue;
                mass += part.rb.mass;
                weighted += part.rb.worldCenterOfMass * part.rb.mass;
            }
            if (mass <= 0f) return;
            massEstimate = mass;
            Vector3 com = weighted / mass;
            centerOfMassLocal = root.InverseTransformPoint(com);

            float airspeed = aircraft.speed;
            float sigma = Mathf.Clamp(aircraft.airDensity / 1.225f, 0.01f, 1.2f);
            float lapse = Mathf.Pow(sigma, LapseExponent);
            bool airborne = aircraft.radarAlt > 0.5f;

            UpdateHotGas(root, airspeed, dt);

            // Lift jets run whenever the nozzle lever (after the stock mode logic) asks for powered lift. The nozzles
            // themselves are held near axial until the jets deliver thrust (NozzleLimit).
            float commandedAngle = commandedNozzle;
            // Once running they stay on until the hull is wing-borne, so selecting axial nozzles early cannot drop the
            // aircraft; a slow aircraft in forward flight with the lever axial does not start them.
            bool wantLiftJets = commandedAngle >= 20f || nozzleAngle >= 12f || (airborne && airspeed < WingborneSpeed && (liftAvailable > 0f || commandedAngle > 5f));
            if (commandedAngle < 12f && nozzleAngle < 12f && airspeed > WingborneSpeed) wantLiftJets = false;
            liftJetsRunTime = wantLiftJets ? liftJetsRunTime + dt : 0f;
            liftJetsRunning = wantLiftJets;
            liftDoorOpen = Mathf.MoveTowards(liftDoorOpen, wantLiftJets || liftAvailable > 0f ? 1f : 0f, dt / 1.5f);
            SetDoorDrag(liftDoorOpen > 0.05f);
            // Doors take the first half of the start time; thrust capacity then spools up over the second half, and
            // spools down over the same time on shutdown so the lift jets never drop out in a single step.
            float liftTarget = wantLiftJets && liftJetsRunTime >= LiftJetStartTime * 0.5f ? 1f : 0f;
            liftAvailable = Mathf.MoveTowards(liftAvailable, liftTarget, dt / (LiftJetStartTime * 0.5f));
            bool liftJetsActive = wantLiftJets || liftAvailable > 0f;

            // Powered-lift authority: full while the lift jets are delivering thrust at low airspeed. Until they are
            // running the engines keep following the pilot's throttle (and the nozzles stay near axial).
            hoverAuthority = liftAvailable * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(35f, 90f, airspeed)));

            // ---- actuator geometry and capacity in aircraft axes, relative to the centre of mass ----
            var r = new Vector3[6]; var d = new Vector3[6]; var lo = new float[6]; var hi = new float[6]; var slowHi = new float[6];
            float turnEfficiency = TurnEfficiency(nozzleAngle);
            float thetaRad = nozzleAngle * Mathf.Deg2Rad;
            for (int j = 0; j < 4; j++)
            {
                r[j] = LiftJetPositions[j] - centerOfMassLocal;
                d[j] = Vector3.up;
                bool ok = !failed[j] && flameoutTimer[j] <= 0f && liftJetParts[j] != null && !liftJetParts[j].IsDetached();
                hi[j] = slowHi[j] = ok ? LiftJetRating * lapse * (1f - HgiLiftLoss * hgiLift) * liftAvailable : 0f;
            }
            var engineThrust = new float[2];
            for (int i = 0; i < 2; i++)
            {
                int k = 4 + i;
                bool ok = !failed[k] && engineParts[i] != null && !engineParts[i].IsDetached();
                float dry = ok ? engines[i].maxThrust * JetThrustRatio(engines[i]) : 0f;
                float wet = ok ? AfterburnerThrust(nozzles[i]) : 0f;
                engineThrust[i] = MainNozzleThrust(dry, wet, sigma, hgiMain, nozzleAngle);
                r[k] = root.InverseTransformPoint(exits[i].position) - centerOfMassLocal;
                d[k] = new Vector3(0f, Mathf.Sin(thetaRad), Mathf.Cos(thetaRad));
                hi[k] = engineThrust[i];
                lo[k] = engineThrust[i] * (hoverAuthority > 0f ? SpoilLimit : 1f);
                slowHi[k] = ok ? engines[i].maxThrust * lapse * turnEfficiency : 0f;
                actuatorCapacity[k] = hi[k];
            }
            for (int j = 0; j < 4; j++) actuatorCapacity[j] = hi[j];

            // ---- commands ----
            Vector3 omega = root.InverseTransformDirection(aircraft.rb.angularVelocity);
            float massRatio = mass / 25400f;
            Vector3 inertia = new Vector3(351000f, 559000f, 366000f) * massRatio; // pitch, yaw, roll (design sheet)
            // Angular acceleration the actuators can deliver around trim at full weight (design sheet): pitch by
            // front/rear exchange ~0.8 rad/s2, roll by left/right ~0.5, yaw by 12 deg lateral vectoring ~0.25.
            // Commands stay well inside those so the loops never ask for more than exists.
            Vector3 rateCmd = AttitudeRateCommand(root, airspeed);
            Vector3 alpha = Vector3.Scale(rateCmd - omega, new Vector3(2.5f, 2f, 2.5f));
            alpha = new Vector3(Mathf.Clamp(alpha.x, -AlphaLimit.x, AlphaLimit.x), Mathf.Clamp(alpha.y, -AlphaLimit.y, AlphaLimit.y), Mathf.Clamp(alpha.z, -AlphaLimit.z, AlphaLimit.z));
            commandedMoment = Vector3.Scale(inertia, alpha);

            float weight = mass * 9.81f;
            float upY = Mathf.Max(root.up.y, 0.5f);
            if (autoHover && hoverAuthority > 0f)

                commandedLift = mass * (9.81f + VerticalAccelerationCommand(dt)) / upY;
            else
            {
                verticalIntegral = 0f;
                altitudeCaptured = false;
                // Manual powered lift: throttle sets the fraction of installed vertical thrust.
                float verticalCapacity = 0f;
                for (int k = 0; k < 6; k++) verticalCapacity += slowHi[k] * Mathf.Max(d[k].y, 0f);
                commandedLift = Mathf.Clamp01(rawThrottle) * verticalCapacity;
            }

            // Yaw: lateral deflection of both main nozzles (moment arm = nozzle distance behind the CoM).
            float rearLeverage = 0f;
            for (int i = 0; i < 2; i++) rearLeverage += hi[4 + i] * r[4 + i].z;
            lateralVector = 0f;
            // Only in powered lift: forward flight is left to the aerodynamic surfaces and the stock fly-by-wire, which
            // is not tuned for thrust vectoring (adding it caused pitch oscillation under AI control).
            if (hoverAuthority > 0f && Mathf.Abs(rearLeverage) > 1f)
                lateralVector = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(commandedMoment.y / rearLeverage, -1f, 1f)) * Mathf.Rad2Deg, -MaxLateralVector, MaxLateralVector);
            for (int i = 0; i < 2; i++)
            {
                float theta = nozzleAngle * Mathf.Deg2Rad, lat = lateralVector * Mathf.Deg2Rad;
                d[4 + i] = new Vector3(Mathf.Sin(lat), Mathf.Sin(theta) * Mathf.Cos(lat), Mathf.Cos(theta) * Mathf.Cos(lat));
            }

            var f = new float[6];
            if (liftJetsActive)
            {
                float collectiveWeight = hoverAuthority;
                // Slow solve: the engines spool towards the split that meets the lift demand and the sustained part of
                // the moment demand (1 s low-pass), with 10% spoil headroom. Transient moments are met by the lift
                // jets and spoiling; a persistent one (for example an asymmetric failure) is carried by engine RPM.
                var slowLo = new float[6];
                float trimLift = Mathf.Clamp(commandedLift, 0.3f * weight, 1.3f * weight);
                sustainedMoment = Vector3.Lerp(sustainedMoment, commandedMoment, Mathf.Clamp01(dt / 1f));
                var prefSlow = Nominal(slowHi, trimLift);
                var slow = slowSolution = Allocate(r, d, slowLo, slowHi, trimLift, sustainedMoment, 1f, prefSlow, weight, slowSolution);
                for (int i = 0; i < 2; i++)
                {
                    float jetMax = engines[i].maxThrust * lapse * turnEfficiency;
                    float ratio = jetMax > 1f ? Mathf.Clamp01(slow[4 + i] / 0.9f / jetMax) : 0f;
                    // Turbojet maps throttle 0..0.88 onto idle..max RPM; thrustRatio = 0.075 + throttle/0.88.
                    engineThrottle[i] = Mathf.Clamp(0.88f * (ratio - 0.075f), 0f, 0.88f);
                }
                // Fast solve within what the engines deliver now.
                var pref = Nominal(hi, commandedLift);
                for (int i = 0; i < 2; i++) pref[4 + i] = hi[4 + i];
                f = fastSolution = Allocate(r, d, lo, hi, commandedLift, commandedMoment, collectiveWeight, pref, weight, fastSolution);
            }
            else
            {
                for (int i = 0; i < 2; i++) { f[4 + i] = hi[4 + i]; engineThrottle[i] = rawThrottle; }
            }

            // Lift jets follow their command with a first-order lag; main nozzles apply instantly (engine spool is
            // already in the Turbojet RPM).
            achievedLift = 0f; achievedMoment = Vector3.zero;
            float fuelFlow = 0f;
            for (int j = 0; j < 4; j++)
            {
                float target = Mathf.Min(f[j], hi[j]);
                liftJetThrust[j] += (target - liftJetThrust[j]) * Mathf.Clamp01(dt / LiftJetTimeConstant);
                // hi[j] itself ramps down over the door-close time (liftAvailable) while this lag filter converges
                // on its own, shorter time constant; without this the applied thrust can momentarily sit above the
                // capacity it is lagging towards. Clamping every tick (not just at hi[j]==0) keeps it physical.
                liftJetThrust[j] = Mathf.Clamp(liftJetThrust[j], 0f, hi[j]);
                ApplyForce(j, root, root.TransformPoint(LiftJetPositions[j]), root.up * liftJetThrust[j], liftJetParts[j], r[j], d[j], liftJetThrust[j]);
                fuelFlow += liftJetThrust[j] * LiftJetTsfc;
                if (liftJetIR != null) liftJetIR[j].intensity = 1.5f * liftJetThrust[j] / LiftJetRating;
            }
            for (int i = 0; i < 2; i++)
            {
                int k = 4 + i;
                float magnitude = Mathf.Clamp(f[k], 0f, hi[k]);
                ApplyForce(k, root, exits[i].position, root.TransformDirection(d[k]) * magnitude, engineParts[i], r[k], d[k], magnitude);
                fuelFlow += AfterburnerFuel(nozzles[i]);
            }
            lastFuelFlow = fuelFlow; // lift jets and afterburners; dry engine flow is charged by Turbojet itself
            ApplyCompressibilityDrag(com);
            fuelOwed += fuelFlow * dt;
            if (fuelOwed > 0.5f)
            {
                if (!aircraft.UseFuel(fuelOwed))
                    for (int j = 0; j < 4; j++) liftJetThrust[j] = 0f;
                fuelOwed = 0f;
            }
            mode = !liftJetsActive ? "forward" : autoHover ? "hover-hold" : hoverAuthority >= 1f ? "powered-lift" : "transition";
        }

        public float transonicDrag, mach;

        public static float TurnEfficiency(float nozzleAngle) => 1f - NozzleTurnLoss * Mathf.Clamp01(nozzleAngle / 90f);

        // Force magnitude of one swivel nozzle: engine thrust (dry from RPM, reheat from the afterburner state) with
        // density lapse, hot-gas-ingestion loss and turning loss. Swivel angle only ever reduces it.
        public static float MainNozzleThrust(float dry, float wet, float densityRatio, float hotGas, float nozzleAngle) =>
            (dry + wet) * Mathf.Pow(Mathf.Clamp(densityRatio, 0.01f, 1.2f), LapseExponent) * (1f - HgiMainLoss * Mathf.Clamp01(hotGas)) * TurnEfficiency(nozzleAngle);

        // Extra drag above the critical Mach number, spread over the simulated bodies in proportion to their mass so it
        // acts through the centre of mass without adding a moment.
        void ApplyCompressibilityDrag(Vector3 com)
        {
            Vector3 air = aircraft.rb.velocity - aircraft.GetWindVelocity();
            float v = air.magnitude;
            mach = v / LevelInfo.GetSpeedOfSound(aircraft.GlobalPosition().y);
            transonicDrag = 0f;
            if (mach <= CriticalMach) return;
            float dcd = 20f * Mathf.Pow(mach - CriticalMach, 4f);
            transonicDrag = 0.5f * aircraft.airDensity * v * v * DragRiseReferenceArea * dcd;
            Vector3 direction = -air / v;
            foreach (var body in bodies)
                if (body != null) body.AddForce(direction * transonicDrag * body.mass / massEstimate);
        }

        void ApplyForce(int k, Transform root, Vector3 point, Vector3 force, UnitPart part, Vector3 r, Vector3 d, float magnitude)
        {
            actuatorForce[k] = magnitude;
            achievedLift += magnitude * d.y;
            achievedMoment += Vector3.Cross(r, d * magnitude);
            if (magnitude <= 0f || part == null || part.rb == null) return;
            part.rb.AddForceAtPosition(force, point);
        }

        float AfterburnerThrust(JetNozzle nozzle)
        {
            float total = 0f;
            foreach (var ab in Traverse.Create(nozzle).Field("afterburners").GetValue<Array>())
                total += (float)afterburnerAmount.GetValue(ab) * (float)afterburnerThrust.GetValue(ab);
            return total;
        }

        float AfterburnerFuel(JetNozzle nozzle)
        {
            float total = 0f;
            foreach (var ab in Traverse.Create(nozzle).Field("afterburners").GetValue<Array>())
                total += (float)afterburnerAmount.GetValue(ab) * (float)afterburnerFuel.GetValue(ab);
            return total;
        }

        bool IsAutoHoverEnabled() => IsAutoHoverActive;

        // Auto hover holds altitude with the throttle centred and commands climb or descent when it is moved.
        float VerticalAccelerationCommand(float dt)
        {
            float altitude = aircraft.GlobalPosition().y;
            float offset = rawThrottle - 0.5f;
            float climb;
            if (Mathf.Abs(offset) > 0.1f)
            {
                climb = (offset - Mathf.Sign(offset) * 0.1f) / 0.4f * 6f;
                altitudeCaptured = false;
            }
            else if (!altitudeCaptured)
            {
                // Arrest the vertical speed first, then hold the altitude where the aircraft stopped.
                climb = 0f;
                altitudeHold = altitude;
                altitudeCaptured = Mathf.Abs(aircraft.rb.velocity.y) < 0.5f;
            }
            else
                climb = Mathf.Clamp(0.6f * (altitudeHold - altitude), -3f, 3f);
            float error = climb - aircraft.rb.velocity.y;
            verticalIntegral = Mathf.Clamp(verticalIntegral + error * 0.4f * dt, -2.5f, 2.5f);
            if (aircraft.radarAlt < 0.1f && climb < 0f)
                aircraft.GetControlsFilter().SetAutoHover(enabled: false);
            return Mathf.Clamp(1.6f * error + verticalIntegral, -4f, 4f);
        }

        // Local angular-rate command (x pitch nose-down, y yaw nose-right, z roll left), from stick and holds.
        Vector3 AttitudeRateCommand(Transform root, float airspeed)
        {
            float pitchAngle = Mathf.Asin(Mathf.Clamp(root.forward.y, -1f, 1f)) * Mathf.Rad2Deg;          // nose up +
            float rollAngle = Mathf.Atan2(-root.right.y, root.up.y) * Mathf.Rad2Deg;                     // right wing down +
            bool stickFree = Mathf.Abs(rawPitch) < 0.05f && Mathf.Abs(rawRoll) < 0.05f;
            float desiredPitch, desiredRoll;
            Vector3 rate = Vector3.zero;

            if (autoHover && hoverAuthority > 0f)

            {
                // Attitude command; with the stick free, tilt to null ground speed (translational rate hold).
                if (stickFree)
                {
                    Vector3 v = aircraft.rb.velocity; v.y = 0f;
                    Vector3 fwd = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
                    Vector3 right = Vector3.Cross(Vector3.up, fwd);
                    // Translation is the outer, lower-priority loop: it fades out while the attitude is disturbed
                    // (for example after an actuator failure) so it never competes with recovering level attitude.
                    float disturbance = Mathf.Max(Mathf.Abs(pitchAngle), Mathf.Abs(rollAngle));
                    float translationAuthority = 1f - Mathf.InverseLerp(10f, 20f, disturbance);
                    float aForward = translationAuthority * Mathf.Clamp(-0.35f * Vector3.Dot(v, fwd), -1.5f, 1.5f);
                    float aRight = translationAuthority * Mathf.Clamp(-0.35f * Vector3.Dot(v, right), -1.5f, 1.5f);
                    desiredPitch = -Mathf.Atan2(aForward, 9.81f) * Mathf.Rad2Deg;
                    desiredRoll = Mathf.Atan2(aRight, 9.81f) * Mathf.Rad2Deg;
                }
                else
                {
                    desiredPitch = -rawPitch * 12f;
                    desiredRoll = rawRoll * 12f;
                }
                holdAttitude = false;
            }
            else
            {
                // Rate command, attitude hold when the stick is released.
                if (!stickFree)
                {
                    holdAttitude = false;
                    rate.x = rawPitch * 15f * Mathf.Deg2Rad;
                    rate.z = -rawRoll * 25f * Mathf.Deg2Rad;
                    desiredPitch = pitchAngle; desiredRoll = rollAngle;
                }
                else
                {
                    if (!holdAttitude) { holdAttitude = true; holdPitch = pitchAngle; holdRoll = Mathf.Abs(rollAngle) < 5f ? 0f : rollAngle; }
                    desiredPitch = holdPitch; desiredRoll = holdRoll;
                }
            }

            // Angle-of-attack protection in wing-borne transition: only meaningful with real forward airflow over the
            // hull (a vertical descent has a large but irrelevant flow angle).
            Vector3 local = root.InverseTransformDirection(aircraft.rb.velocity);
            if (local.z > 40f)
            {
                float aoa = Mathf.Atan2(-local.y, local.z) * Mathf.Rad2Deg;
                if (aoa > 18f) { desiredPitch = Mathf.Min(desiredPitch, pitchAngle - (aoa - 18f)); rate.x = Mathf.Max(rate.x, (aoa - 18f) * 0.02f); }
            }

            desiredPitch = Mathf.Clamp(desiredPitch, -PitchEnvelope, PitchEnvelope);
            desiredRoll = Mathf.Clamp(desiredRoll, -RollEnvelope, RollEnvelope);
            // Attitude error as a local rotation axis: rotate the current up vector onto the desired one.
            Vector3 flatForward = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
            Quaternion desired = Quaternion.LookRotation(flatForward, Vector3.up)
                * Quaternion.Euler(-desiredPitch, 0f, -desiredRoll);
            Vector3 error = root.InverseTransformDirection(Vector3.Cross(root.up, desired * Vector3.up));
            error += root.InverseTransformDirection(Vector3.Cross(root.forward, desired * Vector3.forward)) * 0.5f;
            if (holdAttitude || autoHover)
            {
                rate.x += ShapeRate(error.x, AlphaLimit.x);
                rate.z += ShapeRate(error.z, AlphaLimit.z);
            }
            rate.x = Mathf.Clamp(rate.x, -MaxAttitudeRate, MaxAttitudeRate);
            rate.z = Mathf.Clamp(rate.z, -1.6f * MaxAttitudeRate, 1.6f * MaxAttitudeRate);
            // Attitude envelope protection: whatever the pilot or autopilot asks for, the permitted rate towards a limit
            // shrinks to zero at the limit and reverses beyond it (nose-down rate is +x, roll-left rate is +z).
            rate.x = Mathf.Clamp(rate.x, -(PitchEnvelope - pitchAngle) * Mathf.Deg2Rad, (pitchAngle + PitchEnvelope) * Mathf.Deg2Rad);
            rate.z = Mathf.Clamp(rate.z, -(RollEnvelope - rollAngle) * 1.5f * Mathf.Deg2Rad, (RollEnvelope + rollAngle) * 1.5f * Mathf.Deg2Rad);

            // Heading hold with the pedals free, yaw rate command otherwise.
            float headingNow = Mathf.Atan2(root.forward.x, root.forward.z) * Mathf.Rad2Deg;
            if (Mathf.Abs(rawYaw) < 0.05f)
            {
                if (!holdHeading) { holdHeading = true; heading = headingNow; }
                rate.y = Mathf.Clamp(Mathf.DeltaAngle(headingNow, heading) * Mathf.Deg2Rad * 0.8f, -0.3f, 0.3f);
            }
            else
            {
                holdHeading = false;
                rate.y = rawYaw * 20f * Mathf.Deg2Rad;
            }
            return rate;
        }

        // Even split of a lift demand across the available actuators, used only to settle the redundant directions.
        static float[] Nominal(float[] hi, float lift)
        {
            float total = hi.Sum();
            var p = new float[hi.Length];
            for (int k = 0; k < hi.Length; k++) p[k] = total > 1f ? hi[k] * Mathf.Clamp01(lift / total) : 0f;
            return p;
        }

        // Rate towards an attitude error (radians) that can still be stopped with half the available acceleration.
        static float ShapeRate(float error, float alphaLimit)
        {
            float magnitude = Mathf.Min(1f * Mathf.Abs(error), Mathf.Sqrt(alphaLimit * Mathf.Abs(error)));
            return Mathf.Sign(error) * magnitude;
        }

        // Bounded weighted least squares for six actuator magnitudes: minimise
        //   w_lift (vertical force - demand)^2 + 100 (pitch, roll moment - demand)^2 + small pull to a nominal split
        // subject to each actuator's current bounds, by cyclic coordinate descent from the previous solution. Moments
        // take priority, so an actuator that cannot meet a demand is never exceeded and lost lift shows up as a sink
        // rate rather than a roll or pitch-over. Forces are scaled by the aircraft weight, moments by weight x 5 m.
        public static float[] Allocate(Vector3[] r, Vector3[] d, float[] lo, float[] hi, float lift, Vector3 moment,
            float collectiveWeight, float[] preference, float weight, float[] warmStart, int sweeps = 60)
        {
            const int n = 6;
            const double momentWeight = 100.0, lambda = 3e-3;
            double fScale = Math.Max(weight, 1f), ratio = 1.0 / 5.0;
            var A = new double[3, n];
            for (int k = 0; k < n; k++)
            {
                Vector3 m = Vector3.Cross(r[k], d[k]);
                A[0, k] = d[k].y; A[1, k] = m.x * ratio; A[2, k] = m.z * ratio;
            }
            var b = new[] { lift / fScale, moment.x / fScale * ratio, moment.z / fScale * ratio };
            var w = new[] { Math.Max(collectiveWeight, 1e-3), momentWeight, momentWeight };
            var H = new double[n, n]; var g = new double[n];
            for (int i = 0; i < n; i++)
            {
                for (int row = 0; row < 3; row++)
                {
                    g[i] += w[row] * A[row, i] * b[row];
                    for (int j = 0; j < n; j++) H[i, j] += w[row] * A[row, i] * A[row, j];
                }
                H[i, i] += lambda;
                g[i] += lambda * preference[i] / fScale;
            }
            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = Math.Min(Math.Max((warmStart?[i] ?? 0f) / fScale, lo[i] / fScale), hi[i] / fScale);
            for (int sweep = 0; sweep < sweeps; sweep++)
                for (int i = 0; i < n; i++)
                {
                    double lower = lo[i] / fScale, upper = hi[i] / fScale;
                    if (upper - lower < 1e-9) { x[i] = upper; continue; }
                    double s = g[i];
                    for (int j = 0; j < n; j++) if (j != i) s -= H[i, j] * x[j];
                    x[i] = Math.Min(Math.Max(s / H[i, i], lower), upper);
                }
            var result = new float[n];
            for (int k = 0; k < n; k++) result[k] = Mathf.Clamp((float)(x[k] * fScale), lo[k], hi[k]);
            return result;
        }
        // Hot-gas ingestion: exhaust pointed at nearby ground at low forward speed raises inlet temperature.
        // Severity 0..1 from exhaust-to-ground distance (full below 3 m, none beyond 20 m) and airspeed (none above
        // 25 m/s). Sustained severity above 0.8 flames an engine out; it relights after 8 s once severity has dropped.
        void UpdateHotGas(Transform root, float airspeed, float dt)
        {
            float speedFactor = 1f - Mathf.Clamp01(airspeed / 25f);
            float vertical = Mathf.Clamp01(Mathf.Sin(nozzleAngle * Mathf.Deg2Rad));
            if (Time.timeSinceLevelLoad - lastGroundCheck >= 0.1f)
            {
                lastGroundCheck = Time.timeSinceLevelLoad;
                float nearest = 30f;
                foreach (var p in new[] { exits[0].position, exits[1].position, root.TransformPoint(LiftJetPositions[0]), root.TransformPoint(LiftJetPositions[2]) })
                    if (Physics.Raycast(p, -root.up, out var hit, 30f, PhysicsLayers.StaticsMask | PhysicsLayers.ShipsMask))
                        nearest = Mathf.Min(nearest, hit.distance);
                groundProximity = 1f - Mathf.InverseLerp(3f, 20f, nearest);
            }
            hgiMain = speedFactor * vertical * groundProximity;
            hgiLift = liftJetsRunning ? speedFactor * groundProximity : 0f;
            for (int k = 0; k < 6; k++)
            {
                float severity = k < 4 ? hgiLift : hgiMain;
                if (flameoutTimer[k] > 0f)
                {
                    flameoutTimer[k] -= dt;
                    if (flameoutTimer[k] <= 0f && severity > 0.5f) flameoutTimer[k] = 1f;
                    continue;
                }
                hotTimer[k] = severity > 0.8f ? hotTimer[k] + dt : 0f;
                if (hotTimer[k] > 2f)
                {
                    flameoutTimer[k] = 8f;
                    hotTimer[k] = 0f;
                    Report(k < 4 ? "LIFT JET FLAMEOUT (HOT GAS)" : "ENGINE FLAMEOUT (HOT GAS)");
                }
            }
        }

        float lastGroundCheck, groundProximity;

        void SetDoorDrag(bool open)
        {
            if (open == dragApplied) return;
            dragApplied = open;
            // Open lift-jet doors and inlets: about 1 m2 of extra drag area per pod.
            foreach (var part in liftJetParts.Distinct())
                if (part is AeroPart aero) aero.ModifyDrag(open ? 2f : -2f);
        }

        void Update()
        {
            if (flameMounts == null || exits == null) return;
            // Visual exhaust follows the swivel nozzle about each exit.
            var turn = Quaternion.Euler(-nozzleAngle, 0f, 0f);
            foreach (var mount in flameMounts)
                if (mount != null) mount.rotation = transform.rotation * turn;
        }
    }
}
