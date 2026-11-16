// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>One barcode found on a page.</summary>
    public sealed class BarcodeReadResult
    {
        internal BarcodeReadResult(BarcodeType type, string text, int pageIndex,
                                   int left, int top, int right, int bottom, BarcodeSymbolInfo info = null)
        {
            Type = type; Text = text; PageIndex = pageIndex;
            Left = left; Top = top; Right = right; Bottom = bottom;
            Info = info;
            IsGs1 = info?.IsGs1 ?? false;
            AppendsToNextSymbol = info?.AppendsToNext ?? false;
            IsReaderProgramming = info?.ReaderProgramming ?? false;
            SequencePosition = info?.SequencePosition ?? 0;
            SequenceCount = info?.SequenceCount ?? 0;
            SymbologyIdentifier = type switch
            {
                BarcodeType.Code128 => "]C" + (info?.Modifier ?? 0),
                BarcodeType.DataMatrix => "]d" + (info?.Modifier ?? 1),
                BarcodeType.QrCode => "]Q" + (info?.Modifier ?? 1),
                _ => "]E0",
            };
        }

        internal BarcodeSymbolInfo Info { get; }

        /// <summary>
        /// The AIM symbology identifier (ISO/IEC 15424) a scanner would prefix the payload with:
        /// <c>]C0</c> for Code 128, <c>]C1</c> for GS1-128, <c>]C2</c> for an AIM application;
        /// <c>]E0</c> for EAN-13; <c>]Q1</c> for QR, <c>]Q2</c> with an ECI, <c>]Q3</c> GS1, <c>]Q4</c> GS1 with an ECI; <c>]d1</c>, <c>]d2</c> (GS1), <c>]d3</c> (AIM) for
        /// Data Matrix. Not part of <see cref="Text"/>.
        /// </summary>
        public string SymbologyIdentifier { get; }

        /// <summary>
        /// The symbol carries GS1 data: its first data character was FNC1. <see cref="Text"/> is then
        /// GS1 element strings, each variable-length one ended by the GS separator U+001D.
        /// </summary>
        public bool IsGs1 { get; }

        /// <summary>Code 128's FNC2: the payload is to be stored and transmitted with the next symbol's.</summary>
        public bool AppendsToNextSymbol { get; }

        /// <summary>Code 128's FNC3 or Data Matrix's reader-programming codeword: the payload is an instruction to the reader, not data.</summary>
        public bool IsReaderProgramming { get; }

        /// <summary>Data Matrix structured append: this symbol's position in its message (1-based), or 0 for a symbol that stands alone.</summary>
        public int SequencePosition { get; }

        /// <summary>Data Matrix structured append: how many symbols the message spans, or 0.</summary>
        public int SequenceCount { get; }

        /// <summary>Which symbology decoded it.</summary>
        public BarcodeType Type { get; }

        /// <summary>The decoded payload. For EAN-13 this is all thirteen digits.</summary>
        public string Text { get; }

        /// <summary>Zero-based page the symbol was found on.</summary>
        public int PageIndex { get; }

        /// <summary>Left edge in pixels of the rendered page.</summary>
        public int Left { get; }

        /// <summary>Top edge in pixels — the first row that decoded.</summary>
        public int Top { get; }

        /// <summary>Right edge in pixels, exclusive.</summary>
        public int Right { get; }

        /// <summary>Bottom edge in pixels, exclusive.</summary>
        public int Bottom { get; }

        /// <inheritdoc/>
        public override string ToString() => $"{Type} '{Text}' on page {PageIndex}";
    }

    /// <summary>
    /// Reads Code 128, EAN-13, QR and Data Matrix barcodes back out of a PDF page.
    /// </summary>
    /// <remarks>
    /// <para><b>The counterpart to <see cref="PdfBarcodeWriter"/>, and the reason its Stop pattern
    /// was two modules short for as long as it shipped.</b> Nothing had ever read a barcode this
    /// library wrote, so nothing could notice that the symbol ended in white space. A reader is not
    /// only a feature; it is the only test of a writer that a round trip through the same table
    /// cannot fake.</para>
    ///
    /// <para><b>It reads a rendered page, not the content stream.</b> Deliberately: a barcode in a
    /// PDF may be a path, an image, a form XObject, or a font whose glyphs are bars, and the only
    /// representation all four share is the page as a reader would see it. Rendering first also
    /// means an <i>imported</i> barcode — a scan, a label pasted in as JPEG — reads exactly like a
    /// generated one.</para>
    ///
    /// <para><b>What it reads.</b> Code 128 in code sets A, B and C; EAN-13; QR in every version and
    /// level and the modes a text reader meets (<see cref="QrGridReader"/>); Data Matrix ECC-200 in all
    /// fifteen square and six rectangular sizes and every encodation. QR and Data Matrix are repaired with
    /// Reed–Solomon before they are read. Symbols turned on the page are read, the linear ones along lines
    /// every fifteen degrees, QR and Data Matrix under perspective too; a page of many symbols — a sheet of
    /// labels — is read to the last, each locator's bound counting the candidates that were not read.</para>
    ///
    /// <para><b>What it does not do.</b> Function characters
    /// are reported rather than read as text: GS1 data and the AIM identifier
    /// (<see cref="BarcodeReadResult.IsGs1"/>, <see cref="BarcodeReadResult.SymbologyIdentifier"/>), a
    /// later FNC1 as the GS separator, Code 128's FNC4 as extended ASCII, a message append, a reader
    /// instruction, a Data Matrix structured-append position and its 05/06 macros. An ECI no character
    /// set is known for is refused by name (<see cref="EciCharacterSets"/>).</para>
    ///
    /// <para><b>A page with no barcode is the normal answer, and is silent.</b>
    /// <see cref="LastRefusal"/> is set only when something that really was a symbol could not be
    /// returned — a Code 128 function character, or a failed check digit. Reporting "no barcode
    /// here" for every page would be a channel callers learn to ignore.</para>
    /// </remarks>
    public static class PdfBarcodeReader
    {
        /// <summary>Resolution the page is rendered at when the caller does not choose one.</summary>
        /// <remarks>
        /// A barcode's narrow module is typically 0.33 mm — under a point — so at 72 dpi it is one
        /// pixel wide and any rounding destroys it. 300 dpi puts roughly four pixels in the narrowest
        /// element, which is what the quantiser needs to tell a 1-module element from a 2-module one.
        /// </remarks>
        public const int DefaultDpi = 300;

        /// <summary>
        /// How many rows apart the scan lines are.
        /// </summary>
        /// <remarks>
        /// A barcode is drawn tall precisely so that any horizontal cut through it carries the whole
        /// payload, so there is no need to look at every row. Four keeps the work proportional to
        /// the page while still crossing the shortest barcode anyone would print several times.
        /// </remarks>
        private const int RowStep = 4;

        [ThreadStatic]
        private static string _lastRefusal;

        /// <summary>
        /// Why a symbol that was found could not be returned, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Thread-static, and reset at the start of every read on every path — a caller reads it
        /// after the call, so a value left over from an earlier document would be attributed to this
        /// one. It stays null for a page with no barcode on it, which is not a failure.
        /// </remarks>
        public static string LastRefusal => _lastRefusal;

        /// <summary>
        /// Reads every barcode this understands from one page of a PDF.
        /// </summary>
        /// <param name="pdf">The document. Read from the current position.</param>
        /// <param name="pageIndex">Zero-based page to read.</param>
        /// <param name="dpi">Rendering resolution; see <see cref="DefaultDpi"/>.</param>
        /// <returns>What was found, possibly empty. Never null.</returns>
        public static IReadOnlyList<BarcodeReadResult> Read(
            Stream pdf, int pageIndex = 0, int dpi = DefaultDpi)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));

            _lastRefusal = null;

            RenderedPage page = PdfOperations.RenderPageToPixels(pdf, pageIndex, dpi);
            return page == null
                ? Array.Empty<BarcodeReadResult>()
                : ReadPixels(page, pageIndex);
        }

        /// <summary>
        /// Reads every barcode this understands from an already-rendered page.
        /// </summary>
        /// <param name="page">Rendered pixels, as <c>PdfOperations.RenderPageToPixels</c> returns.</param>
        /// <param name="pageIndex">The page number to report on each result.</param>
        /// <remarks>
        /// The overload for a caller that has already rendered — tiling, or reading several pages
        /// through one session — so the page is not rasterised twice.
        /// </remarks>
        public static IReadOnlyList<BarcodeReadResult> ReadPixels(RenderedPage page, int pageIndex = 0)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));

            _lastRefusal = null;

            int threshold = BarcodeScanline.Threshold(
                page.Pixels, page.Width, page.Height, page.Stride);

            var found = new List<BarcodeReadResult>();
            var seen = new Dictionary<string, int>();   // text -> index in `found`

            // The 2-D pass first, so its symbols lead the results. The linear scan below still
            // crosses them: a QR symbol's timing patterns are alternating single modules, exactly the
            // run list the 1-D readers try, and it is their quantisers and check characters that
            // refuse it. This comment once said the scan skipped a region already accounted for;
            // nothing ever did.
            // Each candidate is decoded as the locator offers it, and the answer goes back: a locator's bound
            // counts the candidates that were not read, so a page of many symbols is read to the end.
            QrLocator.Locate(page.Pixels, page.Width, page.Height, page.Stride, threshold, candidate =>
            {
                string text = QrGridReader.Decode(candidate.Grid, out string qrRefusal, out BarcodeSymbolInfo qrInfo);
                Remember(qrRefusal);
                if (text == null) return false;

                // One symbol can be reached through more than one finder triple, so the same
                // payload arrives more than once; `seen` is the same de-duplication the linear
                // readers use, for the same reason.
                AddOnce(found, seen, BarcodeType.QrCode, text, pageIndex, candidate, qrInfo);
                return true;
            });

            // The page as ink or paper, once: the turned Data Matrix search and the turned lines read it.
            byte[] darkMap = BarcodeScanline.DarkMap(page.Pixels, page.Width, page.Height, page.Stride, threshold);

            // Square symbols by their L's row and column; turned ones by the L's outline.
            bool ReadDataMatrix((bool[,] Grid, int Left, int Top, int Right, int Bottom) candidate)
            {
                string text = DataMatrixGridReader.Decode(candidate.Grid, out string dmRefusal, out BarcodeSymbolInfo dmInfo);
                Remember(dmRefusal);
                if (text == null) return false;
                AddOnce(found, seen, BarcodeType.DataMatrix, text, pageIndex, candidate, dmInfo);
                return true;
            }
            DataMatrixLocator.Locate(page.Pixels, page.Width, page.Height, page.Stride, threshold, ReadDataMatrix);
            DataMatrixTurnedLocator.Locate(darkMap, page.Width, page.Height, ReadDataMatrix);

            for (int y = 0; y < page.Height; y += RowStep)
            {
                List<BarcodeScanline.Run> runs = BarcodeScanline.Runs(
                    page.Pixels, page.Width, page.Stride, y, threshold);
                if (runs == null) continue;

                ScanRow(runs, y, pageIndex, found, seen);
                // An upside-down symbol reads right to left: the same runs, the other way.
                ScanLine(Reversed(runs, page.Width), page.Width - 1, y, -1, 0, pageIndex, found, seen);
            }

            // A linear symbol turned on the page — set sideways on a label, skewed on a scan — is read
            // along lines turned with it. A row crosses a symbol of the usual proportions end to end only
            // while it is within about fourteen degrees of square to its bars, so the other directions
            // are every fifteen degrees, the vertical among them.
            for (int step = 1; step < LineDirections; step++)
            {
                double angle = step * Math.PI / LineDirections;
                double dx = Math.Cos(angle), dy = Math.Sin(angle);
                foreach ((double x0, double y0, int count) in LinesAcross(page.Width, page.Height, dx, dy, LineStep))
                {
                    List<BarcodeScanline.Run> runs = BarcodeScanline.RunsAlong(
                        darkMap, page.Width, page.Height, x0, y0, dx, dy, count);
                    if (runs == null) continue;
                    ScanLine(runs, x0, y0, dx, dy, pageIndex, found, seen);
                    ScanLine(Reversed(runs, count), x0 + (dx * (count - 1)), y0 + (dy * (count - 1)), -dx, -dy, pageIndex, found, seen);
                }
            }

            return found;
        }

        /// <summary>
        /// How far apart the turned lines are: three rows. A linear symbol's bars are tall — a row decodes
        /// one on dozens of consecutive scans — so a turned one is crossed many times at this spacing too,
        /// at a third of the rows' cost.
        /// </summary>
        private const int LineStep = 3 * RowStep;

        /// <summary>
        /// The runs of a line of <paramref name="count"/> samples read from its other end: each run's
        /// start is counted from there, so a symbol pointing back along the line reads as one pointing
        /// forward.
        /// </summary>
        internal static List<BarcodeScanline.Run> Reversed(List<BarcodeScanline.Run> runs, int count)
        {
            var reversed = new List<BarcodeScanline.Run>(runs.Count);
            for (int i = runs.Count - 1; i >= 0; i--)
                reversed.Add(new BarcodeScanline.Run(runs[i].IsDark, runs[i].Length, count - runs[i].Start - runs[i].Length));
            return reversed;
        }

        /// <summary>Directions scanned, the row's included: every <c>180° / LineDirections</c>, each both ways.</summary>
        private const int LineDirections = 12;

        /// <summary>
        /// The lines in direction (<paramref name="dx"/>, <paramref name="dy"/>) that cross the page,
        /// <paramref name="spacing"/> pixels apart: each one's first sample and its sample count.
        /// </summary>
        internal static IEnumerable<(double X0, double Y0, int Count)> LinesAcross(int width, int height, double dx, double dy, int spacing)
        {
            // The lines are n · p = s for the normal n; s runs over the page's corners' range.
            double nx = -dy, ny = dx;
            double low = Math.Min(Math.Min(0, nx * width), Math.Min(ny * height, (nx * width) + (ny * height)));
            double high = Math.Max(Math.Max(0, nx * width), Math.Max(ny * height, (nx * width) + (ny * height)));
            for (double s = low; s <= high; s += spacing)
            {
                double px = s * nx, py = s * ny;
                double tMin = double.NegativeInfinity, tMax = double.PositiveInfinity;
                if (!Slab(px, dx, width, ref tMin, ref tMax) || !Slab(py, dy, height, ref tMin, ref tMax)) continue;
                int count = SaturatingCast.ToInt(Math.Floor(tMax - tMin));
                if (count < 2) continue;
                yield return (px + (dx * tMin), py + (dy * tMin), count);
            }
        }

        /// <summary>Narrows [tMin, tMax] to where p + t·d lies in [0, extent) on one axis.</summary>
        private static bool Slab(double p, double d, int extent, ref double tMin, ref double tMax)
        {
            if (Math.Abs(d) < 1e-12) return p >= 0 && p < extent;
            double a = (0 - p) / d, b = (extent - 1e-9 - p) / d;
            if (a > b) { double t = a; a = b; b = t; }
            tMin = Math.Max(tMin, a);
            tMax = Math.Min(tMax, b);
            return tMin < tMax;
        }

        /// <summary>
        /// <see cref="ScanRow"/> along a line in any direction: the box is the decoded runs' endpoints
        /// on the page.
        /// </summary>
        private static void ScanLine(
            List<BarcodeScanline.Run> runs, double x0, double y0, double dx, double dy, int pageIndex,
            List<BarcodeReadResult> found, Dictionary<string, int> seen)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                if (!runs[i].IsDark) continue;

                string text = Code128Reader.Decode(runs, i, out int end, out string refusal, out BarcodeSymbolInfo info);
                BarcodeType type = BarcodeType.Code128;
                if (text == null)
                {
                    Remember(refusal);
                    text = Ean13Reader.Decode(runs, i, out end, out refusal);
                    type = BarcodeType.Ean13;
                    Remember(refusal);
                }
                if (text == null) continue;

                double first = runs[i].Start, last = runs[end - 1].Start + runs[end - 1].Length;
                double ax = x0 + (dx * first), ay = y0 + (dy * first), bx = x0 + (dx * last), by = y0 + (dy * last);
                Record(found, seen, type, text, pageIndex,
                       SaturatingCast.ToInt(Math.Floor(Math.Min(ax, bx))), SaturatingCast.ToInt(Math.Floor(Math.Min(ay, by))),
                       SaturatingCast.ToInt(Math.Ceiling(Math.Max(ax, bx))), SaturatingCast.ToInt(Math.Ceiling(Math.Max(ay, by))) + 1,
                       type == BarcodeType.Code128 ? info : null);
                i = end - 1;
            }
        }

        /// <summary>Tries every dark run in the row as the start of a symbol.</summary>
        /// <remarks>
        /// <b>Every dark run, because a barcode's position is not known.</b> A start attempt that
        /// fails is silent and cheap — the quantiser rejects most of them on the first six runs — and
        /// a successful one skips the whole symbol, so the row is walked once whatever it holds.
        /// </remarks>
        private static void ScanRow(
            List<BarcodeScanline.Run> runs, int y, int pageIndex,
            List<BarcodeReadResult> found, Dictionary<string, int> seen)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                if (!runs[i].IsDark) continue;

                string text = Code128Reader.Decode(runs, i, out int end, out string refusal, out BarcodeSymbolInfo info);
                BarcodeType type = BarcodeType.Code128;

                if (text == null)
                {
                    Remember(refusal);
                    text = Ean13Reader.Decode(runs, i, out end, out refusal);
                    type = BarcodeType.Ean13;
                    Remember(refusal);
                }

                if (text == null) continue;

                Record(found, seen, type, text, pageIndex, runs, i, end, y, type == BarcodeType.Code128 ? info : null);
                i = end - 1;   // the loop's own increment steps past the symbol's last run
            }
        }

        /// <summary>
        /// Adds a result, or grows the one already recorded for this text.
        /// </summary>
        /// <remarks>
        /// A barcode is tall, so consecutive scan rows decode the same payload. Merging them by text
        /// is what turns those repeats into one result with a real height instead of a list of
        /// identical hits — and the height is the evidence a caller needs to tell a barcode from a
        /// one-row coincidence.
        /// </remarks>
        private static void Record(
            List<BarcodeReadResult> found, Dictionary<string, int> seen,
            BarcodeType type, string text, int pageIndex,
            List<BarcodeScanline.Run> runs, int firstRun, int endRun, int y, BarcodeSymbolInfo info = null)
            => Record(found, seen, type, text, pageIndex,
                      runs[firstRun].Start, y, runs[endRun - 1].Start + runs[endRun - 1].Length, y + 1, info);

        /// <summary>The same, for a box already on the page; a repeat grows the recorded box to cover both.</summary>
        private static void Record(
            List<BarcodeReadResult> found, Dictionary<string, int> seen,
            BarcodeType type, string text, int pageIndex,
            int left, int top, int right, int bottom, BarcodeSymbolInfo info)
        {
            string key = (int)type + ":" + text;

            if (seen.TryGetValue(key, out int index))
            {
                BarcodeReadResult previous = found[index];
                found[index] = new BarcodeReadResult(
                    type, text, pageIndex,
                    Math.Min(previous.Left, left), Math.Min(previous.Top, top),
                    Math.Max(previous.Right, right), Math.Max(previous.Bottom, bottom), previous.Info);
                return;
            }

            seen[key] = found.Count;
            found.Add(new BarcodeReadResult(type, text, pageIndex, left, top, right, bottom, info));
        }

        /// <summary>A 2-D symbol's result, unless one with the same text is already recorded.</summary>
        private static void AddOnce(
            List<BarcodeReadResult> found, Dictionary<string, int> seen, BarcodeType type, string text, int pageIndex,
            (bool[,] Grid, int Left, int Top, int Right, int Bottom) candidate, BarcodeSymbolInfo info)
        {
            string key = (int)type + ":" + text;
            if (seen.ContainsKey(key)) return;
            seen[key] = found.Count;
            found.Add(new BarcodeReadResult(type, text, pageIndex, candidate.Left, candidate.Top, candidate.Right, candidate.Bottom, info));
        }

        /// <summary>Keeps the first refusal, which is the one nearest the caller's intent.</summary>
        private static void Remember(string refusal)
        {
            if (refusal != null && _lastRefusal == null) _lastRefusal = refusal;
        }
    }
}
