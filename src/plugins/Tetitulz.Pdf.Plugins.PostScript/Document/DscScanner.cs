// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Globalization;

namespace Tetitulz.Pdf.Plugins.PostScript.Document
{
    /// <summary>
    /// Observes Document Structuring Convention comments as the scanner walks past them.
    ///
    /// <para>DSC comments are <i>comments</i> — a conforming interpreter ignores them entirely — but
    /// two kinds carry information nothing else does. <c>%%BoundingBox</c> and
    /// <c>%%HiResBoundingBox</c> are the only statement of an EPS's extent, since an EPS by
    /// definition never calls <c>setpagedevice</c>; and <c>%%BeginBinary</c> / <c>%%BeginData</c>
    /// declare a byte count that <b>must</b> be skipped rather than tokenised, because the payload
    /// is arbitrary bytes that will otherwise scan as a storm of garbage names.</para>
    ///
    /// <para>Deliberately not a parser: it is a line observer with no state machine, because DSC
    /// conformance is advisory and a file that violates it must still convert.</para>
    /// </summary>
    internal sealed class DscScanner
    {
        /// <summary><c>%%BoundingBox</c>, in points, or null if absent or unparseable.</summary>
        public PsBoundingBox? BoundingBox { get; private set; }

        /// <summary>
        /// <c>%%HiResBoundingBox</c>, in points, or null. When both are present this one wins: it
        /// is strictly more precise, and the integer one is required to be its outward rounding.
        /// </summary>
        public PsBoundingBox? HiResBoundingBox { get; private set; }

        /// <summary>The best available bounding box: hi-res if present, else the integer one.</summary>
        public PsBoundingBox? EffectiveBoundingBox => HiResBoundingBox ?? BoundingBox;

        /// <summary>True when the file announced itself as EPS (<c>%!PS-Adobe-…&#160;EPSF-…</c>).</summary>
        public bool IsEps { get; private set; }

        /// <summary>
        /// Byte offset of the most recent <c>%!PS-AdobeFont</c> / <c>%!FontType1</c> line, or -1.
        ///
        /// <para>A Type 1 font program embedded in PostScript begins with that line, and a PDF
        /// <c>/FontFile</c> needs the program from exactly there — the clear-text header is part of
        /// the program, not a preamble to it. The scanner asks for this offset when it reaches the
        /// matching <c>eexec</c>, and clears it with <see cref="ClearFontProgramStart"/> once it
        /// has.</para>
        /// </summary>
        public int LastFontProgramStart { get; private set; } = -1;

        /// <summary>
        /// Forgets the recorded header offset, after the <c>eexec</c> it belongs to was captured.
        ///
        /// <para><b>One header, one program.</b> Without this a file carrying a single
        /// <c>%!PS-AdobeFont</c> line and many <c>eexec</c> sections captures each of them from that
        /// same offset, so every capture copies a longer prefix than the last — quadratic in the
        /// input, and all of it retained. A second program legitimately has a second header, so
        /// clearing costs nothing real.</para>
        /// </summary>
        public void ClearFontProgramStart() => LastFontProgramStart = -1;

