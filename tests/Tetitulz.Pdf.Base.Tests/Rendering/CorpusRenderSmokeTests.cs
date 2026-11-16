// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Renders every page of every PDF in the developer-local corpus and asserts two things that
/// hold for any correct renderer, on any input:
///
/// <list type="number">
///   <item>rendering never throws — a malformed page must degrade, not escape; and</item>
///   <item>a page whose content stream contains <b>painting operators</b> must not come out blank.</item>
/// </list>
///
/// The second is the one that matters. "A PNG came back" is satisfied by a blank page, which is
/// exactly how two optional-content defects (indirect <c>/D</c> resolution and <c>BDC /OC</c>
/// suppression) shipped completely non-functional behind six green tests. Correlating the pixels
/// against the operators the page actually contains is what closes that hole: it distinguishes
/// "this page is legitimately empty" from "we dropped its content".
/// </summary>
/// <remarks>
/// <para>
/// Genuinely blank pages are common and are <b>not</b> failures. Of the 20-file corpus this was
/// written against, 8 of 72 pages render blank and all 8 are correct: four have a zero-byte
/// content stream, and four carry 122–366 KB of nothing but colour-setting operators
/// (<c>rg</c>/<c>RG</c>/<c>g</c>/<c>G</c>) with no path, text or XObject operator at all.
/// </para>
/// <para>
/// This is a smoke test, not a fidelity test. It cannot say the output <i>looks</i> right — that
/// needs a reference renderer to diff against, which this repository does not have. It catches
/// the class of regression where content silently stops being drawn.
/// </para>
/// </remarks>
public sealed class CorpusRenderSmokeTests
{
    private readonly ITestOutputHelper _out;

    public CorpusRenderSmokeTests(ITestOutputHelper output) => _out = output;

    /// <summary>Pages rendered per document. Bounds runtime on book-length inputs.</summary>
    private const int MaxPagesPerDocument = 10;

    private const int Dpi = 72;

