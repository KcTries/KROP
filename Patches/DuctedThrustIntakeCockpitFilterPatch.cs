using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // DuctedThrustSystem (the Medusa's ducted lift/bypass system) drives
    // each intake door's intakeSound -- the bypass air rushing in while
    // the doors are open at low speed -- from its own Update(), and nothing
    // else in this mod ever filtered it. Same direct-on-real-source path as
    // tire noise/airbrake: it's outside air, so full cockpit-filter
    // strength. (The nozzle swivel sounds this system also drives share
    // their GameObjects with the nozzles' own thrust sources, so they're
    // already muffled through JetNozzleAudioEffectsPatch's shared filter.)
    //
    // IntakeDoor is a private nested class, so the sources are pulled out
    // by reflection once per DuctedThrustSystem and cached.
    [HarmonyPatch(typeof(DuctedThrustSystem), "Update")]
    internal static class DuctedThrustIntakeCockpitFilterPatch
    {
        private static readonly FieldInfo IntakeDoorsField = AccessTools.Field(typeof(DuctedThrustSystem), "intakeDoors");
        private static readonly FieldInfo IntakeSoundField =
            AccessTools.Field(AccessTools.Inner(typeof(DuctedThrustSystem), "IntakeDoor"), "intakeSound");

        private static readonly ConditionalWeakTable<DuctedThrustSystem, AudioSource[]> IntakeSounds =
            new ConditionalWeakTable<DuctedThrustSystem, AudioSource[]>();

        private static void Postfix(DuctedThrustSystem __instance)
        {
            if (!IntakeSounds.TryGetValue(__instance, out AudioSource[] sources))
            {
                sources = CollectIntakeSounds(__instance);
                IntakeSounds.Add(__instance, sources);
            }
            for (int i = 0; i < sources.Length; i++)
            {
                SoundPropagation.ApplyCockpitOnlyLowpass(sources[i]);
            }
        }

        private static AudioSource[] CollectIntakeSounds(DuctedThrustSystem system)
        {
            List<AudioSource> sources = new List<AudioSource>();
            if (IntakeDoorsField?.GetValue(system) is Array doors && IntakeSoundField != null)
            {
                foreach (object door in doors)
                {
                    if (door != null && IntakeSoundField.GetValue(door) is AudioSource source && source != null)
                    {
                        sources.Add(source);
                    }
                }
            }
            return sources.ToArray();
        }
    }
}
