namespace QOL_Realisim_Fixes
{
    // Copied from BepInEx.ConfigurationManager's own template (per its usage
    // instructions) so we can pass an explicit display Order -- without it,
    // ConfigManager falls back to sorting entries alphabetically by name
    // within each section, which splits up related settings whenever one of
    // their names happens to fall in a different spot in the alphabet.
    // Trimmed to just the field we use; the template allows removing the
    // rest since a missing field just means "don't override this."
    internal sealed class ConfigurationManagerAttributes
    {
        /// <summary>
        /// Order of the setting on the settings list relative to other settings in a category.
        /// 0 by default, higher number is higher on the list.
        /// </summary>
        public int? Order;
    }
}
