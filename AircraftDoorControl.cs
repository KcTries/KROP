using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using QOL_Realisim_Fixes.Patches;
using Rewired;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Player-controlled canopy and cargo ramp toggles, for photo ops and
    // realism (bay doors are left alone -- they already open when weapons are
    // released). Driven by two native Controls-menu actions (see
    // AircraftDoorActionsPatch), polled from a driver on the plugin's own
    // GameObject, same as PCR.
    //
    // CANOPIES -- vanilla only ever OPENS them (Aircraft.OpenCanopies(), used
    // when you eject while landed); nothing closes one. Opening goes through
    // that same vanilla path. Closing reverses the canopy's own private
    // openAmount (and re-animates its hinges the way Canopy.Update does), so
    // the canopy's real state always matches what's on screen -- which is
    // what makes a later vanilla open (eject on the ground) pick up from the
    // current angle instead of snapping. Only works while the aircraft is
    // effectively stopped.
    //
    // CARGO RAMPS -- driven through the game's own CargoRamp.OpenDoor(),
    // asked to stay open for an effectively unlimited time; closing zeroes
    // that timer so the ramp's own state machine closes and re-latches it.
    // Vanilla cargo release calls OpenDoor() on the ramp over and over (via
    // Hardpoint.SpringOpenBayDoors) with its own short duration, which would
    // overwrite the hold and shut a ramp the player opened right after a
    // drop -- CargoRampHoldPatch re-asserts the hold after each of those
    // calls. Works at any speed.
    internal static class AircraftDoorControl
    {
        // Opening: effectively stopped. Closing: anywhere under this.
        private const float MaxCanopyOpenSpeedKmh = 1f;
        private const float MaxCanopyOpenSpeedMetersPerSecond = MaxCanopyOpenSpeedKmh / 3.6f;
        private const float MaxCanopyCloseSpeedKmh = 100f;
        private const float MaxCanopyCloseSpeedMetersPerSecond = MaxCanopyCloseSpeedKmh / 3.6f;

        // Past this, a canopy counts as "open" for toggle purposes.
        private const float CanopyOpenThreshold = 0.5f;

        // An open canopy tears off above a speed that is rolled once per
        // aircraft, uniformly between these (km/h, inclusive).
        private const int CanopyBreakOffMinKmh = 150;
        private const int CanopyBreakOffMaxKmh = 430;

        // Aircraft whose Canopy components are doors rather than a canopy:
        // above the speed limit they close instead of breaking off. Ibis,
        // Tarantula, and the Chicane (side doors, like an Apache).
        private static readonly HashSet<string> DoorAircraftKeys = new HashSet<string>
        {
            "UtilityHelo1", // UH-90 Ibis
            "QuadVTOL1",    // VL-49 Tarantula
            "AttackHelo1",  // SAH-46 Chicane
        };

        // Effectively forever; CargoRamp.Open() just counts this down.
        private const float RampHoldSeconds = 1e9f;

        // Read every frame (break-off check + muffle blend), so a typed field
        // ref rather than a reflection FieldInfo.GetValue.
        private static readonly AccessTools.FieldRef<Aircraft, Canopy[]> CanopiesRef =
            AccessTools.FieldRefAccess<Aircraft, Canopy[]>("canopies");
        private static readonly FieldInfo CanopyHingesField = AccessTools.Field(typeof(Canopy), "canopyHinges");
        private static readonly MethodInfo HingeAnimateMethod =
            AccessTools.Method(AccessTools.Inner(typeof(Canopy), "CanopyHinge"), "Animate");

        private static readonly AccessTools.FieldRef<Canopy, float> OpenAmountRef =
            AccessTools.FieldRefAccess<Canopy, float>("openAmount");
        private static readonly AccessTools.FieldRef<Canopy, float> OpenSpeedRef =
            AccessTools.FieldRefAccess<Canopy, float>("openSpeed");
        private static readonly AccessTools.FieldRef<Canopy, bool> OpeningRef =
            AccessTools.FieldRefAccess<Canopy, bool>("opening");
        private static readonly AccessTools.FieldRef<Canopy, bool> FiringRef =
            AccessTools.FieldRefAccess<Canopy, bool>("firing");
        private static readonly AccessTools.FieldRef<Canopy, Vector3> EjectionForceRef =
            AccessTools.FieldRefAccess<Canopy, Vector3>("ejectionForce");
        private static readonly AccessTools.FieldRef<BayDoor, float> RampOpenTimerRef =
            AccessTools.FieldRefAccess<BayDoor, float>("openTimer");

        private static readonly List<Canopy> _closingCanopies = new List<Canopy>();
        private static readonly HashSet<CargoRamp> _heldRamps = new HashSet<CargoRamp>();
        private static readonly ConditionalWeakTable<Aircraft, CargoRamp[]> _rampCache =
            new ConditionalWeakTable<Aircraft, CargoRamp[]>();

        private static bool _driverEnsured;

        // pluginGameObject is the BepInEx plugin's own GameObject -- a
        // separately created, DontDestroyOnLoad'd one does not reliably
        // survive this game's early bootstrap (see PeriodicCountermeasureControl).
        internal static void Initialize(GameObject pluginGameObject)
        {
            if (_driverEnsured)
            {
                return;
            }
            _driverEnsured = true;
            pluginGameObject.AddComponent<AircraftDoorDriver>();
        }

        // Called on mission/scene teardown so nothing holds references into
        // the mission that just ended.
        internal static void ClearAll()
        {
            _closingCanopies.Clear();
            _heldRamps.Clear();
            SoundPropagation.CanopyOpenBlend = 0f;
        }

        internal static void Tick()
        {
            TickCanopyClose();

            if (!GameManager.GetLocalAircraft(out Aircraft aircraft) || aircraft == null)
            {
                SoundPropagation.CanopyOpenBlend = 0f;
                return;
            }
            TickCanopyBreakOff(aircraft);
            SoundPropagation.CanopyOpenBlend = ComputeCanopyOpenBlend(aircraft);

            Player input = GameManager.playerInput;
            if (input == null)
            {
                return;
            }

            if (AircraftDoorActionsPatch.CanopyActionId >= 0 && input.GetButtonDown(AircraftDoorActionsPatch.CanopyActionName))
            {
                ToggleCanopies(aircraft);
            }
            if (AircraftDoorActionsPatch.CargoRampActionId >= 0 && input.GetButtonDown(AircraftDoorActionsPatch.CargoRampActionName))
            {
                ToggleCargoRamps(aircraft);
            }
        }

        // ---------------------------------------------------------------
        // Canopies
        // ---------------------------------------------------------------

        private static Canopy[] GetCanopies(Aircraft aircraft)
        {
            return CanopiesRef(aircraft);
        }

        // Skips canopies that are gone: destroyed, shot off, or mid-jettison.
        private static bool IsUsable(Canopy canopy)
        {
            return canopy != null
                && canopy.attachedPart != null
                && !canopy.attachedPart.IsDetached()
                && !FiringRef(canopy);
        }

        private static void ToggleCanopies(Aircraft aircraft)
        {
            Canopy[] canopies = GetCanopies(aircraft);
            if (canopies == null || canopies.Length == 0 || aircraft.HasEjected())
            {
                return;
            }

            bool anyOpen = false;
            foreach (Canopy canopy in canopies)
            {
                if (IsUsable(canopy) && OpenAmountRef(canopy) > CanopyOpenThreshold)
                {
                    anyOpen = true;
                    break;
                }
            }

            // Closing is allowed anywhere under MaxCanopyCloseSpeedKmh;
            // opening only when (effectively) stopped.
            if (anyOpen)
            {
                if (aircraft.speed > MaxCanopyCloseSpeedMetersPerSecond)
                {
                    Report("Too Fast to Close the Canopy");
                    return;
                }
            }
            else if (aircraft.speed > MaxCanopyOpenSpeedMetersPerSecond)
            {
                Report("Canopies can only be opened when stationary.");
                return;
            }

            foreach (Canopy canopy in canopies)
            {
                if (!IsUsable(canopy))
                {
                    continue;
                }
                if (anyOpen)
                {
                    BeginClose(canopy);
                }
                else
                {
                    _closingCanopies.Remove(canopy);
                    canopy.OpenHinges();
                }
            }
        }

        // How far open the most-open of the local aircraft's canopies is, 0..1.
        // Feeds the cockpit audio filter (see SoundPropagation.CanopyOpenBlend).
        //
        // A canopy that is GONE (jettisoned or torn off, shot off, or its
        // object destroyed) leaves the cockpit open for good, so once any
        // canopy on an aircraft is seen to be gone the blend stays at 1 for
        // that aircraft's lifetime. (Treating a gone canopy as "not usable"
        // here, like the toggle does, made the muffling come back the moment
        // a canopy tore off -- Canopy.Eject() leaves its `firing` flag set
        // forever, and the ejected canopy object is destroyed ~10s later.)
        private sealed class CanopyTracker
        {
            public bool[] SeenAlive;
            public bool Lost;

            // Rolled once, the first time this aircraft is looked at, so each
            // spawned aircraft has its own (fixed) canopy break-off speed.
            public float BreakOffSpeedMetersPerSecond;
        }

        private static readonly ConditionalWeakTable<Aircraft, CanopyTracker> _canopyTrackers =
            new ConditionalWeakTable<Aircraft, CanopyTracker>();

        // Called twice a frame -- TryGetValue first, since GetValue with a
        // lambda capturing canopyCount would allocate a closure every call.
        private static CanopyTracker GetTracker(Aircraft aircraft, int canopyCount)
        {
            if (_canopyTrackers.TryGetValue(aircraft, out CanopyTracker tracker))
            {
                return tracker;
            }

            // Random.Range(int, int) excludes the max, hence the +1.
            int breakOffKmh = UnityEngine.Random.Range(CanopyBreakOffMinKmh, CanopyBreakOffMaxKmh + 1);
            if (VerboseLoggingConfig.Enabled.Value)
            {
                SoundPropagation.Log.LogInfo(
                    $"[DoorDiag] '{aircraft.name}' canopy break-off speed rolled: {breakOffKmh} km/h");
            }
            tracker = new CanopyTracker
            {
                SeenAlive = new bool[canopyCount],
                BreakOffSpeedMetersPerSecond = breakOffKmh / 3.6f,
            };
            _canopyTrackers.Add(aircraft, tracker);
            return tracker;
        }

        private static bool IsGone(Canopy canopy)
        {
            return FiringRef(canopy) || (canopy.attachedPart != null && canopy.attachedPart.IsDetached());
        }

        private static float ComputeCanopyOpenBlend(Aircraft aircraft)
        {
            Canopy[] canopies = GetCanopies(aircraft);
            if (canopies == null)
            {
                return 0f;
            }

            CanopyTracker tracker = GetTracker(aircraft, canopies.Length);
            float blend = 0f;
            for (int i = 0; i < canopies.Length && i < tracker.SeenAlive.Length; i++)
            {
                Canopy canopy = canopies[i];
                if (canopy == null)
                {
                    // Destroyed after having been seen alive (an array slot
                    // that was never populated doesn't count).
                    if (tracker.SeenAlive[i])
                    {
                        tracker.Lost = true;
                    }
                    continue;
                }
                tracker.SeenAlive[i] = true;
                if (IsGone(canopy))
                {
                    tracker.Lost = true;
                    continue;
                }
                blend = Mathf.Max(blend, Mathf.Clamp01(OpenAmountRef(canopy)));
            }
            return tracker.Lost ? 1f : blend;
        }

        // An open canopy at speed would be torn off by the airflow: above this
        // aircraft's rolled break-off speed (150-430 km/h), any canopy that's more than half open is
        // jettisoned with the game's own Canopy.Eject() (which detaches it,
        // gives it its own rigidbody and plays its ejection sound), but with
        // its jettison thrust zeroed so it just rips away in the airstream
        // instead of being fired off like a deliberate jettison. Local only,
        // like the toggle itself.
        //
        // Aircraft whose "canopies" are really doors (DoorAircraftKeys) are
        // not torn off: at the same speed they're force-closed instead, with
        // the same close animation the toggle uses.
        private static void TickCanopyBreakOff(Aircraft aircraft)
        {
            Canopy[] canopies = GetCanopies(aircraft);
            if (canopies == null)
            {
                return;
            }
            // This aircraft's own rolled break-off speed (see CanopyTracker).
            if (aircraft.speed <= GetTracker(aircraft, canopies.Length).BreakOffSpeedMetersPerSecond)
            {
                return;
            }
            string jsonKey = aircraft.definition != null ? aircraft.definition.jsonKey : null;
            bool isDoors = jsonKey != null && DoorAircraftKeys.Contains(jsonKey);

            foreach (Canopy canopy in canopies)
            {
                if (!IsUsable(canopy) || OpenAmountRef(canopy) <= CanopyOpenThreshold)
                {
                    continue;
                }
                if (isDoors)
                {
                    BeginClose(canopy);
                }
                else
                {
                    _closingCanopies.Remove(canopy);

                    // Most canopies are a separate mesh that gets its own
                    // rigidbody and collider when jettisoned, so a zero-thrust
                    // jettison just lets the airstream peel it away. A few
                    // (the A-19, and the Chicane's doors) are instead a real, damage-
                    // detachable AeroPart (attachedPart IS the ejection
                    // transform): the player's own aircraft runs complex
                    // physics, where that part already has its own rigidbody
                    // held by FixedJoints with collision disabled. Eject()
                    // destroys those joints, which re-enables collision
                    // between the canopy and the airframe it's still sitting
                    // inside (the A-19's titanium tub especially) -- with no
                    // thrust it never clears the airframe and ended up
                    // slipping back through it. Those keep vanilla's jettison
                    // thrust, which lifts them out the way a normal
                    // jettison does.
                    bool isPartCanopy = canopy.attachedPart != null
                        && canopy.attachedPart.transform == canopy.ejectionTransform;
                    if (!isPartCanopy)
                    {
                        EjectionForceRef(canopy) = Vector3.zero;
                    }
                    canopy.Eject();
                }
            }
        }

        private static void BeginClose(Canopy canopy)
        {
            // Stops vanilla's own opening animation (Canopy.Update only
            // advances while enabled && opening) so it can't fight ours.
            OpeningRef(canopy) = false;
            canopy.enabled = false;
            if (!_closingCanopies.Contains(canopy))
            {
                _closingCanopies.Add(canopy);
            }
        }

        // Stops any close animation in progress -- used when vanilla opens the
        // canopies itself (ejecting while landed), which then simply carries
        // on from wherever they are.
        internal static void CancelCanopyClose(Aircraft aircraft)
        {
            Canopy[] canopies = GetCanopies(aircraft);
            if (canopies == null)
            {
                return;
            }
            foreach (Canopy canopy in canopies)
            {
                _closingCanopies.Remove(canopy);
            }
        }

        private static void TickCanopyClose()
        {
            for (int i = _closingCanopies.Count - 1; i >= 0; i--)
            {
                Canopy canopy = _closingCanopies[i];
                if (canopy == null || FiringRef(canopy))
                {
                    _closingCanopies.RemoveAt(i);
                    continue;
                }

                float speed = Mathf.Max(OpenSpeedRef(canopy), 0.1f);
                float amount = OpenAmountRef(canopy) - speed * Time.deltaTime;
                if (amount <= 0f)
                {
                    amount = 0f;
                    _closingCanopies.RemoveAt(i);
                }
                OpenAmountRef(canopy) = amount;
                AnimateHinges(canopy, amount);
            }
        }

        // Same thing Canopy.Update does each frame while opening: every hinge's
        // Animate(openAmount).
        private static void AnimateHinges(Canopy canopy, float amount)
        {
            if (!(CanopyHingesField.GetValue(canopy) is Array hinges))
            {
                return;
            }
            object[] args = { amount };
            foreach (object hinge in hinges)
            {
                if (hinge != null)
                {
                    HingeAnimateMethod.Invoke(hinge, args);
                }
            }
        }

        // ---------------------------------------------------------------
        // Cargo ramps
        // ---------------------------------------------------------------

        private static CargoRamp[] GetRamps(Aircraft aircraft)
        {
            // Aircraft parts are separate bodies rather than Transform children
            // of the aircraft, so ramps are found through the part registry
            // (once per aircraft, cached -- including "has none").
            return _rampCache.GetValue(aircraft, a =>
            {
                List<CargoRamp> found = new List<CargoRamp>();
                foreach (UnitPart part in a.partLookup)
                {
                    if (part == null)
                    {
                        continue;
                    }
                    CargoRamp ramp = part.GetComponentInChildren<CargoRamp>(true);
                    if (ramp != null && !found.Contains(ramp))
                    {
                        found.Add(ramp);
                    }
                }
                return found.ToArray();
            });
        }

        private static void ToggleCargoRamps(Aircraft aircraft)
        {
            CargoRamp[] ramps = GetRamps(aircraft);
            if (ramps.Length == 0)
            {
                return;
            }

            _heldRamps.RemoveWhere(r => r == null);
            bool anyHeld = false;
            foreach (CargoRamp ramp in ramps)
            {
                if (ramp != null && _heldRamps.Contains(ramp))
                {
                    anyHeld = true;
                    break;
                }
            }

            foreach (CargoRamp ramp in ramps)
            {
                if (ramp == null)
                {
                    continue;
                }
                if (anyHeld)
                {
                    if (_heldRamps.Remove(ramp))
                    {
                        // CargoRamp.Open() ticks this down and starts closing at
                        // zero; the ramp itself reports "Locking Cargo Door".
                        RampOpenTimerRef(ramp) = 0f;
                    }
                }
                else
                {
                    // Added before the call so CargoRampHoldPatch (which runs
                    // after every OpenDoor) already treats it as held. The
                    // ramp itself reports "Unlocking Cargo Door".
                    _heldRamps.Add(ramp);
                    ramp.OpenDoor(RampHoldSeconds);
                }
            }
        }

        // Called after every CargoRamp.OpenDoor -- see the class header.
        internal static void EnforceRampHold(CargoRamp ramp)
        {
            if (_heldRamps.Contains(ramp))
            {
                RampOpenTimerRef(ramp) = RampHoldSeconds;
            }
        }

        private static void Report(string text)
        {
            AircraftActionsReport report = SceneSingleton<AircraftActionsReport>.i;
            if (report != null)
            {
                report.ReportText(text, 3f);
            }
        }
    }

    internal sealed class AircraftDoorDriver : MonoBehaviour
    {
        private void Update()
        {
            AircraftDoorControl.Tick();
        }
    }
}
