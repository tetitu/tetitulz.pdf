// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Re-emits a PDF's own embedded font program into an SVG export as a CSS
    /// <c>@font-face</c> rule with a <c>data:</c> URI, so a run drawn as
    /// <c>&lt;text&gt;</c> is drawn in the document's font rather than in a substitute.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the answer for the runs that outlining cannot take.</b>
    /// <c>SvgContentWriter.TryEmitGlyphOutlines</c> draws an embedded font as vector
    /// <c>&lt;path&gt;</c> outlines — the actual glyph shapes, pixel-identical to the raster
    /// renderer — and that stays the preferred path. Two populations fall past it with a font
    /// program sitting right there in the file: a <b>vertical writing-mode</b> run (§9.7.4.3),
    /// which the outline emitter declines because it places each glyph individually, and an
    /// embedded program <b>SkiaSharp would not load</b>, where <c>FontCache</c> has already
    /// resolved a system substitute. A family chosen by name similarity would render both with the
    /// wrong shapes and the wrong widths — and vertical CJK is precisely where a substitute is
    /// least likely to be right.</para>
    ///
    /// <para><b>A viewer may succeed where this library failed.</b> The second case is not a
    /// consolation prize: a browser's font stack is not Skia's, and handing it the
    /// bytes is strictly more information than handing it a family name. The <c>@font-face</c>
    /// leads the family stack and the system names stay behind it, so a program the viewer also
    /// rejects falls back to exactly what the export produced before.</para>
    ///
    /// <para><b>An sfnt is emitted as it is; a bare CFF or a Type 1 program is wrapped first.</b>
    /// A browser accepts TrueType and OpenType containers and nothing else, so a bare CFF
    /// (<c>/FontFile3 /Type1C</c>, <c>/CIDFontType0C</c>) gets the OpenType envelope
    /// <see cref="CffOtfWrapper"/> already builds for SkiaSharp, and a Type 1 program
    /// (<c>/FontFile</c>) is converted to CFF (<c>Type1ToCffConverter</c>, the subsetter's route)
    /// and then wrapped. <b>Such a program is glyph-indexed</b>: its <c>cmap</c> maps
    /// U+F0000 + <i>g</i> to glyph <i>g</i>, because the font's own encoding is not the document's
    /// (the PDF's <c>/Encoding</c> or CMap decides which glyph a code draws, and a CID-keyed CFF has
    /// no encoding at all), so the writer hands the browser those code points for the visible run and
    /// the PDF's text in an invisible overlay beside it, as the outline path does.
    /// <see cref="Program.GlyphIndexed"/> says which kind a program is; a Type 1 program also
    /// carries the converted CFF's <see cref="Program.GlyphNameToGid"/>, since its PDF codes select
    /// glyphs by name. A program neither route can read refuses, and says why.</para>
    ///
    /// <para><b>The licence gate is <c>fsType</c>, and it is checked.</b> The source document
    /// embedded the program under whatever right its producer held; an SVG is a different
    /// container with different distribution, so a font whose <c>OS/2</c> table forbids outline
    /// embedding is not re-emitted. <see cref="FontEmbeddingLicense"/> owns that reading —
    /// including that bits 0–3 are one mutually-exclusive value rather than four flags.</para>
    /// </remarks>
    internal static class SvgFontEmbedder
    {
        /// <summary>An embeddable program, ready to be written into a <c>&lt;style&gt;</c>.</summary>
        internal readonly struct Program
        {
            /// <summary>The font program's own object number, which is its identity for dedup.</summary>
            public readonly long ObjectNumber;

            /// <summary>The decoded sfnt bytes.</summary>
            public readonly byte[] Bytes;

            /// <summary>The CSS <c>format()</c> hint: <c>truetype</c> or <c>opentype</c>.</summary>
            public readonly string Format;

            /// <summary>
            /// True when <see cref="Bytes"/> is a container this class synthesised around a bare CFF or
            /// a converted Type 1 program, whose <c>cmap</c> addresses glyphs by index
            /// (<see cref="CffOtfWrapper.GlyphIndexedCmapBase"/> + glyph): the writer must hand a
            /// viewer those code points, not the document's text.
            /// </summary>
            public readonly bool GlyphIndexed;

            /// <summary>
            /// For a converted Type 1 program, the glyph index of each glyph name in the wrapped CFF —
            /// the PDF selects a Type 1 glyph by name, so a code becomes a name
            /// (<c>Type1GlyphSource.GetGlyphName</c>) and then this index. Null otherwise.
            /// </summary>
            public readonly IReadOnlyDictionary<string, int> GlyphNameToGid;

            internal Program(long objectNumber, byte[] bytes, string format)
                : this(objectNumber, bytes, format, glyphIndexed: false, glyphNameToGid: null) { }

            internal Program(long objectNumber, byte[] bytes, string format, bool glyphIndexed,
                             IReadOnlyDictionary<string, int> glyphNameToGid)
            {
                ObjectNumber = objectNumber;
                Bytes = bytes;
                Format = format;
                GlyphIndexed = glyphIndexed;
                GlyphNameToGid = glyphNameToGid;
            }

            /// <summary>True when this describes a program rather than a refusal.</summary>
            public bool IsUsable => Bytes != null;
        }

        /// <summary>
        /// Descriptors <see cref="TryRead"/> has read on the calling thread: the evidence that a page
        /// reads each once (<c>SvgSharedState.FontPrograms</c>).
        /// </summary>
        [ThreadStatic] internal static long ProgramReads;

        /// <summary>
        /// Reads the embeddable font program behind a font resource, or a non-usable
        /// <see cref="Program"/> when there is none this can re-emit.
        /// </summary>
        /// <param name="descriptor">The resolved <c>/FontDescriptor</c> — for a Type0 font, the
        /// descendant CIDFont's, which is what <c>FontCache.GetFontDescriptor</c> answers.</param>
        /// <param name="document">Used to resolve the font-file reference; may not be null.</param>
        /// <param name="resourceName">Carried into the diagnostic, per <c>FontLoadFailed</c>'s contract.</param>
        internal static Program TryRead(PdfDictionary descriptor, PdfDocument document,
                                        string resourceName)
        {
            if (descriptor == null || document == null) return default;
            ProgramReads++;

            // /FontFile is Type 1 PostScript and /FontFile3 may be a bare CFF, so the key alone
            // does not decide: the sniff below is what accepts or refuses. Reading all three keeps
            // the refusal specific — "this font is a bare CFF" rather than "no program found".
            foreach (PdfName key in new[] { PdfName.FontFile2, PdfName.FontFile3, PdfName.FontFile })
            {
                PdfObject entry = descriptor[key];
                if (entry == null) continue;

                // §7.3.8.1: a stream shall be an indirect object. The object number is this
                // program's identity across the page — two resources sharing one program must
                // share one @font-face — so a direct stream has no identity to dedup on and is
                // malformed besides.
                if (entry is not PdfReference reference) continue;
                if (document.ResolveObject(entry) is not PdfStream stream) continue;

                byte[] bytes;
                try
                {
                    bytes = Core.Fonts.FontStreamBytes.Program(stream);
                }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                        resourceName, ex);
                    return default;
                }

                if (bytes == null || bytes.Length < 4) return default;

                if (bytes.Length > PdfLimits.MaxSvgEmbeddedFontBytes)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                        resourceName + ": the embedded program is "
                        + bytes.Length.ToString(CultureInfo.InvariantCulture)
                        + " bytes, past the SVG data-URI cap, so the export names a system family instead");
                    return default;
                }

                string format = SfntFormat(bytes);
                if (format == null)
                {
                    // Not an sfnt: a bare CFF or a Type 1 program. Both can be given the container
                    // a viewer needs; what cannot be read refuses with the reason.
                    Program wrapped = TryWrap(bytes, stream, descriptor, document, reference, out string why);
                    if (wrapped.IsUsable) return wrapped;

                    RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                        resourceName + ": the embedded program is not an sfnt container and could "
                        + "not be wrapped into one (" + why + "), so the export names a system family instead");
                    return default;
                }

                if (!Licensed(bytes))
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                        resourceName + ": the font's OS/2 fsType forbids outline embedding, "
                        + "so it is not re-emitted into the SVG");
                    return default;
                }

                // ReferenceObjectNumber, not the inherited PdfObject.ObjectNumber — on a
                // PdfReference that one is the reference object's own number, which is 0 for a
                // reference read out of a dictionary. Every font would have deduped against every
                // other, and the compiler accepts both spellings.
                return new Program(reference.ReferenceObjectNumber, bytes, format);
            }

            return default;
        }

        /// <summary>
        /// An OpenType container around a bare CFF, or around the CFF a Type 1 program converts to,
        /// with the glyph-indexed <c>cmap</c>; a non-usable program with <paramref name="why"/> set
        /// when the bytes are neither, do not parse, or convert to nothing a wrapper can hold.
        /// </summary>
        /// <remarks>
        /// <para>A bare CFF starts <c>01 00</c> (major 1, minor 0); a Type 1 program <c>%!</c>, or
        /// <c>0x80</c> for a PFB whose segment headers were left in. The Type 1 lengths come from the
        /// stream's <c>/Length1</c> and <c>/Length2</c>, resolved as <c>FontCache</c> resolves them —
        /// producers write them as indirect references as routinely as <c>/Length</c>.</para>
        /// <para>The wrapper is sized against <see cref="PdfLimits.MaxSvgEmbeddedFontBytes"/> like
        /// any other program: the raw bytes already passed, and the envelope adds tables of a fixed
        /// few hundred bytes plus four per glyph, so the check is for the converted Type 1 case.</para>
        /// </remarks>
        private static Program TryWrap(byte[] bytes, PdfStream stream, PdfDictionary descriptor,
                                       PdfDocument document, PdfReference reference, out string why)
        {
            why = null;
            bool bareCff = bytes[0] == 0x01 && bytes[1] == 0x00;
            bool type1 = (bytes[0] == (byte)'%' && bytes[1] == (byte)'!') || bytes[0] == 0x80;
            if (!bareCff && !type1)
            {
                why = "neither a bare CFF nor a Type 1 program";
                return default;
            }

            try
            {
                byte[] cffBytes = bytes;
                IReadOnlyDictionary<string, int> names = null;
                if (type1)
                {
                    int l1 = PdfResolve.Int(document, stream, PdfName.Length1, 0);
                    int l2 = PdfResolve.Int(document, stream, PdfName.Length2, 0);
                    Tetitulz.Pdf.Core.Fonts.Type1.Type1Font t1 =
                        Tetitulz.Pdf.Core.Fonts.Type1.Type1Parser.Parse(bytes, l1, l2);
                    if (t1 == null)
                    {
                        why = "the Type 1 program did not parse";
                        return default;
                    }

                    string psName = (document.ResolveObject(descriptor[PdfName.FontName]) as PdfName)?.Value;
                    cffBytes = Tetitulz.Pdf.Core.Fonts.Type1.Type1ToCffConverter.Convert(t1, psName);
                    if (cffBytes == null)
                    {
                        why = "the Type 1 program could not be converted to CFF ("
                              + (Tetitulz.Pdf.Core.Fonts.Type1.Type1ToCffConverter.LastError ?? "no reason recorded") + ")";
                        return default;
                    }
                }

                Tetitulz.Pdf.Core.Fonts.Cff.CffFont cff = Tetitulz.Pdf.Core.Fonts.Cff.CffParser.Parse(cffBytes);
                if (cff == null)
                {
                    why = "the CFF program did not parse ("
                          + Tetitulz.Pdf.Core.Fonts.Cff.CffParser.LastFailureReason + ")";
                    return default;
                }
                if (type1) names = cff.GlyphNameToGid;

                byte[] otf = CffOtfWrapper.TryBuild(cffBytes, cff, glyphIndexedCmap: true);
                if (otf == null)
                {
                    why = "no OpenType container could be built around the CFF program";
                    return default;
                }
                if (otf.Length > PdfLimits.MaxSvgEmbeddedFontBytes)
                {
                    why = "the wrapped program is " + otf.Length.ToString(CultureInfo.InvariantCulture)
                          + " bytes, past the SVG data-URI cap";
                    return default;
                }

                return new Program(reference.ReferenceObjectNumber, otf, "opentype",
                                   glyphIndexed: true, glyphNameToGid: names);
            }
            catch (Exception ex) when (ex is Core.PdfException || ex is ArgumentException
                                       || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                why = ex.GetType().Name + ": " + ex.Message;
                return default;
            }
        }

        /// <summary>
        /// The CSS <c>format()</c> for an sfnt container, or null when the bytes are not one.
        /// </summary>
        /// <remarks>
        /// The four accepted signatures are the sfnt versions ISO 32000-1 §9.9 and the OpenType
        /// specification between them allow: <c>0x00010000</c> and <c>'true'</c> are TrueType
        /// outlines, <c>'OTTO'</c> is CFF outlines, and <c>'ttcf'</c> is a collection — which a
        /// browser accepts and which a <c>/FontFile2</c> may legally hold. A bare CFF starts
        /// <c>0x01 0x00</c> and a Type 1 program with <c>%!</c> or <c>0x80</c>; neither matches,
        /// which is the refusal.
        /// </remarks>
        private static string SfntFormat(byte[] bytes)
        {
            uint tag = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16)
                     | ((uint)bytes[2] << 8) | bytes[3];

            return tag switch
            {
                0x00010000u => "truetype",   // TrueType outlines
                0x74727565u => "truetype",   // 'true'
                0x74746366u => "truetype",   // 'ttcf' — a collection
                0x4F54544Fu => "opentype",   // 'OTTO' — CFF outlines
                _ => null,
            };
        }

        /// <summary>Whether the font's own <c>OS/2</c> table permits re-embedding its outlines.</summary>
        /// <remarks>
        /// An <b>undeclared</b> licence permits — that is what
        /// <see cref="FontEmbeddingLicense.PermitsOutlineEmbedding"/> already answers for the
        /// system-substitution gate, and answering differently here would mean a font with no
        /// <c>OS/2</c> table at all (every CFF-only sfnt built by some producers) could never be
        /// embedded, which is a refusal the licence never asked for.
        /// </remarks>
        private static bool Licensed(byte[] bytes)
        {
            try
            {
                return FontEmbeddingLicense.Read(bytes).PermitsOutlineEmbedding;
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                    "the font's embedding licence could not be read", ex);
                return false;
            }
        }

        /// <summary>
        /// The <c>@font-face</c> rule naming <paramref name="cssFamily"/> as
        /// <paramref name="program"/>.
        /// </summary>
        /// <remarks>
        /// The family name is minted by the caller from the shared id counter, so it is CSS-safe
        /// by construction and cannot collide with a real family the page also names — a
        /// <c>@font-face</c> whose family shadows <c>Arial</c> would silently retarget every run
        /// that fell back to Arial for a different reason.
        /// </remarks>
        internal static string FaceRule(string cssFamily, in Program program)
        {
            // The MIME type is advisory — the format() hint is what a browser dispatches on — but
            // a wrong one makes the URI unreadable in some strict tools, so it tracks the sniff.
            string mime = program.Format == "opentype" ? "font/otf" : "font/ttf";

            return "@font-face{font-family:'" + cssFamily + "';src:url(data:" + mime
                   + ";base64," + Convert.ToBase64String(program.Bytes)
                   + ") format('" + program.Format + "');}";
        }
    }
}
