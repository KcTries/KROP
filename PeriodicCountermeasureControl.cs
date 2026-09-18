using BepInEx.Configuration;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // "Periodic Countermeasure Release" (PCR): toggle continuous, automatic
    // dispensing at a configurable interval, instead of needing to
    // physically hold the button down. Vanilla already auto-repeats while
    // the button is held (Aircraft.FixedUpdate ->
    // CountermeasureManager.DeployCountermeasure, called every physics tick
    // for as long as countermeasureTrigger is true), throttled only by
    // whichever ejector's own hardcoded ejectionInterval -- PCR adds a
    // second, independently timed release loop on top of that, driven by
    // our own timer instead of button state, so it keeps firing after the
    // button is released.
    //
    // PCR locks onto whichever countermeasure was active *at the moment it
    // was toggled on* (_pcrTarget) and keeps firing that one specifically,
    // regardless of what the player selects afterward -- switching to ECM
    // (or anything else) while PCR is running flares should let ECM behave
    // completely normally, not redirect PCR onto it. This means PCR fires
    // _pcrTarget.Fire() directly rather than going through
    // CountermeasureManager.DeployCountermeasure (which always targets
    // whatever's currently *selected*, not a fixed item).
    //
    // Toggle used to be a double-tap on the vanilla Countermeasures button,
    // but that made it trivial to trigger by accident while rapidly
    // mashing Countermeasures to dump flares in a hurry. A first attempt at
    // a native Controls-menu integration created a whole new "KQOLRF"
    // category/tab, which surfaced a real bug in BOTE's own (all currently
    // released versions of it) input registration code -- it re-registers
    // its category without a re-entry guard, corrupting the Controls
    // menu's layout once a second mod's category also existed. The current
    // approach avoids that entirely by adding a single "Toggle PCR" action
    // into the EXISTING "Flight" category instead of creating a new one
    // (see PcrToggleActionPatch) -- never touches ControlMapper or its
    // _mappingSets array, so there's no way to collide with BOTE's bug.
    // Defaults to keyboard B (assigned once ReInput is ready, via
    // PcrToggleActionPatch.EnsureDefaultKeyboardBinding, using Rewired's
    // real ControllerMap.CreateElementMap runtime API), and is fully
    // rebindable in the native Controls menu under Flight from there.
    //  - Controller: a second "PCR Modifier" action (also registered into
    //    Flight, ships unbound -- no universal default hardware element
    //    the way KeyCode.B works for keyboard) held alongside the vanilla
    //    Countermeasures button. Rewired's own modifier-key system only
    //    supports keyboard mappings, so a real combo needs two separate
    //    actions checked together here rather than one native binding.
    //    While the modifier is held, the vanilla manual-fire path is also
    //    suppressed (see DeployCountermeasureLockoutPatch) so the same
    //    button press used for the toggle combo never ALSO fires a real
    //    countermeasure release.
    //
    // Won't turn on while the Radar Jammer is the selected countermeasure
    // -- PCR is for automatic flare dispensing, not ECM, so a toggle
    // attempt in that state is simply rejected rather than locking onto
    // the jammer.
    internal static class PeriodicCountermeasureControl
    {
        private const float MinPeriodSeconds = 0.05f;

        private static ConfigEntry<float> _periodSeconds;
        private static ConfigEntry<int> _releasesPerInterval;
        private static bool _driverEnsured;

        private static bool _wasTriggerHeld;

        private static Aircraft _pcrAircraft;
        private static Countermeasure _pcrTarget;
        private static float _nextFireTime;

        // FlareEjector/ChaffEjector.Fire() internally gates on their own
        // lastEjectionTime/ejectionInterval fields -- calling Fire()
        // repeatedly within the same instant would just no-op after the
        // first call otherwise. Forcing this field open before each release
        // in a burst is what makes "releases per interval" fire all at once
        // instead of spaced out at the ejector's own natural cadence.
        private static readonly AccessTools.FieldRef<FlareEjector, float> FlareLastEjectionTimeRef =
            AccessTools.FieldRefAccess<FlareEjector, float>("lastEjectionTime");
        private static readonly AccessTools.FieldRef<ChaffEjector, float> ChaffLastEjectionTimeRef =
            AccessTools.FieldRefAccess<ChaffEjector, float>("lastEjectionTime");

        // True only while the player's current HUD selection is the same
        // item PCR is dispensing -- used both to lock out a redundant
        // manual press on that same item (DeployCountermeasureLockoutPatch)
        // and to decide whether the HUD should read "PCR" (only while it's
        // actually what's being displayed/controlled). False while some
        // other countermeasure (ECM, etc.) is selected, since that should
        // behave completely normally regardless of what PCR is quietly
        // doing in the background.
        internal static bool ShouldOverrideActiveSelection(Aircraft aircraft)
        {
            return aircraft != null && _pcrAircraft == aircraft
                && _pcrTarget != null && _pcrTarget == aircraft.countermeasureManager.GetActiveCountermeasure();
        }

        // Used by DeployCountermeasureLockoutPatch to suppress a real
        // countermeasure release while the PCR Modifier is held -- that
        // combo is always meant as the PCR toggle chord, never a genuine
        // manual fire, regardless of whether this particular press
        // actually lines up with a Countermeasures rising edge.
        internal static bool IsModifierHeld()
        {
            Player playerInput = GameManager.playerInput;
            return playerInput != null && playerInput.GetButton(Patches.PcrToggleActionPatch.ModifierActionName);
        }

        // pluginGameObject is the BepInEx plugin's own GameObject (Plugin's
        // `gameObject`, passed in from Awake()) -- a GameObject created
        // fresh and DontDestroyOnLoad'd by our own code here does NOT
        // reliably survive this game's very early bootstrap scene
        // transition (confirmed previously with a HUD Canvas: OnEnable
        // fired, but the object was destroyed ~2.6s later with zero
        // Update() calls in between), so the driver needs to live on a
        // GameObject BepInEx itself already guarantees persists -- exactly
        // what left this whole feature silently inert: not one [PcrDiag]
        // tick logged all session despite flares clearly firing, meaning
        // Tick() never ran even once. SoundPropagation's own driver avoids
        // this because it's only ever created lazily well after a real
        // gameplay event (first shot fired, etc.), by which point the
        // scene has already stabilized -- PCR needs to be watching for a
        // double-tap from the start, so it can't defer the same way and
        // needs the Plugin's own GameObject instead.
        internal static void Initialize(ConfigFile config, GameObject pluginGameObject)
        {
            _periodSeconds = config.Bind(
                "Countermeasures",
                "Flare Release Period",
                1.0f,
                new ConfigDescription(
                    "Time between flare release when PCR is enabled.",
                    new AcceptableValueRange<float>(0.5f, 3.0f)));

            _releasesPerInterval = config.Bind(
                "Countermeasures",
                "Flare Releases Per Period",
                1,
                new ConfigDescription(
                    "Number of Countermeasure presses per-interval.",
                    new AcceptableValueRange<int>(1, 5)));

            EnsureDriver(pluginGameObject);
            SoundPropagation.Log.LogInfo("[PcrDiag] PeriodicCountermeasureControl initialized, driver ensured.");
        }

        private static void EnsureDriver(GameObject pluginGameObject)
        {
            if (_driverEnsured)
            {
                return;
            }
            _driverEnsured = true;
            pluginGameObject.AddComponent<PeriodicCountermeasureDriver>();
        }

        // Called on mission/scene teardown (see LevelInfoCleanupPatch) so
        // PCR doesn't stay "on" for a stale Aircraft reference into the
        // mission that just ended.
        internal static void ClearAll()
        {
            _pcrAircraft = null;
            _pcrTarget = null;
            _wasTriggerHeld = false;
        }

        internal static void Tick()
        {
            if (!GameManager.GetLocalAircraft(out Aircraft localAircraft) || localAircraft == null)
            {
                return;
            }

            bool held = localAircraft.countermeasureTrigger;
            bool countermeasureRisingEdge = held && !_wasTriggerHeld;
            _wasTriggerHeld = held;

            Player playerInput = GameManager.playerInput;
            Patches.PcrToggleActionPatch.EnsureDefaultKeyboardBinding(playerInput);

            bool nativeActionPressed = playerInput != null && playerInput.GetButtonDown(Patches.PcrToggleActionPatch.ActionName);
            bool modifierHeld = IsModifierHeld();

            if (nativeActionPressed || (modifierHeld && countermeasureRisingEdge))
            {
                SoundPropagation.Log.LogInfo(
                    $"[PcrDiag] Toggle triggered at t={Time.timeSinceLevelLoad:F2} (nativeAction={nativeActionPressed}, "
                    + $"modifierCombo={modifierHeld && countermeasureRisingEdge})");
                TogglePcr(localAircraft);
            }

            if (_pcrAircraft != null && _pcrAircraft != localAircraft)
            {
                // Player switched or respawned into a different aircraft --
                // PCR doesn't carry over to it.
                _pcrAircraft = null;
                _pcrTarget = null;
            }

            if (_pcrAircraft == null || _pcrTarget == null)
            {
                return;
            }

            if (_pcrTarget.ammo <= 0)
            {
                _pcrAircraft = null;
                _pcrTarget = null;
                return;
            }

            if (Time.timeSinceLevelLoad >= _nextFireTime)
            {
                int releases = Mathf.Clamp(_releasesPerInterval.Value, 1, 5);
                for (int i = 0; i < releases && _pcrTarget.ammo > 0; i++)
                {
                    ForceEjectorReady(_pcrTarget);
                    _pcrTarget.Fire();
                    if (!_pcrTarget.chargeable)
                    {
                        _pcrAircraft.RequestRearm();
                    }
                }
                // _pcrTarget.Fire() unconditionally refreshes the HUD to
                // show itself (see FlareEjector.Fire()/UpdateHUD()) even
                // when it isn't the player's current selection -- reassert
                // whatever's actually selected right after, in the same
                // tick, so the player never sees it flash to the wrong
                // countermeasure's name/ammo while something else (ECM,
                // etc.) is what they're actually looking at.
                _pcrAircraft.countermeasureManager.UpdateHUD();
                _nextFireTime = Time.timeSinceLevelLoad + Mathf.Max(MinPeriodSeconds, _periodSeconds.Value);
            }
        }

        private static void ForceEjectorReady(Countermeasure countermeasure)
        {
            if (countermeasure is FlareEjector flareEjector)
            {
                FlareLastEjectionTimeRef(flareEjector) = float.NegativeInfinity;
            }
            else if (countermeasure is ChaffEjector chaffEjector)
            {
                ChaffLastEjectionTimeRef(chaffEjector) = float.NegativeInfinity;
            }
        }

        private static void TogglePcr(Aircraft aircraft)
        {
            if (_pcrAircraft == aircraft)
            {
                SoundPropagation.Log.LogInfo($"[PcrDiag] PCR turned OFF for '{aircraft.name}' at t={Time.timeSinceLevelLoad:F2}");
                _pcrAircraft = null;
                _pcrTarget = null;
            }
            else
            {
                Countermeasure target = aircraft.countermeasureManager.GetActiveCountermeasure();
                if (target is RadarJammer)
                {
                    SoundPropagation.Log.LogInfo(
                        $"[PcrDiag] PCR toggle-on rejected for '{aircraft.name}' -- Radar Jammer is selected.");
                    return;
                }

                SoundPropagation.Log.LogInfo(
                    $"[PcrDiag] PCR turned ON for '{aircraft.name}' at t={Time.timeSinceLevelLoad:F2}, "
                    + $"locked to '{(target != null ? target.displayName : "none")}'");
                _pcrAircraft = aircraft;
                _pcrTarget = target;
                _nextFireTime = Time.timeSinceLevelLoad;
            }
        }
    }

    internal class PeriodicCountermeasureDriver : MonoBehaviour
    {
        private void Update()
        {
            PeriodicCountermeasureControl.Tick();
        }
    }
}
