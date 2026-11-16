// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Generates a PDF Form XObject appearance stream for a visible signature widget.
    /// The appearance shows the signer name, signing date, reason, and location as text,
    /// with an optional JPEG logo on the left.
    ///
    /// Usage:
    ///   var (apStream, fontObjNum, imgObjNum) = SignatureAppearanceGenerator.Build(
    ///       options, ref nextObjNum);
    ///   // Add all returned objects to the incremental-update object list.
    /// </summary>
    public static class SignatureAppearanceGenerator
    {
        // ---- Resource names declared in the Form XObject's /Resources ----
        private const string FontResourceName = "F1";
        private const string LogoResourceName = "Im1";

        // ---- Layout ----
        /// <summary>Fraction of the appearance width reserved for the logo, when one is supplied.</summary>
        private const double LogoColumnWidthFraction = 0.30;
        /// <summary>Inset, in points, between the logo and the edges of its column.</summary>
        private const double LogoMargin = 4;
        /// <summary>
        /// Logo samples are always 8-bit. The <i>colour space</i> is not fixed — it comes from the
        /// JPEG frame header for a JPEG logo, and is /DeviceRGB for a PNG because the decoder
        /// expands every PNG colour type to packed RGB.
        /// </summary>
        private const int LogoBitsPerComponent = 8;

        /// <summary>Signer name, date, reason and location — the four rows the block is centred for.</summary>
        private const int MaxTextRows = 4;
        /// <summary>Extra leading, in points, between text rows.</summary>
        private const double LineSpacing = 2;
        /// <summary>Points added to the font size for the signer-name row, which is set slightly larger.</summary>
        private const double SignerNameExtraSize = 1;

        // ---- Truncation ----
        /// <summary>Average glyph advance as a fraction of font size — good enough for Helvetica.</summary>
        private const double AverageGlyphAdvanceRatio = 0.55;
        /// <summary>Right-hand inset, in points, kept clear when measuring the usable text width.</summary>
        private const double RightTextMargin = 4;
        /// <summary>Never truncate below this many characters, however narrow the box is.</summary>
        private const int MinTruncatedChars = 8;
        /// <summary>Character budget used when the font size is zero and no advance can be estimated.</summary>
        private const int FallbackMaxChars = 64;
        /// <summary>ASCII ellipsis — ISO-8859-1 safe, unlike U+2026.</summary>
        private const string Ellipsis = "...";

        // ---- Variant tints used by BuildAllVariants ----
        /// <summary>Grey level the /R (rollover) appearance paints its background at.</summary>
        private const double HoverBackgroundTint = 0.90;
        /// <summary>Grey level the /D (down) appearance paints its background at.</summary>
        private const double PressedBackgroundTint = 0.85;

        /// <summary>
        /// Builds a signature appearance Form XObject and its required resources.
        ///
        /// <para>Text that fits the <c>/WinAnsiEncoding</c> simple font is drawn exactly as it
        /// always was. Text that does not — Hebrew, Arabic, Greek, Cyrillic, CJK — needs an
        /// embedded composite font, whose licence only the caller can speak for: supply one
        /// through <see cref="SignatureAppearanceOptions.FontProvider"/>, or opt in to an
        /// installed one with <see cref="SignatureAppearanceOptions.AllowSystemFontSubstitution"/>.
        /// When neither yields a font covering the text this overload <b>throws</b> rather than draw
        /// a row of <c>?</c> where the signer's name should be.</para>
        /// </summary>
        /// <param name="opts">Appearance options.</param>
        /// <param name="nextObjNum">
        ///   Next available object number. Incremented for each object allocated — and left
        ///   untouched when no appearance could be built, so a refusal burns no object numbers.
        /// </param>
        /// <exception cref="InvalidOperationException">
        ///   The text needs a composite font and none was available, the font that was supplied
        ///   does not cover it, or <see cref="SignatureAppearanceOptions.LogoJpegBytes"/> is not a
        ///   JPEG this library can describe. Use the overload taking <c>out string error</c> to
        ///   handle that without an exception.
        /// </exception>
        /// <returns>
        ///   A tuple of:
        ///   <list type="bullet">
        ///     <item><term>stream</term><description>The Form XObject PdfStream (already has ObjectNumber set).</description></item>
        ///     <item><term>sideObjects</term><description>Font and image indirect objects to add to the update.</description></item>
        ///   </list>
        /// </returns>
        public static (PdfStream stream, System.Collections.Generic.List<(int objNum, PdfObject obj)> sideObjects)
            Build(SignatureAppearanceOptions opts, ref int nextObjNum)
        {
            var built = Build(opts, ref nextObjNum, out string error);
            if (error != null) throw new InvalidOperationException(error);
            return built;
        }

        /// <summary>
        /// <see cref="Build(SignatureAppearanceOptions, ref int)"/>, reporting a refusal through
        /// <paramref name="error"/> instead of throwing.
        /// </summary>
        /// <param name="opts">Appearance options.</param>
        /// <param name="nextObjNum">
        ///   Next available object number. Incremented for each object allocated, and left
        ///   untouched when <paramref name="error"/> is set.
        /// </param>
        /// <param name="error">
        ///   Why no appearance was drawn, or <c>null</c> on success. Set only when the returned
        ///   stream is <c>null</c>: drawing something wrong is never one of the options, which is
        ///   the rule the AcroForm appearance generator follows when it leaves
        ///   <c>/NeedAppearances</c> true rather than writing a value it could not set.
        /// </param>
        public static (PdfStream stream, System.Collections.Generic.List<(int objNum, PdfObject obj)> sideObjects)
            Build(SignatureAppearanceOptions opts, ref int nextObjNum, out string error)
        {
            if (opts == null) throw new ArgumentNullException(nameof(opts));

            error = null;
            double w = opts.Width;
            double h = opts.Height;
            var side = new System.Collections.Generic.List<(int, PdfObject)>();

            // Object numbers are handed out from a local and written back only on success, so a
            // refused appearance leaves the caller's counter — and therefore the update it was
            // about to write — exactly as it found it.
            int next = nextObjNum;

            // ---- JPEG logo: the frame header is the authority, and it is read before anything
            // is allocated ----
            //
            // /DCTDecode passes the caller's bytes through untouched, so an image dictionary that
            // contradicts the frame is a corrupt image no reader can recover. Written as /ColorSpace
            // /DeviceRGB and /Filter /DCTDecode over whatever arrives, a 1-component grayscale logo
            // (a scanned ink signature — the common case) would have its samples read as RGB triples
            // and render as noise, a 4-component CMYK one fail the same way, and non-JPEG bytes be
            // embedded as /DCTDecode with no error anywhere. VisualSignatureBuilder's image-only path
            // refuses both the same way, through the same single JPEG marker walk
            // (Core.Imaging.RasterImageInspector).
            //
            // The dimensions come from the header too. /Width and /Height that disagree with the
            // frame are the same class of corruption as a wrong /ColorSpace, and the header cannot
            // disagree with the samples the way a caller-supplied number can.
            PdfName logoColorSpace = null;
            double[] logoDecode = null;
            int logoPxW = 0, logoPxH = 0;
            bool hasLogo = opts.LogoJpegBytes != null && opts.LogoJpegBytes.Length > 0;
            if (hasLogo)
            {
                if (!RasterImageInspector.TryInspect(opts.LogoJpegBytes, out var jpegInfo)
                    || jpegInfo.Format != RasterImageFormat.Jpeg)
                {
                    error = "the logo supplied as LogoJpegBytes is not a readable JPEG (no SOI/SOF " +
                            "frame header), so it cannot be embedded as a /DCTDecode image and no " +
                            "appearance was drawn";
                    return (null, new System.Collections.Generic.List<(int, PdfObject)>());
                }

                // The one rule for every writer that embeds a JPEG (Imaging.JpegColorSpace): a
                // four-component frame is CMYK only under Adobe's APP14 marker, and then inverted.
                string logoSpace = Imaging.JpegColorSpace.Of(jpegInfo, out string refused);
                if (logoSpace == null)
                {
                    error = "the logo JPEG cannot be embedded: " + refused + ", so no appearance was drawn";
                    return (null, new System.Collections.Generic.List<(int, PdfObject)>());
                }
                logoColorSpace = new PdfName(logoSpace);
                logoDecode = Imaging.JpegColorSpace.DecodeFor(jpegInfo);

                logoPxW = jpegInfo.Width;
                logoPxH = jpegInfo.Height;
            }

            // The PNG logo is decoded here rather than at the emission site because the LAYOUT
            // needs to know whether a logo column exists, and for a PNG that is only knowable once
            // the header has been read. A PNG that will not decode is treated as no logo at all -
            // the text still draws, which is the half that carries the meaning.
            DecodedPng logoPng = null;
            if (!hasLogo && opts.LogoPngBytes != null && opts.LogoPngBytes.Length > 0)
                PngImageDecoder.TryDecode(opts.LogoPngBytes, out logoPng);

            bool hasAnyLogo = hasLogo || logoPng != null;

            double logoColW = w * LogoColumnWidthFraction;
            double textX = hasAnyLogo ? logoColW + opts.TextMargin : opts.TextMargin;

            // Truncate text fields to a width that comfortably fits the appearance box.
            double avgAdvance = opts.FontSize * AverageGlyphAdvanceRatio;
            int maxChars = avgAdvance > 0
                ? Math.Max(MinTruncatedChars, SaturatingCast.ToInt((w - textX - RightTextMargin) / avgAdvance))
                : FallbackMaxChars;
            string TruncOrEllipsis(string s)
            {
                if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
                return s.Substring(0, Math.Max(1, maxChars - Ellipsis.Length)) + Ellipsis;
            }

            // The four rows exactly as they will be drawn, labels included: the labels come out of
            // the same font, so they are part of the coverage question.
            string nameRow = TruncOrEllipsis(opts.SignerName);
            string dateRow = "Date: " + TruncOrEllipsis(opts.SigningDate);
            string reasonRow = "Reason: " + TruncOrEllipsis(opts.Reason);
            string locationRow = "Location: " + TruncOrEllipsis(opts.Location);

            var drawn = new List<string>(MaxTextRows);
            if (!string.IsNullOrEmpty(opts.SignerName)) drawn.Add(nameRow);
            if (!string.IsNullOrEmpty(opts.SigningDate)) drawn.Add(dateRow);
            if (!string.IsNullOrEmpty(opts.Reason)) drawn.Add(reasonRow);
            if (!string.IsNullOrEmpty(opts.Location)) drawn.Add(locationRow);

            bool composite = false;
            foreach (string row in drawn)
                if (TextRunComposer.NeedsCompositeFont(row)) { composite = true; break; }

            // ---- Font program, for the composite case ----
            TrueTypeFontProgram program = null;
            var gidToUnicode = new Dictionary<ushort, int>();
            var gidToText = new Dictionary<ushort, string>();   // a ligature glyph's letters, for /ToUnicode
            int? missingCodepoint = null;
            if (composite)
            {
                program = ResolveCompositeFont(opts, string.Concat(drawn), out error);
                if (program == null) return (null, side);
            }

            // ---- Font resource ----
            // The simple font is allocated here, before the logo, because that is the numbering
            // every appearance this generator has ever written was built with. The composite font
            // cannot be: /W and /ToUnicode describe the glyphs actually drawn, which is not known
            // until the content stream has been composed.
            var fontResDict = new PdfDictionary();
            if (!composite)
            {
                int fontObjNum = next++;
                var fontDict = new PdfDictionary();
                fontDict.Set(PdfName.Type, PdfName.Font);
                fontDict.Set(PdfName.Subtype, PdfName.Type1);
                fontDict.Set(PdfName.BaseFont, new PdfName(opts.FontName));
                fontDict.Set(PdfName.Encoding, PdfName.WinAnsiEncoding);
                fontDict.ObjectNumber = fontObjNum;
                fontDict.GenerationNumber = 0;
                side.Add((fontObjNum, fontDict));
                fontResDict.Set(new PdfName(FontResourceName), new PdfReference(fontObjNum, 0));
            }

            // ---- Optional image resource ----
            int imgObjNum = -1;
            if (hasLogo)
            {
                imgObjNum = next++;
                var imgStream = new PdfStream();
                imgStream.Set(PdfName.Type, PdfName.XObject);
                imgStream.Set(PdfName.Subtype, PdfName.Image);
                imgStream.Set(PdfName.Width, new PdfNumber(logoPxW));
                imgStream.Set(PdfName.Height, new PdfNumber(logoPxH));
                imgStream.Set(PdfName.BitsPerComponent, new PdfNumber(LogoBitsPerComponent));
                imgStream.Set(PdfName.ColorSpace, logoColorSpace);
                if (logoDecode != null) imgStream.Set(PdfName.Decode, PdfArray.OfNumbers(logoDecode));
                imgStream.Set(PdfName.Filter, PdfName.DCTDecode);
                imgStream.RawData = opts.LogoJpegBytes;
                imgStream.Set(PdfName.Length, new PdfNumber(opts.LogoJpegBytes.Length));
                imgStream.ObjectNumber = imgObjNum;
                imgStream.GenerationNumber = 0;
                side.Add((imgObjNum, imgStream));
            }
            else if (logoPng != null)
            {
                // A PNG logo, with its transparency preserved as a real /SMask (§11.6.5.3). This is
                // the case a signature block actually needs: a corporate mark is supplied with a
                // transparent background, and the JPEG path above has nowhere to put an alpha
                // channel — /DCTDecode is three opaque channels — so such a logo would otherwise have
                // to be pre-flattened against whatever colour the caller guessed the block would be.
                imgObjNum = next++;

                int maskObjNum = -1;
                if (logoPng.Alpha != null)
                {
                    // Emitted BEFORE the image references it, matching the order the rest of the
                    // tree writes soft masks in.
                    maskObjNum = next++;
                    var maskStream = new PdfStream();
                    maskStream.Set(PdfName.Type, PdfName.XObject);
                    maskStream.Set(PdfName.Subtype, PdfName.Image);
                    maskStream.Set(PdfName.Width, new PdfNumber(logoPng.Width));
                    maskStream.Set(PdfName.Height, new PdfNumber(logoPng.Height));
                    maskStream.Set(PdfName.BitsPerComponent, new PdfNumber(LogoBitsPerComponent));
                    maskStream.Set(PdfName.ColorSpace, PdfName.DeviceGray);
                    maskStream.RawData = FlateEncodeFilter.Encode(logoPng.Alpha);
                    maskStream.Set(PdfName.Filter, PdfName.FlateDecode);
                    maskStream.Set(PdfName.Length, new PdfNumber(maskStream.RawData.Length));
                    maskStream.ObjectNumber = maskObjNum;
                    maskStream.GenerationNumber = 0;
                    side.Add((maskObjNum, maskStream));
                }

                var pngStream = new PdfStream();
                pngStream.Set(PdfName.Type, PdfName.XObject);
                pngStream.Set(PdfName.Subtype, PdfName.Image);
                pngStream.Set(PdfName.Width, new PdfNumber(logoPng.Width));
                pngStream.Set(PdfName.Height, new PdfNumber(logoPng.Height));
                pngStream.Set(PdfName.BitsPerComponent, new PdfNumber(LogoBitsPerComponent));
                pngStream.Set(PdfName.ColorSpace, PdfName.DeviceRGB);
                pngStream.RawData = FlateEncodeFilter.Encode(logoPng.Rgb);
                pngStream.Set(PdfName.Filter, PdfName.FlateDecode);
                pngStream.Set(PdfName.Length, new PdfNumber(pngStream.RawData.Length));
                if (maskObjNum > 0)
                    pngStream.Set(PdfName.SMask, new PdfReference(maskObjNum, 0));
                pngStream.ObjectNumber = imgObjNum;
                pngStream.GenerationNumber = 0;
                side.Add((imgObjNum, pngStream));
            }

            // ---- Resources dictionary ----
            var resources = new PdfDictionary();
            resources.Set(PdfName.Font, fontResDict);

            if (imgObjNum > 0)
            {
                var xobjDict = new PdfDictionary();
                xobjDict.Set(new PdfName(LogoResourceName), new PdfReference(imgObjNum, 0));
                resources.Set(PdfName.XObject, xobjDict);
            }

            // One show-text operation, whichever font is in play: a WinAnsi literal string, or the
            // glyph ids of a shaped, bidi-reordered run — Identity-H makes the two-byte codes the
            // glyph ids — with its marks where the font's anchors put them (GlyphRunShowText), whose
            // rise needs the Tf size.
            string Show(string s, double size)
            {
                if (!composite) return "(" + Escape(s) + ") " + PdfOperators.ShowText;

                var run = TextRunComposer.Compose(s, program, new TextRunOptions
                {
                    BaseDirection = opts.BaseDirection,
                    // Refuse, not SubstituteSpace: an appearance that quietly drops a letter of
                    // the signer's name is worse than one that is not drawn at all.
                    MissingGlyphPolicy = MissingGlyphPolicy.Refuse,
                });

                run.RecordUsedGlyphs(gidToUnicode, gidToText);
                if (run.MissingCodePoint.HasValue && missingCodepoint == null)
                    missingCodepoint = run.MissingCodePoint.Value;

                return GlyphRunShowText.Operators(run, size);
            }

            // ---- Content stream ----
            var sb = new StringBuilder();

            // Background fill (light grey by default, or white).
            sb.Append(PdfOperators.Save + "\n");
            if (opts.DrawBorder)
            {
                // PDF content-stream colour operators are 'rg' (fill) and 'RG'
                // (stroke) — NOT the PostScript 'setrgbcolor', which PDF viewers
                // ignore, leaving the background/border unpainted.
                sb.AppendFormat("{0} {1} {2} " + PdfOperators.SetRgbFill + "\n",
                    Fmt(opts.BackgroundR), Fmt(opts.BackgroundG), Fmt(opts.BackgroundB));
                sb.AppendFormat("0 0 {0} {1} " + PdfOperators.Rectangle + " " + PdfOperators.Fill + "\n",
                    Fmt(w), Fmt(h));
                sb.Append("0 0 0 " + PdfOperators.SetRgbStroke + "\n"); // border stroke colour
                sb.Append("0.5 " + PdfOperators.SetLineWidth + "\n");   // hairline border
                sb.AppendFormat("0 0 {0} {1} " + PdfOperators.Rectangle + " " + PdfOperators.Stroke + "\n",
                    Fmt(w), Fmt(h));
            }

            // Logo column, inset on the left.
            if (imgObjNum > 0)
            {
                double logoW = logoColW - LogoMargin * 2;
                double logoH = h - LogoMargin * 2;

                sb.Append(PdfOperators.Save + "\n");
                sb.AppendFormat("{0} 0 0 {1} {2} {3} " + PdfOperators.ConcatMatrix + "\n",
                    Fmt(logoW), Fmt(logoH), Fmt(LogoMargin), Fmt(LogoMargin));
                sb.Append("/" + LogoResourceName + " " + PdfOperators.InvokeXObject + "\n");
                sb.Append(PdfOperators.Restore + "\n");
            }

            // Text rows, as a block vertically centred in the appearance box.
            double lineH = opts.FontSize + LineSpacing;
            double totalTextH = lineH * MaxTextRows;
            double startY = (h - totalTextH) / 2.0 + totalTextH - lineH;

            // Each row is positioned with an ABSOLUTE `Tm`, never `Td`.
            //
            // §9.4.2 defines `Td` as an offset from the start of the CURRENT line, not a position:
            // emitting `textX y Td` per row makes the offsets compound, so row 2 lands at
            // (2·textX, y1+y2) and every later row further out again. With the block centred in a
            // 60 pt box that puts rows 2..4 above the form's /BBox, where §8.10.2 clips them away —
            // so the date, the reason and the location were written into the content stream and
            // never drawn, and only the signer's name was ever visible.
            //
            // Nothing could see it: the rows ARE in the stream, so every assertion that the text was
            // emitted passed. `Tm` sets the text matrix outright, which also means a row can be
            // skipped without the next one having to know.
            sb.Append(PdfOperators.BeginText + "\n");
            sb.AppendFormat("/" + FontResourceName + " {0} " + PdfOperators.SetFont + "\n", Fmt(opts.FontSize));
            sb.Append("0 0 0 " + PdfOperators.SetRgbFill + "\n");

            double y = startY;

            if (!string.IsNullOrEmpty(opts.SignerName))
            {
                // Tf takes TWO operands (font resource name, size). This emitted only the size,
                // producing a malformed operator: conforming readers discard the whole operation,
                // so the signer name silently rendered at the previous size instead of the larger
                // one — and the appearance stream was non-conformant. The sibling calls two lines
                // above and below always got this right.
                sb.AppendFormat("/" + FontResourceName + " {0} " + PdfOperators.SetFont + "\n",
                    Fmt(opts.FontSize + SignerNameExtraSize));
                sb.AppendFormat("1 0 0 1 {0} {1} " + PdfOperators.SetTextMatrix + "\n", Fmt(textX), Fmt(y));
                sb.Append(Show(nameRow, opts.FontSize + SignerNameExtraSize) + "\n");
                sb.AppendFormat("/" + FontResourceName + " {0} " + PdfOperators.SetFont + "\n", Fmt(opts.FontSize));
                y -= lineH + SignerNameExtraSize;
            }

            if (!string.IsNullOrEmpty(opts.SigningDate))
            {
                sb.AppendFormat("1 0 0 1 {0} {1} " + PdfOperators.SetTextMatrix + "\n", Fmt(textX), Fmt(y));
                sb.Append(Show(dateRow, opts.FontSize) + "\n");
                y -= lineH;
            }

            if (!string.IsNullOrEmpty(opts.Reason))
            {
                sb.AppendFormat("1 0 0 1 {0} {1} " + PdfOperators.SetTextMatrix + "\n", Fmt(textX), Fmt(y));
                sb.Append(Show(reasonRow, opts.FontSize) + "\n");
                y -= lineH;
            }

            if (!string.IsNullOrEmpty(opts.Location))
            {
                sb.AppendFormat("1 0 0 1 {0} {1} " + PdfOperators.SetTextMatrix + "\n", Fmt(textX), Fmt(y));
                sb.Append(Show(locationRow, opts.FontSize) + "\n");
            }

            sb.Append(PdfOperators.EndText + "\n");
            sb.Append(PdfOperators.Restore + "\n");

            if (missingCodepoint != null)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "the font has no glyph for U+{0:X4}, so no appearance was drawn",
                    missingCodepoint.Value);
                return (null, new System.Collections.Generic.List<(int, PdfObject)>());
            }

            // Built after composing: only the glyphs actually drawn go into /W and /ToUnicode.
            if (composite)
            {
                var (type0ObjNum, fontObjects) = Type0FontBuilder.BuildObjects(ref next, program, gidToUnicode, gidToText);
                foreach (var entry in fontObjects) side.Add((entry.objNum, entry.obj));
                fontResDict.Set(new PdfName(FontResourceName), new PdfReference(type0ObjNum, 0));
            }

            byte[] contentBytes = Text.Latin1Text.Encode(sb.ToString());

            // ---- BBox / Form XObject ----
            int apObjNum = next++;
            var apStream = new PdfStream();
            apStream.Set(PdfName.Type, PdfName.XObject);
            apStream.Set(PdfName.Subtype, PdfName.Form);
            apStream.Set(PdfName.FormType, new PdfNumber(1));

            var bbox = new PdfArray();
            bbox.Add(new PdfNumber(0)); bbox.Add(new PdfNumber(0));
            bbox.Add(new PdfNumber(w)); bbox.Add(new PdfNumber(h));
            apStream.Set(PdfName.BBox, bbox);
            apStream.Set(PdfName.Resources, resources);
            apStream.RawData = contentBytes;
            apStream.Set(PdfName.Length, new PdfNumber(contentBytes.Length));
            apStream.ObjectNumber = apObjNum;
            apStream.GenerationNumber = 0;

            nextObjNum = next;
            return (apStream, side);
        }

        /// <summary>
        /// Finds the font program for text the WinAnsi simple font cannot carry: the caller's
        /// <see cref="SignatureAppearanceOptions.FontProvider"/> first, then — only where the
        /// caller has opted in — an installed font whose <c>cmap</c> covers every character, found
        /// through the same search the AcroForm appearance path uses.
        /// </summary>
        private static TrueTypeFontProgram ResolveCompositeFont(
            SignatureAppearanceOptions opts, string sample, out string error)
        {
            error = null;

            // Same resolver as the AcroForm appearance path and the template engine — see the
            // note there — including the `OS/2` `fsType` gate, so a signature block cannot embed a
            // font whose own licence bits forbid it while the caller asked for nothing of the sort.
            var resolution = SystemFontResolver.Resolve(
                new FontResolutionRequest
                {
                    FamilyHint = opts.FontName,
                    // The shaped run's code points — TextRunComposer.CodePointsToCover, the same
                    // question the form and template paths ask.
                    RequiredCodePoints = TextRunComposer.CodePointsToCover(sample),
                    AllowSystemFontSubstitution = opts.AllowSystemFontSubstitution,
                    AllowRestrictedSystemFonts = opts.AllowRestrictedSystemFonts,
                },
                opts.FontProvider);

            if (!resolution.Success)
            {
                // The resolver's reason is appended on BOTH branches, and that is not tidiness:
                // it is the half that names the offending code point ("does not cover U+0378"),
                // which is the only actionable part when the caller DID supply a font and it was
                // one glyph short. Putting it on the substitution branch alone — which is how this
                // was first written — loses it in exactly the case where the caller can act on it.
                error = (opts.AllowSystemFontSubstitution
                    ? "no font on this machine covers the appearance text, so no appearance was drawn " +
                      "(set SignatureAppearanceOptions.FontProvider to supply one)"
                    : "the appearance text needs characters outside /WinAnsiEncoding and no usable font " +
                      "program was available, so no appearance was drawn (set " +
                      "SignatureAppearanceOptions.FontProvider, or AllowSystemFontSubstitution to use " +
                      "an installed font)") + ": " + resolution.FailureReason;
                return null;
            }

            return resolution.Program;
        }

        /// <summary>
        /// Builds /N (normal), /R (rollover/hover), and /D (down/click)
        /// appearance streams in a single call so callers wiring an /AP
        /// dictionary get all three variants without three separate Build()
        /// passes.  Most viewers fall back to /N for /R and /D when those
        /// keys are absent, but viewer hover effects on form-field
        /// signature widgets only kick in when /R and /D are present.
        ///
        /// /R replaces the background with a fixed grey tint (visualises hover);
        /// /D uses a darker tint and forces the border on (visualises mouse-down).
        /// Both keep the text content of /N so the three appearances stay in step.
        /// </summary>
        public static (PdfStream normal, PdfStream rollover, PdfStream down,
                       System.Collections.Generic.List<(int objNum, PdfObject obj)> sideObjects)
            BuildAllVariants(SignatureAppearanceOptions opts, ref int nextObjNum)
        {
            if (opts == null) throw new ArgumentNullException(nameof(opts));
            var combinedSide = new System.Collections.Generic.List<(int, PdfObject)>();

            var normal = Build(opts, ref nextObjNum);
            combinedSide.AddRange(normal.sideObjects);

            // Rollover variant: clone options with a faint "hover" background tint.
            var rolloverOpts = CloneVariant(opts, hoverBg: true, downBorder: false);
            var rollover = Build(rolloverOpts, ref nextObjNum);
            combinedSide.AddRange(rollover.sideObjects);

            // Down variant: darker tint plus a forced border to indicate mouse-down state.
            var downOpts = CloneVariant(opts, hoverBg: false, downBorder: true);
            var down = Build(downOpts, ref nextObjNum);
            combinedSide.AddRange(down.sideObjects);

            return (normal.stream, rollover.stream, down.stream, combinedSide);
        }

        private static SignatureAppearanceOptions CloneVariant(
            SignatureAppearanceOptions src, bool hoverBg, bool downBorder)
        {
            // Reflection-free shallow clone of the publicly-set fields.  We
            // only override the visual hints; the textual content is shared
            // so the three appearances remain semantically identical.  Both
            // tints replace the source background outright rather than scaling it.
            return new SignatureAppearanceOptions
            {
                Width = src.Width,
                Height = src.Height,
                SignerName = src.SignerName,
                SigningDate = src.SigningDate,
                Reason = src.Reason,
                Location = src.Location,
                FontName = src.FontName,
                FontSize = src.FontSize,
                TextMargin = src.TextMargin,
                DrawBorder = src.DrawBorder || downBorder,
                BackgroundR = hoverBg ? HoverBackgroundTint : (downBorder ? PressedBackgroundTint : src.BackgroundR),
                BackgroundG = hoverBg ? HoverBackgroundTint : (downBorder ? PressedBackgroundTint : src.BackgroundG),
                BackgroundB = hoverBg ? HoverBackgroundTint : (downBorder ? PressedBackgroundTint : src.BackgroundB),
                LogoJpegBytes = src.LogoJpegBytes,
                LogoPngBytes = src.LogoPngBytes,
                LogoWidthPx = src.LogoWidthPx,
                LogoHeightPx = src.LogoHeightPx,
                // The font source travels with the variant. Dropping it would leave /N drawing
                // Hebrew and /R and /D refusing it — three appearances of one signature that do
                // not agree on whether the name can be drawn at all.
                FontProvider = src.FontProvider,
                AllowSystemFontSubstitution = src.AllowSystemFontSubstitution,
                AllowRestrictedSystemFonts = src.AllowRestrictedSystemFonts,
                BaseDirection = src.BaseDirection,
            };
        }

        private static string Fmt(double v) => PdfRealFormat.Format(v, "F3");

        /// <summary>
        /// §7.3.4.2 literal-string escaping, through the single implementation in Core — signer
        /// name, reason and location are caller text and may contain anything.
        /// </summary>
        private static string Escape(string s) => Core.Objects.PdfStringEscape.EscapeLiteral(s);
    }

    /// <summary>
    /// Options for the signature appearance stream.
    /// </summary>
    public sealed class SignatureAppearanceOptions
    {
        /// <summary>Width of the appearance rectangle in PDF points.</summary>
        public double Width { get; set; } = 200;
        /// <summary>Height of the appearance rectangle in PDF points.</summary>
        public double Height { get; set; } = 60;

        /// <summary>Signer's display name (shown large at top).</summary>
        public string SignerName { get; set; }
        /// <summary>Signing date as a display string, e.g. "2026-03-18 12:00 UTC".</summary>
        public string SigningDate { get; set; }
        /// <summary>Reason for signing.</summary>
        public string Reason { get; set; }
        /// <summary>Signing location.</summary>
        public string Location { get; set; }

        /// <summary>PDF standard base font name (default Helvetica).</summary>
        ///
        /// <remarks>
        /// Also the family hint passed to <see cref="FontProvider"/> and to the installed-font
        /// search, for text this standard font cannot carry.
        /// </remarks>
        public string FontName { get; set; } = StandardFonts.Helvetica;

        /// <summary>
        /// Supplies a TrueType / OpenType font program for text outside <c>/WinAnsiEncoding</c>,
        /// given <see cref="FontName"/> as a hint. Return <c>null</c> to decline.
        ///
        /// <para>This is the primary route for Hebrew, Arabic, Greek, Cyrillic and CJK, and it is
        /// a callback rather than a search because <b>embedding redistributes the font</b>: only
        /// the caller knows what its licence permits.</para>
        /// </summary>
        public Func<string, byte[]> FontProvider { get; set; }

        /// <summary>
        /// Whether an installed font may be embedded when <see cref="FontProvider"/> supplies
        /// none. Defaults to <c>false</c> for the licensing reason above; with both unset, text
        /// needing a composite font is refused rather than drawn wrongly.
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; }

        /// <summary>
        /// Consider a host font whose <c>OS/2</c> <c>fsType</c> forbids embedding its outlines.
        /// Defaults to <c>false</c>, so such a font is skipped and the search continues.
        /// </summary>
        /// <remarks>
        /// <para>Same name and default as
        /// <see cref="Fonts.FontResolutionRequest.AllowRestrictedSystemFonts"/>. It governs the
        /// system search only — never <see cref="FontProvider"/>, because a caller who supplies a
        /// font program has made a licensing decision this code cannot see.</para>
        ///
        /// <para>Added because the refusal already named it: when the gate skips every candidate,
        /// the <c>error</c> this generator reports ends "set
        /// <c>FontResolutionRequest.AllowRestrictedSystemFonts</c>…", a remedy a signature-appearance
        /// caller must be able to act on.</para>
        /// </remarks>
        public bool AllowRestrictedSystemFonts { get; set; }

        /// <summary>
        /// Paragraph direction for the text rows. <see cref="BaseDirection.Auto"/> takes it from
        /// the first strong character, which is wrong for a value that opens with a number or a
        /// Latin word inside otherwise right-to-left text — set it explicitly there.
        ///
        /// <para>Applies only where a composite font is in play; single-byte WinAnsi text has no
        /// right-to-left characters to reorder.</para>
        /// </summary>
        public BaseDirection BaseDirection { get; set; } = BaseDirection.Auto;
        /// <summary>Font size in points (default 8).</summary>
        public double FontSize { get; set; } = 8;
        /// <summary>Left margin before text in points (default 4).</summary>
        public double TextMargin { get; set; } = 4;

        /// <summary>Whether to draw a thin border and fill background.</summary>
        public bool DrawBorder { get; set; } = true;
        /// <summary>Background red channel (default 0.95).</summary>
        public double BackgroundR { get; set; } = 0.95;
        /// <summary>Background green channel (default 0.95).</summary>
        public double BackgroundG { get; set; } = 0.95;
        /// <summary>Background blue channel (default 0.95).</summary>
        public double BackgroundB { get; set; } = 0.95;

        /// <summary>Optional JPEG logo bytes to display on the left third.</summary>
        /// <remarks>
        /// <para>Embedded verbatim as <c>/DCTDecode</c>, so it costs no decode — but JPEG has no
        /// alpha channel at all. Use <see cref="LogoPngBytes"/> for a logo with a transparent
        /// background.</para>
        ///
        /// <para>The frame header is read (never decoded) to learn the size and the component
        /// count, so <b>a grayscale or CMYK logo is described correctly</b> rather than declared
        /// <c>/DeviceRGB</c>, and bytes that are <b>not a readable JPEG are refused</b> with a
        /// reason instead of being written into a <c>/DCTDecode</c> stream no reader can decode.
        /// Unlike <see cref="LogoPngBytes"/>, which is dropped when it will not decode, this is a
        /// refusal: <c>/DCTDecode</c> means the caller has asserted the byte format, and there is
        /// no decode step that could disagree with them later.</para>
        /// </remarks>
        public byte[] LogoJpegBytes { get; set; }
        /// <summary>
        /// Logo image width in pixels. <b>Not read for a JPEG</b> — the frame header supplies it,
        /// and a caller-supplied number that disagreed with the frame described a corrupt image.
        /// </summary>
        public int LogoWidthPx { get; set; }
        /// <summary>
        /// Logo image height in pixels. <b>Not read for a JPEG</b>, for the reason on
        /// <see cref="LogoWidthPx"/>.
        /// </summary>
        public int LogoHeightPx { get; set; }

        /// <summary>
        /// Optional PNG logo, <b>transparency preserved</b> — the alpha channel becomes a real
        /// <c>/SMask</c> (§11.6.5.3) rather than being flattened against a guessed background.
        /// </summary>
        /// <remarks>
        /// <para>Needs no width or height: the decoder reads them from the IHDR, as the JPEG path
        /// reads them from the frame header.</para>
        ///
        /// <para><see cref="LogoJpegBytes"/> wins if both are set, so an existing caller's output
        /// cannot change by adding this property. A PNG that will not decode is treated as no logo
        /// at all rather than failing the signature: the text is the half that carries the meaning,
        /// and refusing to sign because a decoration would not parse is the wrong trade.</para>
        /// </remarks>
        public byte[] LogoPngBytes { get; set; }
    }
}
