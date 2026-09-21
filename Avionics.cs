using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FS2Hercules
{
    // Passive infrared search and track (atmospheric-refit choice; FreeSpace 2 has no such sensor). It reports aircraft
    // and missiles through the same faction tracking path as the stock visual and radar detectors, without emitting
    // anything. Detection range follows the inverse-square law on the target's summed exhaust IR intensity (the game's
    // IRSource units): 20 km for intensity 5 (about a stock jet at military power), capped at 40 km, inside a +-60 deg
    // field of regard around the nose, blocked by terrain. Clouds, humidity and aspect are not modelled.
    public class HerculesIrst : TargetDetector
    {
        public const float ReferenceRange = 20000f, ReferenceIntensity = 5f, MaxRange = 40000f, FieldOfRegard = 60f;
        static readonly AccessTools.FieldRef<Unit, List<IRSource>> Sources = AccessTools.FieldRefAccess<Unit, List<IRSource>>("IRSources");

        public static float DetectionRange(float intensity) =>
            Mathf.Min(MaxRange, ReferenceRange * Mathf.Sqrt(Mathf.Max(intensity, 0f) / ReferenceIntensity));

        public static void Install(GameObject prefab, Unit unit, UnitPart nosePart)
        {
            var irst = nosePart.gameObject.AddComponent<HerculesIrst>();
            var t = Traverse.Create(irst);
            t.Field("attachedUnit").SetValue(unit);
            t.Field("scanner").SetValue(nosePart.transform);
            t.Field("part").SetValue(nosePart);
            t.Field("checkInterval").SetValue(3f);
            t.Field("alertCheckInterval").SetValue(1.5f);
            t.Field("visualRange").SetValue(0f);
            t.Field("magnification").SetValue(1f);
            t.Field("maxSpeed").SetValue(5000f);
            t.Field("rotators").SetValue(new Rotator[0]);
        }

        protected override void TargetSearch()
        {
            var origin = scanner.GlobalPosition();
            BattlefieldGrid.GetUnitsInRangeNonAlloc(origin, MaxRange, unitsInRange);
            foreach (var unit in unitsInRange)
            {
                if (unit == null || unit.disabled || unit == attachedUnit || unit.NetworkHQ == attachedUnit.NetworkHQ) continue;
                if (!(unit is Aircraft) && !(unit is Missile)) continue;
                if (detectedTargets.Contains(unit)) continue;
                float intensity = 0f;
                var sources = Sources(unit);
                if (sources == null) continue;
                foreach (var source in sources)
                    if (source != null && !source.flare) intensity += source.intensity;
                Vector3 toTarget = unit.transform.position - scanner.position;
                float distance = toTarget.magnitude;
                if (distance > DetectionRange(intensity) || Vector3.Angle(scanner.forward, toTarget) > FieldOfRegard) continue;
                if (Physics.Linecast(scanner.position, unit.transform.position, PhysicsLayers.StaticsMask)) continue;
                DetectTarget(unit);
            }
        }
    }
}
