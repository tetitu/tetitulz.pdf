// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// One OCR word with its bounding rectangle on the source page.
    /// Coordinates are in PDF user-space (points, origin bottom-left).
    /// </summary>
    public sealed class PdfOcrWord
    {
        public string Text   { get; }
        public float  X      { get; }  // bottom-left X in PDF points
        public float  Y      { get; }  // bottom-left Y in PDF points
        public float  Width  { get; }
        public float  Height { get; }

        /// <exception cref="ArgumentOutOfRangeException">
        /// A coordinate is NaN or infinite, or a size is NaN, infinite or negative: the layer's text
        /// matrix is built from them, and a non-finite one is written as 0, collapsing the word.
        /// </exception>
        public PdfOcrWord(string text, float x, float y, float width, float height)
        {
            Core.Util.NumericArguments.RequireFinite(x, nameof(x), nameof(X));
            Core.Util.NumericArguments.RequireFinite(y, nameof(y), nameof(Y));
            Core.Util.NumericArguments.RequireNonNegative(width, nameof(width), nameof(Width));
            Core.Util.NumericArguments.RequireNonNegative(height, nameof(height), nameof(Height));
            Text = text ?? string.Empty;
            X = x; Y = y; Width = width; Height = height;
        }
    }

    /// <summary>
    /// Writes an invisible "searchable-text" layer over an existing PDF page — the output produced
    /// by OCR devices generally. The result renders exactly like the original, but the
    /// invisible text (PDF text render mode 3) sits at the OCR-detected word positions, so readers
    /// support copy / select / search with no visible change.
    ///
    /// <para><b>The layer is written in VISUAL order — top to bottom, then left to right — with
    /// each word's characters reordered from logical to visual, because that is how a PDF stores
    /// text.</b> §9.4 places glyphs in the order they appear on the page, so a Hebrew word is
    /// stored rightmost-glyph-first and every extractor runs the bidi algorithm on what it reads to
    /// recover logical order. An OCR engine hands its words over in <i>reading</i> order instead —
    /// rightmost word first for Hebrew and Arabic, characters logical within each word — so
    /// emitting them as they arrive produces a layer that extracts <b>backwards</b>. Measured, not
    /// assumed: writing <c>שלום</c> logically and extracting it returns <c>םולש</c>.</para>
    /// </summary>
    /// <remarks>
    /// Reached from <c>PdfOperations.MakeSearchable</c>, which does the rendering and the
    /// recognition; this class does the writing, and a caller with its own engine can drive it
    /// directly.
    /// </remarks>
    public static class PdfOcrLayerWriter
    {
        /// <summary>Font size the per-word text matrix scales from; the layer is invisible, so it is nominal.</summary>
        private const float NominalFontSize = 10f;

        /// <summary>Floor on the per-word scale, so a zero-height box does not collapse the run.</summary>
        private const float MinimumScale = 0.5f;

        /// <summary>File-path overload.</summary>
        public static void AddLayer(string inputPath, int pageIndex,
            IReadOnlyList<PdfOcrWord> words, string outputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            using var input  = File.OpenRead(inputPath);
            using var output = File.Create(outputPath);
            AddLayer(input, pageIndex, words, output);
        }

        /// <summary>
        /// Stream overload. Emits an incremental update to <paramref name="output"/> that appends
        /// an invisible-text content stream to the target page. If <paramref name="words"/> is null
        /// or empty the input is copied verbatim.
        /// </summary>
        public static void AddLayer(Stream input, int pageIndex,
            IReadOnlyList<PdfOcrWord> words, Stream output)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            if (words == null || words.Count == 0)
            {
                CopyVerbatim(input, output);
                return;
            }

            AddLayers(input, new Dictionary<int, IReadOnlyList<PdfOcrWord>> { [pageIndex] = words }, output);
        }

        /// <summary>
        /// Adds an invisible text layer to several pages in <b>one</b> incremental update.
        /// </summary>
        /// <param name="input">The PDF to add layers to. Not disposed.</param>
        /// <param name="wordsByPage">Zero-based page index → that page's words, in reading order.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <remarks>
        /// <para><b>One update for the whole document, not one per page.</b> Looping the
        /// single-page overload over a scan of N pages appends N revisions, each carrying a
        /// complete cross-reference section — which grows the file by more than the text it adds,
        /// and leaves a document whose revision history claims N separate edits that never
        /// happened.</para>
        /// <para>Pages with no words are left untouched, and a document with no words anywhere is
        /// copied verbatim: "nothing was recognised" must not rewrite the file.</para>
        /// </remarks>
        public static void AddLayers(Stream input,
            IReadOnlyDictionary<int, IReadOnlyList<PdfOcrWord>> wordsByPage, Stream output)
            => AddLayers(input, wordsByPage, output, alwaysEmbeddedFont: false);

        /// <summary>
        /// <see cref="AddLayers(Stream, IReadOnlyDictionary{int, IReadOnlyList{PdfOcrWord}}, Stream)"/>,
        /// with <paramref name="alwaysEmbeddedFont"/> writing every layer in the embedded glyphless
        /// composite font (<c>OcrTextLayerFont</c>) — never the standard Helvetica the WinAnsi route
        /// draws in, which PDF/A refuses as an unembedded font. The transparency flattener asks for it,
        /// since its output is on the way to PDF/A.
        /// </summary>
        internal static void AddLayers(Stream input,
            IReadOnlyDictionary<int, IReadOnlyList<PdfOcrWord>> wordsByPage, Stream output, bool alwaysEmbeddedFont)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            if (wordsByPage == null || !AnyWords(wordsByPage))
            {
                CopyVerbatim(input, output);
                return;
            }

            var ms = Metadata.PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            var objects = new List<IndirectObject>();
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;

            // The composite decision is made for the DOCUMENT, not per page: one font shared by
            // every page beats one per page on a scan, and a document is either all-WinAnsi or not.
            bool composite = alwaysEmbeddedFont;
            var usedCodes = new HashSet<ushort>();
            foreach (var entry in wordsByPage)
                if (entry.Value != null && NeedsCompositeFont(entry.Value)) { composite = true; break; }

            int fontObjNum = 0;
            if (composite)
            {
                // Collected before anything is emitted, so /ToUnicode describes exactly the codes
                // the layer shows — the same rule the AcroForm and signature appearance paths
                // follow, reached from the other direction because here the text is all known up
                // front and no two-pass build is needed.
                foreach (var entry in wordsByPage)
                {
                    if (entry.Value == null) continue;
                    foreach (var w in entry.Value)
                    {
                        if (w == null || string.IsNullOrEmpty(w.Text)) continue;
                        foreach (ushort code in OcrTextLayerFont.CodeUnits(w.Text)) usedCodes.Add(code);
                    }
                }

                var (builtFont, fontObjects) = OcrTextLayerFont.BuildObjects(ref nextObjNum, usedCodes);
                foreach (var (num, obj) in fontObjects) objects.Add(new IndirectObject(num, 0, obj));
                fontObjNum = builtFont;
            }

            var isolation = new Documents.PdfPageContentAppender.StateIsolation();
            var sharing = new Documents.PdfPageContentAppender.ResourceSharing();
            foreach (var entry in wordsByPage)
            {
                if (entry.Value == null || entry.Value.Count == 0) continue;
                if (entry.Key < 0 || entry.Key >= doc.Pages.Count)
                    throw new ArgumentOutOfRangeException(nameof(wordsByPage),
                        $"page index {entry.Key} is outside the document's {doc.Pages.Count} pages");

                var words = entry.Value;

                // Read here rather than taken from the caller: the words arrive in UNROTATED user
                // space, and how they must be drawn is a property of the page, not of the engine
                // that found them. A parameter would let the two disagree, and a text layer that
                // disagrees with its page is invisible until somebody selects a word.
                //
                // Core.Document.PdfPageRotation is the one normaliser, so this and the renderer
                // (through PageRenderSize.Rotation) cannot answer differently for the same page —
                // which they must not, because one produced the coordinates the other draws.
                int rotation = Core.Document.PdfPageRotation.Normalize(
                    doc.Pages[entry.Key].Rotate, out _);

                // The font under a name the page does not use: a page with a text layer already holds
                // the font that layer was written in, a subset of its own codes, and naming this one
                // the same left that layer's text mapped through this font's codes.
                PdfPage page = doc.Pages[entry.Key];
                string fontName = composite ? FreeFontName(page, doc) : null;
                IReadOnlyDictionary<string, int> fontMap = composite
                    ? new Dictionary<string, int> { [fontName] = fontObjNum } : null;
                Documents.PdfPageContentAppender.AppendToPage(
                    page,
                    builder => EmitInvisibleText(builder, words, fontName, rotation),
                    compressStream: true,
                    ref nextObjNum, objects, fontMap, isolation, sharing);
            }

            Documents.PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
        }

        /// <summary>The first <see cref="OcrTextLayerFont.ResourcePrefix"/> name from 1 up (OcrF1, OcrF2, …) <paramref name="page"/>'s resources do not use.</summary>
        internal static string FreeFontName(PdfPage page, PdfDocument doc)
            => Documents.PdfPageContentAppender.FreeResourceName(page.Resources, doc, OcrTextLayerFont.ResourcePrefix, 1);

        private static bool AnyWords(IReadOnlyDictionary<int, IReadOnlyList<PdfOcrWord>> wordsByPage)
        {
            foreach (var entry in wordsByPage)
                if (entry.Value != null)
                    foreach (var w in entry.Value)
                        if (!string.IsNullOrEmpty(w?.Text)) return true;
            return false;
        }

        private static void CopyVerbatim(Stream input, Stream output)
        {
            // Rewind first: the natural call sequence renders the page (leaving the stream at its
            // end) and only then writes the layer back, so copying from the current position
            // produced a zero-byte "success". The non-empty path is unaffected because
            // PdfPageContentAppender normalises internally.
            PdfHelpers.EnsureSeekable(input).CopyTo(output);
        }

        /// <summary>
        /// True when the words hold a character the WinAnsi simple font cannot carry, so the layer
        /// needs a composite font instead.
        /// </summary>
        private static bool NeedsCompositeFont(IReadOnlyList<PdfOcrWord> words)
        {
            foreach (var w in words)
                if (w != null && Core.Text.TextRunComposer.NeedsCompositeFont(w.Text)) return true;
            return false;
        }

        /// <summary>
        /// Emits the invisible-text content. Render mode 3 = "invisible, but still
        /// selectable/searchable". Text is scaled per word so the bounding box roughly matches the
        /// visible glyph width when the viewer highlights a search hit.
        /// </summary>
        /// <remarks>
        /// <b>Text outside WinAnsi goes through a composite font, and used not to.</b>
        /// <c>PdfContentStreamBuilder.ShowText</c> writes one byte per character and substitutes
        /// <c>?</c> above U+00FF, so the searchable layer over a Hebrew, Arabic, Greek, Cyrillic or
        /// CJK scan was a row of question marks: the document looked identical, reported a text
        /// layer, and could not be searched in its own language. The composite path is
        /// <see cref="OcrTextLayerFont"/> — read its remarks before reusing it, because a
        /// non-embedded font is right <i>here</i> and wrong almost everywhere else.
        /// </remarks>
        /// <param name="builder">The page's appended content stream.</param>
        /// <param name="words">The recognised words, in unrotated user space.</param>
        /// <param name="compositeFontName">
        /// The Type0 font's resource name on this page when the document-wide font decision chose it
        /// (<see cref="FreeFontName"/>); null for the standard font.
        /// </param>
        /// <param name="rotation">
        /// The page's <c>/Rotate</c>, normalised. The words are in unrotated user space either way;
        /// this decides which way the invisible baselines run so that they lie along the glyphs a
        /// reader sees once the page is turned.
        /// </param>
        private static void EmitInvisibleText(
            Tetitulz.Pdf.Core.Writing.PdfContentStreamBuilder builder,
            IReadOnlyList<PdfOcrWord> words,
            string compositeFontName,
            int rotation)
        {
            bool composite = compositeFontName != null;
            builder.SaveState();
            builder.BeginText();
            // Tr operand 3 == TextRenderMode.Invisible — painted nowhere, still
            // selectable and searchable.  No builder helper for Tr yet.
            builder.AppendRaw("3 Tr");

            if (composite)
            {
                builder.UseEmbeddedFont(compositeFontName);
                builder.SetFont(compositeFontName, NominalFontSize);
            }
            else
            {
                // AddStandardFont, not the base-font name: SetFont takes a RESOURCE name, and the
                // page's /Font dictionary is built from what AddStandardFont registered. Passing
                // "Helv" straight to SetFont emitted `/Helv 10 Tf` against a resource dictionary
                // that declared no such name — an undefined font, which a conforming reader
                // resolves by discarding the text object. The layer was inert in exactly the
                // readers that matter, and invisible either way, so nothing showed it.
                builder.SetFont(builder.AddStandardFont(StandardFonts.Helvetica), NominalFontSize);
            }

            foreach (var w in VisualOrder(words, rotation))
            {
                if (w == null || string.IsNullOrEmpty(w.Text)) continue;

                // The glyph-up extent is the box's HEIGHT upright and its WIDTH on a quarter turn:
                // the box is in unrotated user space, and the text runs across it. Using the height
                // regardless scales a rotated word by its length instead of by its cap height.
                float upright = rotation == 90 || rotation == 270 ? w.Width : w.Height;
                float scale = Math.Max(upright / NominalFontSize, MinimumScale);

                // Logical → visual, which is a no-op for text with no right-to-left run.
                string visual = Core.Text.UnicodeBidi.Reorder(w.Text) ?? w.Text;

                // Along the baseline the word is fitted to its box, so it ends where the box does and
                // the gap to the next word is the gap the engine saw. Scaled by its height alone, a
                // word in the composite font — every glyph a full em wide — ran far past its box, and
                // an extractor read the words as overlapping: no space between them, and a letter
                // that landed on an earlier one dropped as a copy ("Archivednvoice").
                float along = rotation == 90 || rotation == 270 ? w.Height : w.Width;
                float natural = NaturalWidthEm(visual, composite) * NominalFontSize;
                float alongScale = natural > 0 && along > 0 ? along / natural : scale;

                (float a, float b, float c, float d) = TextAxes(rotation, alongScale, scale);
                (float originX, float originY) = BaselineOrigin(w, rotation);
                builder.SetTextMatrix(a, b, c, d, originX, originY);

                if (composite) builder.ShowGlyphs(OcrTextLayerFont.CodeUnits(visual));
                else builder.ShowText(visual);
            }

            builder.EndText();
            builder.RestoreState();
        }

        /// <summary>
        /// The text matrix's four axis entries for a page turned by <paramref name="rotation"/>.
        /// </summary>
        /// <remarks>
        /// A viewer rotates the page <b>clockwise</b> by <c>/Rotate</c>, so text that is to read
        /// horizontally after that rotation must run counter-clockwise by the same angle in the
        /// unrotated space the layer is written in. At 90 the baseline advances up the page, which
        /// is what this handles rather than refuses.
        /// </remarks>
        /// <param name="rotation">The page's normalised <c>/Rotate</c>.</param>
        /// <param name="along">Scale along the baseline — the word fitted to its box.</param>
        /// <param name="s">Scale across it, the glyphs' height.</param>
        private static (float A, float B, float C, float D) TextAxes(int rotation, float along, float s)
        {
            switch (rotation)
            {
                case 90:  return (0, along, -s, 0);
                case 180: return (-along, 0, 0, -s);
                case 270: return (0, -along, s, 0);
                default:  return (along, 0, 0, s);
            }
        }

        /// <summary>
        /// The word's advance at a font size of 1: Helvetica's widths for the simple font, and one em
        /// per code for the composite one, every code of which is <c>OcrTextLayerFont</c>'s default width.
        /// </summary>
        private static float NaturalWidthEm(string text, bool composite)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (composite) return text.Length;
            double total = 0;
            foreach (char ch in text) total += Core.Fonts.Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, ch);
            return (float)(total / PdfUnits.GlyphSpaceUnitsPerEm);
        }

        /// <summary>
        /// Where the baseline starts, which is a different corner of the box for each rotation.
        /// </summary>
        /// <remarks>
        /// The origin is the corner the first glyph sits on: the one the baseline runs away from
        /// and the ascender rises from. Upright that is bottom-left; at 90 the baseline runs up and
        /// the ascender points left, so it is bottom-<b>right</b>. Keeping the bottom-left corner
        /// for every rotation puts each word one word-length away from where it belongs — in the
        /// direction it reads, which is the hardest displacement to notice.
        /// </remarks>
        private static (float X, float Y) BaselineOrigin(PdfOcrWord w, int rotation)
        {
            switch (rotation)
            {
                case 90:  return (w.X + w.Width, w.Y);
                case 180: return (w.X + w.Width, w.Y + w.Height);
                case 270: return (w.X, w.Y + w.Height);
                default:  return (w.X, w.Y);
            }
        }

        /// <summary>
        /// The words in the order a PDF stores them: lines top to bottom, and within a line left to
        /// right, whichever direction the script reads in.
        /// </summary>
        /// <remarks>
        /// <para>Lines are found by banding on the vertical centre rather than by trusting the
        /// engine's grouping, because the input is a flat list of rectangles and a caller driving
        /// this class with its own engine may have no line structure at all. The band is half the
        /// word's own height, which separates ordinary leading and merges the baseline jitter an
        /// OCR engine reports for a single line.</para>
        /// <para>Ordering here rather than in the caller is deliberate: it is a property of the
        /// <i>file format</i> — §9.4's "glyphs in the order they appear" — not of any one engine,
        /// so every caller needs it and none should have to know it.</para>
        /// </remarks>
        internal static IReadOnlyList<PdfOcrWord> VisualOrder(
            IReadOnlyList<PdfOcrWord> words, int rotation = 0)
        {
            var ordered = new List<PdfOcrWord>(words.Count);
            foreach (var w in words) if (w != null && !string.IsNullOrEmpty(w.Text)) ordered.Add(w);
            if (ordered.Count < 2) return ordered;

            // Ordered as the page is READ, which is the rotated frame — reading order is a property
            // of what a person sees, and on a turned page the axis that runs down the page is not
            // user-space y. Sorting in unrotated space instead puts a rotated page's words in
            // column order, which extracts as gibberish while every word is individually correct.
            ordered.Sort((a, b) =>
            {
                float downA = DownThePage(a, rotation);
                float downB = DownThePage(b, rotation);
                float band = Math.Max(Math.Max(LineExtent(a, rotation),
                                               LineExtent(b, rotation)) / 2f, MinimumBandPoints);

                if (Math.Abs(downA - downB) > band)
                    return downA.CompareTo(downB);          // nearer the top of the page comes first
                return AlongTheLine(a, rotation).CompareTo(AlongTheLine(b, rotation));
            });
            return ordered;
        }

        /// <summary>How far down the page as displayed the word's centre is; smaller is nearer the top.</summary>
        private static float DownThePage(PdfOcrWord w, int rotation)
        {
            switch (rotation)
            {
                case 90:  return w.X + w.Width / 2f;
                case 180: return w.Y + w.Height / 2f;
                case 270: return -(w.X + w.Width / 2f);
                default:  return -(w.Y + w.Height / 2f);
            }
        }

        /// <summary>How far along its line the word starts; smaller comes first.</summary>
        private static float AlongTheLine(PdfOcrWord w, int rotation)
        {
            switch (rotation)
            {
                case 90:  return w.Y;
                case 180: return -w.X;
                case 270: return -w.Y;
                default:  return w.X;
            }
        }

        /// <summary>The word's extent across the line, which the banding tolerance is drawn from.</summary>
        private static float LineExtent(PdfOcrWord w, int rotation)
            => rotation == 90 || rotation == 270 ? w.Width : w.Height;

        /// <summary>
        /// Floor on the line-banding tolerance, so zero-height boxes do not collapse every word
        /// onto one line and get ordered by x across the whole page.
        /// </summary>
        private const float MinimumBandPoints = 1f;
    }
}
