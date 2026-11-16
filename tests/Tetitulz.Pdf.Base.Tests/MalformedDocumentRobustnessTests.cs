// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Corrupted PDFs are <b>refused</b>, never met with an escaping runtime exception: a malformed
/// file may produce a <c>PdfException</c>, a filter or budget refusal, or a degraded document — but
/// never a <see cref="NullReferenceException"/>, an index error or a bad cast out of the public
/// entry points.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> This library's whole job is consuming hostile input, and pure
/// managed code removes the memory-corruption class but not this one. An
/// <c>IndexOutOfRangeException</c> escaping <c>PdfDocument.Open</c> is a denial of service for any
/// host that did not wrap every call in <c>catch (Exception)</c> — and the documented contract says
/// it should be catching <c>PdfException</c>.</para>
///
/// <para><b>What it is and is not.</b> This is a deterministic corruption sweep, not a fuzzer. It
/// takes documents the library itself wrote, mutates them at a fixed seed, and asserts the refusal
/// contract holds. A coverage-guided fuzzer (AFL++ via SharpFuzz) explores far more of the input
/// space and would be a better investment; this runs in CI on every platform, in seconds, and
/// costs nothing to keep. The two are complementary and this one is not a substitute.</para>
///
/// <para><b>The assertion is the exception TYPE, not the absence of exceptions.</b> Refusing a
/// broken file is correct behaviour and must stay possible, so a test demanding "no throw" would
/// forbid the right answer. What is asserted is that failure arrives through the documented
/// channel.</para>
///
/// <para><b>Every failure names its mutation.</b> Offset, original byte and replacement are in the
/// message, so a red run is reproducible without re-running the sweep.</para>
/// </remarks>
public class MalformedDocumentRobustnessTests
{
    /// <summary>
    /// Failure modes a malformed document is <b>allowed</b> to produce. Everything else indicates
    /// an internal invariant broke rather than the parser refusing the input.
    /// </summary>
    /// <remarks>
    /// <para><c>IOException</c> and <c>EndOfStreamException</c> are here because truncation is one
    /// of the mutations: a stream that ends early is a legitimate I/O failure, not a logic
    /// error.</para>
    /// <para><b><c>PdfFilterException</c> and <c>WorkBudgetExceededException</c> used to need their
    /// own arms and no longer do</b> — they derive from <c>PdfException</c> now, and the compiler
    /// said so by calling the arms unreachable. That this list could shrink is the point of the
    /// change: a caller no longer has to enumerate the library's exception types to catch them.</para>
    /// </remarks>
    private static bool IsDocumentedRefusal(Exception ex) => ex switch
    {
        Tetitulz.Pdf.Core.PdfException => true,
        EndOfStreamException => true,
        IOException => true,
        NotSupportedException => true,   // an unimplemented-by-design branch, reported not crashed
        OutOfMemoryException => true,    // the host's limit, not a defect in the refusal contract
        _ => false,
    };

