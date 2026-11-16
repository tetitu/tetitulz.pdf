// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Strong counterparts to the accessibility tests that assert only <c>output.Length &gt; 0</c> or
/// <c>result != null</c>.
///
/// <para>
/// <b>The problem being closed.</b> A test of the shape
/// <code>
/// var result = PdfAccessibility.AutoTag(input, output);
/// Assert.True(output.Length &gt; 0);
/// Assert.NotNull(result);
/// </code>
/// passes verbatim if the whole operation is replaced by <c>input.CopyTo(output)</c>. It reports
/// green whatever the feature does, which makes it worse than no test: it occupies the slot where
/// a real one would live. Every test in this file is written so that it would <b>fail</b> against
/// such a no-op — the produced PDF is re-opened and the specific structural change is asserted
/// (an MCID in the content stream, a <c>/ParentTree</c> key that resolves, a rewritten
/// <c>/S</c>, a <c>/Lang</c>, an <c>/ActualText</c>), or the operation is required to state
/// honestly that it did nothing and why.
/// </para>
///
/// <para>
/// Also here: the first end-to-end exercise of the per-page content-size ceilings. Those were
/// added as adversarial-input guards but nothing drove them through the public API, so nothing
/// checked that a refusal is <em>reported</em> rather than silently producing an untagged file
/// that claims to be tagged.
/// </para>
///
/// <para>
/// The existing weak tests are deliberately left in place — retiring them is a separate decision —
/// so a few assertions here overlap with <see cref="ContentBearingPdfTests"/>. Where that suite
/// already locks a behaviour down (the ParentTree round trip, artifact marking on a page that
/// draws text), this file does not repeat it.
/// </para>
/// </summary>
public class WeakAssertionUpgradeTests
{
    // =====================================================================
    //  PDF assembly — self-contained, in memory
    // =====================================================================

    private static byte[] BuildPdf(IReadOnlyList<string> bodies, string extraTrailerEntries = "")
    {
        var bytes = new List<byte[]>(bodies.Count);
        for (int i = 0; i < bodies.Count; i++)
            bytes.Add(Encoding.ASCII.GetBytes(bodies[i]));
        return BuildPdfBytes(bytes, extraTrailerEntries);
    }

    /// <summary>
    /// Byte-level assembler, so an object body may carry binary stream data (a Flate payload)
    /// rather than only ASCII.
    /// </summary>
    private static byte[] BuildPdfBytes(IReadOnlyList<byte[]> bodies, string extraTrailerEntries = "")
    {
        var ms = new MemoryStream();

        void Ascii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        Ascii("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = (int)ms.Length;
            Ascii((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            ms.Write(bodies[i], 0, bodies[i].Length);
            Ascii("\nendobj\n");
        }

        int xref = (int)ms.Length;
        int size = bodies.Count + 1;

        Ascii("xref\n0 " + size.ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        for (int i = 0; i < offsets.Length; i++)
            Ascii(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

        Ascii("trailer\n<< /Size " + size.ToString(CultureInfo.InvariantCulture) +
              " /Root 1 0 R" + extraTrailerEntries + " >>\n");
        Ascii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return ms.ToArray();
    }

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "\nendstream";

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    // ---------------------------------------------------------------
    //  Fixtures
    // ---------------------------------------------------------------

    private const string HeadingText = "Chapter One";

    /// <summary>
    /// One 612x792 page drawing a large heading and five 10pt body lines, all comfortably inside
    /// the 50pt header/footer bands so nothing is classified as page furniture.
    /// <paramref name="paddingBytes"/> trailing spaces are appended to the content stream; they
    /// change its <em>size</em> and nothing else, which is what makes the size ceiling testable
    /// without also changing what the page draws.
    /// </summary>
    private static byte[] TextPagePdf(float headingSize = 36f, int paddingBytes = 0)
    {
        var content = new StringBuilder(paddingBytes + 512);
        content.Append("BT /F1 ")
               .Append(headingSize.ToString("0.##", CultureInfo.InvariantCulture))
               .Append(" Tf 72 700 Td (").Append(HeadingText).Append(") Tj ET\n");
        for (int i = 0; i < 5; i++)
        {
            content.Append("BT /F1 10 Tf 72 ")
                   .Append((640 - i * 12).ToString(CultureInfo.InvariantCulture))
                   .Append(" Td (Body line ").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                   .Append(".) Tj ET\n");
        }
        if (paddingBytes > 0) content.Append(' ', paddingBytes);

        string cs = content.ToString();

        return BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(cs),
            HelveticaFont,
        });
    }

    /// <summary>A parseable, untagged page that carries no <c>/Contents</c> at all.</summary>
    private static byte[] ContentlessPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Producer (Tetitulz.Pdf tests) >>",
    }, extraTrailerEntries: " /Info 4 0 R");

