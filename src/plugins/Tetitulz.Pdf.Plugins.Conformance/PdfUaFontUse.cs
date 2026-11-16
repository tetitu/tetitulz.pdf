// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The fonts a document paints text with — ISO 14289-1 7.21.4.1's "used for rendering": a glyph
    /// of the font referenced from a content stream, in a render mode that marks the page. A font
    /// referenced only in mode 3 (invisible) is exempt from embedding by NOTE 2, and one referenced
    /// nowhere is not used at all.
    /// </summary>
    /// <remarks>
    /// <para><b>One answer for both PDF/UA checkers.</b> The plugin's UA25 counted every font in a
    /// page's resources; the deep checker's UA21 tracked the text state in a scan that did not read
    /// tiling patterns, Type 3 glyph procedures or annotation appearances. So the two disagreed —
    /// the audit, which runs both, failed this library's own searchable PDFs, whose OCR layer is
    /// written in a font that is deliberately not embedded — and neither could say a font was unused
    /// without a blind spot. This reads <see cref="PdfXValidator.ScanPage"/>'s streams, which cover
    /// all of those, with <see cref="TextPaintTracker"/> for what each one paints. Public because
    /// the Accessibility feature reads it, across the sanctioned edge, as it reads
    /// <see cref="PdfUaAnnotationScope"/>.</para>
    ///
    /// <para><b><c>null</c> means "treat every font as used", never "no font is used".</b> It is
    /// the answer for an encrypted document (the content is ciphertext), a page the scan could not
    /// finish, content that does not parse, and text shown before any font is selected — a form
    /// XObject or appearance drawing in a font it inherits from its caller, which reading each
    /// stream on its own cannot see.</para>
    /// </remarks>
    public static class PdfUaFontUse
    {
        [ThreadStatic]
        private static string _lastError;

        /// <summary>
        /// Why the last <see cref="PaintedFonts(PdfDocument)"/> on this thread answered <c>null</c>
        /// because content could not be read; <c>null</c> otherwise. <c>[ThreadStatic]</c>.
        /// </summary>
        public static string LastError => _lastError;

        /// <summary>
        /// The font dictionaries the document paints text with, compared by reference — the
        /// resolved objects its resources name — or <c>null</c> when that cannot be established.
        /// </summary>
        public static ICollection<PdfDictionary> PaintedFonts(PdfDocument doc)
            => PaintedFonts(doc, PdfUaChecker.DefaultMaxMcidScanBytesPerPage,
                            PdfUaChecker.DefaultMaxMcidScanBytesPerDocument);

        /// <summary><see cref="PaintedFonts(PdfDocument)"/> under the caller's content-scan ceilings.</summary>
        internal static HashSet<PdfDictionary> PaintedFonts(PdfDocument doc, int maxPageContentBytes,
            long maxDocumentContentBytes)
        {
            // Reference identity: PdfDictionary does not override Equals, and a font reached through
            // any resource dictionary resolves to the same cached object.
            var painted = new HashSet<PdfDictionary>();
            return VisitPaintedText(doc, maxPageContentBytes, maxDocumentContentBytes, (font, operands) => painted.Add(font))
                ? painted : null;
        }

        /// <summary>
        /// The codes each font's painting text operators show: a simple font's bytes (ISO 32000-1
        /// §9.6.6.1), and a composite font's CIDs through its own encoding CMap (§9.7.5,
        /// <c>Core.Fonts.EmbeddedGlyphSets.CidReader</c>). A composite font whose CMap could not be
        /// honoured is present with a <c>null</c> set: its strings were not decoded. The same reading of
        /// the same streams as <see cref="PaintedFonts(PdfDocument, int, long)"/>, and <c>null</c> where
        /// that is; a font that paints only empty strings is present with no codes.
        /// </summary>
        internal static Dictionary<PdfDictionary, HashSet<int>> PaintedCodes(PdfDocument doc, int maxPageContentBytes,
            long maxDocumentContentBytes)
        {
            var codes = new Dictionary<PdfDictionary, HashSet<int>>();
            var readers = new Dictionary<PdfDictionary, Func<byte[], IReadOnlyList<int>>>();
            bool known = VisitPaintedText(doc, maxPageContentBytes, maxDocumentContentBytes, (font, operands) =>
            {
                if (!readers.TryGetValue(font, out Func<byte[], IReadOnlyList<int>> read))
                {
                    read = CodeReader(doc, font);
                    readers[font] = read;
                    codes[font] = read == null ? null : new HashSet<int>();
                }
                if (read == null) return;
                HashSet<int> set = codes[font];
                // Tj and ' show their last operand, " its third, TJ the strings of its array.
                PdfObject shown = operands.Count > 0 ? operands[operands.Count - 1] : null;
                if (shown is PdfString text) set.UnionWith(read(text.RawBytes));
                else if (shown is PdfArray array)
                    for (int i = 0; i < array.Count; i++)
                        if (array[i] is PdfString part) set.UnionWith(read(part.RawBytes));
            });
            return known ? codes : null;
        }

        /// <summary>
        /// Every string a text-showing operator shows, in any render mode — ISO 19005-2/3 6.2.11.8's
        /// "regardless of text rendering mode" — with the font it shows through, over the same streams
        /// as <see cref="PaintedFonts(PdfDocument, int, long)"/>. A show whose font the stream inherits
        /// from its caller is passed over, not guessed. False — with <see cref="LastError"/> set when
        /// content would not read — where the strings could not all be read.
        /// </summary>
        internal static bool ShownStrings(PdfDocument doc, int maxPageContentBytes, long maxDocumentContentBytes,
            Action<PdfDictionary, byte[]> onShown)
            => Visit(doc, maxPageContentBytes, maxDocumentContentBytes, paintingOnly: false, (font, operands) =>
            {
                PdfObject shown = operands.Count > 0 ? operands[operands.Count - 1] : null;
                if (shown is PdfString text) onShown(font, text.RawBytes);
                else if (shown is PdfArray array)
                    for (int i = 0; i < array.Count; i++)
                        if (array[i] is PdfString part) onShown(font, part.RawBytes);
            });

        /// <summary>A simple font's string as its bytes, a composite font's as its CIDs, or <c>null</c> when the CMap could not be honoured.</summary>
        private static Func<byte[], IReadOnlyList<int>> CodeReader(PdfDocument doc, PdfDictionary font)
        {
            if ((doc.ResolveObject(font[PdfName.Subtype]) as PdfName)?.Value != "Type0")
                return bytes => System.Array.ConvertAll(bytes, b => (int)b);
            return Tetitulz.Pdf.Core.Fonts.EmbeddedGlyphSets.CidReader(font, doc, out _);
        }

        /// <summary>
        /// Calls <paramref name="onPaint"/> with the font and operands of every text-showing operator that
        /// paints, over <see cref="PdfXValidator.ScanPage"/>'s streams. False — with <see cref="LastError"/>
        /// set when content would not read — where the answer is "treat every font as used".
        /// </summary>
        private static bool VisitPaintedText(PdfDocument doc, int maxPageContentBytes, long maxDocumentContentBytes,
            Action<PdfDictionary, IReadOnlyList<PdfObject>> onPaint)
            => Visit(doc, maxPageContentBytes, maxDocumentContentBytes, paintingOnly: true, onPaint);

        /// <summary>
        /// The one walk: every text-showing operator over <see cref="PdfXValidator.ScanPage"/>'s streams —
        /// only those that paint when <paramref name="paintingOnly"/>, where a show with no font selected
        /// makes the answer unknown; otherwise all of them, such a show passed over.
        /// </summary>
        private static bool Visit(PdfDocument doc, int maxPageContentBytes, long maxDocumentContentBytes,
            bool paintingOnly, Action<PdfDictionary, IReadOnlyList<PdfObject>> onPaint)
        {
            _lastError = null;
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (doc.IsEncrypted || doc.Pages == null) return false;

            long documentBytes = 0;
            try
            {
                for (int p = 0; p < doc.Pages.Count; p++)
                {
                    var scan = PdfXValidator.ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                        ref documentBytes, maxDocumentContentBytes, collectContent: true);
                    if (scan.Incomplete != null) return false;

                    for (int s = 0; s < scan.ContentStreams.Count; s++)
                    {
                        var fonts = doc.ResolveObject(scan.ContentStreamResources[s]?[PdfName.Font]) as PdfDictionary;
                        var byName = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
                        var tracker = new TextPaintTracker();

                        foreach (var op in ScanReuse.Parse(scan.ContentStreams[s]))
                        {
                            if (!tracker.Observe(op.Operator, op.Operands) || (paintingOnly && !tracker.Paints)) continue;
                            if (tracker.CurrentFont == null)
                            {
                                if (paintingOnly) return false;
                                continue;
                            }

                            if (!byName.TryGetValue(tracker.CurrentFont, out PdfDictionary font))
                                byName[tracker.CurrentFont] = font =
                                    doc.ResolveObject(fonts?[new PdfName(tracker.CurrentFont)]) as PdfDictionary;
                            if (font != null) onPaint(font, op.Operands);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = "content could not be read to tell used fonts from unused ones — " +
                             ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            return true;
        }
    }
}
