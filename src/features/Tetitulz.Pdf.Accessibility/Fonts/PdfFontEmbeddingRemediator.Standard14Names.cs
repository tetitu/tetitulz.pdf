// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  Standard-14 name canonicalisation
        // ================================================================

        /// <summary>
        /// Maps a <c>/BaseFont</c> onto its canonical Standard-14 name, or null when it is not one
        /// of them.
        /// </summary>
        /// <remarks>
        /// <para>The rule moved to <see cref="Standard14Names.Canonical"/>, in Core beside the AFM
        /// metrics it selects. It lived here and — separately — in the PostScript plugin's
        /// <c>PsFontMapper</c>, and the two disagreed: on <c>TimesNewRomanPSMT</c> this body
        /// answered Times-Roman and that one answered <b>Helvetica</b>, so a PostScript program
        /// naming that font had its text drawn in Helvetica at Helvetica advances.</para>
        ///
        /// <para>Kept as a named forwarder rather than inlined at the two call sites because the
        /// name is what those sites read as, and because it is the anchor for this note.</para>
        /// </remarks>
        internal static string CanonicalStandard14Name(string baseFont)
            => Standard14Names.Canonical(baseFont);
    }
}