    /// <summary>
    /// A document that already declares a structure tree but whose single page draws nothing, so
    /// the auto-tagger can produce no marked content and must therefore leave the existing tree
    /// exactly where it found it.
    /// </summary>
    private static byte[] TaggedButContentlessPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /StructTreeRoot /K [5 0 R] >>",
        "<< /Type /StructElem /S /Document /P 4 0 R /K [6 0 R] >>",
        "<< /Type /StructElem /S /P /P 5 0 R >>",
    });

    // ---------------------------------------------------------------
    //  Read-back helpers — everything is asserted against the parsed graph
    // ---------------------------------------------------------------

    private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
        => obj == null ? null : doc.ResolveObject(obj);

    private static PdfDictionary ResolveDict(PdfDocument doc, PdfObject obj)
        => Resolve(doc, obj) as PdfDictionary;

    private static PdfDictionary CatalogOf(PdfDocument doc) => doc.Catalog?.Dictionary;

    private static string NameValue(PdfDocument doc, PdfDictionary dict, string key)
        => (Resolve(doc, dict?[new PdfName(key)]) as PdfName)?.Value;

    private static string TextValue(PdfDocument doc, PdfDictionary dict, string key)
        => (Resolve(doc, dict?[new PdfName(key)]) as PdfString)?.GetText();

    private static bool FlagIsTrue(PdfDocument doc, PdfDictionary dict, string subKey, string flag)
    {
        PdfDictionary sub = ResolveDict(doc, dict?[new PdfName(subKey)]);
        return Resolve(doc, sub?[new PdfName(flag)]) is PdfBoolean b && b.Value;
    }

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

    private static string DecodePageContentText(PdfDocument doc, int pageIndex)
        => Encoding.ASCII.GetString(DecodePageContent(doc, pageIndex));

    /// <summary>Every <c>/S</c> value reachable from the catalog's structure tree, in tree order.</summary>
    private static List<string> StructureTypesOf(PdfDocument doc)
    {
        var roles = new List<string>();
        PdfDictionary root = ResolveDict(doc, CatalogOf(doc)?[new PdfName("StructTreeRoot")]);
        if (root == null) return roles;

        var seen = new HashSet<PdfDictionary>();
        var stack = new Stack<PdfObject>();
        stack.Push(root[new PdfName("K")]);

        while (stack.Count > 0)
        {
            PdfObject current = Resolve(doc, stack.Pop());
            if (current is PdfArray arr)
            {
                for (int i = arr.Count - 1; i >= 0; i--) stack.Push(arr[i]);
                continue;
            }

            if (current is not PdfDictionary dict) continue;
            if (!seen.Add(dict)) continue;              // cycle / shared-node guard

            string role = NameValue(doc, dict, "S");
            if (role != null) roles.Add(role);

            PdfObject kids = dict[new PdfName("K")];
            if (kids != null) stack.Push(kids);
        }

        return roles;
    }

    private static byte[] RunAutoTag(byte[] input, AutoTagOptions options, out AutoTagResult result)
    {
        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        result = PdfAccessibility.AutoTag(inStream, outStream, options);
        return outStream.ToArray();
    }

    private static void AssertStrictAppend(byte[] original, byte[] produced)
    {
        Assert.True(produced.Length >= original.Length,
            "an incremental update may only append; the output is shorter than the input.");
        for (int i = 0; i < original.Length; i++)
        {
            // Compared by hand rather than with Assert.Equal so a mismatch inside a multi-megabyte
            // fixture reports the offset instead of dumping both arrays.
            Assert.True(original[i] == produced[i],
                "the output diverges from the input at byte " +
                i.ToString(CultureInfo.InvariantCulture) +
                "; an accessibility edit must append a revision, never rewrite the original bytes.");
        }
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string what)
    {
        Assert.True(expected.Length == actual.Length,
            what + ": expected " + expected.Length.ToString(CultureInfo.InvariantCulture) +
            " bytes but got " + actual.Length.ToString(CultureInfo.InvariantCulture) + ".");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i] == actual[i],
                what + ": first difference at byte " + i.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    // =====================================================================
    //  1. The per-page content-size ceiling, end to end
    //     (PageTagOptions.MaxContentBytes / ContentStreamTagger.DefaultMaxContentBytes)
    // =====================================================================

    /// <summary>
    /// The tagging ceiling defaults to 4 MiB (<c>ContentStreamTagger.DefaultMaxContentBytes</c>,
    /// surfaced as the internal <c>PageTagOptions.MaxContentBytes</c>).
    ///
    /// <para>
    /// <b>These two tests deliberately drive the <em>default</em>, not a lowered one.</b>
    /// <see cref="AutoTagOptions.MaxPageContentBytes"/> now exposes the ceiling publicly, and
    /// <c>PublicCeilingOptionsTests</c> uses it to prove the wiring cheaply. That is a different
    /// question from the one asked here: these tests hand the pipeline a page whose <em>decoded</em>
    /// content really is larger than the shipped default, which is the only way to show that the
    /// default is the value actually applied to a caller who sets nothing. Kept as a named constant
    /// so the two tests below agree on the number the skip reason has to name.
    /// </para>
    /// </summary>
    private const int TaggingCeilingBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Control for <see cref="AutoTag_PageOverTheContentCeiling_RefusesAndNamesTheLimit"/>: the
    /// same page, padded but still under the ceiling, must tag normally. Without this the refusal
    /// test could be passing because the padding broke tagging rather than because of the limit.
    /// </summary>
    [Fact]
    public void AutoTag_PaddedPageUnderTheContentCeiling_IsStillTagged()
    {
        byte[] input = TextPagePdf(paddingBytes: 4096);
        byte[] output = RunAutoTag(input,
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        Assert.True(result.ContentMarked,
            "a padded but under-ceiling page must still be tagged. Skips: " +
            string.Join(" | ", result.SkippedPageReasons));
        Assert.Equal(0, result.PagesSkipped);
        Assert.True(result.McidCount > 0, "at least one MCID must be minted.");

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Contains("/MCID", DecodePageContentText(doc, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// A page whose decoded content stream exceeds the tagging ceiling must be <b>refused</b>, and
    /// the refusal must be visible: the page is named in
    /// <see cref="AutoTagResult.SkippedPageReasons"/> with both byte counts, no MCID is minted,
    /// no structure tree is written, <c>/MarkInfo /Marked</c> is not set, and the page's content
    /// stream comes back byte-for-byte as it went in.
    ///
    /// <para>
    /// The last three matter most. Tagging costs a large multiple of the stream it is given, so
    /// the guard exists to stop the work — but a guard that stopped the work and still let the
    /// document claim to be tagged would turn a resource problem into a false conformance claim.
    /// </para>
    /// </summary>
    [Fact]
    public void AutoTag_PageOverTheContentCeiling_RefusesAndNamesTheLimit()
    {
        // Just over the ceiling: the guard is checked before the first scan, so this costs a
        // couple of array copies rather than the multiple of memory tagging would have spent.
        byte[] input = TextPagePdf(paddingBytes: TaggingCeilingBytes + 1024);

        byte[] originalContent;
        using (var before = PdfDocument.Open(new MemoryStream(input)))
        {
            originalContent = DecodePageContent(before, 0);
            Assert.True(originalContent.Length > TaggingCeilingBytes,
                "the fixture must actually exceed the ceiling it is testing.");
        }

        byte[] output = RunAutoTag(input,
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        // ---- the refusal is reported, and names the limit ----------------------------
        Assert.Equal(1, result.PagesProcessed);
        Assert.Equal(1, result.PagesSkipped);
        Assert.Single(result.SkippedPageReasons);
        string reason = result.SkippedPageReasons[0];
        Assert.Contains("Page 1", reason, StringComparison.Ordinal);
        Assert.Contains(TaggingCeilingBytes.ToString(CultureInfo.InvariantCulture), reason);
        Assert.Contains(originalContent.Length.ToString(CultureInfo.InvariantCulture), reason);
        Assert.Contains("limit", reason, StringComparison.OrdinalIgnoreCase);

        // ---- nothing was claimed that did not happen ---------------------------------
        Assert.False(result.ContentMarked);
        Assert.Equal(0, result.McidCount);
        Assert.Equal(0, result.ElementsAdded);

        // ---- and nothing partial was emitted -----------------------------------------
        AssertStrictAppend(input, output);

        using var after = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary catalog = CatalogOf(after);
        Assert.NotNull(catalog);
        Assert.Null(catalog[new PdfName("StructTreeRoot")]);
        Assert.False(FlagIsTrue(after, catalog, "MarkInfo", "Marked"),
            "a document whose only page was refused must not claim to be a Tagged PDF.");

        byte[] producedContent = DecodePageContent(after, 0);
        AssertBytesEqual(originalContent, producedContent,
            "the refused page's content stream must come back byte-for-byte");
        Assert.DoesNotContain("BDC", Encoding.ASCII.GetString(
            producedContent, 0, Math.Min(producedContent.Length, 4096)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The audit's own content ceiling — <c>PdfDeepAccessibilityChecker.MaxFormScanBytes</c>, a
    /// document-wide 32 MiB budget on distinct Form XObject content it will tokenise.
    ///
    /// <para>
    /// The fixture is a few kilobytes on disk and inflates to just over the budget, which is
    /// exactly the shape the guard exists for. What is asserted is not that the audit is fast but
    /// that it stays <b>honest</b>: the diagnostic names the ceiling, and the report itself carries
    /// a UA99 notice saying the page was scanned only in part. A silent skip would leave a clean
    /// report that reads as "nothing wrong with this page".
    /// </para>
    /// </summary>
    [Fact]
    public void Audit_FormXObjectOverTheScanCeiling_SaysThePageWasScannedOnlyInPart()
    {
        const long FormScanCeilingBytes = 32L * 1024 * 1024;

        var oversized = new byte[FormScanCeilingBytes + 1];
        Array.Fill(oversized, (byte)' ');
        byte[] compressed = ZlibCompress(oversized);

        var formBody = new MemoryStream();
        byte[] formHeader = Encoding.ASCII.GetBytes(
            "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Filter /FlateDecode /Length "
            + compressed.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        formBody.Write(formHeader, 0, formHeader.Length);
        formBody.Write(compressed, 0, compressed.Length);
        byte[] formTail = Encoding.ASCII.GetBytes("\nendstream");
        formBody.Write(formTail, 0, formTail.Length);

        byte[] pdf = BuildPdfBytes(new[]
        {
            Encoding.ASCII.GetBytes(
                "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R "
                + "/MarkInfo << /Marked true >> /Lang (en-US) >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Fm0 5 0 R >> >> /Contents 4 0 R >>"),
            Encoding.ASCII.GetBytes(StreamObject("/Fm0 Do\n")),
            formBody.ToArray(),
            Encoding.ASCII.GetBytes("<< /Type /StructTreeRoot /K [7 0 R] >>"),
            Encoding.ASCII.GetBytes("<< /Type /StructElem /S /Document /P 6 0 R >>"),
        });

        var diagnostics = new List<string>();
        int auditThreadId = Environment.CurrentManagedThreadId;
        var gate = new object();

        void Handler(string message)
        {
            // Other test classes may run concurrently on other threads; the sink is process-wide
            // but fires on the reporting thread, so filtering by thread id isolates this audit.
            if (Environment.CurrentManagedThreadId != auditThreadId) return;
            lock (gate) diagnostics.Add(message);
        }

        AccessibilityAuditReport report;
        PdfAccessibility.DiagnosticReported += Handler;
        try
        {
            report = PdfAccessibility.Audit(new MemoryStream(pdf));
        }
        finally
        {
            PdfAccessibility.DiagnosticReported -= Handler;
        }

        Assert.NotNull(report);

        List<string> raised;
        lock (gate) raised = new List<string>(diagnostics);

        Assert.Contains(raised, m =>
            m.IndexOf("ceiling on Form XObject content tokenised", StringComparison.Ordinal) >= 0 &&
            m.IndexOf(FormScanCeilingBytes.ToString(CultureInfo.InvariantCulture),
                      StringComparison.Ordinal) >= 0);

        // The refusal has to reach the report, not just the diagnostics sink: a page nobody could
        // scan must never read as a page with nothing wrong. The finding's wording differs between
        // the single-occurrence and aggregated shapes, so both halves are matched against the whole
        // finding rather than against one field.
        Assert.Contains(report.Issues, i =>
        {
            string text = (i.Message ?? string.Empty) + " " + (i.Remediation ?? string.Empty);
            return string.Equals(i.Code, "UA99", StringComparison.Ordinal)
                && text.IndexOf("scanned only in part", StringComparison.Ordinal) >= 0
                && text.IndexOf("Form XObject", StringComparison.Ordinal) >= 0;
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
    //  2. Strong counterparts to the weak-assertion cluster
    // =====================================================================

    /// <summary>
    /// Counterpart to <c>PdfAutoTaggerTests.AutoTag_MinimalPdf_ProducesOutput</c> /
    /// <c>_ReturnsResult</c> / <c>_WithNullOptions_UsesDefaults</c>, which assert only that some
    /// bytes came out.
    ///
    /// <para>A page that draws nothing cannot be tagged. The operation is therefore required to
    /// <em>say so</em> — name the page and the reason — and to leave the document reporting as
    /// untagged rather than writing an empty structure tree over it.</para>
    /// </summary>
    [Fact]
    public void AutoTag_PageWithNoContentStream_ReportsTheSkipInsteadOfClaimingSuccess()
    {
        byte[] input  = ContentlessPdf();
        byte[] output = RunAutoTag(input, new AutoTagOptions(), out AutoTagResult result);

        Assert.Equal(1, result.PagesProcessed);
        Assert.Equal(1, result.PagesSkipped);

        Assert.Single(result.SkippedPageReasons);
        string reason = result.SkippedPageReasons[0];
        Assert.StartsWith("Page 1:", reason, StringComparison.Ordinal);
        Assert.True(reason.Length > "Page 1:".Length + 5,
            "the skip reason must explain the skip, not just number the page: " + reason);

        Assert.False(result.ContentMarked);
        Assert.Equal(0, result.McidCount);
        Assert.Equal(0, result.ElementsAdded);

        AssertStrictAppend(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary catalog = CatalogOf(doc);
        Assert.NotNull(catalog);
        Assert.Null(catalog[new PdfName("StructTreeRoot")]);
        Assert.False(FlagIsTrue(doc, catalog, "MarkInfo", "Marked"),
            "no page was marked, so the document must not declare itself a Tagged PDF.");
    }

    /// <summary>
    /// Counterpart to <c>PdfAutoTaggerTests.AutoTag_AlreadyTagged_SkipsOrProducesOutput</c>.
    ///
    /// <para>AutoTag replaces an existing structure tree rather than merging with it — but only
    /// once it has a real one to put there. When no page could be marked it must leave the
    /// document's original tree exactly where it was, or the operation would have destroyed the
    /// only structure the file had and replaced it with nothing.</para>
    /// </summary>
    [Fact]
    public void AutoTag_TaggedButUntaggableDocument_LeavesTheExistingStructureTreeInPlace()
    {
        byte[] input  = TaggedButContentlessPdf();
        byte[] output = RunAutoTag(input, new AutoTagOptions(), out AutoTagResult result);

        Assert.False(result.ContentMarked);
        Assert.Equal(0, result.ElementsAdded);
        Assert.Equal(0, result.McidCount);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary catalog = CatalogOf(doc);
        Assert.NotNull(catalog);

        // Still the *original* tree: object 4, holding the Document → P chain the fixture wrote.
        var reference = catalog[new PdfName("StructTreeRoot")] as PdfReference;
        Assert.NotNull(reference);
        Assert.Equal(4, reference.ReferenceObjectNumber);

        Assert.Equal(new[] { "Document", "P" }, StructureTypesOf(doc));
    }

    /// <summary>
    /// Counterpart to <c>PdfAutoTaggerTests.AutoTagOptions_CustomRatios_AreAccepted</c> (which
    /// asserts only <c>result != null</c>).
    ///
    /// <para>The ratio options are the whole heading heuristic. Driving the same page twice with
    /// different ratios must change the <c>/S</c> the structure tree ends up carrying — that is
    /// the observable effect of the option, and asserting it is what a no-op cannot fake.</para>
    /// </summary>
    [Fact]
    public void AutoTagOptions_HeadingRatios_DecideTheEmittedStructureType()
    {
        byte[] input = TextPagePdf(headingSize: 36f);

        // Defaults: 36pt against a 10pt body is a ratio of 3.6, well past H1Ratio (1.8).
        byte[] asHeading = RunAutoTag(input,
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult headingResult);

        Assert.True(headingResult.ContentMarked,
            "the fixture must tag. Skips: " + string.Join(" | ", headingResult.SkippedPageReasons));

        using (var doc = PdfDocument.Open(new MemoryStream(asHeading)))
        {
            List<string> roles = StructureTypesOf(doc);
            Assert.Contains("H1", roles);
            Assert.Contains("P", roles);
        }

        Assert.True(headingResult.HeadingsByLevel.ContainsKey(1),
            "the reported heading counts must agree with the tree that was written.");

        // Thresholds nothing on this page can reach: the same line must come out a paragraph.
        byte[] asParagraph = RunAutoTag(input, new AutoTagOptions
        {
            CompressRewrittenStreams = false,
            H1Ratio = 99f,
            H2Ratio = 98f,
            H3Ratio = 97f,
        }, out AutoTagResult paragraphResult);

        Assert.True(paragraphResult.ContentMarked,
            "raising the ratios must change the roles, not stop the tagging. Skips: " +
            string.Join(" | ", paragraphResult.SkippedPageReasons));

        using (var doc = PdfDocument.Open(new MemoryStream(asParagraph)))
        {
            List<string> roles = StructureTypesOf(doc);
            Assert.DoesNotContain("H1", roles);
            Assert.DoesNotContain("H2", roles);
            Assert.DoesNotContain("H3", roles);
            Assert.Contains("P", roles);
        }

        Assert.False(paragraphResult.HeadingsByLevel.ContainsKey(1));
    }

    /// <summary>
    /// Counterpart to <c>PdfAutoTaggerTests.AutoTagOptions_WithTitle_PassedThrough</c>, whose only
    /// assertion is <c>result != null</c> — it would pass with the title thrown away.
    /// </summary>
    [Fact]
    public void AutoTagOptions_Title_ReachesInfoTitleAndDisplayDocTitle()
    {
        const string Title = "My Accessible Document";

        byte[] output = RunAutoTag(ContentlessPdf(),
            new AutoTagOptions { Title = Title, Language = "fr-FR" }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));

        PdfDictionary info = ResolveDict(doc, doc.Trailer?[new PdfName("Info")]);
        Assert.NotNull(info);
        Assert.Equal(Title, TextValue(doc, info, "Title"));

        PdfDictionary catalog = CatalogOf(doc);
        Assert.Equal("fr-FR", TextValue(doc, catalog, "Lang"));
        Assert.True(FlagIsTrue(doc, catalog, "ViewerPreferences", "DisplayDocTitle"),
            "a document that has been given a title must also be told to display it (UA04).");
    }

    /// <summary>
    /// Counterpart to the <c>PdfArtifactMarkerTests</c> cluster
    /// (<c>_MinimalPdf_ProducesOutput</c>, <c>_TaggedPdf_ProducesOutput</c>,
    /// <c>_CustomMargins_ProducesOutput</c>, <c>_ZeroMargins_ProducesOutput</c>), all of which
    /// assert only that bytes were produced.
    ///
    /// <para>On a page that draws nothing there is nothing to mark. The contract that has to hold
    /// is the documented one: artifacts are marked <em>in the content stream</em> and no structure
    /// element is ever created for them — so a run that marks nothing must add neither.</para>
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_PageWithNoContent_MarksNothingAndBuildsNoStructureTree()
    {
        byte[] input = ContentlessPdf();

        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        ArtifactMarkResult result = PdfAccessibility.MarkHeadersAndFooters(inStream, outStream);
        byte[] output = outStream.ToArray();

        Assert.Equal(0, result.ArtifactsMarked);
        AssertStrictAppend(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary catalog = CatalogOf(doc);
        Assert.NotNull(catalog);

        // ISO 32000-1 §14.8.2.2: an artifact is content that is *not* in the logical structure.
        // Marking must therefore never manufacture a structure tree.
        Assert.Null(catalog[new PdfName("StructTreeRoot")]);
        Assert.False(FlagIsTrue(doc, catalog, "MarkInfo", "Marked"));
        Assert.DoesNotContain("/Artifact", DecodePageContentText(doc, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// Counterpart to <c>PdfStructureTreeEditorTests.SetDocumentLanguage_MinimalPdf_ProducesOutput</c>
    /// and <c>_ValidPdf_ReturnsTrueOrProducesBytes</c>, which assert only <c>output.Length &gt; 0</c>
    /// and would pass with <c>/Lang</c> never written.
    /// </summary>
    [Fact]
    public void SetDocumentLanguage_WritesTheTagIntoTheCatalog()
    {
        byte[] input = ContentlessPdf();

        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        bool ok = PdfAccessibility.SetDocumentLanguage(inStream, outStream, "fr-FR");
        byte[] output = outStream.ToArray();

        Assert.True(ok);
        AssertStrictAppend(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal("fr-FR", TextValue(doc, CatalogOf(doc), "Lang"));
    }

    /// <summary>
    /// Counterpart to <c>PdfStructureTreeEditorTests.SetActualText_MinimalPdf_ProducesOutput</c>,
    /// which asserts <c>count &gt;= 0</c> — true of every possible implementation, including one
    /// that does nothing.
    /// </summary>
    [Fact]
    public void SetActualText_ParagraphElement_WritesActualTextOntoThatElement()
    {
        byte[] input = TaggedButContentlessPdf();   // object 6 is the single /P element

        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        int count = PdfAccessibility.SetActualText(
            inStream, outStream, "P", (index, existing) => "Paragraph " + index);
        byte[] output = outStream.ToArray();

        Assert.Equal(1, count);
        AssertStrictAppend(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output));

        var paragraph = doc.GetObject(6) as PdfDictionary;
        Assert.NotNull(paragraph);
        Assert.Equal("P", NameValue(doc, paragraph, "S"));
        Assert.Equal("Paragraph 0", TextValue(doc, paragraph, "ActualText"));

        // The sibling it walked past must be untouched.
        var document = doc.GetObject(5) as PdfDictionary;
        Assert.NotNull(document);
        Assert.Null(document[new PdfName("ActualText")]);
    }

    /// <summary>
    /// Counterpart to <c>PdfReadingOrderTests.AnalyzeReadingOrder_MinimalPdf_ReturnsAnalysis</c>
    /// (<c>Assert.NotNull(analysis)</c>).
    ///
    /// <para>An empty issue list on a document with no structure tree must not read as "the
    /// reading order is correct" — nothing was examined. <see cref="ReadingOrderAnalysis.Analyzed"/>
    /// is exactly that distinction, so it is what the test asserts.</para>
    /// </summary>
    [Fact]
    public void AnalyzeReadingOrder_UntaggedDocument_SaysNotAnalyzedRatherThanOrdered()
    {
        ReadingOrderAnalysis analysis =
            PdfAccessibility.AnalyzeReadingOrder(new MemoryStream(ContentlessPdf()));

        Assert.NotNull(analysis);
        Assert.False(analysis.Analyzed,
            "a document with no structure tree was not examined, so it must not be reported as analyzed.");
        Assert.False(string.IsNullOrEmpty(analysis.NotAnalyzedReason));
        Assert.Empty(analysis.Issues);

        // The whole point: IsOrdered is true only because nothing was checked, and ToString has to
        // say so rather than printing a bare "0 issues".
        Assert.Contains("not analyzed", analysis.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Counterpart to <c>PdfAccessibilityAuditTests.Remediate_MinimalPdf_ProducesOutput</c>
    /// (<c>output.Length &gt; 0</c> plus <c>report != null</c>).
    ///
    /// <para>The pipeline's document-metadata pass is the part that is provable on a page which
    /// draws nothing: <c>/Lang</c>, <c>/Info /Title</c> and <c>/ViewerPreferences
    /// /DisplayDocTitle</c> must actually be in the output. Equally, the pass must <em>not</em>
    /// have written <c>/MarkInfo /Marked</c>, because no structure tree could be built — claiming
    /// tagged is worse than the violation it hides.</para>
    /// </summary>
    [Fact]
    public void Remediate_UntaggedDocument_WritesTheMetadataItClaimsAndNoFalseTaggedClaim()
    {
        const string Title = "Quarterly Report";

        byte[] input = ContentlessPdf();
        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();

        AccessibilityAuditReport report = PdfAccessibility.Remediate(inStream, outStream,
            new RemediationOptions { Language = "en-US", Title = Title });
        byte[] output = outStream.ToArray();

        Assert.NotNull(report);
        AssertStrictAppend(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary catalog = CatalogOf(doc);
        Assert.NotNull(catalog);

        Assert.Equal("en-US", TextValue(doc, catalog, "Lang"));
        Assert.True(FlagIsTrue(doc, catalog, "ViewerPreferences", "DisplayDocTitle"));

        PdfDictionary info = ResolveDict(doc, doc.Trailer?[new PdfName("Info")]);
        Assert.NotNull(info);
        Assert.Equal(Title, TextValue(doc, info, "Title"));

        // No page could be marked, so no structure tree exists and the document must not pretend.
        Assert.Null(catalog[new PdfName("StructTreeRoot")]);
        Assert.False(FlagIsTrue(doc, catalog, "MarkInfo", "Marked"),
            "remediation must never write /MarkInfo /Marked onto a document it could not tag.");
    }
}
