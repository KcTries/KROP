using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // JetNozzle.Afterburner is a PRIVATE class nested inside a PUBLIC class
    // (same situation as SonicBoomManager.ManagedSonicBoom) -- can't be
    // named with typeof(), so it's resolved via AccessTools.Inner and
    // patched manually from Plugin.Awake() instead of the normal
    // [HarmonyPatch(Type, string)] attribute.
    //
    // Afterburner.Run() mixes flame/nozzle-glow visuals with its own
    // source.volume/.dopplerLevel, AND has an early-return path (when
    // afterburnerAmount is near zero) that sets source.volume = 0f directly
    // without ever reaching the separate Audio() method that handles the
    // "actually running" case -- patching Audio() alone would miss that
    // silent case entirely, leaving a stale nonzero volume in the clone's
    // recorded history forever. A Postfix on the whole of Run() sidesteps
    // that: let the original run completely (both paths), then read
    // whatever it just left on source.volume/.dopplerLevel either way,
    // record that, and mute the real source.
    internal static class JetNozzleAfterburnerPatch
    {
        internal static void ApplyManualPatch(Harmony harmony)
        {
            Type afterburnerType = AccessTools.Inner(typeof(JetNozzle), "Afterburner");
            MethodInfo runMethod = AccessTools.Method(afterburnerType, "Run");
            harmony.Patch(runMethod, postfix: new HarmonyMethod(typeof(JetNozzleAfterburnerPatch), nameof(Postfix)));
        }

        private static void Postfix(object __instance, AudioSource ___source)
        {
            if (___source == null)
            {
                return;
            }
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                // Cockpit-only filtering for this source is handled from
                // JetNozzleAudioEffectsPatch instead (which runs first,
                // guaranteed, every time this can) -- an Afterburner has no
                // back-reference to its owning JetNozzle's thrustAudio, so
                // it can't declare that as an accepted sibling from here,
                // and thrustAudio commonly shares this same GameObject.
                return;
            }

            SoundPropagation.RecordEngineSample(
                ___source, ___source, ___source.pitch, ___source.volume, ___source.transform.position, ___source.dopplerLevel);
            ___source.volume = 0f;
        }
    }
}