    /// <summary>
    /// A multi-page document carrying text, vector art, <b>an embedded TrueType program</b> and
    /// <b>a raster image</b> — written by the library itself.
    /// </summary>
    /// <remarks>
    /// <para><b>The font and the image are the point, not decoration.</b> A text-and-vector
    /// document never reaches the font parser or the image decoders, and those are the two highest
    /// -risk surfaces in a PDF library: a hand-written sfnt reader and a hand-written raster
    /// pipeline, both fed entirely by attacker-controlled bytes. Corrupting a document that lacks
    /// them measures the object parser and calls it coverage.</para>
    ///
    /// <para>Embedding also means a corrupted byte can land <i>inside</i> the compressed font or
    /// image stream, so the mutation survives inflation and reaches the decoder as a structurally
    /// plausible but wrong table — which is exactly the input those decoders are least often given
    /// by a test suite.</para>
    /// </remarks>
    private static byte[] BaseDocument()
    {
        var builder = new PdfDocumentBuilder();

        // A real sfnt, so /FontFile2 carries a program the font reader must actually parse.
        //
        // The glyphs have to be NOTED or the subset is empty and nothing is embedded at all — which
        // is how the first version of this fixture silently failed to reach the font parser while
        // every sweep still passed. TheBaseDocumentReallyEmbedsAFontAndAnImage is what caught it.
        const string embeddedText = "Robust0123";
        byte[] fontBytes = MinimalTrueType.Build(embeddedText);
        var embedded = builder.AddEmbeddedFont(TrueTypeFontProgram.Parse(fontBytes));

        Dictionary<int, int> cmap = MinimalTrueType.ReadCmap(fontBytes);
        var gids = new List<ushort>();
        foreach (char c in embeddedText)
        {
            if (!cmap.TryGetValue(c, out int gid) || gid == 0) continue;
            embedded.Note((ushort)gid, c);
            gids.Add((ushort)gid);
        }

        for (int i = 0; i < 3; i++)
        {
            PdfContentStreamBuilder page = builder.AddPage(612, 792);

            page.BeginText().SetFont(page.AddStandardFont("Helvetica"), 12)
                .SetTextPosition(72, 720 - i * 14)
                .ShowText("Robustness sweep page " + i).EndText();

            // Identity-H: the 2-byte codes ARE glyph ids, so this shows through the embedded
            // program rather than through a standard-14 substitute.
            page.UseEmbeddedFont(embedded.ResourceName);
            page.BeginText().SetFont(embedded.ResourceName, 14)
                .SetTextPosition(72, 680).ShowGlyphs(gids.ToArray()).EndText();

            page.SetLineWidth(2).MoveTo(72, 100).LineTo(400, 300).Stroke();

            // A 16x16 RGB raster, Flate-encoded by the builder: the image decode path.
            byte[] samples = new byte[16 * 16 * 3];
            for (int p = 0; p < samples.Length; p++) samples[p] = (byte)(p * 7 % 251);
            string img = page.AddImage(samples, 16, 16);
            page.DrawImage(img, 400, 600, 120, 120);
        }

        using var ms = new MemoryStream();
        builder.Build(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Everything a host can reach with an untrusted file, in one place. Each is wrapped so the
    /// sweep reports which operation broke rather than only that something did.
    /// </summary>
    private static void ExerciseEntryPoints(byte[] pdf)
    {
        int pageCount;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false)))
        {
            pageCount = doc.Pages.Count;
            for (int i = 0; i < pageCount && i < 4; i++)
            {
                PdfPage page = doc.Pages[i];
                _ = page.GetContentStreamData();
                _ = page.MediaBox;
            }
        }

        _ = PdfOperations.ExtractText(new MemoryStream(pdf, writable: false));

