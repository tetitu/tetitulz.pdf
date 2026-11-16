// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Threading;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The cancellation-polling cadence shared by both renderers' content-stream loops.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a shared leaf rather than a constant in each renderer.</b> The raster interpreter
    /// and the SVG writer must abandon a hostile page on the same terms; two private copies of the
    /// interval are two things to forget to change together, and this repository's recurring defect
    /// class is "one renderer fixed, the other silently disagreeing". Sharing it <i>downward into a
    /// leaf</i> is the sanctioned move — neither renderer may call the other's statics, because
    /// <c>ContentStreamInterpreter</c> has a static initializer and pointing the SVG writer at it
    /// once crashed the Base test host part-way through a parallel run while still reporting
    /// success.</para>
    ///
    /// <para><b>No static fields, so no type initializer</b> — the property that makes
    /// <c>SvgSyntax</c> and <c>RenderBudget</c> safe leaves by construction rather than by
    /// assertion.</para>
    /// </remarks>
    internal static class RenderCancellation
    {
        /// <summary>
        /// How many operators run between cancellation checks. Small enough that a cancelled render
        /// stops promptly, large enough that the check is not measurable.
        /// </summary>
        /// <remarks>
        /// Polling every operator would put an interlocked read in the hottest loop in the renderer
        /// for a token that is almost never cancellable; a page is tens of thousands of operators,
        /// so this bounds the overrun to a fraction of a millisecond while costing a counter
        /// increment.
        /// </remarks>
        internal const int CheckInterval = 256;

        /// <summary>
        /// Advances <paramref name="sinceCheck"/> and throws once the interval elapses.
        /// </summary>
        /// <remarks>
        /// <b>Call this OUTSIDE the per-operator <c>try</c>.</b> Both renderers fault-isolate a bad
        /// operator with a blanket <c>catch (Exception)</c>, so a cancellation raised inside dispatch
        /// is swallowed, reported as a content-stream error the file does not have, and the loop
        /// carries on to the end — which is not cancellation at all.
        /// </remarks>
        internal static void Poll(ref int sinceCheck, bool cancellable, in CancellationToken token)
        {
            if (!cancellable) return;
            if (++sinceCheck < CheckInterval) return;

            sinceCheck = 0;
            token.ThrowIfCancellationRequested();
        }
    }
}
