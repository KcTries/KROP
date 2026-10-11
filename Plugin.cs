using BepInEx;
using HarmonyLib;
using QOL_Realisim_Fixes.Patches;

namespace QOL_Realisim_Fixes
{
    // GUID and namespace intentionally left as "qolrealismfixes"/
    // "QOL_Realisim_Fixes" through the rename to KROP -- the GUID is what
    // BepInEx keys the saved .cfg file by, so changing it would reset every
    // setting this user already has tuned; the namespace is invisible
    // internal plumbing. Only the display name here, the .csproj
    // AssemblyName, and the deployed plugin folder name changed. Same
    // convention already used for the KaceyTronic-RWR rename.
    [BepInPlugin("pavehog727.qolrealismfixes", "KROP", "1.1.2")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            VerboseLoggingConfig.Initialize(Config);
            FlareVelocityControl.Initialize(Config);
            EngineAudioConfig.Initialize(Config);
            FragmentSim.EnsureDriver(gameObject);
            BulletCrackConfig.Initialize(Config);
            DistanceLowpassConfig.Initialize(Config);
            CockpitLowpassConfig.Initialize(Config);
            MissileSonicBoomConfig.Initialize(Config);
            LifeboatConfig.Initialize(Config);
            NightVisionConfig.Initialize(Config); // also binds AutoGainConfig into the same section
            NightVisionAutoGain.EnsureSubscribed();
            LifeboatAssets.Initialize();
            LifeboatSpawnQueue.EnsureDriver(gameObject);
            AirWakeConfig.Initialize(Config);
            ContrailConfig.Initialize(Config);
            FuelExplosionConfig.Initialize(Config);
            // Bound last so the Non-Multiplayer Compatible section lands at
            // the very bottom of the ConfigManager window (it orders
            // sections by first-bind order).
            FlareCountConfig.Initialize(Config);
            CramFragmentationConfig.Initialize(Config);

            PrepareTypesForPatching();
            Harmony harmony = new Harmony("pavehog727.qolrealismfixes");
            harmony.PatchAll();
            // SonicBoomManager.ManagedSonicBoom, JetNozzle.Afterburner, and
            // Missile.Motor are all private nested types -- PatchAll()'s
            // attribute scan can't target any of them (no typeof() name
            // reaches them), so they're patched manually via reflection.
            SonicBoomManagePatch.ApplyManualPatch(harmony);
            JetNozzleAfterburnerPatch.ApplyManualPatch(harmony);
            MissileMotorPatch.ApplyManualPatch(harmony);
            MissileContrailPatch.ApplyManualPatch(harmony);
            NavLightsParkedOverridePatch.ApplyManualPatch(harmony);
            MissileAlarmFilterBypassPatch.ApplyManualPatch(harmony);

            PeriodicCountermeasureControl.Initialize(Config, gameObject);
            AircraftDoorControl.Initialize(gameObject);
        }

        // Harmony copies a method's IL before patching it, asking the runtime
        // about every type in it. GroundVehicle.Update has a
        // GroundVehicleFields local, which holds a Nullable<SteeringInfo> --
        // and SteeringInfo has a static field of its own nullable type
        // (SteeringInfo? None). If the runtime reaches Nullable<SteeringInfo>
        // BEFORE SteeringInfo itself has ever been set up, laying it out
        // needs SteeringInfo, whose static field needs Nullable<SteeringInfo>
        // mid-layout -- and Mono crashes outright (native crash during
        // PatchAll, no BepInEx log). The game itself always reaches
        // SteeringInfo first; so does any plugin that touches it before
        // this one (NOAutopilot's AI taxi patches do), which is why the
        // crash only showed up on some installs. So: SteeringInfo alone
        // first, in its own method, then the rest -- all before patching.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PrepareTypesForPatching()
        {
            PrepareSteeringInfo();
            PrepareGroundVehicleFields();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PrepareSteeringInfo()
        {
            SteeringInfo info = default;
            if (info.nextWaypointAngle > 0f)
            {
                info = default;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PrepareGroundVehicleFields()
        {
            NuclearOption.Jobs.GroundVehicleFields fields = default;
            SteeringInfo? steering = fields.steeringInfoNullable;
            NuclearOption.Jobs.PtrAllocation<NuclearOption.Jobs.GroundVehicleFields> allocation = default;
            if (steering.HasValue && allocation.IsCreated)
            {
                fields.speed = 0f;
            }
        }
    }
}
