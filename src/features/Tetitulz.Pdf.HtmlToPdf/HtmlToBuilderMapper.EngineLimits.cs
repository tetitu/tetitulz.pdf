// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// Reports the layout constructs the managed engine cannot express, so a caller learns to switch
    /// engines instead of shipping a wrong-looking PDF.
    ///
    /// <para><b>Why this exists at all.</b> The two engines are a deliberate pair, not a migration:
    /// the managed one is fast and light, the Playwright one is powerful and expensive, and the
    /// developer picks per call. That makes <i>completeness</i> the wrong goal for the managed engine
    /// and <i>honesty</i> the right one — but <c>display: grid</c> and <c>position: absolute</c> were
    /// named as unsupported in four doc comments while no code looked for them, so a document using
    /// either fell through to ordinary block flow and stacked. The output is a perfectly valid PDF
    /// with the right elements present and the wrong geometry, and nothing said so.</para>
    ///
    /// <para><b>That is the worst failure available under an engine-choice model</b>, because the
    /// decision it corrupts is the caller's, not ours: silence reads as "the managed engine handled
    /// it". <c>flex</c> and <c>float</c> already report what they approximate — these are the two
    /// that did not.</para>
    ///
    /// <para><b>Presence is not deviation.</b> Following the rule the render diagnostics already
    /// follow, only values that actually differ from what the engine does are reported:
    /// <c>position: static</c> is the default and <c>position: relative</c> with no offsets renders
    /// identically, so neither fires. A channel that fires on conformant input is one a caller learns
    /// to ignore.</para>
    /// </summary>
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>
        /// Reports <c>display: grid</c> and out-of-flow <c>position</c> on one element, if present.
        /// Called for every block, beside the flex and float handling.
        /// </summary>
        private void ReportEngineLayoutLimits(IElement el)
        {
            // display: grid IS mapped now, onto a table, whenever the container declares a usable
            // column template (HtmlToBuilderMapper.Grid.cs). What remains reportable is the case
            // that mapping declines: a grid with no template, whose children really do stack.
            //
            // The narrowing matters more than it looks. Fired for every grid, including the ones laid
            // out correctly, this would be a channel that fires on input the engine handles — one a
            // caller learns to ignore — which would take the genuine
            // reports (spanning, gap, explicit placement) down with it.
            string? display = StyleValue(el, "display")?.Trim();
            if (display != null && (Eq(display, "grid") || Eq(display, "inline-grid"))
                && string.IsNullOrWhiteSpace(StyleValue(el, "grid-template-columns")))
            {
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "display: " + display + " with no grid-template-columns; the children were "
                    + "stacked as ordinary blocks. The managed engine takes its column count from "
                    + "the template, and cannot infer one from the items — give the container a "
                    + "grid-template-columns, or use HtmlRenderEngine.Browser if the layout "
                    + "matters.");
            }

            string? position = StyleValue(el, "position")?.Trim();
            if (position == null) return;

            // static is the default and relative-without-offsets is indistinguishable from it, so
            // neither is a deviation from what this engine already does.
            if (Eq(position, "static")) return;
            if (Eq(position, "relative") && !HasAnyOffset(el)) return;
            // absolute and fixed are placed out of flow where MapBlock hands the element to
            // MapPositioned, so they are reported after the mapping, and only
            // where the element stayed in the flow (ReportUnplacedOutOfFlow), so a placed stamp is
            // never told it "stayed in normal flow" while it sits at its offsets.
            if (IsPositioned(el)) return;

            ReportPositionNotSupported(position);
        }

        /// <summary>
        /// Reports an absolutely positioned or fixed element that MapBlock did not place out of flow —
        /// a table, a list, a heading, a flex, grid or multi-column container, which the switch maps
        /// in the flow before <c>MapPositioned</c> is reached. Called after the element is mapped.
        /// </summary>
        private void ReportUnplacedOutOfFlow(IElement el)
        {
            if (IsPositioned(el)) ReportPositionNotSupported(StyleValue(el, "position")!.Trim());
        }

        private void ReportPositionNotSupported(string position)
        {
            ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                "position: " + position + " is not supported; the element stayed in normal flow and "
                + "any top/right/bottom/left offsets were ignored. Out-of-flow placement and z-order "
                + "need a positioned-box model the managed engine does not have — use "
                + "HtmlRenderEngine.Browser if the layout matters.");
        }

        /// <summary>The four box-offset properties, hoisted out of the per-element loop below.</summary>
        private static readonly string[] BoxOffsetSides = { "top", "right", "bottom", "left" };

        /// <summary>True when any box-offset property is set to something other than <c>auto</c>.</summary>
        private bool HasAnyOffset(IElement el)
        {
            foreach (string side in BoxOffsetSides)
            {
                string? value = StyleValue(el, side)?.Trim();
                if (value != null && value.Length > 0 && !Eq(value, "auto")) return true;
            }
            return false;
        }
    }
}
