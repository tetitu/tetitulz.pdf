// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  Font program classification and stream building
        // ================================================================

        private enum ProgramKind
        {
            Unsupported,
            /// <summary>sfnt with <c>glyf</c> outlines → <c>/FontFile2</c>.</summary>
            SfntTrueType,
            /// <summary>sfnt with CFF outlines ('OTTO') → <c>/FontFile3 /OpenType</c>.</summary>
            SfntOpenTypeCff,
            /// <summary>A bare CFF program → <c>/FontFile3 /Type1C</c>.</summary>
            BareCff,
        }

        private static ProgramKind ClassifyProgram(byte[] program, out string problem)
        {
            problem = null;

            if (program == null || program.Length < 4)
            {
                problem = "the supplied font program is empty or too short to identify";
                return ProgramKind.Unsupported;
            }

            uint tag = (uint)((program[0] << 24) | (program[1] << 16) | (program[2] << 8) | program[3]);

            switch (tag)
            {
                case 0x74746366:   // 'ttcf'
                    problem = "the supplied program is a TrueType Collection ('ttcf'). A collection " +
                              "holds several fonts and cannot be embedded as one; extract the single " +
                              "face you want and supply that";
                    return ProgramKind.Unsupported;

                case 0x4F54544F:   // 'OTTO' — OpenType with CFF outlines
                    return ProgramKind.SfntOpenTypeCff;

                case 0x00010000:
                case 0x74727565:   // 'true'
                    return ProgramKind.SfntTrueType;
            }

            // A bare CFF starts with major/minor version 1.0 and a header size of at least 4.
            if (program[0] == 0x01 && program[1] == 0x00 && program[2] >= 0x04)
                return ProgramKind.BareCff;

            if (program[0] == 0x80 || (program[0] == '%' && program[1] == '!'))
            {
                problem = "the supplied program is a Type 1 font (PFB/PFA). This step embeds TrueType " +
                          "and OpenType programs only; convert it to OpenType (CFF) first";
                return ProgramKind.Unsupported;
            }

            problem = "the supplied font program is in a format this step does not recognise " +
                      "(expected sfnt TrueType, OpenType or bare CFF)";
            return ProgramKind.Unsupported;
        }

        private static PdfName FontFileKey(ProgramKind kind)
            => kind == ProgramKind.SfntTrueType ? PdfName.FontFile2 : PdfName.FontFile3;

        /// <summary>
        /// Wraps the program in a Flate-compressed stream with the length keys its format needs.
        ///
        /// <para>
        /// <c>/Length1</c> (the uncompressed program length) is required for <c>/FontFile2</c>.
        /// <c>/Length2</c> and <c>/Length3</c> exist only for <c>/FontFile</c> — the Type 1
        /// clear/encrypted/trailer split — and this step never emits <c>/FontFile</c>, because
        /// <see cref="ClassifyProgram"/> refuses Type 1 programs outright rather than guessing at
        /// their segment boundaries.
        /// </para>
        /// </summary>
        private static PdfStream BuildFontFileStream(byte[] program, ProgramKind kind)
        {
            var stream = new PdfStream();
            byte[] payload = program;

            try
            {
                byte[] compressed = FlateEncodeFilter.Encode(program);
                if (compressed != null && compressed.Length > 0 && compressed.Length < program.Length)
                {
                    payload = compressed;
                    stream.Set(PdfName.Filter, PdfName.FlateDecode);
                }
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the font program could not be Flate-compressed; it was embedded uncompressed", ex);
            }

            stream.RawData = payload;
            stream.Set(PdfName.Length, new PdfNumber(payload.Length));

            if (kind == ProgramKind.SfntTrueType)
                stream.Set(PdfName.Length1, new PdfNumber(program.Length));
            else if (kind == ProgramKind.SfntOpenTypeCff)
                stream.Set(PdfName.Subtype, PdfName.OpenType);
            else
                stream.Set(PdfName.Subtype, PdfName.Type1C);

            return stream;
        }

        /// <summary>
        /// Fills in the descriptor keys ISO 32000-1 Table 122 requires, <b>without overwriting any
        /// value the document already states</b> — those are what the content stream assumes.
        /// </summary>
        private static void CompleteDescriptor(
            PdfDictionary descriptor, TrueTypeMetrics metrics, FontSite site, string fontName, bool overwriteFontName)
        {
            if (overwriteFontName || descriptor[PdfName.FontName] == null)
                descriptor.Set(PdfName.FontName, new PdfName(fontName));

            if (descriptor[PdfName.Flags] == null)
            {
                int flags = metrics?.Flags ?? 32;
                if (site.IsSymbolic) flags = (flags & ~32) | 4;
                else                 flags = (flags & ~4) | 32;
                descriptor.Set(PdfName.Flags, new PdfNumber(flags));
            }

            if (metrics == null) return;

            if (descriptor[PdfName.FontBBox] == null)
            {
                var bbox = new PdfArray();
                bbox.Add(new PdfNumber(metrics.BBoxXMin));
                bbox.Add(new PdfNumber(metrics.BBoxYMin));
                bbox.Add(new PdfNumber(metrics.BBoxXMax));
                bbox.Add(new PdfNumber(metrics.BBoxYMax));
                descriptor.Set(PdfName.FontBBox, bbox);
            }

            if (descriptor[PdfName.ItalicAngle] == null)
                descriptor.Set(PdfName.ItalicAngle, new PdfNumber(metrics.ItalicAngle));
            if (descriptor[PdfName.Ascent] == null)
                descriptor.Set(PdfName.Ascent, new PdfNumber(metrics.Ascent));
            if (descriptor[PdfName.Descent] == null)
                descriptor.Set(PdfName.Descent, new PdfNumber(metrics.Descent));
            if (descriptor[PdfName.CapHeight] == null)
                descriptor.Set(PdfName.CapHeight, new PdfNumber(metrics.CapHeight));
            if (descriptor[PdfName.StemV] == null)
                descriptor.Set(PdfName.StemV, new PdfNumber(metrics.StemV));
            if (descriptor[PdfName.MissingWidth] == null && !site.IsType0)
                descriptor.Set(PdfName.MissingWidth, new PdfNumber(0));
        }
    }
}
