using UnityEngine;
using Verse;

namespace DesignationsTooltip
{
    // Two opt-in display options. This is the mod's only persistent state, and it lives in the
    // Config folder (Config/Mod_*_DesignationsTooltip.xml), NOT in the save game: no Scribe_* in
    // saves, no defs, no comps. The mod stays freely add/removable from any save.
    public class DesignationsTooltipSettings : ModSettings
    {
        // Feature 1 — show only the affected count (e.g. "8") instead of "affected / total" ("8 / 100").
        // Off by default: preserves the released v1.0 readout unless the player opts in.
        public bool classicMode = false;

        // Feature 2 — draw the center count even for small drags the base game suppresses
        // (anything narrower than 3 cells on a side). On by default: a strictly additive improvement.
        public bool alwaysShowSmall = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref classicMode, "classicMode", false);
            Scribe_Values.Look(ref alwaysShowSmall, "alwaysShowSmall", true);
        }
    }

    // The Mod subclass is deliberately separate from the [StaticConstructorOnStartup] init class
    // (DesignationsTooltip_Init): the Mod ctor runs during assembly load, before Defs exist, while the
    // static ctor runs after — mixing them silently fails (hard rule #7). This class only loads the
    // settings and draws the settings window; PatchAll still lives in DesignationsTooltip_Init.
    public class DesignationsTooltipMod : Mod
    {
        public static DesignationsTooltipSettings Settings { get; private set; }

        public DesignationsTooltipMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DesignationsTooltipSettings>();
        }

        public override string SettingsCategory()
        {
            return "DesignationsTooltip.SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            list.CheckboxLabeled(
                "DesignationsTooltip.ClassicMode".Translate(),
                ref Settings.classicMode,
                "DesignationsTooltip.ClassicMode.Tooltip".Translate());

            list.Gap();

            list.CheckboxLabeled(
                "DesignationsTooltip.AlwaysShowSmall".Translate(),
                ref Settings.alwaysShowSmall,
                "DesignationsTooltip.AlwaysShowSmall.Tooltip".Translate());

            list.End();
        }

        // Consumed by the Feature-2 IL injection in Patch_DraggerOnGUI. Null-safe for robustness,
        // though at GUI runtime (mid-drag) Settings is always set: the Mod ctor populates it at
        // assembly load, long before PatchAll and any drag.
        public static bool ForceShowSmall()
        {
            return Settings != null && Settings.alwaysShowSmall;
        }
    }
}