    [CorpusFact]
    public void EveryCorpusPage_RendersWithoutThrowing_AndPaintsWhenItHasPaintingOperators()
    {
        var pdfs = TestCorpus.Pdfs();
        Assert.NotEmpty(pdfs);   // [CorpusFact] already skips when empty; this pins the contract.

        var failures = new List<string>();
        var diagnostics = new List<string>();
        var croppedNotes = new List<string>();
        int docs = 0, pages = 0, blankAndEmpty = 0, painted = 0, blankButCropped = 0;

        void Sink(RenderDiagnosticKind kind, string detail, Exception? ex)
        {
            lock (diagnostics)
                if (diagnostics.Count < 60) diagnostics.Add($"{kind}: {Truncate(detail, 140)}");
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            foreach (string path in pdfs)
            {
                string name = Path.GetFileName(path);
                docs++;

                int pageCount;
                try
                {
                    using var probe = PdfDocument.Open(File.OpenRead(path));
                    pageCount = probe.Pages.Count;
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: could not open — {ex.GetType().Name}: {Truncate(ex.Message, 160)}");
                    continue;
                }

                for (int i = 0; i < Math.Min(pageCount, MaxPagesPerDocument); i++)
                {
                    pages++;

                    // What does the page actually ask to be painted, and is it cropped?
                    int paintOps;
                    bool cropped;
                    try
                    {
                        using var doc = PdfDocument.Open(File.OpenRead(path));
                        var pg = doc.Pages[i];
                        paintOps = CountPaintingOperators(pg.GetContentStreamData());
                        cropped = IsCropped(pg);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{name} p{i}: content stream unreadable — " +
                                     $"{ex.GetType().Name}: {Truncate(ex.Message, 160)}");
                        continue;
                    }

                    bool blank;
                    try
                    {
                        using var fs = File.OpenRead(path);
                        byte[] png = PdfPageRenderer.RenderPage(fs, i, Dpi);
                        Assert.NotNull(png);
                        using var bmp = SKBitmap.Decode(png);
                        if (bmp == null)
                        {
                            failures.Add($"{name} p{i}: the produced PNG did not decode.");
                            continue;
                        }
                        blank = IsBlank(bmp);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{name} p{i}: render threw {ex.GetType().Name}: " +
                                     $"{Truncate(ex.Message, 160)}");
                        continue;
                    }

                    if (blank && paintOps > 0 && !cropped)
                    {
                        // The page asked for ink, is not cropped, and got none. This is the
                        // regression this test exists to catch.
                        failures.Add($"{name} p{i}: BLANK despite {paintOps} painting operator(s) " +
                                     "in its content stream and no CropBox to explain it — " +
                                     "content was dropped.");
                    }
                    else if (blank && paintOps > 0)
                    {
                        // Cropped and blank: cannot be judged from operator counts alone.
                        // Reported, not failed — see IsCropped.
                        blankButCropped++;
                        if (croppedNotes.Count < 20)
                            croppedNotes.Add($"{name} p{i}: blank, {paintOps} paint op(s), CropBox ⊂ MediaBox");
                    }
                    else if (blank) blankAndEmpty++;
                    else painted++;
                }
            }
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        _out.WriteLine($"corpus: {docs} document(s), {pages} page(s) at {Dpi} dpi " +
                       $"(first {MaxPagesPerDocument} pages each)");
        _out.WriteLine($"  painted             : {painted}");
        _out.WriteLine($"  blank, no paint-op  : {blankAndEmpty}  (correct — nothing was asked for)");
        _out.WriteLine($"  blank, but cropped  : {blankButCropped}  (not judged — see IsCropped)");
        _out.WriteLine($"  failures            : {failures.Count}");
        foreach (var n in croppedNotes) _out.WriteLine("    " + n);
        if (diagnostics.Count > 0)
        {
            _out.WriteLine("\nrender diagnostics raised (first 60):");
            foreach (var d in diagnostics) _out.WriteLine("  " + d);
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} corpus page(s) failed to render:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// Guards the guard: a page built to paint a black square must not be classified as blank,
    /// and an empty content stream must not be classified as painting. Without this, a bug in
    /// <see cref="IsBlank"/> or <see cref="CountPaintingOperators"/> would silently disarm the
    /// corpus test above — and unlike that one, this runs on every machine.
    /// </summary>
    [Fact]
    public void TheBlankAndPaintOperatorProbes_AgreeOnKnownInput()
    {
        byte[] painting = MinimalPdfBuilder.CreateMinimalPdf(
            contentStream: "0 g 100 100 400 400 re f");
        byte[] empty = MinimalPdfBuilder.CreateMinimalPdf(contentStream: " ");

        using var paintedBmp = SKBitmap.Decode(
            PdfPageRenderer.RenderPage(new MemoryStream(painting), 0, Dpi));
        using var emptyBmp = SKBitmap.Decode(
            PdfPageRenderer.RenderPage(new MemoryStream(empty), 0, Dpi));

        Assert.False(IsBlank(paintedBmp!), "IsBlank called a painted page blank.");
        Assert.True(IsBlank(emptyBmp!), "IsBlank called an empty page painted.");

        Assert.True(CountPaintingOperators(Encoding.ASCII.GetBytes("0 g 100 100 400 400 re f")) > 0);
        Assert.Equal(0, CountPaintingOperators(Encoding.ASCII.GetBytes("1 0 0 RG 0.5 g 2 w")));
        Assert.Equal(0, CountPaintingOperators(Array.Empty<byte>()));

        // Operator names inside a string literal are operands, not operators.
        Assert.Equal(0, CountPaintingOperators(Encoding.ASCII.GetBytes("(f S Do) 5 6")));
    }

    // ------------------------------------------------------------------
    //  Probes
    // ------------------------------------------------------------------

    /// <summary>
    /// True when the page's /CropBox is strictly smaller than its /MediaBox, so content can
    /// legitimately be painted and then cropped entirely out of view.
    /// </summary>
    /// <remarks>
    /// This exemption is load-bearing and was added because the test produced a false positive
    /// on real input. <c>w_pacc74.pdf</c> page 6 is an InDesign slug page: MediaBox
    /// [0 0 474 690], CropBox [21.02 21.02 452.98 668.98], and its six painting operators draw
    /// printer's crop marks at y≈672–690 and y≈0–18 plus slug text at y≈6 — every one of them
    /// outside the CropBox. Rendering it blank is correct; cropping that furniture away is what
    /// a CropBox is for.
    ///
    /// <para>Deliberately conservative: it exempts the whole page rather than trying to decide
    /// which marks land inside the box, because doing that properly means running the content
    /// stream — i.e. re-implementing the renderer under test. The cost is that a genuinely
    /// dropped page would be missed if it also happens to be cropped; those are reported in the
    /// test output as "blank, but cropped" so a regression is still visible to a reader, and the
    /// case the test was written for (content silently stops drawing, no cropping involved) is
    /// still a hard failure.</para>
    /// </remarks>
    private static bool IsCropped(PdfPage page)
    {
        var media = page.MediaBox;
        var crop = page.CropBox;
        if (media == null || crop == null || media.Count < 4 || crop.Count < 4) return false;

        double N(Tetitulz.Pdf.Core.Objects.PdfArray a, int i)
            => (a[i] as Tetitulz.Pdf.Core.Objects.PdfNumber)?.DoubleValue ?? 0;

        const double Epsilon = 0.01;
        return N(crop, 0) > N(media, 0) + Epsilon || N(crop, 1) > N(media, 1) + Epsilon
            || N(crop, 2) < N(media, 2) - Epsilon || N(crop, 3) < N(media, 3) - Epsilon;
    }

