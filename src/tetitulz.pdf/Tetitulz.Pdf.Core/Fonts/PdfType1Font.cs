// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A <c>/Subtype /Type1</c> (or <c>/MMType1</c>, or <c>/Type3</c>) simple font.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is here but the construction order.</b> The encoding ladder and the Standard-14
    /// width tier are <see cref="PdfSimpleFont"/>'s (<see cref="PdfSimpleFont.GetWidth"/>,
    /// <see cref="PdfSimpleFont.ReadEncoding"/>), since a <c>/Subtype /TrueType</c> font with no
    /// <c>/Widths</c> needs them too and two copies of the §9.6.6 ladder drift. A subtype class that reads as a list of what it owns, and is wrong about every item,
    /// is the same defect marker as one asserting that two implementations agree.
    /// </remarks>
    internal sealed class PdfType1Font : PdfSimpleFont
    {
        public PdfType1Font(PdfDictionary fontDict, PdfDocument document, Dictionary<int, string> toUnicode)
        {
            BaseFont = ReadBaseFontAndWidths(fontDict, document, toUnicode);
            // /MissingWidth and §9.6.6.4's Symbolic flag — one implementation for both simple
            // subtypes, in PdfSimpleFont. See ReadFontDescriptor for what Table 123 actually says
            // and why bit 3 alone is the rule both subtypes apply.
            bool symbolic = ReadFontDescriptor(fontDict, document);

            // §9.6.6's whole encoding ladder — one implementation for both simple subtypes, in
            // PdfSimpleFont. Two copies would drift: one assuming WinAnsi for every font with no
            // /Encoding while the other follows §9.6.6.4's StandardEncoding would extract byte 0x27
            // as U+0027 APOSTROPHE through one class and U+2019 RIGHT SINGLE
            // QUOTATION MARK through that one; 0x60 diverged the same way. A /Subtype /Type1 dict
            // with no /Encoding is the single most common simple-font shape there is.
            //
            // The return value — whether the dictionary named an /Encoding at all — is only of
            // interest to the TrueType subtype's built-in-cmap test, so it is discarded here.
            ReadEncoding(fontDict, document, symbolic);
        }
    }
}
