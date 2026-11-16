// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The dictionaries every composite-font writer shares (ISO 32000-1 §9.7): a
    /// <c>/CIDSystemInfo</c> (Table 116), a descendant CIDFont's common entries (Table 117) and the
    /// Type0 font over it (Table 114). Each writer adds what is its own — <c>/DW</c>, <c>/W</c>,
    /// <c>/CIDToGIDMap</c>, <c>/ToUnicode</c> — after them.
    /// </summary>
    /// <remarks>
    /// <para><b>Public because a plugin needs it.</b> Core's <c>InternalsVisibleTo</c> names no
    /// plugin, and <c>Plugins.PostScript</c> writes three kinds of composite font beside
    /// <see cref="Type0FontBuilder"/> and Base's OCR text-layer font.</para>
    /// </remarks>
    public static class CompositeFontDictionaries
    {
        /// <summary>A <c>/CIDSystemInfo</c> dictionary (Table 116): the character collection a CIDFont's CIDs belong to.</summary>
        /// <param name="registry">The collection's issuer, <c>Adobe</c> for every registered collection.</param>
        /// <param name="ordering">The collection within the registry: <c>Japan1</c>, <c>Identity</c>, …</param>
        /// <param name="supplement">The collection's supplement number.</param>
        public static PdfDictionary CidSystemInfo(string registry, string ordering, int supplement)
        {
            var info = new PdfDictionary();
            info.Set(PdfName.Registry, new PdfString(registry));
            info.Set(PdfName.Ordering, new PdfString(ordering));
            info.Set(PdfName.Supplement, new PdfNumber(supplement));
            return info;
        }

        /// <summary>
        /// Adobe-Identity-0: the collection of a font whose CIDs are its glyph indices or its own codes.
        /// </summary>
        public static PdfDictionary IdentityCidSystemInfo() => CidSystemInfo("Adobe", "Identity", 0);

        /// <summary>
        /// A descendant CIDFont (Table 117) with the entries every one carries, in this order:
        /// <c>/Type /Font</c>, <paramref name="subtype"/>, <c>/BaseFont</c>, <c>/CIDSystemInfo</c> and
        /// <c>/FontDescriptor</c>.
        /// </summary>
        /// <param name="subtype"><c>/CIDFontType0</c> (CFF glyphs) or <c>/CIDFontType2</c> (TrueType glyphs).</param>
        /// <param name="baseFont">The font's PostScript name, the same as its descriptor's <c>/FontName</c>.</param>
        /// <param name="cidSystemInfo">The collection its CIDs belong to (<see cref="CidSystemInfo"/>).</param>
        /// <param name="fontDescriptor">The descriptor, a dictionary or a reference to one.</param>
        public static PdfDictionary DescendantCidFont(
            PdfName subtype, string baseFont, PdfDictionary cidSystemInfo, PdfObject fontDescriptor)
        {
            var cid = new PdfDictionary();
            cid.Set(PdfName.Type, PdfName.Font);
            cid.Set(PdfName.Subtype, subtype);
            cid.Set(PdfName.BaseFont, new PdfName(baseFont));
            cid.Set(PdfName.CIDSystemInfo, cidSystemInfo);
            cid.Set(PdfName.FontDescriptor, fontDescriptor);
            return cid;
        }

        /// <summary>
        /// A Type0 font (Table 114) over one descendant, in this order: <c>/Type /Font</c>,
        /// <c>/Subtype /Type0</c>, <c>/BaseFont</c>, <c>/Encoding</c> and the one-element
        /// <c>/DescendantFonts</c>.
        /// </summary>
        /// <param name="baseFont">The Type0 font's name: the descendant's, or with the CMap's name after a hyphen.</param>
        /// <param name="encoding">A CMap: its name (<c>/Identity-H</c>, a predefined CMap) or an embedded CMap stream.</param>
        /// <param name="descendant">The descendant CIDFont, a dictionary or a reference to one.</param>
        public static PdfDictionary Type0(string baseFont, PdfObject encoding, PdfObject descendant)
        {
            var type0 = new PdfDictionary();
            type0.Set(PdfName.Type, PdfName.Font);
            type0.Set(PdfName.Subtype, PdfName.Type0);
            type0.Set(PdfName.BaseFont, new PdfName(baseFont));
            type0.Set(PdfName.Encoding, encoding);
            var descendants = new PdfArray();
            descendants.Add(descendant);
            type0.Set(PdfName.DescendantFonts, descendants);
            return type0;
        }
    }
}
