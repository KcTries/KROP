using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // FlareEjector.EjectFlare() and ChaffEjector.EjectChaff() are both async
    // UniTask methods (same MethodType.Enumerator situation as Canopy's
    // jettison sound) that spawn the flare/chaff object, apply launch
    // physics, and only then play the ejection "thump" as literally the
    // last line -- audioSource.PlayOneShot(ejectionSound), the single-arg
    // overload. A transpiler swaps just that call, leaving the countermeasure
    // spawn/launch physics completely untouched, same approach as the
    // canopy and sonic boom patches.
    [HarmonyPatch(typeof(FlareEjector), "EjectFlare", MethodType.Enumerator)]
    internal static class FlareEjectorPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return CountermeasureEjectorPatch.SwapPlayOneShot(instructions);
        }
    }

    [HarmonyPatch(typeof(ChaffEjector), "EjectChaff", MethodType.Enumerator)]
    internal static class ChaffEjectorPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return CountermeasureEjectorPatch.SwapPlayOneShot(instructions);
        }
    }

    internal static class CountermeasureEjectorPatch
    {
        private static readonly MethodInfo PlayOneShotMethod =
            AccessTools.Method(typeof(AudioSource), "PlayOneShot", new[] { typeof(AudioClip) });
        private static readonly MethodInfo PlayOneShotDelayedMethod =
            AccessTools.Method(typeof(CountermeasureEjectorPatch), nameof(PlayOneShotDelayed));

        internal static IEnumerable<CodeInstruction> SwapPlayOneShot(IEnumerable<CodeInstruction> instructions)
        {
            return Transpilers.MethodReplacer(instructions, PlayOneShotMethod, PlayOneShotDelayedMethod);
        }

        // Signature matches AudioSource.PlayOneShot(AudioClip) exactly.
        private static void PlayOneShotDelayed(AudioSource source, AudioClip clip)
        {
            SoundPropagation.ScheduleDelayedOneShot(source, clip, source.transform.position);
        }
    }
}
