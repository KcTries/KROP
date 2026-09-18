using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Hangar's own "moving" and open/close sounds are entirely driven by
    // an async UniTask loop (MoveDoors), not a regular Update()/
    // FixedUpdate() -- Harmony can't cleanly hook "once per iteration" of
    // an async method's compiler-generated state machine the way this mod
    // hooks BayDoor.Update()/SwingWingController.FixedUpdate() for the
    // same kind of continuous cockpit-filtered sound elsewhere (that's why
    // hangar door audio wasn't covered at all). Cockpit-only cutoff is
    // recomputed from live, per-frame state (ComputeCockpitLowpassCutoffOnly
    // et al.), so a one-time application when the sources are created
    // wouldn't track the player entering/leaving a cockpit correctly --
    // instead, a small dedicated driver component is added to the Hangar's
    // own GameObject the moment its AudioSources are (lazily) created, and
    // just re-applies the filter every frame for as long as the Hangar
    // exists.
    [HarmonyPatch(typeof(Hangar), "CreateAudioSources")]
    internal static class HangarAudioSourceCockpitFilterPatch
    {
        private static void Postfix(Hangar __instance, AudioSource ___oneShotSource, AudioSource ___loopSource)
        {
            __instance.gameObject.AddComponent<HangarCockpitFilterDriver>().Configure(___oneShotSource, ___loopSource);
        }
    }

    // oneShotSource and loopSource are both added to the SAME GameObject
    // (see CreateAudioSources), and Unity's AudioLowPassFilter/
    // AudioHighPassFilter apply to a GameObject's whole combined output,
    // not per-source -- so only one of the two ever needs to actually call
    // ApplyCockpitOnlyLowpass each frame, as long as the other is declared
    // as an accepted sibling (same reasoning as the sibling-cache patches
    // elsewhere in this mod).
    internal class HangarCockpitFilterDriver : MonoBehaviour
    {
        private AudioSource _primary;
        private AudioSource[] _acceptedSiblings;

        internal void Configure(AudioSource oneShotSource, AudioSource loopSource)
        {
            _primary = oneShotSource;
            _acceptedSiblings = new[] { loopSource };
        }

        private void Update()
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(_primary, _acceptedSiblings);
        }
    }
}
