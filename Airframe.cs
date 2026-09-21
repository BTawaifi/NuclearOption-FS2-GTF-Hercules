using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FS2Hercules
{
    // Replaces the cloned Ifrit's physics skeleton with one laid out on the Hercules hull. The 34 Ifrit parts keep
    // their names, joints, damage wiring and ids (HUD, engines and networking refer to them), but each is moved onto a
    // measured region of the FS2 mesh and gets a box collider, mass, lift area, drag area and fuel matching that
    // region. Regions come from slicing hercules.nomesh in aircraft space (see the audit report): fuselage x +-2,
    // pods x 1.9..6.2 and y -4..3.8, nose to z 10.3, pod tails to z -7.9, ventral fin to y -7.4.
    public static class HerculesAirframe
    {
        const int BodyFoil = 0, TailFoil = 1;
        const float InterferenceDrag = 1.1f; // junction and excrescence drag on top of the component build-up

        sealed class Spec
        {
            public string Name;
            public Vector3 Center, Size;
            public float Mass, WingArea, CdA, Fuel;
            public int Foil = BodyFoil;
            public Vector3 LiftUp = Vector3.up;
            public bool KeepTransform, Mirrored;
        }

        // Right-hand and centreline parts in aircraft space; *_R and *_FR parts are mirrored to *_L and *_FL.
        // Masses total the inherited 16,040 kg empty mass and include two RD-41-class lift jets per pod front
        // (about 300 kg each) and a ~1,750 kg main engine per pod. CdA is subsonic zero-lift drag area,
        // Cd x frontal area: fuselage 0.08 x 19.9 m2, each pod 0.10 x 27.6 m2, fins 0.02, plus 10% interference.
        static readonly Spec[] Specs =
        {
            new Spec { Name = "root",       Center = new Vector3(0, 0, -0.25f),    Size = new Vector3(3.9f, 5.4f, 3.5f), Mass = 1480, WingArea = 14, CdA = 0.25f, Fuel = 1800, KeepTransform = true },
            new Spec { Name = "fuselage_F", Center = new Vector3(0, 0, 3.2f),      Size = new Vector3(3.9f, 5.2f, 3.4f), Mass = 900,  WingArea = 13, CdA = 0.45f, Fuel = 200, KeepTransform = true },
            new Spec { Name = "cockpit",    Center = new Vector3(0, 0.5f, 5.9f),   Size = new Vector3(2.6f, 2.8f, 2.0f), Mass = 650,  WingArea = 5,  CdA = 0.35f, KeepTransform = true },
            new Spec { Name = "nose",       Center = new Vector3(0, 0.45f, 8.6f),  Size = new Vector3(2.2f, 2.4f, 3.4f), Mass = 150,  WingArea = 4,  CdA = 0.25f },
            new Spec { Name = "fuselage_R", Center = new Vector3(0, 0, -3.6f),     Size = new Vector3(3.6f, 4.2f, 3.2f), Mass = 1650, WingArea = 10, CdA = 0.29f, Fuel = 1600 },
            new Spec { Name = "tail",       Center = new Vector3(0, -4.9f, -0.9f), Size = new Vector3(0.8f, 4.2f, 4.0f), Mass = 250,  WingArea = 6,  CdA = 0.07f, Foil = TailFoil, LiftUp = Vector3.right },
            new Spec { Mirrored = true, Name = "intake_R",   Center = new Vector3(3.6f, 0.4f, 2.9f),   Size = new Vector3(3.4f, 4.6f, 2.8f), Mass = 1100, WingArea = 8,  CdA = 1.10f },
            new Spec { Mirrored = true, Name = "wingRoot_R", Center = new Vector3(3.9f, 1.7f, -0.25f), Size = new Vector3(3.9f, 3.2f, 3.5f), Mass = 500,  WingArea = 12, CdA = 0.30f, Fuel = 600 },
            new Spec { Mirrored = true, Name = "gearbay_R",  Center = new Vector3(3.9f, -1.7f, -0.25f),Size = new Vector3(3.9f, 3.6f, 3.5f), Mass = 650,  WingArea = 5,  CdA = 0.30f, Fuel = 1100, LiftUp = Vector3.right },
            new Spec { Mirrored = true, Name = "engine_R",   Center = new Vector3(4.1f, 0, -4.4f),     Size = new Vector3(4.2f, 7.0f, 4.8f), Mass = 2150, WingArea = 13, CdA = 0.40f, Fuel = 600 },
            new Spec { Mirrored = true, Name = "nozzle_R",   Center = new Vector3(4.0f, 0.3f, -7.35f), Size = new Vector3(3.8f, 5.6f, 1.1f), Mass = 350,  WingArea = 5,  CdA = 0.66f, LiftUp = Vector3.right },
            new Spec { Mirrored = true, Name = "tail_R",     Center = new Vector3(4.8f, 3.85f, -6.0f), Size = new Vector3(1.9f, 0.35f, 1.4f), Mass = 150, WingArea = 2,  CdA = 0.02f, Foil = TailFoil },
            new Spec { Mirrored = true, Name = "elevator_R", Center = new Vector3(4.8f, 3.85f, -8.2f), Size = new Vector3(1.9f, 0.3f, 3.0f),  Mass = 70,  WingArea = 6,  CdA = 0.01f, Foil = TailFoil },
            new Spec { Mirrored = true, Name = "wing1_R",    Center = new Vector3(4.8f, -4.55f, -2.9f),Size = new Vector3(2.4f, 1.9f, 1.6f), Mass = 180,  WingArea = 3.5f, CdA = 0.07f, Foil = TailFoil, LiftUp = new Vector3(0.459f, 0.889f, 0) },
            new Spec { Mirrored = true, Name = "wingtip_R",  Center = new Vector3(6.5f, -4.9f, -2.6f), Size = new Vector3(0.7f, 1.4f, 1.2f), Mass = 40,   WingArea = 1,  CdA = 0.01f, Foil = TailFoil, LiftUp = new Vector3(0.459f, 0.889f, 0) },
            new Spec { Mirrored = true, Name = "wing2_R",    Center = new Vector3(5.7f, 2.3f, -1.0f),  Size = new Vector3(0.9f, 1.5f, 3.0f), Mass = 100,  WingArea = 6,  CdA = 0,     LiftUp = Vector3.right },
            new Spec { Mirrored = true, Name = "wing2_FR",   Center = new Vector3(5.2f, 1.5f, 2.6f),   Size = new Vector3(0.8f, 1.2f, 2.2f), Mass = 60,   WingArea = 3,  CdA = 0,     LiftUp = Vector3.right },
            new Spec { Mirrored = true, Name = "wing1_FR",   Center = new Vector3(2.3f, 2.6f, 2.6f),   Size = new Vector3(0.8f, 1.0f, 2.2f), Mass = 60,   WingArea = 1,  CdA = 0 },
            new Spec { Mirrored = true, Name = "aileron_R",  Center = new Vector3(5.9f, 3.4f, -3.2f),  Size = new Vector3(0.8f, 0.2f, 1.2f), Mass = 30,   WingArea = 1.5f, CdA = 0,   Foil = TailFoil },
            new Spec { Mirrored = true, Name = "flap_R",     Center = new Vector3(5.2f, -5.2f, -4.3f), Size = new Vector3(1.6f, 0.2f, 1.2f), Mass = 40,   WingArea = 1.5f, CdA = 0,   Foil = TailFoil, LiftUp = new Vector3(0.459f, 0.889f, 0) },
        };

        // Control surfaces: hinge at the leading edge, rotating about the aircraft's lateral axis.
        // Ifrit sign conventions are kept (left roll range negative, pitch range negative).
        static readonly Dictionary<string, (float pitch, float roll, float yaw)> SurfaceRanges = new Dictionary<string, (float, float, float)>
        {
            { "elevator_R", (-30f, 20f, 0f) }, { "elevator_L", (-30f, -20f, 0f) },  // stronger differential elevon authority for faster roll response
            { "aileron_R", (0f, 35f, 0f) },    { "aileron_L", (0f, -35f, 0f) },     // increased roll deflection; area and mass unchanged
            { "flap_R", (0f, 0f, -20f) },      { "flap_L", (0f, 0f, 20f) },         // lower canted fin rudders
        };

        // Pad contacts. The centre of mass stands ~8.7 m above them, so the stance is set by tip-over, not by the
        // Ifrit's wheel positions: mains under the lower fin tips (x +-6.5) and the nose pad well forward (z 8.0) put the
        // nose-to-main tipping line 4.7 m from the centre of mass, i.e. sideways tip-over needs about 5.3 m/s2, above
        // the 0.4 g at which the pads start to slide; nose-over under braking needs ~10 m/s2. Nose pad load ~15%.
        public static readonly Vector3 NoseGearContact = new Vector3(0, -8.05f, 8.0f);
        public static readonly Vector3 MainGearContactR = new Vector3(6.5f, -7.95f, -3.0f);

        public static float TotalMass => Specs.Sum(s => s.Mirrored ? 2 * s.Mass : s.Mass);
        public static float TotalFuel => Specs.Sum(s => s.Mirrored ? 2 * s.Fuel : s.Fuel);

        public static void Fit(GameObject prefab, AircraftParameters parameters)
        {
            var root = prefab.transform;
            var parts = prefab.GetComponentsInChildren<UnitPart>(true).ToDictionary(p => p == root.GetComponent<UnitPart>() ? "root" : p.name);
            var specs = new Dictionary<string, Spec>();
            foreach (var s in Specs)
            {
                specs[s.Name] = s;
                if (!s.Mirrored) continue;
                var left = s.Name.EndsWith("_FR") ? s.Name.Substring(0, s.Name.Length - 2) + "FL" : s.Name.Substring(0, s.Name.Length - 1) + "L";
                specs[left] = new Spec
                {
                    Name = left, Center = Mirror(s.Center), Size = s.Size, Mass = s.Mass, WingArea = s.WingArea, CdA = s.CdA,
                    Fuel = s.Fuel, Foil = s.Foil, LiftUp = Mirror(s.LiftUp), KeepTransform = s.KeepTransform
                };
            }
            var missing = specs.Keys.Where(k => !parts.ContainsKey(k)).Concat(parts.Keys.Where(k => !specs.ContainsKey(k))).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("Hercules airframe parts do not match the base prefab: " + string.Join(", ", missing));

            var tankTemplate = prefab.GetComponentsInChildren<FuelTank>(true).First();
            // Parents come before children in GetComponentsInChildren, so each part is placed after its ancestors.
            foreach (var part in prefab.GetComponentsInChildren<UnitPart>(true))
            {
                var spec = specs[part == root.GetComponent<UnitPart>() ? "root" : part.name];
                var t = part.transform;
                if (!spec.KeepTransform) t.position = root.TransformPoint(spec.Center);

                // Box hitbox on the Hercules region. Unity derives the part's centre of mass and inertia from it.
                // Remove EVERY inherited Ifrit collider on this UnitPart before installing the Hercules hull box.
                // Keeping old Box/Capsule/Sphere colliders as well as the new Hercules box can leave invisible Ifrit
                // geometry below the Hercules gear or outside the new hull, producing impact damage immediately on
                // spawn and other bogus world contacts. Preserve the first physics material so tyre/hull contact
                // behavior does not silently change.
                var inheritedColliders = part.GetComponents<Collider>();
                var material = inheritedColliders.FirstOrDefault()?.sharedMaterial;
                foreach (var old in inheritedColliders) UnityEngine.Object.DestroyImmediate(old);
                var box = part.gameObject.AddComponent<BoxCollider>();
                box.sharedMaterial = material;
                box.center = t.InverseTransformPoint(root.TransformPoint(spec.Center));
                box.size = spec.Size;

                part.mass = spec.Mass;
                if (part is AeroPart aero)
                {
                    var a = Traverse.Create(aero);
                    a.Field("wingArea").SetValue(spec.WingArea);
                    aero.dragArea = 2f * spec.CdA * InterferenceDrag; // AeroJob_Math applies 0.25 * rho * v^2 * dragArea
                    a.Field("centerOfLift").SetValue(Vector3.zero);
                    a.Field("airfoil").SetValue(spec.Foil);
                    a.Field("streamlining").SetValue(0f);
                    if (part.GetComponent<ControlSurface>() == null)
                        a.Field("liftNormal").SetValue(LiftNormal(t, root, spec.LiftUp));
                }
                FitTank(part, spec.Fuel, tankTemplate);
            }

            foreach (var surface in prefab.GetComponentsInChildren<ControlSurface>(true))
                FitSurface(surface, root, specs);

            // The Ifrit's leading-edge devices add wing area at low speed; the Hercules hull has none.
            foreach (var device in prefab.GetComponentsInChildren<HighLiftDevice>(true))
                UnityEngine.Object.DestroyImmediate(device);

            FitAirfoils(parameters);
            prefab.AddComponent<HerculesSelfCollision>();
        }

        static Vector3 Mirror(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        static Transform LiftNormal(Transform part, Transform root, Vector3 up)
        {
            if (up == Vector3.up) return part;
            var normal = new GameObject("herc_liftNormal").transform;
            normal.SetParent(part, false);
            normal.rotation = root.rotation * Quaternion.LookRotation(Vector3.forward, up.normalized);
            return normal;
        }

        // Fuel capacity lives in FuelTank components on parts. Existing tanks are resized; parts that need a tank but
        // had none get a copy of an Ifrit tank's serialized settings; tanks on parts that now hold no fuel are removed.
        static void FitTank(UnitPart part, float capacity, FuelTank template)
        {
            var tank = part.GetComponent<FuelTank>();
            if (capacity <= 0f)
            {
                if (tank != null) UnityEngine.Object.DestroyImmediate(tank);
                return;
            }
            if (tank == null)
            {
                tank = part.gameObject.AddComponent<FuelTank>();
                foreach (var field in AccessTools.GetDeclaredFields(typeof(FuelTank)).Where(f => !f.IsStatic))
                    field.SetValue(tank, field.GetValue(template));
                Traverse.Create(tank).Field("part").SetValue(part);
                Traverse.Create(tank).Field("connectedTanks").SetValue(Array.Empty<FuelTank>());
            }
            Traverse.Create(tank).Field("fuelCapacity").SetValue(capacity);
        }

        static void FitSurface(ControlSurface surface, Transform root, Dictionary<string, Spec> specs)
        {
            var cs = Traverse.Create(surface);
            var part = cs.Field("attachedSurface").GetValue<UnitPart>();
            var hinge = cs.Field("visibleMesh").GetValue<GameObject>().transform;
            var spec = specs[part.name];
            var liftNormal = Traverse.Create(part).Field("liftNormal").GetValue<Transform>();

            // Hinge line along the aircraft's lateral axis at the surface leading edge, tilted to the surface plane.
            var surfaceRotation = root.rotation * Quaternion.LookRotation(Vector3.forward, spec.LiftUp.normalized);
            hinge.SetPositionAndRotation(root.TransformPoint(spec.Center + Vector3.forward * spec.Size.z / 2f), surfaceRotation);
            for (var t = liftNormal; t != null && t != hinge; t = t.parent)
            {
                t.localRotation = Quaternion.identity;
                t.localPosition = Vector3.zero;
            }
            liftNormal.position = root.TransformPoint(spec.Center);

            if (SurfaceRanges.TryGetValue(part.name, out var r))
            {
                cs.Field("pitchRange").SetValue(r.pitch);
                cs.Field("rollRange").SetValue(r.roll);
                cs.Field("yawRange").SetValue(r.yaw);
            }
        }

        // Airfoil 0 (body): low-aspect-ratio lifting body. Helmbold slope for AR = b^2/S = 13.7^2/138 = 1.36 is
        // 2*pi*A/(2+sqrt(A^2+4)) = 1.93 /rad; vortex lift keeps CL rising to about 1.0 at 35 deg, then it falls to 0
        // at 90 deg. With no leading-edge suction the force is normal to the planform, so CD = CL tan(alpha), reaching
        // flat-plate CD 1.2 broadside. Parasite drag is in each part's dragArea. Airfoil 1 (tail) keeps the Ifrit
        // elevator curves for the thinner stabilators and fins.
        static void FitAirfoils(AircraftParameters parameters)
        {
            var body = parameters.airfoils[0];
            body.name = "GTF_Hercules_body";
            var lift = new AnimationCurve();
            var drag = new AnimationCurve();
            for (int deg = -180; deg <= 180; deg += 5)
            {
                lift.AddKey(deg * Mathf.Deg2Rad, BodyLift(deg));
                drag.AddKey(deg * Mathf.Deg2Rad, BodyDrag(deg));
            }
            body.liftCoef = lift;
            body.dragCoef = drag;
            parameters.airfoils[1].name = "GTF_Hercules_tail";
        }

        // Lift coefficient per unit planform area. Negative and reverse-flow angles mirror the positive branch.
        public static float BodyLift(float deg)
        {
            float sign = Mathf.Sign(deg);
            float a = Mathf.Abs(deg);
            if (a > 90f) return -BodyLift(sign * (180f - a)) * 0.6f;
            float cl = a <= 25f ? 1.93f * a * Mathf.Deg2Rad
                : a <= 35f ? Mathf.Lerp(0.842f, 1.0f, (a - 25f) / 10f)
                : a <= 45f ? Mathf.Lerp(1.0f, 0.9f, (a - 35f) / 10f)
                : Mathf.Lerp(0.9f, 0f, (a - 45f) / 45f);
            return sign * cl;
        }

        // Normal-force drag: CL tan(alpha) to 45 deg, easing to broadside CD 1.2 at 90 deg; reverse flow mirrors it.
        public static float BodyDrag(float deg)
        {
            float a = Mathf.Abs(deg);
            if (a > 90f) a = 180f - a;
            return a <= 45f ? Mathf.Abs(BodyLift(a)) * Mathf.Tan(a * Mathf.Deg2Rad)
                : 0.9f + 0.3f * Mathf.Sin((a - 45f) / 45f * Mathf.PI / 2f);
        }
    }

    // Box hitboxes of neighbouring parts touch or overlap the way the hull does. Parts become separate jointed
    // rigidbodies when the aircraft is near the camera, so collisions between them are ignored; contacts with the
    // world and other units are unchanged.
    public class HerculesSelfCollision : MonoBehaviour
    {
        void Awake()
        {
            var colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                for (int j = i + 1; j < colliders.Length; j++)
                    Physics.IgnoreCollision(colliders[i], colliders[j]);
        }
    }
}
