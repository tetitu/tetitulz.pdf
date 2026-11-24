// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The two memory ceilings the auto-tagger applies per page, driven entirely through the public
/// API (<see cref="PdfAccessibility.AutoTag(Stream,Stream,AutoTagOptions)"/> plus
/// <see cref="AutoTagOptions"/>), plus the pre-decode half of the concatenation guard.
///
/// <para>
/// <b>Why these tests exist.</b> Two defects, closed together because they are two halves of one
/// story.
/// </para>
///
/// <list type="number">
///   <item><description><b>The concatenation ceiling was consulted too late.</b>
///     <c>ContentStreamTagger.ConcatenatePageContent</c> measured a single-stream <c>/Contents</c>
///     only after <c>PdfStream.GetDecodedData()</c> had returned it, so one hostile page could
///     transiently allocate whatever Core lets a stream inflate to (512 MiB) before any limit was
///     looked at. Measuring an allocation you have already made is not a guard.</description></item>
///   <item><description><b>Neither ceiling was reachable.</b> Both lived on the internal
///     <c>PageTagOptions</c>, so a host that legitimately processes very large pages could not raise
///     them and a host taking untrusted upload could not lower them. A safety limit nobody can
///     adjust is a support ticket waiting to happen.</description></item>
/// </list>
///
/// <para>
/// Every fixture here is a few hundred bytes (or a few kilobytes of Flate), and the ceilings are
/// lowered through the options instead of the documents being grown to meet them — which is the
/// point of the options existing.
/// </para>
/// </summary>
public class PublicCeilingOptionsTests
{
    // =====================================================================
    //  PDF assembly — self-contained, in memory
    // =====================================================================

