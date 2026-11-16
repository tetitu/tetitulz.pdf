// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Artifacts;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// End-to-end tests that run the accessibility pipeline against a PDF that actually
/// <em>draws text</em>.
/// <para>
/// The pre-existing suites (<see cref="PdfAutoTaggerTests"/>, <see cref="PdfArtifactMarkerTests"/>)
/// only ever feed <c>MinimalPdf()</c> / <c>TaggedPdf()</c>, whose pages carry no
/// <c>/Contents</c> at all. Every one of those documents takes the "nothing to mark"
/// early-out, so the content-stream rewriting pipeline — marked-content splicing, MCID
/// allocation, the structure tree and the number tree — was never executed by a test.
/// These tests exist to exercise it.
/// </para>
/// </summary>
public class ContentBearingPdfTests
{
    // ---------------------------------------------------------------
    //  A PDF that actually draws text
    // ---------------------------------------------------------------

    private const string HeaderText = "Confidential Header";
    // internal: EncryptedIncrementalTaggingTests asserts this survives the marked-content splice.
    internal const string HeadingText = "Chapter One";
    private const string BodyLine1 = "Body line one of the paragraph.";
    private const string BodyLine2 = "Body line two of the paragraph.";
    private const string BodyLine3 = "Body line three of the paragraph.";
    private const string FooterText = "Page 1 of 1";

