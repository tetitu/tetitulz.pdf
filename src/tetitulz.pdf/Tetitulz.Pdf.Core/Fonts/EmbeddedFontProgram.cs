// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Reads the Type 1 family program embedded under a font or CIDFont dictionary's
    /// <c>/FontDescriptor</c> — <c>/FontFile3</c> as CFF, <c>/FontFile</c> as a Type 1 program — for a
    /// caller that compares what the dictionary declares against what the program holds.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="SimpleFontWidthProbe"/> and the PDF/UA-2 font checks, so both read the same
    /// stream the same way, under the font-program ceiling (<see cref="FontStreamBytes.Program"/>). A
    /// stream that will not decode, or decodes past the ceiling, throws; a program that does not parse
    /// answers null.
    /// </remarks>
    internal static class EmbeddedFontProgram
    {
        /// <summary>The <c>/FontFile3</c> program, parsed as CFF (bare, or inside an OpenType wrapper), or null.</summary>
        internal static CffFont ReadCff(PdfDictionary fontOrCidFont, PdfDocument document)
        {
            if (!(Resolve(document, Descriptor(fontOrCidFont, document)?[PdfName.FontFile3]) is PdfStream stream)) return null;
            byte[] data = FontStreamBytes.Program(stream);
            return data == null || data.Length == 0 ? null : CffParser.Parse(data);
        }

        /// <summary>
        /// The <c>/FontFile3</c> program read as an sfnt, for the metric tables an OpenType wrapper
        /// (<c>'OTTO'</c>) carries around its CFF — <c>hmtx</c>, <c>vhea</c>/<c>vmtx</c>. A bare CFF has no
        /// table directory, so it reads as no program or as one with none of those tables; no
        /// signature check is needed to tell the two apart, and none is made.
        /// </summary>
        internal static TrueTypeFontProgram ReadOpenTypeWrapper(PdfDictionary fontOrCidFont, PdfDocument document)
        {
            if (!(Resolve(document, Descriptor(fontOrCidFont, document)?[PdfName.FontFile3]) is PdfStream stream)) return null;
            byte[] data = FontStreamBytes.Program(stream);
            return data == null ? null : TrueTypeFontProgram.Parse(data);
        }

        /// <summary>
        /// The <c>/FontFile</c> program, parsed with its <c>/Length1</c> cleartext and <c>/Length2</c>
        /// encrypted portions, or null.
        /// </summary>
        internal static Type1Font ReadType1(PdfDictionary font, PdfDocument document)
        {
            if (!(Resolve(document, Descriptor(font, document)?[PdfName.FontFile]) is PdfStream stream)) return null;
            if (!(Resolve(document, stream[PdfName.Length1]) is PdfNumber length1)
                || !(Resolve(document, stream[PdfName.Length2]) is PdfNumber length2))
                return null;

            byte[] data = FontStreamBytes.Program(stream);
            return data == null || data.Length == 0 ? null : Type1Parser.Parse(data, length1.IntValue, length2.IntValue);
        }

        private static PdfDictionary Descriptor(PdfDictionary font, PdfDocument document)
            => Resolve(document, font?[PdfName.FontDescriptor]) as PdfDictionary;

        private static PdfObject Resolve(PdfDocument document, PdfObject obj)
            => obj == null ? null : document?.ResolveObject(obj) ?? obj;
    }
}
