using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Canopy.EjectionSequence() plays the canopy jettison sound buried in
    // the middle of an async UniTask method that also breaks joints,
    // creates a Rigidbody, and reparents the canopy -- unlike EjectionSeat's
    // FireEffects(), there's no clean way to Prefix-and-replace the whole
    // method without risking reimplementing that physics/networking logic
    // incorrectly. Instead, a transpiler on the compiler-generated state
    // machine (MethodType.Enumerator -- this Harmony build's documented way
    // to target a UniTask coroutine's real MoveNext) swaps out just the one
    // `audioSource.Play()` call for our own delayed version, leaving every
    // other instruction in the method completely untouched.
    [HarmonyPatch(typeof(Canopy), "EjectionSequence", MethodType.Enumerator)]
    internal static class CanopyEjectionSequencePatch
    {
        private static readonly MethodInfo AudioSourcePlay =
            AccessTools.Method(typeof(AudioSource), "Play", System.Type.EmptyTypes);
        private static readonly MethodInfo PlayDelayedMethod =
            AccessTools.Method(typeof(CanopyEjectionSequencePatch), nameof(PlayDelayed));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Transpilers.MethodReplacer(instructions, AudioSourcePlay, PlayDelayedMethod);
        }

        // Signature matches AudioSource.Play() exactly (instance -> static
        // taking the instance as the first arg) so MethodReplacer can swap
        // the call in place without touching the surrounding IL/stack at all.
        private static void PlayDelayed(AudioSource source)
        {
            SoundPropagation.ScheduleDelayedOneShot(source, source.clip, source.transform.position);
        }
    }
}
