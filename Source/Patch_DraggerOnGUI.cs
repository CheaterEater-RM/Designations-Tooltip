using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DesignationsTooltip
{
    // RimWorld 1.6 changed the center drag tooltip to draw the enclosing rectangle's cell count
    // (buffer.Count) instead of the number of cells actually affected. We swap that single
    // Widgets.DrawNumberOnMap call for our own draw method that renders "affected / total".
    //
    // A transpiler is used (rather than the usually-preferred postfix) because the number is drawn
    // mid-method: a postfix can't un-draw the box, and a bool-prefix would force reimplementing the
    // whole method (outline + both edge measurements + boundary/snap math). The transpiler leaves
    // every other instruction byte-identical and fails safe to vanilla if the IL site ever moves.
    //
    // The target call is uniquely identifiable: it is the only Widgets.DrawNumberOnMap whose int
    // argument comes from `ldarg.0; ldfld buffer; callvirt List<IntVec3>.get_Count`. The two edge
    // measurements push intVec.x / intVec.z (locals), never buffer.
    [HarmonyPatch(typeof(DesignationDragger), nameof(DesignationDragger.DraggerOnGUI))]
    public static class Patch_DraggerOnGUI
    {
        private static readonly FieldInfo BufferField =
            AccessTools.Field(typeof(DesignationDragger), "buffer");
        private static readonly MethodInfo GetCount =
            AccessTools.PropertyGetter(typeof(List<IntVec3>), "Count");
        private static readonly MethodInfo GetWhite =
            AccessTools.PropertyGetter(typeof(Color), "white");
        private static readonly MethodInfo DrawNumber =
            AccessTools.Method(typeof(Widgets), "DrawNumberOnMap",
                new[] { typeof(Vector2), typeof(int), typeof(Color) });
        private static readonly MethodInfo Replacement =
            AccessTools.Method(typeof(Patch_DraggerOnGUI), nameof(DrawAffectedCount));

        private static bool loggedDrawError;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions);
            matcher.MatchStartForward(
                new CodeMatch(i => i.LoadsField(BufferField)),
                new CodeMatch(i => i.Calls(GetCount)),
                new CodeMatch(i => i.Calls(GetWhite)),
                new CodeMatch(i => i.Calls(DrawNumber)));

            if (!matcher.IsValid)
            {
                Log.Error("[Designations Tooltip] Could not locate the area-count draw site in "
                    + "DesignationDragger.DraggerOnGUI; leaving vanilla behavior intact. "
                    + "RimWorld version may have changed.");
                return instructions;
            }

            // At the call site the stack holds, in order:
            //   [Vector2 pos] [this (from ldarg.0)] [buffer.Count] [Color.white]
            // We match the trailing 4 instructions (ldfld buffer; get_Count; get_white;
            // DrawNumberOnMap) and replace them with a call to DrawAffectedCount(Vector2,
            // DesignationDragger). That consumes the leftover `this` (pushed by the ldarg.0 just
            // before our window) plus the Vector2, so the stack stays balanced — no need to touch
            // the ldarg.0. Carry the first removed instruction's labels/blocks (the `ldfld buffer`
            // is the surrounding if-block's branch target) onto our call.
            var call = new CodeInstruction(OpCodes.Call, Replacement);
            call.labels.AddRange(matcher.Instruction.labels);
            call.blocks.AddRange(matcher.Instruction.blocks);

            matcher.RemoveInstructions(4).Insert(call);
            return matcher.InstructionEnumeration();
        }

        // Replicates Widgets.DrawNumberOnMap's box geometry but renders "affected / total".
        // screenPos is the center position already computed by vanilla and left on the IL stack;
        // dragger is the DesignationDragger instance (the leftover `this` from the call site).
        private static void DrawAffectedCount(Vector2 screenPos, DesignationDragger dragger)
        {
            try
            {
                int area = dragger.CellBuffer.Count;    // total enclosing rectangle
                int affected = dragger.DragCells.Count; // cells passing CanDesignateCell
                string text = affected + " / " + area;

                Text.Anchor = TextAnchor.MiddleCenter;
                Text.Font = GameFont.Medium;
                float val = Text.CalcSize(text).x + 8f;
                Rect rect = new Rect(screenPos.x - 20f, screenPos.y - 15f, Mathf.Max(40f, val), 30f);
                GUI.DrawTexture(rect, TexUI.GrayBg);
                GUI.color = Color.white;
                Widgets.Label(rect, text);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
            }
            catch (Exception e)
            {
                if (!loggedDrawError)
                {
                    loggedDrawError = true;
                    Log.Error("[Designations Tooltip] DrawAffectedCount failed: " + e);
                }
            }
        }
    }
}