        /// <summary>
        /// Examines one comment line.
        /// </summary>
        /// <param name="data">The whole buffer.</param>
        /// <param name="start">Offset of the leading <c>%</c>.</param>
        /// <param name="length">Length of the line, excluding its terminator.</param>
        /// <param name="skipInLines">
        /// True when the returned count is in lines rather than bytes — <c>%%BeginData</c>'s
        /// <c>Lines</c> unit.
        /// </param>
        /// <returns>
        /// The length of a <b>binary</b> payload the caller must skip without interpreting — in
        /// bytes, or in lines when <paramref name="skipInLines"/> is set — non-zero only for
        /// <c>%%BeginBinary</c> and a Binary <c>%%BeginData</c> (DSC 3.0 §5.7). An ASCII or Hex
        /// data section is program text — the <c>image</c> that reads it is normally the section's
        /// first line — so it is 0: the interpreter runs it. Skipping every section by its count
        /// as bytes would skip the operator inside an ASCII one with its data, losing the picture,
        /// and cut a section counted in Lines off mid-way.
        /// </returns>
        public int Observe(byte[] data, int start, int length, out bool skipInLines)
        {
            skipInLines = false;
            if (length < 2 || data[start] != (byte)'%') return 0;

            // "%!" is the magic line; "%%" is a DSC comment. Anything else is an ordinary comment.
            byte second = data[start + 1];
            if (second != (byte)'%' && second != (byte)'!') return 0;

            string line = Latin1(data, start, Math.Min(length, MaxObservedLineLength));

            if (second == (byte)'!')
            {
                if (line.IndexOf("EPSF", StringComparison.Ordinal) >= 0) IsEps = true;

                // "%!PS-AdobeFont-1.0: Name" / "%!FontType1-1.0: Name" open a font program.
                if (line.IndexOf("AdobeFont", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("FontType1", StringComparison.Ordinal) >= 0)
                    LastFontProgramStart = start;

                return 0;
            }

            // An included document (DSC 3.0 §5.3, %%BeginDocument … %%EndDocument) carries its own
            // comments, and its bounding box is the included figure's, not this document's: read as
            // ours, an EPS placed on the page resized the whole page to the figure.
            if (line.StartsWith("%%BeginDocument", StringComparison.Ordinal)) { _includedDepth++; return 0; }
            if (line.StartsWith("%%EndDocument", StringComparison.Ordinal))
            {
                if (_includedDepth > 0) _includedDepth--;
                return 0;
            }

            if (TryReadBox(line, "%%BoundingBox:", out var box))
            {
                // "(atend)" defers the value to the trailer; a later real one simply overwrites.
                if (_includedDepth == 0 && Accept(box)) BoundingBox = box;
                return 0;
            }

            if (TryReadBox(line, "%%HiResBoundingBox:", out var hires))
            {
                if (_includedDepth == 0 && Accept(hires)) HiResBoundingBox = hires;
                return 0;
            }

            if (TryReadCount(line, "%%BeginBinary:", out int binaryBytes, out _)) return binaryBytes;

            // %%BeginData: <count> [ Binary | Hex | ASCII ] [ Bytes | Lines ] — the type defaults
            // to Binary and the unit to Bytes (DSC 3.0 §5.7). Only a binary section is skipped.
            if (TryReadCount(line, "%%BeginData:", out int dataCount, out string[] parts))
            {
                bool binary = parts.Length < 2 || parts[1] == "Binary";
                skipInLines = parts.Length > 2 && parts[2] == "Lines";
                return binary ? dataCount : 0;
            }

            return 0;
        }

        // A DSC line longer than this is malformed; reading it whole would let a file with one
        // enormous "comment" allocate freely inside the scanner's hot path.
        private const int MaxObservedLineLength = 4096;

        private bool _nonFiniteBoxReported;

        // How many %%BeginDocument sections the scan is inside.
        private int _includedDepth;

        /// <summary>
        /// Keeps a parsed box only when it is a real rectangle, reporting the refusal once.
        ///
        /// <para>Refused <b>here</b> as well as in <see cref="PsBoundingBox.IsValid"/> so a
        /// non-finite comment never becomes the recorded box at all: <c>EffectiveBoundingBox</c> is
        /// also handed to callers as <c>PostScriptDocumentResult.DeclaredBoundingBox</c>, which no
        /// validity test guards. Reported once because a file can carry an unbounded number of
        /// <c>%%BoundingBox</c> lines, and a diagnostic per line is the same flood the scanner's own
        /// stray-byte reports were.</para>
        /// </summary>
        private bool Accept(in PsBoundingBox box)
        {
            if (box.IsFinite) return true;

            if (!_nonFiniteBoxReported)
            {
                _nonFiniteBoxReported = true;
                PostScriptDiagnostics.Report("DscScanner",
                    "a %%BoundingBox comment states a non-finite value (an overflowing literal such " +
                    "as '1e400' parses as infinity); it was ignored, so the page size comes from the " +
                    "measured extent instead. Reported once per conversion.");
            }

            return false;
        }

        private static bool TryReadBox(string line, string prefix, out PsBoundingBox box)
        {
            box = default;
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;

            string rest = line.Substring(prefix.Length).Trim();
            if (rest.StartsWith("(atend)", StringComparison.Ordinal)) return false;

            string[] parts = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return false;

            var v = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out v[i]))
                    return false;

