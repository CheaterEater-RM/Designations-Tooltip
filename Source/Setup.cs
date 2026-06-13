using HarmonyLib;
using Verse;

namespace DesignationsTooltip
{
    [StaticConstructorOnStartup]
    public static class DesignationsTooltip_Init
    {
        static DesignationsTooltip_Init()
        {
            new Harmony("com.cheatereater.designationstooltip").PatchAll();
            Log.Message("[Designations Tooltip] Harmony patches applied.");
        }
    }
}
