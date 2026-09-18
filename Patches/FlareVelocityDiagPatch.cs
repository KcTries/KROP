using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // ejectionVelocity/ejectionVelocityVariance/ejectionInterval/
    // ejectionGrouping are all [SerializeField] private fields baked into
    // each aircraft's prefab data, not literals anywhere in code -- the
    // only way to read (or change) their real configured values is at
    // runtime, after Unity's own deserialization has already populated
    // them (i.e. after base.Awake() -- confirmed safe to read/write here
    // since FlareEjector/ChaffEjector.Awake() don't touch ejectionVelocity
    // themselves). ref on the injected field lets Harmony write the raised
    // value back for every later EjectFlare()/EjectChaff() call to use.
    [HarmonyPatch(typeof(FlareEjector), "Awake")]
    internal static class FlareEjectorAwakeDiagPatch
    {
        private static void Postfix(
            FlareEjector __instance,
            ref float ___ejectionVelocity,
            float ___ejectionVelocityVariance,
            float ___ejectionInterval,
            int ___ejectionGrouping)
        {
            float original = ___ejectionVelocity;
            ___ejectionVelocity = Mathf.Max(___ejectionVelocity, FlareVelocityControl.MinimumVelocity.Value);
            SoundPropagation.Log.LogInfo(
                $"[FlareDiag] FlareEjector on '{__instance.name}' -- "
                + $"ejectionVelocity original={original:F2} adjusted={___ejectionVelocity:F2} "
                + $"(floor={FlareVelocityControl.MinimumVelocity.Value:F2}) variance={___ejectionVelocityVariance:F2} "
                + $"interval={___ejectionInterval:F3} grouping={___ejectionGrouping}");
        }
    }

    [HarmonyPatch(typeof(ChaffEjector), "Awake")]
    internal static class ChaffEjectorAwakeDiagPatch
    {
        private static void Postfix(
            ChaffEjector __instance,
            ref float ___ejectionVelocity,
            float ___ejectionVelocityVariance,
            float ___ejectionInterval,
            int ___ejectionGrouping)
        {
            float original = ___ejectionVelocity;
            ___ejectionVelocity = Mathf.Max(___ejectionVelocity, FlareVelocityControl.MinimumVelocity.Value);
            SoundPropagation.Log.LogInfo(
                $"[FlareDiag] ChaffEjector on '{__instance.name}' -- "
                + $"ejectionVelocity original={original:F2} adjusted={___ejectionVelocity:F2} "
                + $"(floor={FlareVelocityControl.MinimumVelocity.Value:F2}) variance={___ejectionVelocityVariance:F2} "
                + $"interval={___ejectionInterval:F3} grouping={___ejectionGrouping}");
        }
    }
}