            box = new PsBoundingBox(v[0], v[1], v[2], v[3]);
            return true;
        }

        private static bool TryReadCount(string line, string prefix, out int count, out string[] parts)
        {
            count = 0;
            parts = null;
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;

            string rest = line.Substring(prefix.Length).Trim();
            parts = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) return false;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out int n) || n < 0)
                return false;

            count = n;
            return true;
        }

        // One implementation, in Core.Text.Latin1Text — six byte-identical copies existed.
        private static string Latin1(byte[] data, int start, int length)
            => Core.Text.Latin1Text.Decode(data, start, length);
    }

    /// <summary>A rectangle in PostScript points, as a DSC bounding-box comment states it.</summary>
    internal readonly struct PsBoundingBox
    {
        public readonly double LowerLeftX, LowerLeftY, UpperRightX, UpperRightY;

        public PsBoundingBox(double llx, double lly, double urx, double ury)
        {
            LowerLeftX  = llx;
            LowerLeftY  = lly;
            UpperRightX = urx;
            UpperRightY = ury;
        }

        public double Width  => UpperRightX - LowerLeftX;
        public double Height => UpperRightY - LowerLeftY;

        /// <summary>
        /// True when all four corners are real numbers.
        ///
        /// <para><b>An overflowing DSC literal is not a parse failure.</b>
        /// <c>double.TryParse(NumberStyles.Float, InvariantCulture)</c> returns <c>true</c> and
        /// <c>+infinity</c> for <c>1e400</c> on .NET Core 3.0 and later — it stopped throwing
        /// <c>OverflowException</c> there — and it accepts the literal word <c>Infinity</c> outright.
        /// So <c>%%BoundingBox: 0 0 1e400 1e400</c> reaches this type as a box whose width and height
        /// are both infinite.</para>
        /// </summary>
        public bool IsFinite =>
            PsNumeric.IsFinite(LowerLeftX)  && PsNumeric.IsFinite(LowerLeftY) &&
            PsNumeric.IsFinite(UpperRightX) && PsNumeric.IsFinite(UpperRightY);

        /// <summary>
        /// False for a reversed or degenerate box, which some producers do emit — and false for a
        /// non-finite one.
        ///
        /// <para><b>The finiteness half is what keeps an unopenable file out of the writer.</b>
        /// <c>Width > 0</c> is <c>true</c> for infinity, so an infinite box passed as valid and
        /// <c>PostScriptDocumentWriter</c> put it straight into <c>/MediaBox</c>:
        /// <c>new PdfNumber(double.PositiveInfinity)</c> is not an integer, so the writer takes its
        /// <c>F6</c> path and <c>(double.PositiveInfinity).ToString("F6", InvariantCulture)</c> is the
        /// literal text <c>Infinity</c> — which this repo's own parser reads back as null, so the page
        /// is unopenable. The mirror case makes <c>Origin</c> infinite and prepends
        /// <c>1 0 0 1 Infinity Infinity cm</c> to the content stream, so the stream itself will not
        /// parse. NaN never had the problem: every comparison against it is false, so a NaN box was
        /// already refused here.</para>
        ///
        /// <para>Magnitude is deliberately <b>not</b> bounded. A finite 1e300-point box is ugly and
        /// still opens; clamping to Annex C.2's 14 400-unit implementation limit would silently
        /// resize legitimate oversize artwork, which is a different decision from refusing a value
        /// that is not a number at all.</para>
        /// </summary>
        /// <remarks>The width and height are finite too: corners of -1e308 and 1e308 are each finite
        /// and differ by more than a double holds, and an infinite width is written as 0.</remarks>
        public bool IsValid => IsFinite && Width > 0 && Height > 0
            && PsNumeric.IsFinite(Width) && PsNumeric.IsFinite(Height);
    }
}