    private static byte[] BuildPdf(IReadOnlyList<byte[]> bodies)
    {
        var ms = new MemoryStream();

        void Write(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        Write("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = (int)ms.Length;
            Write((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            ms.Write(bodies[i], 0, bodies[i].Length);
            Write("\nendobj\n");
        }

        int xref = (int)ms.Length;
        int size = bodies.Count + 1;

        Write("xref\n0 " + size.ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        for (int i = 0; i < offsets.Length; i++)
            Write(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

        Write("trailer\n<< /Size " + size.ToString(CultureInfo.InvariantCulture) + " /Root 1 0 R >>\n");
        Write("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>
    /// The page every non-bomb test uses: a 36 pt heading and three 10 pt body lines on a
    /// 612×792 page, all well inside the 50 pt header/footer bands so nothing becomes page
    /// furniture. Under two hundred bytes of content, so a ceiling set from
    /// <c>PageContent.Length</c> sits four orders of magnitude below the 4 MiB default and a
    /// refusal can therefore only be coming from the option.
    /// </summary>
    private const string PageContent =
        "BT /F1 36 Tf 72 700 Td (Chapter One) Tj ET\n" +
        "BT /F1 10 Tf 72 640 Td (Body line one.) Tj ET\n" +
        "BT /F1 10 Tf 72 628 Td (Body line two.) Tj ET\n" +
        "BT /F1 10 Tf 72 616 Td (Body line three.) Tj ET\n";

    /// <summary>One untagged page whose <c>/Contents</c> is a single uncompressed stream.</summary>
    private static byte[] TextPagePdf() => BuildPdf(new[]
    {
        Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
        Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
              + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
        Ascii("<< /Length " + PageContent.Length.ToString(CultureInfo.InvariantCulture)
              + " >>\nstream\n" + PageContent + "\nendstream"),
        Ascii(HelveticaFont),
    });

    /// <summary>
    /// The adversarial fixture: a page whose <c>/Contents</c> is a few kilobytes of Flate on disk
    /// and <paramref name="decodedBytes"/> once inflated.
    ///
    /// <para>
    /// It declares <c>/DL</c> — "a non-negative integer representing the number of bytes in the
    /// decoded (defiltered) stream", ISO 32000-1 Table 5 — which is exactly the signal the
    /// pre-decode guard is built on and the reason this test can assert that the refusal happened
    /// <em>without</em> inflating anything. That is also the honest limit of the guard: a filtered
    /// stream that declares nothing cannot have its decoded size predicted from the file, and is
    /// still inflated once (bounded by Core's 512 MiB) before the post-decode check refuses it.
    /// Closing that would need a bounded decode in Core, which this assembly does not own.
    /// </para>
    /// </summary>
    private static byte[] FlateBombPagePdf(int decodedBytes)
        => FlateBombPagePdf(decodedBytes, declaredDecodedLength: decodedBytes);

    /// <summary>
    /// The same fixture with a caller-chosen <c>/DL</c>, so the guard can also be tested against a
    /// stream that <em>lies</em> about its decoded size.
    /// </summary>
    private static byte[] FlateBombPagePdf(int decodedBytes, int declaredDecodedLength)
    {
        var payload = new byte[decodedBytes];
        // Spaces, not zeros: the result still has to be a legal content stream, so that a run which
        // wrongly decodes it gets all the way through and the test measures the real cost of the
        // miss rather than an early parse failure.
        Array.Fill(payload, (byte)' ');
        byte[] text = Ascii(PageContent);
        Buffer.BlockCopy(text, 0, payload, 0, text.Length);

        byte[] compressed = ZlibCompress(payload);

        var stream = new MemoryStream();
        byte[] header = Ascii(
            "<< /Filter /FlateDecode /Length " + compressed.Length.ToString(CultureInfo.InvariantCulture)
            + " /DL " + declaredDecodedLength.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        stream.Write(header, 0, header.Length);
        stream.Write(compressed, 0, compressed.Length);
        byte[] tail = Ascii("\nendstream");
        stream.Write(tail, 0, tail.Length);

        return BuildPdf(new[]
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                  + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            stream.ToArray(),
            Ascii(HelveticaFont),
        });
    }

    /// <summary>zlib (RFC 1950) wrapper, which is what <c>/FlateDecode</c> expects.</summary>
    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    // =====================================================================
    //  Read-back helpers — everything is asserted against the parsed graph
    // =====================================================================

    private static byte[] RunAutoTag(byte[] input, AutoTagOptions options, out AutoTagResult result)
    {
        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        result = PdfAccessibility.AutoTag(inStream, outStream, options);
        return outStream.ToArray();
    }

    private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
        => obj == null ? null : doc.ResolveObject(obj);

    private static PdfDictionary CatalogOf(PdfDocument doc) => doc.Catalog?.Dictionary;

    /// <summary>Decodes the (possibly array-valued) /Contents of a page.</summary>
    private static byte[] DecodePageContent(PdfDocument doc, int pageIndex)
    {
        PdfDictionary page = doc.Pages[pageIndex].Dictionary;
        PdfObject contents = Resolve(doc, page[new PdfName("Contents")]);

        var buffer = new MemoryStream();
        if (contents is PdfArray arr)
        {
            foreach (PdfObject part in arr)
            {
                if (Resolve(doc, part) is PdfStream ps)
                {
                    byte[] d = ps.GetDecodedData();
                    buffer.Write(d, 0, d.Length);
                    buffer.WriteByte((byte)'\n');
                }
            }
        }
        else if (contents is PdfStream s)
        {
            byte[] d = s.GetDecodedData();
            buffer.Write(d, 0, d.Length);
        }
        return buffer.ToArray();
    }

    private static bool MarkedTrue(PdfDocument doc, PdfDictionary catalog)
    {
        var markInfo = Resolve(doc, catalog?[new PdfName("MarkInfo")]) as PdfDictionary;
        return Resolve(doc, markInfo?[new PdfName("Marked")]) is PdfBoolean b && b.Value;
    }

    /// <summary>
    /// The whole "nothing partial was emitted" claim, asserted in one place: the input bytes are
    /// still a prefix of the output (an accessibility edit only ever appends a revision), the page's
    /// decoded content stream is byte-for-byte what went in, it carries no marked-content operator,
    /// no structure tree was written, and the document does not claim to be a Tagged PDF.
    /// </summary>
    private static void AssertUntouchedAndUnclaimed(byte[] input, byte[] output)
    {
        Assert.True(output.Length >= input.Length,
            "an incremental update may only append; the output is shorter than the input.");
        for (int i = 0; i < input.Length; i++)
        {
            Assert.True(input[i] == output[i],
                "the output diverges from the input at byte " + i.ToString(CultureInfo.InvariantCulture) +
                "; a refused page must be left exactly as it was found.");
        }

        byte[] originalContent;
        using (var before = PdfDocument.Open(new MemoryStream(input)))
            originalContent = DecodePageContent(before, 0);

        using var after = PdfDocument.Open(new MemoryStream(output));
        byte[] producedContent = DecodePageContent(after, 0);

        Assert.Equal(originalContent.Length, producedContent.Length);
        for (int i = 0; i < originalContent.Length; i++)
        {
            Assert.True(originalContent[i] == producedContent[i],
                "the refused page's content stream differs at byte " +
                i.ToString(CultureInfo.InvariantCulture) + ".");
        }

        string text = Encoding.ASCII.GetString(
            producedContent, 0, Math.Min(producedContent.Length, 8192));
        Assert.DoesNotContain("BDC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/MCID", text, StringComparison.Ordinal);

        PdfDictionary catalog = CatalogOf(after);
        Assert.NotNull(catalog);
        Assert.Null(catalog[new PdfName("StructTreeRoot")]);
        Assert.False(MarkedTrue(after, catalog),
            "a document whose only page was refused must not claim to be a Tagged PDF.");
    }

    private static string OnlySkipReason(AutoTagResult result)
    {
        Assert.Equal(1, result.PagesProcessed);
        Assert.Equal(1, result.PagesSkipped);
        return Assert.Single(result.SkippedPageReasons);
    }

    /// <summary>Everything a refusal must leave false, so a resource limit never becomes a false claim.</summary>
    private static void AssertNothingWasClaimed(AutoTagResult result)
    {
        Assert.False(result.ContentMarked);
        Assert.Equal(0, result.McidCount);
        Assert.Equal(0, result.ElementsAdded);
        Assert.Equal(0, result.PagesTagged);
    }

    // =====================================================================
    //  1. The defaults — pinned, because a silent change moves every caller
    // =====================================================================

    /// <summary>
    /// Both ceilings were internal constants before they were options, and the whole point of
    /// surfacing them was that existing behaviour stays identical. These are the shipped numbers;
    /// changing either one changes what every caller who never touches the options gets, so it has
    /// to be a deliberate edit here rather than a side effect somewhere else.
    /// </summary>
    [Fact]
    public void AutoTagOptions_Defaults_AreExactlyTheShippedCeilings()
    {
        var options = new AutoTagOptions();

        Assert.Equal(4 * 1024 * 1024, options.MaxPageContentBytes);
        Assert.Equal(512L * 1024 * 1024, options.MaxConcatenatedContentBytes);

        // …and they are the same numbers the internal ceilings still carry, so the public surface
        // cannot drift away from the guard it configures.
        Assert.Equal(ContentStreamTagger.DefaultMaxContentBytes, options.MaxPageContentBytes);
        Assert.Equal(ContentStreamTagger.MaxConcatenatedContentBytes, options.MaxConcatenatedContentBytes);

        var pageOptions = new PageTagOptions();
        Assert.Equal(options.MaxPageContentBytes, pageOptions.MaxContentBytes);
        Assert.Equal(options.MaxConcatenatedContentBytes, pageOptions.MaxConcatenatedContentBytes);
    }

    /// <summary>
    /// The negative control for everything below: under the defaults this fixture tags normally.
    /// Without it, a refusal test could be green because the document is untaggable rather than
    /// because a ceiling stopped it.
    /// </summary>
    [Fact]
    public void AutoTag_DefaultCeilings_TagTheFixtureNormally()
    {
        byte[] input = TextPagePdf();

        byte[] output = RunAutoTag(input,
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        Assert.True(result.ContentMarked,
            "the fixture must tag under the defaults. Skips: " + string.Join(" | ", result.SkippedPageReasons));
        Assert.Equal(0, result.PagesSkipped);
        Assert.True(result.McidCount > 0);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Contains("/MCID", Encoding.ASCII.GetString(DecodePageContent(doc, 0)), StringComparison.Ordinal);
    }

    // =====================================================================
    //  2. MaxPageContentBytes — the analysis ceiling
    // =====================================================================

    /// <summary>
    /// A page over <see cref="AutoTagOptions.MaxPageContentBytes"/> is skipped whole, and the
    /// skip is <em>visible</em>: the reason names the page, the page's real size and the limit that
    /// stopped it.
    ///
    /// <para>
    /// The limit used here is one byte short of the page and four orders of magnitude below the
    /// default, so a refusal can only be coming from the option. That is the assertion that the
    /// property is wired through to <c>PageTagOptions</c> and not merely stored on a POCO.
    /// </para>
    /// </summary>
    [Fact]
    public void AutoTag_PageOverTheConfiguredPageCeiling_IsSkippedWithAReasonNamingTheLimit()
    {
        // Derived from the fixture rather than written as a literal, so the test cannot rot into
        // "the ceiling happens to sit above the page" if the fixture's text is ever edited.
        int ceiling = PageContent.Length - 1;
        Assert.True(ceiling < new AutoTagOptions().MaxPageContentBytes / 1000,
            "the ceiling under test must be far below the default, so a refusal can only come from the option.");

        byte[] input = TextPagePdf();
        byte[] output = RunAutoTag(input,
            new AutoTagOptions { MaxPageContentBytes = ceiling, CompressRewrittenStreams = false },
            out AutoTagResult result);

        string reason = OnlySkipReason(result);
        Assert.Contains("Page 1", reason, StringComparison.Ordinal);
        Assert.Contains(ceiling.ToString(CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
        Assert.Contains(PageContent.Length.ToString(CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
        Assert.Contains("limit", reason, StringComparison.OrdinalIgnoreCase);

        AssertNothingWasClaimed(result);
        AssertUntouchedAndUnclaimed(input, output);
    }

    /// <summary>
    /// The other direction, and the real proof the option is a lever rather than a decoration: the
    /// same bytes that were refused one byte short tag successfully once the ceiling reaches the
    /// page. A stored-but-ignored property would make this test pass and the one above fail; a
    /// hard-coded ceiling would make this one pass and the one above fail too — only a wired option
    /// passes both. It also pins the ceiling as <em>inclusive</em>.
    /// </summary>
    [Fact]
    public void AutoTag_RaisingThePageCeiling_TagsTheDocumentThatWasRefused()
    {
        byte[] input = TextPagePdf();

        RunAutoTag(input,
            new AutoTagOptions
            {
                MaxPageContentBytes      = PageContent.Length - 1,
                CompressRewrittenStreams = false,
            },
            out AutoTagResult refused);
        Assert.False(refused.ContentMarked);

        byte[] output = RunAutoTag(input,
            new AutoTagOptions { MaxPageContentBytes = PageContent.Length, CompressRewrittenStreams = false },
            out AutoTagResult allowed);

        Assert.True(allowed.ContentMarked,
            "raising the ceiling to the page's exact size must let it tag. Skips: " +
            string.Join(" | ", allowed.SkippedPageReasons));
        Assert.Equal(0, allowed.PagesSkipped);
        Assert.True(allowed.McidCount > 0);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Contains("/MCID", Encoding.ASCII.GetString(DecodePageContent(doc, 0)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-positive ceiling is an opt-out, not "refuse everything" — the difference matters,
    /// because the naive reading of <c>length &gt; limit</c> would silently stop tagging every page
    /// for a caller who wrote <c>0</c> meaning "no limit".
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AutoTag_NonPositivePageCeiling_DisablesTheCeilingRatherThanRefusingEveryPage(int ceiling)
    {
        byte[] input = TextPagePdf();

        RunAutoTag(input,
            new AutoTagOptions { MaxPageContentBytes = ceiling, CompressRewrittenStreams = false },
            out AutoTagResult result);

        Assert.True(result.ContentMarked,
            "a non-positive ceiling must disable the limit. Skips: " +
            string.Join(" | ", result.SkippedPageReasons));
    }

    // =====================================================================
    //  3. MaxConcatenatedContentBytes — the decode ceiling
    // =====================================================================

    /// <summary>
    /// The earlier ceiling, and — for an unfiltered stream — the one that proves defect 1 is closed:
    /// the page is refused <em>before</em> <c>GetDecodedData()</c> is called at all, which the
    /// refusal reason says in so many words. An unfiltered stream's raw length <em>is</em> its
    /// decoded length, so this is knowable for free and there is no excuse for measuring it after
    /// the allocation.
    /// </summary>
    [Fact]
    public void AutoTag_PageOverTheConfiguredDecodeCeiling_IsRefusedBeforeItIsDecoded()
    {
        int ceiling = PageContent.Length / 2;
        Assert.True(ceiling > 0 && PageContent.Length > ceiling,
            "the fixture must actually exceed the ceiling under test.");

        byte[] input = TextPagePdf();
        byte[] output = RunAutoTag(input,
            new AutoTagOptions { MaxConcatenatedContentBytes = ceiling, CompressRewrittenStreams = false },
            out AutoTagResult result);

        string reason = OnlySkipReason(result);
        Assert.Contains("Page 1", reason, StringComparison.Ordinal);
        Assert.Contains(ceiling.ToString(CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
        Assert.Contains("decodes to more than", reason, StringComparison.Ordinal);
        Assert.Contains("refused before being decoded", reason, StringComparison.Ordinal);

        AssertNothingWasClaimed(result);
        AssertUntouchedAndUnclaimed(input, output);
    }

    /// <summary>Raising the decode ceiling past the page lets the same document tag.</summary>
    [Fact]
    public void AutoTag_RaisingTheDecodeCeiling_TagsTheDocumentThatWasRefused()
    {
        byte[] input = TextPagePdf();

        RunAutoTag(input,
            new AutoTagOptions
            {
                MaxConcatenatedContentBytes = PageContent.Length / 2,
                CompressRewrittenStreams    = false,
            },
            out AutoTagResult refused);
        Assert.False(refused.ContentMarked);

        RunAutoTag(input,
            new AutoTagOptions
            {
                MaxConcatenatedContentBytes = PageContent.Length,
                CompressRewrittenStreams    = false,
            },
            out AutoTagResult allowed);

        Assert.True(allowed.ContentMarked,
            "raising the decode ceiling to the page's exact size must let it tag. Skips: " +
            string.Join(" | ", allowed.SkippedPageReasons));
        Assert.True(allowed.McidCount > 0);
    }

    // =====================================================================
    //  4. Defect 1 proper — a compression bomb is refused without inflating it
    // =====================================================================

    /// <summary>
    /// Total decoded size of the bomb fixture. Large enough that inflating it is unmissable in the
    /// allocation trace, small enough that a regression costs the suite one 64 MiB array rather
    /// than wedging the machine.
    /// </summary>
    private const int BombDecodedBytes = 64 * 1024 * 1024;

    /// <summary>Ceiling the bomb is run under — four orders of magnitude below its decoded size.</summary>
    private const int BombCeilingBytes = 64 * 1024;

    /// <summary>
    /// <b>The defect, end to end.</b> A page that is a few hundred kilobytes of Flate on disk and
    /// 64 MiB once inflated must be refused without ever being inflated.
    ///
    /// <para>
    /// Calling <c>GetDecodedData()</c> first and only then comparing the length against the
    /// ceiling would make this fixture allocate its full 64 MiB (and a real one 512 MiB, Core's
    /// per-stream decode limit) before anything declined it. Allocated bytes is the primary
    /// assertion because it is deterministic and
    /// machine-independent — the budget sits four times under the fixture's decoded size and many
    /// times over what a refusal costs — with elapsed time as a coarse second signal, since a
    /// 64 MiB inflate is not free either.
    /// </para>
    ///
    /// <para>
    /// What is <em>not</em> claimed: that every bomb is caught this early. The fixture declares
    /// <c>/DL</c>, and that is the signal the guard reads. A filtered stream that declares nothing
    /// is unpredictable by construction and is still inflated once, bounded by Core's 512 MiB, before
    /// the post-decode check refuses it. See
    /// <c>ContentStreamTagger.TryGetKnownDecodedLength</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void AutoTag_CompressedBombPage_IsRefusedWithoutInflatingIt()
    {
        // Warm-up on an ordinary document so first-run statics and cached tables are not charged to
        // the measurement below.
        RunAutoTag(TextPagePdf(), new AutoTagOptions(), out _);

        byte[] input = FlateBombPagePdf(BombDecodedBytes);
        Assert.True(input.Length < 2 * 1024 * 1024,
            "the fixture must be small on disk — that asymmetry is the whole attack.");

        var options = new AutoTagOptions
        {
            MaxConcatenatedContentBytes = BombCeilingBytes,
            CompressRewrittenStreams    = false,
        };

        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        byte[] output = RunAutoTag(input, options, out AutoTagResult result);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // ---- refused, and refused honestly -------------------------------------------
        string reason = OnlySkipReason(result);
        Assert.Contains("Page 1", reason, StringComparison.Ordinal);
        Assert.Contains(BombCeilingBytes.ToString(CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
        Assert.Contains("refused before being decoded", reason, StringComparison.Ordinal);
        AssertNothingWasClaimed(result);

        // ---- nothing partial was emitted ---------------------------------------------
        Assert.True(output.Length >= input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            Assert.True(input[i] == output[i],
                "the output diverges from the input at byte " + i.ToString(CultureInfo.InvariantCulture) + ".");
        }
        using (var after = PdfDocument.Open(new MemoryStream(output)))
        {
            PdfDictionary catalog = CatalogOf(after);
            Assert.NotNull(catalog);
            Assert.Null(catalog[new PdfName("StructTreeRoot")]);
            Assert.False(MarkedTrue(after, catalog));
        }

        // ---- and the bomb was never inflated -----------------------------------------
        const long Budget = 24L * 1024 * 1024;
        Assert.True(
            allocated < Budget,
            "Refusing a page declaring " + BombDecodedBytes.ToString(CultureInfo.InvariantCulture) +
            " decoded bytes allocated " + allocated.ToString(CultureInfo.InvariantCulture) +
            " bytes, over the " + Budget.ToString(CultureInfo.InvariantCulture) +
            "-byte budget — the stream is being decompressed before its size is checked.");

        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(20),
            "Refusing the page took " + watch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) +
            " s, which is not the cost of reading /DL and comparing two integers.");
    }

    /// <summary>
    /// The same guard at the decoder itself, isolated from the auto-tagger so the measurement covers
    /// nothing but the decode decision.
    ///
    /// <para>
    /// The budget is 4 MiB rather than something tiny on purpose: resolving <c>/Contents</c> may
    /// materialise the part's <em>raw</em> (still-compressed) bytes, which are a few hundred
    /// kilobytes here and are not what this test is about. 4 MiB is comfortably above that and
    /// sixteen times below the 64 MiB the stream would cost if it were inflated, so the assertion
    /// still fails loudly the moment the guard stops firing.
    /// </para>
    /// </summary>
    [Fact]
    public void ConcatenatePageContent_DeclaredDecodedSizeOverTheCeiling_RefusesWithoutInflating()
    {
        byte[] pdf = FlateBombPagePdf(BombDecodedBytes);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfDictionary pageDict = doc.Pages[0].Dictionary;

        // Warm-up: the first call through this path resolves /Contents and touches statics.
        ContentStreamTagger.ConcatenatePageContent(doc, pageDict, BombCeilingBytes);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] content = ContentStreamTagger.ConcatenatePageContent(doc, pageDict, BombCeilingBytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(content);
        Assert.NotNull(ContentStreamTagger.LastTagError);
        Assert.Contains("refused before being decoded", ContentStreamTagger.LastTagError,
                        StringComparison.Ordinal);

        const long Budget = 4L * 1024 * 1024;
        Assert.True(
            allocated < Budget,
            "Refusing a stream declaring " + BombDecodedBytes.ToString(CultureInfo.InvariantCulture) +
            " decoded bytes allocated " + allocated.ToString(CultureInfo.InvariantCulture) +
            " bytes, over the " + Budget.ToString(CultureInfo.InvariantCulture) +
            "-byte budget — it is being inflated before its declared size is consulted.");
    }

    /// <summary>
    /// The counterpart that keeps the guard from being a blanket refusal: the same kind of page, run
    /// under a ceiling at its declared size, really is decoded and returned in full. Without this
    /// the pre-decode check could be refusing every filtered stream and every test above would still
    /// be green.
    /// </summary>
    [Fact]
    public void ConcatenatePageContent_DeclaredDecodedSizeUnderTheCeiling_StillDecodesTheStream()
    {
        // Small enough to decode in the test, still a genuine /DL-declaring Flate stream.
        const int decoded = 32 * 1024;
        byte[] pdf = FlateBombPagePdf(decoded);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        byte[] content = ContentStreamTagger.ConcatenatePageContent(
            doc, doc.Pages[0].Dictionary, decoded);

        Assert.NotNull(content);
        Assert.Equal(decoded, content.Length);
        Assert.Null(ContentStreamTagger.LastTagError);
        Assert.Equal("BT /F1 36 Tf", Encoding.ASCII.GetString(content, 0, 12));
    }

    /// <summary>
    /// An under-declared <c>/DL</c> must not become a way past the ceiling. The stream says it
    /// decodes to 1 byte and actually decodes to 32 KiB, so the pre-decode check lets it through —
    /// and it is refused anyway.
    /// </summary>
    /// <remarks>
    /// <para><b>Refused during the decode.</b> <c>PdfStream.TryGetDecodedData</c> stops the filter
    /// at the budget (GAPS.md §5c), so the 32 KiB is never allocated against the 4 KiB ceiling;
    /// <c>TryAccumulate</c> stays as the backstop after decoding.</para>
    ///
    /// <para>The invariant under test is the one that matters: <b>under-declaring <c>/DL</c> gains
    /// an attacker nothing</b>. The assertion does not pin which gate fired — pinning that would be
    /// pinning the implementation.</para>
    /// </remarks>
    [Fact]
    public void ConcatenatePageContent_UnderDeclaredDecodedSize_IsStillRefused()
    {
        const int decoded = 32 * 1024;
        byte[] pdf = FlateBombPagePdf(decoded, declaredDecodedLength: 1);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        byte[] content = ContentStreamTagger.ConcatenatePageContent(
            doc, doc.Pages[0].Dictionary, 4096);

        Assert.Null(content);
        Assert.NotNull(ContentStreamTagger.LastTagError);
    }
}