    /// <summary>Operators that put marks on the page (PDF §8.5.3, §9.4.3, §8.7, §8.8).</summary>
    private static readonly HashSet<string> PaintingOperators = new(StringComparer.Ordinal)
    {
        "S", "s", "f", "F", "f*", "B", "B*", "b", "b*",   // path painting
        "Tj", "TJ", "'", "\"",                            // text showing
        "Do",                                             // XObjects (image / form)
        "sh",                                             // shadings
        "BI",                                             // inline images
        "d0", "d1",                                       // Type 3 glyph procedures
    };

    /// <summary>
    /// Counts painting operators in a content stream. Tokenises rather than string-searching so
    /// that operator names appearing inside <c>(string)</c> or <c>&lt;hex&gt;</c> operands — or as
    /// part of a <c>/Name</c> — are not miscounted. The library's own parser is <c>internal</c>,
    /// and duplicating it here is deliberate: this probe must stay independent of the code under
    /// test, or a parser regression would hide itself from the assertion.
    /// </summary>
    internal static int CountPaintingOperators(byte[] content)
        => CountOperators(content, PaintingOperators);

    /// <summary>
    /// Counts occurrences of <paramref name="wanted"/> as <b>operators</b> in a content stream —
    /// the shared token walk behind <see cref="CountPaintingOperators"/> and the text-showing count
    /// the extraction smoke test needs.
    /// </summary>
    /// <remarks>
    /// <para>Generalised from the painting-operator counter rather than copied. The walk is the hard
    /// part and it is all edge cases: comments, literal strings with nesting and backslash escapes,
    /// hex strings, name objects, and an inline image whose binary payload between <c>ID</c> and
    /// <c>EI</c> must not be tokenised at all. A second copy would have to get every one of those
    /// right again, and this repository's standing rule is that the second copy is where the
    /// divergence lives.</para>
    /// <para>The numeric guard matters: <c>f</c> is a painting operator and <c>0.5</c> is not an
    /// operator at all, but both survive the tokeniser as bare words.</para>
    /// </remarks>
    internal static int CountOperators(byte[] content, HashSet<string> wanted)
    {
        if (content == null || content.Length == 0) return 0;
        string s = Encoding.Latin1.GetString(content);

        int count = 0, i = 0;
        while (i < s.Length)
        {
            char c = s[i];

            if (c == '%') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '(')                                  // literal string: nesting + \-escapes
            {
                int depth = 1; i++;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '\\') i++;
                    else if (s[i] == '(') depth++;
                    else if (s[i] == ')') depth--;
                    i++;
                }
                continue;
            }
            if (c == '<') { while (i < s.Length && s[i] != '>') i++; i++; continue; }
            if (c == '/')                                  // name object — an operand
            {
                i++;
                while (i < s.Length && !IsDelimiter(s[i])) i++;
                continue;
            }
            if (IsDelimiter(c)) { i++; continue; }

            int start = i;
            while (i < s.Length && !IsDelimiter(s[i])) i++;
            string token = s.Substring(start, i - start);

            if (wanted.Contains(token) &&
                !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                count++;
                // An inline image's binary payload runs from ID to EI and must not be tokenised.
                if (token == "BI")
                {
                    int ei = s.IndexOf("EI", i, StringComparison.Ordinal);
                    i = ei < 0 ? s.Length : ei + 2;
                }
            }
        }
        return count;
    }

    private static bool IsDelimiter(char c)
        => char.IsWhiteSpace(c) || c == '/' || c == '[' || c == ']' || c == '(' || c == ')'
        || c == '<' || c == '>' || c == '{' || c == '}' || c == '%';

    /// <summary>
    /// True when every sampled pixel is white or transparent. Samples on a 2 px grid: enough to
    /// find any real content, and 4x cheaper over a book-length corpus. A hairline thinner than
    /// 2 px could in principle slip through — acceptable for a smoke test whose failure mode is
    /// a page that renders *entirely* empty.
    /// </summary>
    internal static bool IsBlank(SKBitmap bmp)
    {
        if (bmp == null) return true;
        for (int y = 0; y < bmp.Height; y += 2)
            for (int x = 0; x < bmp.Width; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha > 8 && (c.Red < 245 || c.Green < 245 || c.Blue < 245)) return false;
            }
        return true;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length > max ? s.Substring(0, max - 3) + "..." : s;
    }
}