    /// <summary>
    /// Untagged, uncompressed, single-page PDF drawing six lines of text:
    /// a header at y=760 (inside the 50pt top band), a 24pt heading at y=700,
    /// three 10pt body lines at y=650/638/626, and a footer at y=30 (inside the
    /// 50pt bottom band). MediaBox is 612x792.
    /// </summary>
    internal static byte[] ContentPdf()
    {
        var content = new StringBuilder();
        content.Append("BT /F1 9 Tf 72 760 Td (").Append(HeaderText).Append(") Tj ET\n");
        content.Append("BT /F1 24 Tf 72 700 Td (").Append(HeadingText).Append(") Tj ET\n");
        content.Append("BT /F1 10 Tf 72 650 Td (").Append(BodyLine1).Append(") Tj ET\n");
        content.Append("BT /F1 10 Tf 72 638 Td (").Append(BodyLine2).Append(") Tj ET\n");
        content.Append("BT /F1 10 Tf 72 626 Td (").Append(BodyLine3).Append(") Tj ET\n");
        content.Append("BT /F1 9 Tf 72 30 Td (").Append(FooterText).Append(") Tj ET\n");
        string cs = content.ToString();

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ")
          .Append("/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        int off4 = sb.Length;
        sb.Append("4 0 obj\n<< /Length ").Append(cs.Length).Append(" >>\nstream\n")
          .Append(cs).Append("endstream\nendobj\n");
        int off5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica ")
          .Append("/Encoding /WinAnsiEncoding >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", off1);
        sb.AppendFormat("{0:D10} 00000 n \n", off2);
        sb.AppendFormat("{0:D10} 00000 n \n", off3);
        sb.AppendFormat("{0:D10} 00000 n \n", off4);
        sb.AppendFormat("{0:D10} 00000 n \n", off5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    /// <summary>Decodes the (possibly array-valued) /Contents of page <paramref name="pageIndex"/>.</summary>
    private static string DecodePageContent(PdfDocument doc, int pageIndex)
    {
        PdfDictionary page = doc.Pages[pageIndex].Dictionary;
        PdfObject contents = doc.ResolveObject(page.Get(new PdfName("Contents")));
        var buffer = new MemoryStream();
        if (contents is PdfArray arr)
        {
            foreach (PdfObject part in arr)
            {
                if (doc.ResolveObject(part) is PdfStream ps)
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
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    /// <summary>Counts occurrences of a PDF operator, i.e. a token delimited by whitespace.</summary>
    private static int CountOperator(string content, string op)
    {
        int n = 0;
        for (int i = 0; i + op.Length <= content.Length; i++)
        {
            if (string.CompareOrdinal(content, i, op, 0, op.Length) != 0) continue;
            bool leftOk = i == 0 || char.IsWhiteSpace(content[i - 1]) || content[i - 1] == '>';
            int after = i + op.Length;
            bool rightOk = after >= content.Length || char.IsWhiteSpace(content[after]);
            if (leftOk && rightOk) { n++; i = after - 1; }
        }
        return n;
    }

    private static byte[] RunAutoTag(byte[] input, AutoTagOptions options, out AutoTagResult result)
    {
        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        result = PdfAccessibility.AutoTag(inStream, outStream, options);
        return outStream.ToArray();
    }

    // ---------------------------------------------------------------
    //  The corpus itself must be sane
    // ---------------------------------------------------------------

    [Fact]
    public void ContentPdf_IsParseableAndDrawsText()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ContentPdf()));
        Assert.Single(doc.Pages);
        string content = DecodePageContent(doc, 0);
        Assert.Contains(HeadingText, content, StringComparison.Ordinal);
        Assert.Equal(6, CountOperator(content, "Tj"));
        Assert.Equal(0, CountOperator(content, "BDC"));
    }

    // ---------------------------------------------------------------
    //  AutoTag actually marks content
    // ---------------------------------------------------------------

    [Fact]
    public void AutoTag_ContentPdf_MarksContent()
    {
        byte[] outBytes = RunAutoTag(ContentPdf(),
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        Assert.True(result.ContentMarked,
            "AutoTag must mark content on a page that draws text. Skips: " +
            string.Join(" | ", result.SkippedPageReasons));
        Assert.Equal(0, result.PagesSkipped);
        Assert.Equal(1, result.PagesProcessed);
        Assert.True(result.McidCount > 0, "at least one MCID must be minted");
        Assert.True(result.ElementsAdded > 0, "at least the /Document element must be added");
    }

    [Fact]
    public void AutoTag_ContentPdf_SplicesBalancedMarkedContent()
    {
        byte[] outBytes = RunAutoTag(ContentPdf(),
            new AutoTagOptions { CompressRewrittenStreams = false }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        string content = DecodePageContent(doc, 0);

        int bdc = CountOperator(content, "BDC");
        int emc = CountOperator(content, "EMC");
        Assert.True(bdc > 0, "content must contain marked-content sequences");
        Assert.Equal(bdc, emc);
        Assert.Equal(CountOperator(content, "BT"), CountOperator(content, "ET"));
    }

    [Fact]
    public void AutoTag_ContentPdf_PreservesEveryOriginalTextRun()
    {
        byte[] outBytes = RunAutoTag(ContentPdf(),
            new AutoTagOptions { CompressRewrittenStreams = false }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        string content = DecodePageContent(doc, 0);

        foreach (string original in new[]
                 { HeaderText, HeadingText, BodyLine1, BodyLine2, BodyLine3, FooterText })
        {
            Assert.Contains(original, content, StringComparison.Ordinal);
        }
        Assert.Equal(6, CountOperator(content, "Tj"));
    }

    [Fact]
    public void AutoTag_ContentPdf_ContentsIsASingleStreamAfterwards()
    {
        byte[] outBytes = RunAutoTag(ContentPdf(),
            new AutoTagOptions { CompressRewrittenStreams = false }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        PdfObject contents = doc.ResolveObject(
            doc.Pages[0].Dictionary.Get(new PdfName("Contents")));
        Assert.IsType<PdfStream>(contents);
    }

    [Fact]
    public void AutoTag_ContentPdf_MarksHeaderAndFooterAsArtifacts()
    {
        byte[] outBytes = RunAutoTag(ContentPdf(),
            new AutoTagOptions { CompressRewrittenStreams = false }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        string content = DecodePageContent(doc, 0);

        Assert.Contains("/Artifact", content, StringComparison.Ordinal);
        Assert.Contains("/Pagination", content, StringComparison.Ordinal);
        Assert.Contains("/Header", content, StringComparison.Ordinal);
        Assert.Contains("/Footer", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTag_ContentPdf_BuildsStructureTreeReachableFromCatalog()
    {
        byte[] outBytes = RunAutoTag(ContentPdf(), new AutoTagOptions(), out _);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        PdfDictionary catalog = doc.Catalog.Dictionary;
        var str = doc.ResolveObject(catalog.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
        Assert.NotNull(str);

        var markInfo = doc.ResolveObject(catalog.Get(new PdfName("MarkInfo"))) as PdfDictionary;
        Assert.NotNull(markInfo);
        Assert.True((doc.ResolveObject(markInfo.Get(new PdfName("Marked"))) as PdfBoolean)?.Value);

        Assert.NotNull(doc.ResolveObject(str.Get(new PdfName("ParentTree"))) as PdfDictionary);
    }

    /// <summary>
    /// The load-bearing PDF/UA round trip: page /StructParents -> ParentTree /Nums ->
    /// array indexed by MCID -> structure element, and that MCID must literally appear
    /// in the page's content stream.
    /// </summary>
    [Fact]
    public void AutoTag_ContentPdf_ParentTreeResolvesEveryMcid()
    {
        byte[] outBytes = RunAutoTag(ContentPdf(),
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        PdfDictionary page = doc.Pages[0].Dictionary;

        var structParents = doc.ResolveObject(page.Get(new PdfName("StructParents"))) as PdfNumber;
        Assert.NotNull(structParents);

        var str = doc.ResolveObject(
            doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
        var parentTree = doc.ResolveObject(str.Get(new PdfName("ParentTree"))) as PdfDictionary;
        var nums = doc.ResolveObject(parentTree.Get(new PdfName("Nums"))) as PdfArray;
        Assert.NotNull(nums);

        PdfArray mcidToElement = null;
        for (int i = 0; i + 1 < nums.Count; i += 2)
        {
            var key = doc.ResolveObject(nums[i]) as PdfNumber;
            if (key != null && key.IntValue == structParents.IntValue)
            {
                mcidToElement = doc.ResolveObject(nums[i + 1]) as PdfArray;
                break;
            }
        }
        Assert.NotNull(mcidToElement);
        Assert.Equal(result.McidCount, mcidToElement.Count);

        string content = DecodePageContent(doc, 0);
        for (int mcid = 0; mcid < mcidToElement.Count; mcid++)
        {
            var element = doc.ResolveObject(mcidToElement[mcid]) as PdfDictionary;
            Assert.NotNull(element);
            Assert.NotNull(doc.ResolveObject(element.Get(new PdfName("S"))) as PdfName);
            Assert.Contains("/MCID " + mcid.ToString(), content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AutoTag_ContentPdf_DetectsTheLargeHeading()
    {
        RunAutoTag(ContentPdf(), new AutoTagOptions(), out AutoTagResult result);

        // 24pt against a 10pt median body size is 2.4x — above the 1.8x H1 ratio.
        Assert.NotEmpty(result.HeadingsByLevel);
        int totalHeadings = 0;
        foreach (KeyValuePair<int, int> kv in result.HeadingsByLevel) totalHeadings += kv.Value;
        Assert.True(totalHeadings > 0, "the 24pt line must be classified as a heading");
    }

    [Fact]
    public void AutoTag_ContentPdf_GroupsTheThreeBodyLinesIntoOneParagraph()
    {
        RunAutoTag(ContentPdf(), new AutoTagOptions(), out AutoTagResult result);

        // 3 body lines, 12pt apart, under the 6pt gap threshold => one <P>.
        Assert.True(result.ParagraphCount >= 1, "body lines must produce at least one paragraph");
        Assert.True(result.ParagraphCount <= 3,
            $"expected the 3 body lines to be grouped, got {result.ParagraphCount} paragraphs");
    }

    [Fact]
    public void AutoTag_ContentPdf_IsAStrictAppend()
    {
        byte[] input = ContentPdf();
        byte[] outBytes = RunAutoTag(input, new AutoTagOptions(), out _);

        Assert.True(outBytes.Length > input.Length);
        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], outBytes[i]);
    }

    [Fact]
    public void AutoTag_RewriteContentStreamsDisabled_MarksNothing()
    {
        RunAutoTag(ContentPdf(),
            new AutoTagOptions { RewriteContentStreams = false }, out AutoTagResult result);

        Assert.False(result.ContentMarked);
        Assert.Equal(0, result.McidCount);
        Assert.NotEmpty(result.SkippedPageReasons);
    }

    [Fact]
    public void AutoTag_RunTwice_SecondRunSkipsAlreadyMarkedPage()
    {
        byte[] once = RunAutoTag(ContentPdf(), new AutoTagOptions(), out _);
        RunAutoTag(once, new AutoTagOptions(), out AutoTagResult second);

        // The page now contains BDC/EMC, so the tagger must refuse to re-tag it
        // rather than nesting a second layer of marked content.
        Assert.False(second.ContentMarked);
        Assert.Equal(1, second.PagesSkipped);
    }

    [Fact]
    public void AutoTag_ContentPdf_CompressedStreamStillDecodes()
    {
        byte[] outBytes = RunAutoTag(ContentPdf(),
            new AutoTagOptions { CompressRewrittenStreams = true }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        string content = DecodePageContent(doc, 0);
        Assert.Contains(HeadingText, content, StringComparison.Ordinal);
        Assert.Contains("BDC", content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    //  Artifact marker
    // ---------------------------------------------------------------

    [Fact]
    public void MarkHeadersAndFooters_ContentPdf_MarksHeaderAndFooterOnly()
    {
        using var input = new MemoryStream(ContentPdf());
        using var output = new MemoryStream();
        ArtifactMarkResult result = PdfAccessibility.MarkHeadersAndFooters(input, output);

        Assert.True(result.ArtifactsMarked > 0,
            "the header at y=760 and footer at y=30 sit inside the default 50pt bands");

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string content = DecodePageContent(doc, 0);

        Assert.Contains("/Artifact", content, StringComparison.Ordinal);

        // Body text must NOT be swallowed by an artifact sequence: every BDC is closed
        // before the body line starts.
        int bodyAt = content.IndexOf(BodyLine1, StringComparison.Ordinal);
        Assert.True(bodyAt > 0);
        string beforeBody = content.Substring(0, bodyAt);
        Assert.Equal(CountOperator(beforeBody, "BDC"), CountOperator(beforeBody, "EMC"));
    }

    [Fact]
    public void MarkHeadersAndFooters_ContentPdf_DoesNotCreateStructureElements()
    {
        using var input = new MemoryStream(ContentPdf());
        using var output = new MemoryStream();
        PdfAccessibility.MarkHeadersAndFooters(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        // Artifacts are content-level only: no /S /Artifact structure element may be invented.
        Assert.DoesNotContain("/S /Artifact",
            Encoding.ASCII.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void MarkHeadersAndFooters_ZeroMargins_ContentPdf_MarksNothing()
    {
        using var input = new MemoryStream(ContentPdf());
        using var output = new MemoryStream();
        ArtifactMarkResult result = PdfAccessibility.MarkHeadersAndFooters(
            input, output, headerMarginPt: 0f, footerMarginPt: 0f);
        Assert.Equal(0, result.ArtifactsMarked);
    }

    // ---------------------------------------------------------------
    //  Remediate
    // ---------------------------------------------------------------

    [Fact]
    public void Remediate_ContentPdf_OutputIsParseableAndTagged()
    {
        using var input = new MemoryStream(ContentPdf());
        using var output = new MemoryStream();
        AccessibilityAuditReport after = PdfAccessibility.Remediate(input, output,
            new RemediationOptions { Language = "en-US", Title = "Verification Document" });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));

        // A structure tree must exist AND reach content — a hollow /StructTreeRoot is exactly the
        // thing this feature refuses to write, so asserting only its presence would accept the
        // failure mode. /MarkInfo /Marked is the document's own claim to be tagged; it is only
        // written when tagging really happened.
        var tree = doc.ResolveObject(
            doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
        Assert.NotNull(tree);
        Assert.NotNull(tree![new PdfName("K")]);

        var markInfo = doc.ResolveObject(
            doc.Catalog.Dictionary.Get(new PdfName("MarkInfo"))) as PdfDictionary;
        Assert.NotNull(markInfo);
        Assert.True((doc.ResolveObject(markInfo![new PdfName("Marked")]) as PdfBoolean)?.Value);

        Assert.NotNull(after);
    }

    [Fact]
    public void Remediate_ContentPdf_ReducesErrorCount()
    {
        byte[] input = ContentPdf();
        AccessibilityAuditReport before = PdfAccessibility.Audit(new MemoryStream(input));

        using var inStream = new MemoryStream(input);
        using var output = new MemoryStream();
        PdfAccessibility.Remediate(inStream, output,
            new RemediationOptions { Language = "en-US", Title = "Verification Document" });

        AccessibilityAuditReport after = PdfAccessibility.Audit(new MemoryStream(output.ToArray()));
        Assert.True(after.ErrorCount < before.ErrorCount,
            $"remediation must reduce errors: before={before.ErrorCount}, after={after.ErrorCount}");
    }

    [Fact]
    public void Remediate_ContentPdf_WritesPdfUaIdentifier()
    {
        using var input = new MemoryStream(ContentPdf());
        using var output = new MemoryStream();
        PdfAccessibility.Remediate(input, output, new RemediationOptions
        {
            Language = "en-US",
            Title = "Verification Document",
            SetPdfUaIdentifier = true,
        });

        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("pdfuaid", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Remediate_IsIdempotent_SecondRunAddsNoErrors()
    {
        using var input = new MemoryStream(ContentPdf());
        using var first = new MemoryStream();
        PdfAccessibility.Remediate(input, first,
            new RemediationOptions { Language = "en-US", Title = "Doc" });

        using var second = new MemoryStream();
        using var firstIn = new MemoryStream(first.ToArray());
        PdfAccessibility.Remediate(firstIn, second,
            new RemediationOptions { Language = "en-US", Title = "Doc" });

        AccessibilityAuditReport a = PdfAccessibility.Audit(new MemoryStream(first.ToArray()));
        AccessibilityAuditReport b = PdfAccessibility.Audit(new MemoryStream(second.ToArray()));
        Assert.True(b.ErrorCount <= a.ErrorCount,
            $"re-remediating must not regress: {a.ErrorCount} -> {b.ErrorCount}");
    }

    // ---------------------------------------------------------------
    //  Non-Latin-1 text must survive
    // ---------------------------------------------------------------

    [Fact]
    public void SetDocumentTitle_Hebrew_RoundTripsThroughPdfString()
    {
        const string hebrew = "שלום";
        using var input = new MemoryStream(ContentPdf());
        using var output = new MemoryStream();
        Assert.True(PdfAccessibility.SetDocumentTitle(input, output, hebrew));

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var info = doc.ResolveObject(doc.Trailer.Get(new PdfName("Info"))) as PdfDictionary;
        Assert.NotNull(info);
        var title = doc.ResolveObject(info.Get(new PdfName("Title"))) as PdfString;
        Assert.NotNull(title);
        Assert.Equal(hebrew, title.GetText());
    }

    [Fact]
    public void AutoTag_HebrewTitle_RoundTrips()
    {
        const string hebrew = "מסמך";
        byte[] outBytes = RunAutoTag(ContentPdf(),
            new AutoTagOptions { Title = hebrew }, out _);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        var info = doc.ResolveObject(doc.Trailer.Get(new PdfName("Info"))) as PdfDictionary;
        Assert.NotNull(info);
        var title = doc.ResolveObject(info.Get(new PdfName("Title"))) as PdfString;
        Assert.NotNull(title);
        Assert.Equal(hebrew, title.GetText());
    }

    // ---------------------------------------------------------------
    //  Reading order / deep check must operate on a real tagged file
    // ---------------------------------------------------------------

    [Fact]
    public void AnalyzeReadingOrder_AutoTaggedContentPdf_IsAnalyzed()
    {
        byte[] tagged = RunAutoTag(ContentPdf(), new AutoTagOptions(), out _);

        ReadingOrderAnalysis analysis =
            PdfAccessibility.AnalyzeReadingOrder(new MemoryStream(tagged));

        Assert.True(analysis.Analyzed,
            "a freshly auto-tagged file must be analysable: " + analysis.NotAnalyzedReason);
        Assert.True(analysis.IsOrdered,
            "AutoTag emits elements in visual order, so the analyzer must find no issues");
    }

    [Fact]
    public void Audit_AutoTaggedContentPdf_HasNoUntaggedContentError()
    {
        byte[] tagged = RunAutoTag(ContentPdf(), new AutoTagOptions(), out _);
        AccessibilityAuditReport report =
            PdfAccessibility.Audit(new MemoryStream(tagged));

        Assert.NotEmpty(report.Issues);   // guards against a vacuous pass
        foreach (AccessibilityIssue issue in report.Issues)
        {
            Assert.False(issue.Code == "UA17" && issue.Severity == IssueSeverity.Error,
                "auto-tagged page must not report 'draws text with no marked content': " + issue.Message);
            Assert.False(issue.Code == "UA18" && issue.Severity == IssueSeverity.Error,
                "auto-tagged page must have /StructParents: " + issue.Message);
        }
    }

    // ---------------------------------------------------------------
    //  Structural edge cases
    // ---------------------------------------------------------------

    /// <summary>
    /// Page whose /Contents is an array of three streams. The tagger's byte offsets refer to the
    /// concatenation, so the array must be replaced by a single stream; writing back into the
    /// individual parts would corrupt the page.
    /// </summary>
    private static byte[] ArrayContentsPdf()
    {
        string p1 = "BT /F1 9 Tf 72 760 Td (Header Alpha) Tj ET\n";
        string p2 = "BT /F1 24 Tf 72 700 Td (Big Heading) Tj ET\n";
        string p3 = "BT /F1 10 Tf 72 650 Td (Body text here.) Tj ET\n";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offs = new List<int>();
        void Obj(int n, string s) { offs.Add(sb.Length); sb.Append(n).Append(" 0 obj\n").Append(s).Append("\nendobj\n"); }
        string Str(string c) => "<< /Length " + c.Length + " >>\nstream\n" + c + "endstream";

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
             + "/Resources << /Font << /F1 7 0 R >> >> /Contents [4 0 R 5 0 R 6 0 R] >>");
        Obj(4, Str(p1));
        Obj(5, Str(p2));
        Obj(6, Str(p3));
        Obj(7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void AutoTag_ArrayContents_CollapsesToOneStreamAndKeepsAllText()
    {
        byte[] outBytes = RunAutoTag(ArrayContentsPdf(),
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        Assert.True(result.ContentMarked,
            "an array /Contents must still be taggable: " + string.Join(" | ", result.SkippedPageReasons));

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        Assert.IsType<PdfStream>(doc.ResolveObject(
            doc.Pages[0].Dictionary.Get(new PdfName("Contents"))));

        string content = DecodePageContent(doc, 0);
        Assert.Contains("Header Alpha", content, StringComparison.Ordinal);
        Assert.Contains("Big Heading", content, StringComparison.Ordinal);
        Assert.Contains("Body text here.", content, StringComparison.Ordinal);
        Assert.Equal(CountOperator(content, "BDC"), CountOperator(content, "EMC"));
    }

    /// <summary>Two pages, each drawing a heading, a body line and a footer.</summary>
    private static byte[] TwoPagePdf()
    {
        string Page(string heading, string body, string footer) =>
            "BT /F1 24 Tf 72 700 Td (" + heading + ") Tj ET\n"
            + "BT /F1 10 Tf 72 650 Td (" + body + ") Tj ET\n"
            + "BT /F1 9 Tf 72 30 Td (" + footer + ") Tj ET\n";

        string c1 = Page("Page One Heading", "Page one body.", "Footer 1");
        string c2 = Page("Page Two Heading", "Page two body.", "Footer 2");

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offs = new List<int>();
        void Obj(int n, string s) { offs.Add(sb.Length); sb.Append(n).Append(" 0 obj\n").Append(s).Append("\nendobj\n"); }
        string Str(string c) => "<< /Length " + c.Length + " >>\nstream\n" + c + "endstream";

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 5 0 R >>");
        Obj(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>");
        Obj(5, Str(c1));
        Obj(6, Str(c2));
        Obj(7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Each page needs its own /StructParents key, and the ParentTree must carry one entry per
    /// key. Sharing a key would make page 2's marked content resolve to page 1's elements.
    /// </summary>
    [Fact]
    public void AutoTag_TwoPages_EachGetsItsOwnParentTreeKey()
    {
        byte[] outBytes = RunAutoTag(TwoPagePdf(), new AutoTagOptions(), out AutoTagResult result);
        Assert.True(result.ContentMarked);
        Assert.Equal(2, result.PagesProcessed);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));

        var keys = new List<int>();
        for (int p = 0; p < doc.Pages.Count; p++)
        {
            var sp = doc.ResolveObject(
                doc.Pages[p].Dictionary.Get(new PdfName("StructParents"))) as PdfNumber;
            Assert.NotNull(sp);
            keys.Add(sp.IntValue);
        }
        Assert.Equal(2, keys.Count);
        Assert.NotEqual(keys[0], keys[1]);

        var str = doc.ResolveObject(
            doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
        var parentTree = doc.ResolveObject(str.Get(new PdfName("ParentTree"))) as PdfDictionary;
        var nums = doc.ResolveObject(parentTree.Get(new PdfName("Nums"))) as PdfArray;
        Assert.Equal(2, nums.Count / 2);

        var nextKey = doc.ResolveObject(str.Get(new PdfName("ParentTreeNextKey"))) as PdfNumber;
        Assert.NotNull(nextKey);
        foreach (int k in keys)
            Assert.True(k < nextKey.IntValue,
                "/ParentTreeNextKey must exceed every key already in use");
    }

    /// <summary>Single page with /Rotate <paramref name="rotate"/> drawing text near y=760 and y=400.</summary>
    private static byte[] RotatedPdf(int rotate)
    {
        string c = "BT /F1 10 Tf 72 760 Td (Edge text) Tj ET\n"
                 + "BT /F1 10 Tf 72 400 Td (Middle text) Tj ET\n";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offs = new List<int>();
        void Obj(int n, string s) { offs.Add(sb.Length); sb.Append(n).Append(" 0 obj\n").Append(s).Append("\nendobj\n"); }

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate " + rotate
             + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        Obj(4, "<< /Length " + c.Length + " >>\nstream\n" + c + "endstream");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Regression: on a 90°/270°-rotated page the user-space Y bands are the visual LEFT and RIGHT
    /// edges, so a Y-band test classifies body text running down the side as a running header and
    /// emits it as /Artifact — removing it from the structure tree and hiding it from assistive
    /// technology. AutoTag must tag the page but leave the edge classification off.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void AutoTag_RotatedPage_NeverHidesEdgeTextAsAnArtifact(int rotate)
    {
        byte[] outBytes = RunAutoTag(RotatedPdf(rotate),
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        Assert.True(result.ContentMarked, "the page must still be tagged");
        Assert.Equal(0, result.ArtifactCount);

        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        string content = DecodePageContent(doc, 0);

        Assert.DoesNotContain("/Artifact", content, StringComparison.Ordinal);
        Assert.Contains("Edge text", content, StringComparison.Ordinal);

        // The edge line must be inside a real content sequence, not an artifact one.
        int at = content.IndexOf("Edge text", StringComparison.Ordinal);
        int bdc = content.LastIndexOf("BDC", at, StringComparison.Ordinal);
        Assert.True(bdc >= 0);
        Assert.Contains("/MCID", content.Substring(bdc - 40 < 0 ? 0 : bdc - 40, Math.Min(40, bdc)),
            StringComparison.Ordinal);

        Assert.Contains(result.Warnings,
            w => w.IndexOf("rotated", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>An unrotated page is the control: the same y=760 line IS a header artifact.</summary>
    [Fact]
    public void AutoTag_UnrotatedPage_StillMarksEdgeTextAsArtifact()
    {
        byte[] outBytes = RunAutoTag(RotatedPdf(0),
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        Assert.True(result.ArtifactCount > 0);
        using var doc = PdfDocument.Open(new MemoryStream(outBytes));
        Assert.Contains("/Artifact", DecodePageContent(doc, 0), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void MarkHeadersAndFooters_RotatedPage_SkipsRatherThanMisclassify(int rotate)
    {
        using var input = new MemoryStream(RotatedPdf(rotate));
        using var output = new MemoryStream();
        ArtifactMarkResult result = PdfAccessibility.MarkHeadersAndFooters(input, output);

        Assert.Equal(0, result.ArtifactsMarked);
        Assert.True(output.Length > 0);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.DoesNotContain("/Artifact", DecodePageContent(doc, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.7.3.3 requires <c>/Rotate</c> to be a multiple of 90. A page that is not gets refused,
    /// and the warning must quote the angle <b>the file asked for</b>.
    /// </summary>
    /// <remarks>
    /// This is what kept both Accessibility copies of the normalisation off the shared
    /// <c>PdfPageRotation</c> reader: its <c>Normalize</c> returns 0 for a malformed value — the
    /// spec default, and the right answer for a renderer that draws the page upright anyway — so a
    /// message built from that return reads "/Rotate 0 is not a multiple of 90". The copies now
    /// call <c>Reduce</c> + <c>IsMalformed</c>, which separate the two questions. Asserting the
    /// angle rather than merely "a warning exists" is the whole point: the refusal was already
    /// correct, and only the text could regress.
    /// </remarks>
    [Fact]
    public void MarkHeadersAndFooters_MalformedRotation_RefusesAndNamesTheAngleTheFileAsked()
    {
        using var input = new MemoryStream(RotatedPdf(45));
        using var output = new MemoryStream();
        ArtifactMarkDetail detail = PdfArtifactMarker.MarkHeadersAndFootersDetailed(input, output);

        Assert.Equal(0, detail.ArtifactsMarked);
        Assert.Contains(detail.Warnings, w => w.Contains("/Rotate 45", StringComparison.Ordinal));
        Assert.DoesNotContain(detail.Warnings, w => w.Contains("/Rotate 0", StringComparison.Ordinal));
    }

    /// <summary>
    /// A negative <c>/Rotate</c> is legal once reduced: -90 is 270, so the page must be treated as
    /// rotated — refused with the "rotated" warning — and not as the malformed case.
    /// </summary>
    /// <remarks>
    /// The separating input for the reduction itself. Dropping the <c>+ 360</c> leaves -90, which
    /// <c>% 90 == 0</c> accepts, so the page would be classified as upright and its side-running
    /// body text emitted as a header artifact — the exact loss
    /// <see cref="AutoTag_RotatedPage_NeverHidesEdgeTextAsAnArtifact"/> exists to prevent, reached
    /// by a different route.
    /// </remarks>
    [Fact]
    public void AutoTag_NegativeRotation_IsReducedRatherThanTreatedAsUpright()
    {
        byte[] outBytes = RunAutoTag(RotatedPdf(-90),
            new AutoTagOptions { CompressRewrittenStreams = false }, out AutoTagResult result);

        Assert.Equal(0, result.ArtifactCount);
        Assert.DoesNotContain("/Artifact", DecodePageContent(
            PdfDocument.Open(new MemoryStream(outBytes)), 0), StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("270", StringComparison.Ordinal));
    }
}
