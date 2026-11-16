// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>How a combined fill and stroke composites (ISO 32000-2 §11.7.4.4).</summary>
    internal enum FillStrokeComposite
    {
        /// <summary>The fill, then the stroke, each painted as it is: no group changes the result.</summary>
        Direct,

        /// <summary>A non-isolated knockout group: the fill is kept out from under the stroke.</summary>
        Knockout,

        /// <summary>The overprint case: a non-knockout group painted at alpha 1, composited at the alpha and blend mode.</summary>
        OverprintGroup,
    }

    /// <summary>
    /// ISO 32000-2 §11.7.4.4: <c>B</c>, <c>B*</c>, <c>b</c>, <c>b*</c> and text rendering modes 2
    /// and 6 fill and stroke "as if they were enclosed in a transparency group" — a non-isolated,
    /// non-knockout one painted at alpha 1 when both overprint parameters are true and the alpha
    /// constants equal, a non-isolated knockout one otherwise. The one answer both renderers ask.
    /// </summary>
    /// <remarks>
    /// <para><b>When no group is needed.</b> The overprint group at alpha 1 and Normal blending is
    /// the two paints as they are; and outside it, an opaque Normal stroke with no soft mask covers
    /// the fill whichever way it is composited. Those are <see cref="FillStrokeComposite.Direct"/>, so
    /// ordinary paths pay for nothing.</para>
    /// <para>A leaf: no state, so either renderer can reach it without a type-initialisation edge.</para>
    /// </remarks>
    internal static class FillStrokeCompositing
    {
        private const float AlphaEpsilon = 1f / 512f;

        internal static FillStrokeComposite Of(bool overprintFill, bool overprintStroke,
                                               float fillAlpha, float strokeAlpha,
                                               bool normalBlend, bool softMask)
        {
            if (overprintFill && overprintStroke && Math.Abs(fillAlpha - strokeAlpha) <= AlphaEpsilon)
                return strokeAlpha < 1f - AlphaEpsilon || !normalBlend
                    ? FillStrokeComposite.OverprintGroup
                    : FillStrokeComposite.Direct;

            return strokeAlpha < 1f - AlphaEpsilon || !normalBlend || softMask
                ? FillStrokeComposite.Knockout
                : FillStrokeComposite.Direct;
        }
    }
}
