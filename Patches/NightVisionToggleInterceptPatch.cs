using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // NightVision.Toggle() (the method the "Night Vis" keybind calls) just
    // flips a private bool, which vanilla's own Update() picks up almost
    // immediately (within a frame). To get the exact choreography
    // requested -- the real NVG filter only switches while the screen is
    // ALREADY fully covered by the wipe animation, not the instant the
    // button is pressed -- this Prefix blocks the real toggle and hands
    // control to NightVisionTransitionOverlay instead, which calls back
    // into PerformRealToggle() once the cover animation has fully
    // obscured the screen.
    //
    // Toggle() is `public static`, operating on the NightVision.i
    // singleton's PRIVATE nightVisSelected field -- Harmony's ___fieldName
    // parameter injection only works for instance-method patches (where
    // `this` is implicit), so a static method like this one needs
    // Traverse to read that private field directly instead.
    [HarmonyPatch(typeof(NightVision), "Toggle")]
    internal static class NightVisionToggleInterceptPatch
    {
        // True only while THIS class is re-invoking Toggle() itself (from
        // the animation's flip-point callback) -- lets that specific call
        // through instead of being intercepted again.
        private static bool _allowRealToggle;

        private static bool Prefix()
        {
            if (_allowRealToggle || !NightVisionConfig.TransitionAnimationEnabled.Value || NightVision.i == null)
            {
                return true;
            }
            if (NightVisionTransitionOverlay.IsBusy)
            {
                // Ignore rapid re-presses while a transition is already
                // playing rather than starting a second one mid-animation.
                return false;
            }

            bool currentlySelected = Traverse.Create(NightVision.i).Field("nightVisSelected").GetValue<bool>();
            bool entering = !currentlySelected;
            NightVisionTransitionOverlay.BeginTransition(entering, PerformRealToggle);
            return false;
        }

        private static void PerformRealToggle()
        {
            _allowRealToggle = true;
            NightVision.Toggle();
            _allowRealToggle = false;
        }
    }
}
