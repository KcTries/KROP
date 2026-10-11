using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // ThreatList.MissileAlarm.Setup() creates each missile-warning alarm
    // loop (one per seeker type: the IR tone, the radar tone, ...) with
    // aircraft.gameObject.AddComponent<AudioSource>() -- i.e. directly on the
    // aircraft's ROOT GameObject, and without bypassEffects. Unity applies an
    // AudioLowPassFilter/AudioHighPassFilter to every source on the same
    // GameObject, and a handful of engine/airbrake sources this mod filters
    // for the cockpit view also live on that root (Cricket and Revoker
    // turbines, the airbrake on the Revoker and Vagrant, ...), so the cockpit
    // lowpass could end up muffling the alarm too. Reported as the IR warning
    // tone sounding muffled in the cockpit.
    //
    // Vanilla already protects its other interface sources on the same root
    // this way (AoADisplay sets bypassEffects = true on the stall horn), so
    // this simply gives the missile alarms the same flag: filter components
    // are ignored for these sources, whatever ends up on their GameObject.
    // MissileAlarm is a private nested class, so it's patched manually from
    // Plugin.Awake() like the other private nested types.
    internal static class MissileAlarmFilterBypassPatch
    {
        private static FieldInfo _alarmSourceField;

        internal static void ApplyManualPatch(Harmony harmony)
        {
            Type alarmType = AccessTools.Inner(typeof(ThreatList), "MissileAlarm");
            _alarmSourceField = AccessTools.Field(alarmType, "alarmSource");
            harmony.Patch(
                AccessTools.Method(alarmType, "Setup"),
                postfix: new HarmonyMethod(typeof(MissileAlarmFilterBypassPatch), nameof(SetupPostfix)));
        }

        private static void SetupPostfix(object __instance)
        {
            if (_alarmSourceField.GetValue(__instance) is AudioSource alarmSource)
            {
                alarmSource.bypassEffects = true;
            }
        }
    }
}
