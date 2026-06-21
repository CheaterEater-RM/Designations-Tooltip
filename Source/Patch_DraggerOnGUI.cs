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

        // Feature 2 (always-show-small) injection anchors.
        private static readonly MethodInfo SqrMagnitudeGetter =
            AccessTools.PropertyGetter(typeof(IntVec3), "SqrMagnitude");
        private static readonly MethodInfo ForceShowSmall =
            AccessTools.Method(typeof(DesignationsTooltipMod), nameof(DesignationsTooltipMod.ForceShowSmall));

        private static bool loggedDrawError;

        // The ILGenerator is injected by Harmony and must be handed to CodeMatcher: CreateLabel (used by
        // the always-show injection below) throws "Generator must be provided" without it.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var matcher = new CodeMatcher(instructions, generator);
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

            // Second, independent edit: when the "always show on small drags" setting is on, bypass
            // vanilla's size guard. Fails on its own (the draw-site swap above already applied), so a
            // missing anchor leaves the affected/total readout working and only drops Feature 2.
            TryInjectAlwaysShow(matcher);

            return matcher.InstructionEnumeration();
        }

        // Vanilla draws the center count only when
        //     intVec.SqrMagnitude >= 9f && intVec.x >= 3 && intVec.z >= 3   (A,B,C — the size guard)
        //     && buffer.Count > 0 && SelStyle.drawArea                       (D,E — keep these)
        // so any drag with a side of 1–2 cells is suppressed. We inject, immediately before A,
        //     if (DesignationsTooltipMod.ForceShowSmall()) goto D;
        // which skips A/B/C straight to the buffer.Count>0 check when the setting is on — small drags
        // still draw, but empty buffers and area-disabled styles are still gated out by D/E. When the
        // setting is off the injected call returns false and control falls through into A: byte-identical
        // to vanilla. The single draw call is unchanged, so there is no double-draw.
        private static void TryInjectAlwaysShow(CodeMatcher matcher)
        {
            // Condition D: ldarg.0; ldfld buffer; callvirt get_Count; ldc.i4.0; ble. Unique now that the
            // draw-site swap above removed the only other `ldfld buffer; get_Count` in the method; the
            // trailing ldc.i4.0 + ble also distinguishes it from that (get_white) site regardless.
            matcher.Start();
            matcher.MatchStartForward(
                new CodeMatch(i => i.opcode == OpCodes.Ldarg_0),
                new CodeMatch(i => i.LoadsField(BufferField)),
                new CodeMatch(i => i.Calls(GetCount)),
                new CodeMatch(i => i.opcode == OpCodes.Ldc_I4_0),
                new CodeMatch(i => i.opcode == OpCodes.Ble || i.opcode == OpCodes.Ble_S));

            if (!matcher.IsValid)
            {
                LogAlwaysShowAnchorError();
                return;
            }

            // Label condition D's first instruction (the jump target). It has no incoming branch today.
            matcher.CreateLabel(out Label dLabel);

            // Walk back to condition A's start: ldloca.s intVec; call get_SqrMagnitude.
            matcher.MatchStartBackwards(
                new CodeMatch(i => i.opcode == OpCodes.Ldloca || i.opcode == OpCodes.Ldloca_S),
                new CodeMatch(i => i.Calls(SqrMagnitudeGetter)));

            if (!matcher.IsValid)
            {
                LogAlwaysShowAnchorError();
                return;
            }

            // Condition A's first instruction is the branch target of the edge-measurement blocks, so its
            // labels/blocks must MOVE onto our injected call — leaving them here would let those branches
            // skip the bypass.
            CodeInstruction anchor = matcher.Instruction;
            var movedLabels = new List<Label>(anchor.labels);
            var movedBlocks = new List<ExceptionBlock>(anchor.blocks);
            anchor.labels.Clear();
            anchor.blocks.Clear();

            var callForce = new CodeInstruction(OpCodes.Call, ForceShowSmall);
            callForce.labels.AddRange(movedLabels);
            callForce.blocks.AddRange(movedBlocks);
            // Long-form brtrue: Harmony shortens it at emit if it fits, but offsets shift as instructions
            // are inserted, so never hand-pick the short form.
            var branchTrue = new CodeInstruction(OpCodes.Brtrue, dLabel);

            matcher.Insert(callForce, branchTrue);
        }

        private static bool loggedAnchorError;

        private static void LogAlwaysShowAnchorError()
        {
            if (loggedAnchorError)
            {
                return;
            }
            loggedAnchorError = true;
            Log.Error("[Designations Tooltip] Could not locate the always-show-small anchor in "
                + "DesignationDragger.DraggerOnGUI; the affected/total readout still works but the "
                + "\"always show on small drags\" option has no effect. RimWorld version may have changed.");
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
                // Classic mode shows just the affected count ("8"); default shows "affected / total".
                string text = (DesignationsTooltipMod.Settings != null && DesignationsTooltipMod.Settings.classicMode)
                    ? affected.ToString()
                    : affected + " / " + area;

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