        // ONLY render a page that exists. Corrupting an ` obj` marker yields a document that opens
        // with a catalog but no reachable pages, and asking such a document for page 0 is a CALLER
        // error — ArgumentOutOfRangeException is the correct answer to it, not a robustness defect.
        // Rendering unconditionally reported that correct behaviour as a finding on this file's
        // second run, which is the second time the sweep's first results were about the sweep.
        if (pageCount > 0)
            _ = PdfOperations.RenderPageToPng(new MemoryStream(pdf, writable: false), 0, 48);
    }

    /// <summary>
    /// Single-byte substitutions across the file. The classic corruption: it keeps the document
    /// structurally plausible, which is what reaches deep into the parser rather than being
    /// rejected at the header.
    /// </summary>
    [Fact]
    public void MalformedSingleByteMutations_FailOnlyThroughTheDocumentedChannel()
    {
        byte[] original = BaseDocument();
        var rng = new Random(20260830);
        var offenders = new List<string>();

        for (int trial = 0; trial < 600; trial++)
        {
            byte[] mutant = (byte[])original.Clone();
            int offset = rng.Next(mutant.Length);
            byte before = mutant[offset];
            byte after = (byte)rng.Next(256);
            mutant[offset] = after;

            try
            {
                ExerciseEntryPoints(mutant);
            }
            catch (Exception ex)
            {
                // Catch EVERYTHING and classify. Filtering the catch to offenders only lets a
                // documented refusal propagate and fail the test - which is the correct behaviour
                // being reported as a defect, and is exactly how this file failed on its first run.
                if (!IsDocumentedRefusal(ex))
                    offenders.Add(
                        $"offset {offset} (0x{before:X2} -> 0x{after:X2}): " +
                        $"{ex.GetType().Name}: {Shorten(ex.Message)}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} of 600 single-byte mutations escaped an undocumented exception. A "
            + "host following the documented contract catches PdfException; these reach it as "
            + "something else.\n  " + string.Join("\n  ", Head(offenders, 12)));
    }

    /// <summary>
    /// Truncation at every scale — the shape a partial download or a cut-off upload has, and the
    /// one that most often finds a read past the end of a buffer.
    /// </summary>
    [Fact]
    public void MalformedTruncation_FailsOnlyThroughTheDocumentedChannel()
    {
        byte[] original = BaseDocument();
        var offenders = new List<string>();

        // Every 1/64th, plus the first few bytes where the header itself is incomplete.
        var lengths = new List<int> { 0, 1, 2, 8, 32 };
        for (int i = 1; i < 64; i++) lengths.Add(original.Length * i / 64);

        foreach (int length in lengths)
        {
            byte[] mutant = new byte[length];
            Array.Copy(original, mutant, length);

            try
            {
                ExerciseEntryPoints(mutant);
            }
            catch (Exception ex)
            {
                if (!IsDocumentedRefusal(ex))
                    offenders.Add($"truncated to {length} of {original.Length}: "
                                + $"{ex.GetType().Name}: {Shorten(ex.Message)}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} truncations escaped an undocumented exception:\n  "
            + string.Join("\n  ", Head(offenders, 12)));
    }

    /// <summary>
    /// Corruption aimed at the structural keywords a parser keys off — <c>xref</c>, <c>trailer</c>,
    /// <c>obj</c>, <c>stream</c> and the <c>startxref</c> offset.
    /// </summary>
    /// <remarks>
    /// Random single bytes rarely land on these, so a uniform sweep under-tests exactly the
    /// machinery that decides how the rest of the file is read. This targets them directly, which
    /// is the same "ask the complementary question" move the PostScript rounds needed.
    /// </remarks>
    [Theory]
    [InlineData("xref")]
    [InlineData("trailer")]
    [InlineData("startxref")]
    [InlineData(" obj")]
    [InlineData("stream")]
    [InlineData("/Root")]
    [InlineData("/Length")]
    [InlineData("%PDF-")]
    public void MalformedStructuralKeyword_FailsOnlyThroughTheDocumentedChannel(string keyword)
    {
        byte[] original = BaseDocument();
        byte[] needle = Encoding.ASCII.GetBytes(keyword);
        var offenders = new List<string>();

        int found = 0;
        for (int i = 0; i + needle.Length <= original.Length; i++)
        {
            if (!Matches(original, i, needle)) continue;
            found++;

            byte[] mutant = (byte[])original.Clone();
            mutant[i] = (byte)'Z';           // break the keyword, keep every offset intact

            try
            {
                ExerciseEntryPoints(mutant);
            }
            catch (Exception ex)
            {
                if (!IsDocumentedRefusal(ex))
                    offenders.Add($"'{keyword}' at {i} broken: {ex.GetType().Name}: {Shorten(ex.Message)}");
            }
        }

        // A keyword the fixture never contains would make this vacuous.
        Assert.True(found > 0, $"the base document contains no '{keyword}', so nothing was tested");
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} of {found} '{keyword}' corruptions escaped an undocumented "
            + $"exception:\n  " + string.Join("\n  ", Head(offenders, 8)));
    }

    /// <summary>
    /// The floor: the base document must be a real, working PDF, or every sweep above is corrupting
    /// something that was already broken and passing for the wrong reason.
    /// </summary>
    [Fact]
    public void TheBaseDocumentIsValidBeforeAnyCorruption()
    {
        byte[] pdf = BaseDocument();

        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        Assert.Equal(3, doc.Pages.Count);

        string text = PdfOperations.ExtractText(new MemoryStream(pdf, writable: false));
        Assert.Contains("Robustness sweep page 0", text, StringComparison.Ordinal);

        byte[] png = PdfOperations.RenderPageToPng(new MemoryStream(pdf, writable: false), 0, 48);
        Assert.NotNull(png);
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int dark = 0;
        for (int y = 0; y < bmp!.Height; y += 2)
            for (int x = 0; x < bmp.Width; x += 2)
                if (bmp.GetPixel(x, y).Red + bmp.GetPixel(x, y).Green + bmp.GetPixel(x, y).Blue < 600)
                    dark++;

        Assert.True(dark > 20, $"the base document renders blank ({dark} dark pixels), so the "
                             + "corruption sweeps exercise nothing");
    }

    /// <summary>
    /// The base document really carries an embedded font program and an image XObject, so the
    /// sweeps reach the font parser and the image decoders rather than only the object parser.
    /// </summary>
    /// <remarks>
    /// <para><b>Without this the enrichment is unverifiable.</b> If <c>AddEmbeddedFont</c> or
    /// <c>AddImage</c> silently produced nothing, every sweep above would still pass — corrupting a
    /// plain text document and reporting a clean bill for decoders it never touched. That is this
    /// repository's most-recorded failure: a fixture that does not reach the feature under test,
    /// producing a plausible result for the wrong thing.</para>
    ///
    /// <para>Asserted on the written bytes rather than on the builder's return values, because what
    /// matters is what a corrupting sweep will actually find in the file.</para>
    /// </remarks>
    [Fact]
    public void TheBaseDocumentReallyEmbedsAFontAndAnImage()
    {
        byte[] pdf = BaseDocument();
        string raw = Encoding.Latin1.GetString(pdf);

        Assert.Contains("/FontFile2", raw, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Image", raw.Replace("/Subtype/Image", "/Subtype /Image"),
            StringComparison.Ordinal);

        // …and both are reachable as objects, not just present as bytes in some unused corner.
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        PdfDictionary resources = doc.Pages[0].Resources;
        Assert.NotNull(resources);

        var xobjects = doc.ResolveObject(resources[PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobjects);
        Assert.NotEmpty(xobjects!.Keys);

        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);

        // §9.7.1: a composite font keeps its descriptor on the DESCENDANT CIDFont, not on the Type0
        // dictionary the page names. Looking only at the named font finds nothing here — the
        // embedded program is one indirection further down, and this assertion failed for that
        // reason before it was written to follow the chain.
        bool anyEmbedded = false;
        foreach (PdfName key in fonts!.Keys)
        {
            if (doc.ResolveObject(fonts[key]) is not PdfDictionary font) continue;

            foreach (PdfDictionary candidate in DescriptorBearingFonts(doc, font))
            {
                if (doc.ResolveObject(candidate[PdfName.FontDescriptor]) is not PdfDictionary fd)
                    continue;
                if (doc.ResolveObject(fd[PdfName.FontFile2]) is PdfStream) anyEmbedded = true;
            }
        }

        Assert.True(anyEmbedded,
            "no /FontFile2 stream is reachable from page 0's fonts, so the corruption sweeps never "
            + "reach the sfnt parser and their clean result says nothing about it");
    }

    /// <summary>
    /// The font dictionary itself, plus any <c>/DescendantFonts</c> below it — the places §9.7.1
    /// allows a <c>/FontDescriptor</c> to live.
    /// </summary>
    private static IEnumerable<PdfDictionary> DescriptorBearingFonts(PdfDocument doc, PdfDictionary font)
    {
        yield return font;

        if (doc.ResolveObject(font[PdfName.DescendantFonts]) is not PdfArray descendants)
            yield break;

        for (int i = 0; i < descendants.Count; i++)
            if (doc.ResolveObject(descendants[i]) is PdfDictionary descendant)
                yield return descendant;
    }

    private static bool Matches(byte[] haystack, int at, byte[] needle)
    {
        for (int i = 0; i < needle.Length; i++)
            if (haystack[at + i] != needle[i]) return false;
        return true;
    }

    private static string Shorten(string s)
        => string.IsNullOrEmpty(s) ? "(no message)"
         : s.Length <= 110 ? s : s.Substring(0, 110) + "…";

    private static IEnumerable<string> Head(List<string> items, int n)
        => items.Count <= n ? items : items.GetRange(0, n);
}
