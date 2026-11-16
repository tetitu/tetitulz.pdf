// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// The name values a graphics-state soft mask takes — the <c>/SMask</c> entry itself
    /// (§11.6.4.4, Table 58) and the mask dictionary's <c>/S</c> subtype (§11.6.5.2).
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately not folded into <see cref="PdfBlendModes"/>, which already has a
    /// <c>Luminosity</c>.</b> They are different concepts that happen to share a spelling: the
    /// blend mode is §11.3.5's separable-and-non-separable function, the soft-mask subtype is
    /// §11.6.5.2's "which channel of the mask group becomes the mask". Collapsing them would make
    /// a rename in one silently change the other, which is the trap
    /// <c>PdfLimits.BezierFlattenSteps</c> vs <c>SvgGradientStopCount</c> already records.</para>
    ///
    /// <para><b><see cref="Identity"/> is not in Table 58.</b> The table gives <c>/SMask</c> a mask
    /// dictionary or the name <c>/None</c>, and nothing else. Real producers write
    /// <c>/SMask /Identity</c> for "no mask", and both renderers must agree on what to do with it —
    /// they did not, which is why this constant exists rather than a bare literal: the raster path
    /// cleared only on <c>/None</c> and so left a stale soft mask in force, while the SVG path
    /// cleared on either.</para>
    /// </remarks>
    public static class PdfSoftMaskNames
    {
        /// <summary><c>/None</c> — Table 58's "no soft mask".</summary>
        public const string None = "None";

        /// <summary>
        /// <c>/Identity</c> — not a Table 58 value, but written by real producers to mean
        /// <see cref="None"/>, and treated as such by both renderers.
        /// </summary>
        public const string Identity = "Identity";

        /// <summary><c>/S /Alpha</c> — §11.6.5.2's alpha soft mask. The subtype default.</summary>
        public const string Alpha = "Alpha";

        /// <summary><c>/S /Luminosity</c> — §11.6.5.2's luminosity soft mask.</summary>
        public const string Luminosity = "Luminosity";

        /// <summary>
        /// True when <paramref name="name"/> is a <c>/SMask</c> name meaning "no soft mask" —
        /// <see cref="None"/> or <see cref="Identity"/>. <b>One implementation, both renderers.</b>
        /// </summary>
        public static bool IsNoSoftMask(string name) => name == None || name == Identity;
    }
}
