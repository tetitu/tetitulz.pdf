// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Resolves the font a content stream names with <c>Tf</c> — resource dictionary → <c>/Font</c>
    /// → the operand's name → a parsed <see cref="PdfFont"/> (§7.8.3, §9.4.2).
    /// </summary>
    /// <remarks>
    /// <para><b>Shared by the two rewriters that must destroy content.</b>
    /// <c>PdfRedactionEngine</c> and <c>PdfTemplateEngine</c> are already a documented pair — both
    /// go through <see cref="PdfFullRewriter"/> because both have to leave no copy of the bytes
    /// they replace — and both opened their <c>GetFont</c> with this identical seven-line walk.</para>
    ///
    /// <para><b>A font that will not parse yields <c>null</c>, not an exception.</b> Neither
    /// operation can abandon a page over one unreadable <c>/Font</c> entry: the redactor still has
    /// to remove the content it was asked to remove, and the template filler still has to fill the
    /// rest of the document. What each does <i>with</i> a null font is its own decision and stays at
    /// the call site.</para>
    ///
    /// <para><b>What is deliberately NOT shared is the code-splitting policy</b>, and it is the more
    /// interesting half. Both engines wrap this in a <c>Split</c> that turns a string's bytes into
    /// character codes, and they differ on purpose: the template filler falls back to one code per
    /// byte when the CMap will not decode, while the redactor lets the failure propagate. That is
    /// not an oversight — a mixed-width CMap split one byte at a time lets a redaction hit drop half
    /// of a two-byte CJK code and keep the other half, "leaving bytes that decode to something the
    /// author never wrote", as the redactor's own comment puts it. Guessing is acceptable when
    /// filling a template and unacceptable when destroying evidence. <b>Do not unify them.</b></para>
    /// </remarks>
    internal static class ResourceFontLookup
    {
        /// <summary>
        /// The font <paramref name="fontName"/> names in <paramref name="resources"/>, or
        /// <c>null</c> when the name, the resources, the <c>/Font</c> sub-dictionary or the entry is
        /// absent — or when the font dictionary cannot be parsed.
        /// </summary>
        internal static PdfFont Resolve(string fontName, PdfDictionary resources, PdfDocument doc)
        {
            if (fontName == null || resources == null || doc == null) return null;

            var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
            if (fonts == null) return null;

            var fontDict = doc.ResolveObject(fonts[new PdfName(fontName)]) as PdfDictionary;
            if (fontDict == null) return null;

            try { return PdfFont.Create(fontDict, doc); }
            catch { return null; }
        }
    }
}
