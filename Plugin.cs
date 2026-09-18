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
    [BepInPlugin("pavehog727.qolrealismfixes", "KROP 1.0", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            VerboseLoggingConfig.Initialize(Config);
            FlareVelocityControl.Initialize(Config);
            FlareCountConfig.Initialize(Config);
            EngineAudioConfig.Initialize(Config);
            CramFragmentationConfig.Initialize(Config);
            FragmentSim.EnsureDriver(gameObject);
            BulletCrackConfig.Initialize(Config);
            DistanceLowpassConfig.Initialize(Config);
            CockpitLowpassConfig.Initialize(Config);
            LifeboatConfig.Initialize(Config);
            NightVisionConfig.Initialize(Config); // also binds AutoGainConfig into the same section
            NightVisionAutoGain.EnsureSubscribed();
            LifeboatAssets.Initialize();
            LifeboatSpawnQueue.EnsureDriver(gameObject);

            Harmony harmony = new Harmony("pavehog727.qolrealismfixes");
            harmony.PatchAll();
            // SonicBoomManager.ManagedSonicBoom, JetNozzle.Afterburner, and
            // Missile.Motor are all private nested types -- PatchAll()'s
            // attribute scan can't target any of them (no typeof() name
            // reaches them), so they're patched manually via reflection.
            SonicBoomManagePatch.ApplyManualPatch(harmony);
            JetNozzleAfterburnerPatch.ApplyManualPatch(harmony);
            MissileMotorPatch.ApplyManualPatch(harmony);
            NavLightsParkedOverridePatch.ApplyManualPatch(harmony);

            PeriodicCountermeasureControl.Initialize(Config, gameObject);
        }
    }
}
