// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Accessibility.Artifacts;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Tests for defects an adversarial reading of the accessibility stack finds.
///
/// <para>
/// Each test names the failure it pins. They fall into four groups: content-stream corruption
/// (splicing into inline-image samples), content hidden from assistive technology, false
/// conformance claims, and false positives on valid files — plus the adversarial-input tests
/// CLAUDE.md requires for the stack's DoS guards.
/// </para>
///
/// <para>Every document is built in memory as ASCII bytes; nothing is read from disk.</para>
/// </summary>
public class RepairRoundRegressionTests
{
    // =====================================================================
    //  Fixtures
    // =====================================================================

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Cat(params byte[][] parts)
    {
        var output = new List<byte>();
        foreach (byte[] part in parts) output.AddRange(part);
        return output.ToArray();
    }

    /// <summary>Assembles a classic-xref PDF; bodies[0] is object 1 and is always the catalog.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    private static PageTagOptions TagOptions() => new PageTagOptions
    {
        PageHeight = 792f,
        HeaderMargin = 50f,
        FooterMargin = 50f,
        MedianFontSize = 10f,
    };

    /// <summary>
    /// Runs an operation on a worker thread and fails if it has not finished within a generous
    /// budget, which is what an unguarded traversal looks like. The budget is a backstop against a
    /// hang, never the claim: a loaded host stretches a sub-second walk to half a minute, so each
    /// test asserts its claim on the result and the budget is minutes.
    /// </summary>
    private static T RunWithinBudget<T>(Func<T> operation, int budgetMs = 600_000)
    {
        T result = default;
        Exception failure = null;

        var thread = new Thread(() =>
        {
            try { result = operation(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };

        thread.Start();
        Assert.True(thread.Join(budgetMs),
            "the operation did not finish within " + budgetMs.ToString(CultureInfo.InvariantCulture) +
            " ms, which is what an unguarded traversal looks like");

        if (failure != null) throw new Xunit.Sdk.XunitException("the operation threw: " + failure);
        return result;
    }

    // =====================================================================
    //  1. Corruption: splicing into inline-image sample data
    // =====================================================================

    /// <summary>
    /// The highest-severity defect here. A 4x4 8-bpc DeviceGray inline image holds
    /// exactly 16 payload bytes. When every one of those bytes happens to be printable ASCII —
    /// which is what any <em>dark</em> grayscale image looks like — the "whitespace-delimited EI
    /// followed by printable ASCII" heuristic terminates the image early, tokenises pixel data as
    /// operators, and splices BDC/EMC into the samples. The declared geometry settles the length
    /// exactly, so no guessing is needed and the decoy must be ignored.
    /// </summary>
    [Fact]
    public void Scanner_UnfilteredInlineImage_UsesTheComputedLengthAndIgnoresAnAsciiDecoyEi()
    {
        // 16 bytes: " EI BT(x)Tj ET  " — a whitespace-delimited EI followed by printable ASCII.
        byte[] payload = Ascii(" EI BT(x)Tj ET  ");
        Assert.Equal(16, payload.Length);   // fixture self-check: 4 x 4 x 8bpc DeviceGray

        byte[] header = Ascii("q\nBI /W 4 /H 4 /BPC 8 /CS /G ID ");
        byte[] tail   = Ascii("\nEI\nQ\n");
        byte[] content = Cat(header, payload, tail);

        int realEi = header.Length + payload.Length + 1;
        Assert.Equal("EI", Encoding.ASCII.GetString(content, realEi, 2));   // fixture self-check

        ContentScanResult scan = ContentStreamScanner.ScanDetailed(content);

        Assert.True(scan.IsSafe, scan.Error);
        Assert.True(scan.HasInlineImage);

        // Computed from /W /H /BPC /CS, so the boundary is known rather than guessed.
        Assert.False(scan.HasUncertainInlineImage);

        // Nothing from inside the payload surfaced as an operation.
        Assert.Equal(new[] { "q", "BI", "Q" }, scan.Tokens.Select(t => t.Operator).ToArray());
        Assert.Equal(0, scan.TextObjectCount);

        ContentToken bi = Assert.Single(scan.Tokens, t => t.Operator == "BI");
        Assert.Equal(realEi + 2, bi.EndOffset);
    }

    /// <summary>
    /// The same stream through the tagger: no marked-content operator may be written anywhere
    /// inside the image, and the payload must survive byte for byte.
    /// </summary>
    [Fact]
    public void Tagger_AsciiInlineImagePayload_IsNeverSplicedInto()
    {
        byte[] payload = Ascii(" EI BT(x)Tj ET  ");
        byte[] head    = Ascii("q\nBI /W 4 /H 4 /BPC 8 /CS /G ID ");
        byte[] tail    = Ascii("\nEI\nQ\nBT /F1 10 Tf 72 400 Td (Real body text) Tj ET\n");
        byte[] original = Cat(head, payload, tail);

        PageTagResult result = ContentStreamTagger.TagPage(original, TagOptions());

        Assert.True(result.Changed, result.SkipReason);

        // The payload appears exactly once and unaltered.
        Assert.Equal(1, CountOccurrences(result.NewContent, payload));

        // Every insertion lands after the image ends.
        int imageEnd = head.Length + payload.Length + 3;   // past "\nEI"
        TaggedRun run = Assert.Single(result.Runs);
        Assert.True(run.TokenStart >= imageEnd,
            "a marked-content sequence must never open inside an inline image's samples");

        // The real sentence is tagged as content, never demoted to an artifact by a CTM leaked
        // out of misread pixel data.
        Assert.False(run.IsArtifact);
        Assert.Equal("P", run.Role);
    }

    /// <summary>
    /// When the length genuinely cannot be computed — the image is filtered and declares no /L —
    /// the boundary is a guess, and a guess must never be spliced at. Refusing the page is the
    /// only safe answer; the previous behaviour was to splice at the guessed offsets.
    ///
    /// <para><b>"Filtered" is no longer sufficient on its own, so the payload matters.</b> A
    /// self-delimiting filter's end-of-data marker now settles the length exactly
    /// (<c>InlineImageLength.ComputeSelfDelimiting</c>), and <c>/AHx</c> is one of the four. This
    /// payload is not hex at all — <c>'q'</c> and <c>'z'</c> are outside the alphabet and there is
    /// no <c>&gt;</c> — so the measuring scan declines it and the guess is genuinely all that is
    /// left. A well-formed hex payload here would assert the opposite of this test's name.</para>
    /// </summary>
    [Fact]
    public void Tagger_FilteredInlineImageWithNoDeclaredLength_IsRefusedRatherThanGuessedAt()
    {
        byte[] payload = Ascii("qqq EI zzz");
        byte[] head    = Ascii("q\nBI /W 4 /H 4 /BPC 8 /CS /G /F /AHx ID ");
        byte[] tail    = Ascii("\nEI\nQ\nBT /F1 10 Tf 72 400 Td (Body) Tj ET\n");
        byte[] original = Cat(head, payload, tail);

        ContentScanResult scan = ContentStreamScanner.ScanDetailed(original);
        Assert.True(scan.IsSafe, scan.Error);
        Assert.True(scan.HasUncertainInlineImage);

        PageTagResult result = ContentStreamTagger.TagPage(original, TagOptions());

        Assert.False(result.Changed);
        Assert.Same(original, result.NewContent);
        Assert.Contains("could not be determined", result.SkipReason, StringComparison.Ordinal);
    }

    /// <summary>A declared /L still wins, and is exact.</summary>
    [Fact]
    public void Scanner_InlineImageWithDeclaredLength_IsStillTreatedAsExact()
    {
        byte[] payload = Ascii("BT ET EI EMC junk");
        byte[] header  = Ascii("q\nBI /W 9 /H 9 /BPC 8 /CS /G /F /AHx /L "
                             + payload.Length.ToString(CultureInfo.InvariantCulture) + " ID ");
        byte[] content = Cat(header, payload, Ascii("\nEI\nQ\n"));

        ContentScanResult scan = ContentStreamScanner.ScanDetailed(content);

        Assert.True(scan.IsSafe, scan.Error);
        Assert.False(scan.HasUncertainInlineImage);
        Assert.Equal(new[] { "q", "BI", "Q" }, scan.Tokens.Select(t => t.Operator).ToArray());
    }

    private static int CountOccurrences(byte[] haystack, byte[] needle)
    {
        int count = 0;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length && match; j++)
                if (haystack[i + j] != needle[j]) match = false;
            if (match) count++;
        }
        return count;
    }

    // =====================================================================
    //  2. Content hidden from AT: a half-tagged page reported as tagged
    // =====================================================================

    /// <summary>
    /// A page that marks its own text but also invokes a Form XObject is only <em>partially</em>
    /// tagged: the form's text lives in another stream this pass does not rewrite. The flag was
    /// computed and then dropped inside the rewriter, so no caller could ever warn about it and
    /// the document asserted Tagged PDF while carrying unmarked real content.
    /// </summary>
    [Fact]
    public void Tagger_PageMixingOwnTextAndAFormXObject_ReportsTheFormInvocation()
    {
        byte[] content = Ascii("BT /F1 10 Tf 72 400 Td (page level) Tj ET\nq 1 0 0 1 0 0 cm /Fm0 Do Q\n");

        PageTagResult result = ContentStreamTagger.TagPage(content, TagOptions());

        Assert.True(result.Changed, result.SkipReason);
        Assert.True(result.ContainsXObjectInvocations);
    }

    /// <summary>
    /// A form invoked from one page now has its text <b>tagged</b>, so the page is no longer
    /// partially tagged.
    /// </summary>
    /// <remarks>
    /// This asserted the opposite until the form tagger landed: <c>PagesPartiallyTagged == 1</c>
    /// and a warning naming the form. That was the correct assertion while the gap existed, and
    /// keeping it would now pin the gap open.
    /// </remarks>
    [Fact]
    public void AutoTag_PageInvokingAFormXObject_TagsTheFormAndReportsNoPartialTagging()
    {
        AutoTagResult result = AutoTagBytes(FormXObjectPagePdf());

        Assert.True(result.ContentMarked);
        Assert.Equal(0, result.PagesPartiallyTagged);
        Assert.DoesNotContain(result.Warnings,
            w => w.IndexOf("still untagged", StringComparison.Ordinal) >= 0);
        Assert.DoesNotContain("PARTIALLY", result.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The structure element for a form's text points into the <b>form's</b> stream, and the
    /// form's MCIDs live in the form's own <c>/StructParents</c> entry — not the page's.
    /// </summary>
    /// <remarks>
    /// <para><c>/Stm</c> is the entry to be careful with.
    /// Without <c>/Stm</c> the marked-content reference still parses, still has the right shape,
    /// and addresses MCID <i>n</i> of the <b>page's</b> content stream — different content, in a
    /// tree that validates. Nothing else in the suite would notice.</para>
    /// <para>It also pins that MCIDs restart at 0 inside the form: the page's own text takes MCID
    /// 0 in the page's scope, and the form's text takes MCID 0 in the form's.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_FormElement_ReferencesTheFormStreamNotThePage()
    {
        byte[] tagged = AutoTagBytesToArray(FormXObjectPagePdf());

        using var ms = new MemoryStream(tagged);
        using PdfDocument doc = PdfDocument.Open(ms);

        // The form is the object that now carries /StructParents.
        PdfStream form = FindFormXObject(doc);
        Assert.NotNull(form);
        var formKey = form[PageContentRewriter.StructParents] as PdfNumber
                      ?? doc.ResolveObject(form[PageContentRewriter.StructParents]) as PdfNumber;
        Assert.True(formKey != null, "the tagged form must carry its own /StructParents");

        PdfDictionary page = doc.Pages[0].Dictionary;
        var pageKey = page[PageContentRewriter.StructParents] as PdfNumber
                      ?? doc.ResolveObject(page[PageContentRewriter.StructParents]) as PdfNumber;
        Assert.True(pageKey != null, "the page must still carry its own /StructParents");
        Assert.NotEqual((int)pageKey.LongValue, (int)formKey.LongValue);

        // Find an /MCR carrying /Stm and check it names the form object, not the page.
        PdfDictionary mcr = FindMcrWithStm(doc);
        Assert.True(mcr != null, "a form's content must be referenced through an /MCR with /Stm");

        var stm = mcr[new PdfName("Stm")] as PdfReference;
        Assert.True(stm != null, "/Stm must be an indirect reference to the form stream");

        // ReferenceObjectNumber, not ObjectNumber: the latter is inherited from PdfObject and is
        // the number of the reference *itself* (0 for a direct one), not of its target. Reading it
        // makes this assertion compare 6 against 0 and look like a code defect.
        Assert.Equal(form.ObjectNumber, stm.ReferenceObjectNumber);

        // ...and it is NOT the page: the failure /Stm exists to prevent.
        int pageObjectNumber = doc.Pages[0].Dictionary.ObjectNumber;
        Assert.NotEqual(pageObjectNumber, stm.ReferenceObjectNumber);
    }

    private static byte[] AutoTagBytesToArray(byte[] pdf, AutoTagOptions options = null)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfAutoTagger.AutoTag(input, output, options ?? new AutoTagOptions());
        return output.ToArray();
    }

    /// <summary>The first Form XObject in page 1's resources.</summary>
    private static PdfStream FindFormXObject(PdfDocument doc)
    {
        var resources = Res<PdfDictionary>(doc, doc.Pages[0].Dictionary[PdfName.Resources]);
        var xobjects  = Res<PdfDictionary>(doc, resources?[PdfName.XObject]);
        if (xobjects == null) return null;

        foreach (KeyValuePair<PdfName, PdfObject> entry in xobjects)
        {
            var stream = Res<PdfStream>(doc, entry.Value);
            if (stream != null && Res<PdfObject>(doc, stream[PdfName.Subtype]) is PdfName s && s.Value == "Form")
                return stream;
        }
        return null;
    }

    /// <summary>Walks the structure tree for a marked-content reference carrying <c>/Stm</c>.</summary>
    private static PdfDictionary FindMcrWithStm(PdfDocument doc)
    {
        var stmKey = new PdfName("Stm");
        var root = Res<PdfDictionary>(doc, doc.Catalog?.Dictionary?[new PdfName("StructTreeRoot")]);
        return root == null ? null : Search(doc, root, stmKey, 0);
    }

    private static PdfDictionary Search(PdfDocument doc, PdfObject node, PdfName stmKey, int depth)
    {
        if (depth > 32) return null;

        if (Res<PdfObject>(doc, node) is PdfDictionary dict)
        {
            if (dict[stmKey] != null) return dict;

            PdfObject k = dict[PdfName.K];
            if (k != null) return Search(doc, k, stmKey, depth + 1);
            return null;
        }

        if (Res<PdfObject>(doc, node) is PdfArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                PdfDictionary found = Search(doc, arr[i], stmKey, depth + 1);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static T Res<T>(PdfDocument doc, PdfObject obj) where T : PdfObject
        => obj == null ? null : ((doc.ResolveObject(obj) as T) ?? obj as T);

    /// <summary>
    /// With duplication switched off, a form invoked from <b>two</b> pages is not taggable and both
    /// pages are reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Renamed from <c>AutoTag_FormSharedByTwoPages_IsNotTaggedAndBothPagesAreReported</c>.</b>
    /// One stream dictionary carries one <c>/StructParents</c>, so a shared form's marked content
    /// could belong to only one of the pages — which used to make this permanent. Item 49's escape
    /// is to give each page its own copy of the form;
    /// <see cref="AutoTagOptions.DuplicateSharedFormXObjects"/> turns that off for a caller who
    /// needs the object graph left as it was found, and this pins what they get instead: the old
    /// behaviour, reported the way it always was.
    /// </para>
    /// <para>
    /// The assertions are unchanged from the version that named the old default. Only the option
    /// and the name moved — the behaviour still exists, it is simply no longer what happens
    /// automatically.
    /// </para>
    /// </remarks>
    [Fact]
    public void AutoTag_SharedFormWithDuplicationDisabled_IsNotTaggedAndBothPagesAreReported()
    {
        AutoTagResult result = AutoTagBytes(TwoPagesSharingOneFormPdf(),
            new AutoTagOptions { DuplicateSharedFormXObjects = false });

        Assert.Equal(2, result.PagesPartiallyTagged);
        Assert.Contains(result.Warnings,
            w => w.IndexOf("/StructParents", StringComparison.Ordinal) >= 0);
    }

    /// <summary>Two pages drawing text through one shared Form XObject.</summary>
    private static byte[] TwoPagesSharingOneFormPdf()
    {
        const string formContent = "BT /F1 10 Tf 10 10 Td (shared form text) Tj ET\n";
        const string pageContent = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n"
                                 + "q 1 0 0 1 0 0 cm /Fm0 Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >> >>",
            StreamObject("", pageContent),
            StreamObject("", pageContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 7 0 R >> >>", formContent),
        });
    }

    /// <summary>
    /// A page drawing an <b>image</b> XObject is fully tagged — an image holds no text.
    /// </summary>
    /// <remarks>
    /// The partial-tagging warning used to fire on <i>any</i> <c>Do</c>, so an ordinary page with
    /// a logo was reported as partially tagged and <c>PagesPartiallyTagged</c> counted pages where
    /// the tagging was complete. A number that over-reports is one a caller learns to ignore,
    /// which costs the real signal its meaning.
    /// </remarks>
    [Fact]
    public void ImageXObjectPage_IsNotReportedPartiallyTagged()
    {
        AutoTagResult result = AutoTagBytes(ImageXObjectPagePdf());

        Assert.True(result.ContentMarked);
        Assert.Equal(0, result.PagesPartiallyTagged);
        Assert.DoesNotContain(result.Warnings,
            w => w.IndexOf("partially tagged", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// A Form XObject holding only vector art likewise leaves no text untagged.
    /// </summary>
    [Fact]
    public void FormXObjectWithoutText_IsNotReportedPartiallyTagged()
    {
        AutoTagResult result = AutoTagBytes(VectorOnlyFormPagePdf());

        Assert.True(result.ContentMarked);
        Assert.Equal(0, result.PagesPartiallyTagged);
    }

    /// <summary>
    /// A form that cannot be resolved counts as a <i>possible</i> gap, never as "nothing there".
    /// </summary>
    [Fact]
    public void UnresolvableFormXObject_IsReportedPartiallyTagged()
    {
        AutoTagResult result = AutoTagBytes(DanglingXObjectPagePdf());

        Assert.Equal(1, result.PagesPartiallyTagged);
        Assert.Contains(result.Warnings,
            w => w.IndexOf("does not resolve", StringComparison.Ordinal) >= 0);
    }

    /// <summary>A page whose text is marked and which also draws an image XObject.</summary>
    private static byte[] ImageXObjectPagePdf()
    {
        const string pageContent = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n"
                                 + "q 10 0 0 10 0 0 cm /Im0 Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> >>",
            StreamObject("", pageContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Image /Width 1 /Height 1 "
                + "/ColorSpace /DeviceGray /BitsPerComponent 8", " "),
        });
    }

    /// <summary>A page drawing a Form XObject that paints a rectangle and no text.</summary>
    private static byte[] VectorOnlyFormPagePdf()
    {
        const string formContent = "0 0 1 rg 0 0 100 20 re f\n";
        const string pageContent = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n"
                                 + "q 1 0 0 1 0 0 cm /Fm0 Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
            StreamObject("", pageContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50]", formContent),
        });
    }

    /// <summary>A page invoking an XObject name its resources do not define.</summary>
    private static byte[] DanglingXObjectPagePdf()
    {
        const string pageContent = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n"
                                 + "q /Missing Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
            StreamObject("", pageContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50]", "0 0 1 rg\n"),
        });
    }

    /// <summary>A page whose own text is marked and which also draws text inside a Form XObject.</summary>
    private static byte[] FormXObjectPagePdf()
    {
        const string formContent = "BT /F1 10 Tf 10 10 Td (text inside the form) Tj ET\n";
        const string pageContent = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n"
                                 + "q 1 0 0 1 0 0 cm /Fm0 Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
            StreamObject("", pageContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 5 0 R >> >>", formContent),
        });
    }

    private static AutoTagResult AutoTagBytes(byte[] pdf, AutoTagOptions options = null)
    {
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        return PdfAutoTagger.AutoTag(input, output, options ?? new AutoTagOptions());
    }

    // =====================================================================
    //  3. K2: the checker must see text inside Form XObjects
    // =====================================================================

    /// <summary>
    /// UA24 counted text-showing operators in page-level /Contents only, so a page whose text all
    /// lives inside a Form XObject was accused of being "almost certainly a scanned image" — a
    /// false accusation about a page that is entirely readable.
    /// </summary>
    [Fact]
    public void Audit_PageWhoseTextLivesInAFormXObject_IsNotCalledAScannedImage()
    {
        AccessibilityAuditReport report = Audit(ImageAndFormTextPdf());

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA24");
    }

    /// <summary>
    /// The control: a page that really does draw an image and no text anywhere — including inside
    /// its forms — must still be reported, or the test above would also pass with UA24 disabled.
    /// </summary>
    [Fact]
    public void Audit_PageWithAnImageAndNoTextAnywhere_IsStillReportedAsScanned()
    {
        AccessibilityAuditReport report = Audit(ImageOnlyPdf());

        Assert.Contains(report.Issues, i => i.Code == "UA24");
    }

    /// <summary>Page-level content draws an image; all its text is inside a Form XObject.</summary>
    private static byte[] ImageAndFormTextPdf()
    {
        const string formContent = "BT /F1 10 Tf 10 10 Td (all the text is in here) Tj ET\n";
        const string pageContent = "q 200 0 0 100 10 10 cm /Im0 Do Q\nq 1 0 0 1 0 600 cm /Fm0 Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R /Fm0 7 0 R >> >> >>",
            StreamObject("", pageContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Image /Width 2 /Height 2 "
                + "/ColorSpace /DeviceGray /BitsPerComponent 8", "abcd"),
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 5 0 R >> >>", formContent),
        });
    }

    private static byte[] ImageOnlyPdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /XObject << /Im0 5 0 R >> >> >>",
            StreamObject("", "q 200 0 0 100 10 10 cm /Im0 Do Q\n"),
            StreamObject("/Type /XObject /Subtype /Image /Width 2 /Height 2 "
                + "/ColorSpace /DeviceGray /BitsPerComponent 8", "abcd"),
        });

    // =====================================================================
    //  4. False conformance: UA17 must not be immunised by one artifact
    // =====================================================================

    /// <summary>
    /// UA17 used to ask "does this page contain any BDC at all". One artifact sequence therefore
    /// immunised every untagged run beside it — the exact hollow-tagging case the check exists to
    /// catch. The rule is per text-showing operator: ISO 14289-1 §7.1 requires <em>all</em>
    /// content to be tagged or marked as an artifact.
    /// </summary>
    [Fact]
    public void Audit_PageWithOneArtifactAndManyUntaggedRuns_StillReportsUa17()
    {
        AccessibilityAuditReport report = Audit(PartiallyMarkedPdf());

        AccessibilityIssue ua17 = Assert.Single(report.Issues.Where(i => i.Code == "UA17"));
        Assert.Equal(IssueSeverity.Error, ua17.Severity);
        Assert.Equal(1, ua17.PageNumber);
    }

    /// <summary>The control: every run marked, so UA17 must stay silent.</summary>
    [Fact]
    public void Audit_PageWhereEveryRunIsMarked_ReportsNoUa17()
    {
        AccessibilityAuditReport report = Audit(FullyMarkedPdf());

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA17");
    }

    private static byte[] PartiallyMarkedPdf()
        => TaggedSinglePagePdf(
            "/Artifact <</Type /Pagination /Subtype /Header>> BDC\n"
            + "BT /F1 9 Tf 72 760 Td (Running header) Tj ET\nEMC\n"
            + "BT /F1 12 Tf 72 700 Td (Untagged body one) Tj ET\n"
            + "BT /F1 12 Tf 72 680 Td (Untagged body two) Tj ET\n"
            + "BT /F1 12 Tf 72 660 Td (Untagged body three) Tj ET\n");

    private static byte[] FullyMarkedPdf()
        => TaggedSinglePagePdf(
            "/Artifact <</Type /Pagination /Subtype /Header>> BDC\n"
            + "BT /F1 9 Tf 72 760 Td (Running header) Tj ET\nEMC\n"
            + "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Body one) Tj ET\nEMC\n");

    /// <summary>
    /// A document that declares itself tagged, with a connected structure tree, whose page
    /// content is supplied by the caller.
    /// </summary>
    private static byte[] TaggedSinglePagePdf(string pageContent)
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", pageContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [7 0 R]] >>",
        });

    // =====================================================================
    //  5. False positives on valid files
    // =====================================================================

    /// <summary>
    /// /K is Optional (ISO 32000-1 Table 323) and an empty &lt;TD&gt; is the standard way to keep
    /// a table grid rectangular — required, in fact, by this checker's own regularity rule, so
    /// reporting it made the two checks contradict each other.
    /// </summary>
    [Fact]
    public void Audit_EmptyTableCell_IsNotReportedAsAnEmptyStructureElement()
    {
        AccessibilityAuditReport report = Audit(TableWithEmptyCellPdf());

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA16");
    }

    /// <summary>
    /// The control: an empty &lt;P&gt; carries no such exemption and must still be reported, so
    /// the exemption above cannot be mistaken for UA16 having been switched off.
    /// </summary>
    [Fact]
    public void Audit_EmptyParagraphElement_IsStillReportedAsEmpty()
    {
        AccessibilityAuditReport report = Audit(EmptyParagraphPdf());

        Assert.Contains(report.Issues, i => i.Code == "UA16");
    }

    private static byte[] TableWithEmptyCellPdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/TD <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Cell) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Table /P 6 0 R /K [8 0 R] >>",
            "<< /Type /StructElem /S /TR /P 7 0 R /K [9 0 R 11 0 R] >>",
            "<< /Type /StructElem /S /TD /P 8 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [9 0 R]] >>",
            "<< /Type /StructElem /S /TD /P 8 0 R >>",           // the deliberately empty cell
        });

    private static byte[] EmptyParagraphPdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Body) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 6 0 R >>",             // empty, and not exempt
            "<< /Nums [0 [7 0 R]] >>",
        });

    /// <summary>
    /// Table 30 makes /StructParents required only "if the page contains structural content
    /// items". A page whose content is entirely artifacts — a divider, or a blank page carrying a
    /// running footer — contains none, so the key is correctly absent.
    /// </summary>
    [Fact]
    public void Audit_PageContainingOnlyArtifacts_IsNotReportedAsMissingStructParents()
    {
        AccessibilityAuditReport report = Audit(ArtifactOnlySecondPagePdf());

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA18" && i.PageNumber == 2);
    }

    /// <summary>
    /// A tagged 2-page document whose second page carries nothing but a pagination footer.
    /// </summary>
    private static byte[] ArtifactOnlySecondPagePdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 8 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Body) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/Artifact <</Type /Pagination /Subtype /Footer>> BDC\n"
                           + "BT /F1 9 Tf 72 30 Td (Page 2) Tj ET\nEMC\n"),
            "<< /Type /StructTreeRoot /K [9 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [9 0 R]] >>",
        });

    /// <summary>
    /// PDF/UA-1 exempts printer's marks from structure-tree inclusion, exactly as it does /Link
    /// and /Widget. Reporting one was the only error on an otherwise clean document.
    /// </summary>
    [Fact]
    public void Audit_PrinterMarkAnnotation_IsExemptFromTheStructureTreeRequirement()
    {
        AccessibilityAuditReport report = Audit(PrinterMarkAnnotationPdf());

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA23");
    }

    private static byte[] PrinterMarkAnnotationPdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
                + "/Tabs /S /Annots [9 0 R] /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Body) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [7 0 R]] >>",
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 20 20] >>",
        });

    /// <summary>
    /// ISO 32000-1 §9.6.6.2: a non-symbolic simple font with no /Encoding uses StandardEncoding,
    /// whose glyph names are AGL-mappable, so its text is recoverable. Reporting it as an Error
    /// also made this checker contradict the sibling PdfUaChecker on the same file.
    /// </summary>
    [Fact]
    public void Audit_EmbeddedNonSymbolicFontWithNoEncoding_IsNotReportedAsUnmappable()
    {
        AccessibilityAuditReport report = Audit(NonSymbolicEmbeddedFontPdf());

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA20" || i.Code == "UA21");
    }

    private static byte[] NonSymbolicEmbeddedFontPdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 8 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Body) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Garamond /FirstChar 32 "
                + "/LastChar 32 /Widths [500] /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Garamond /Flags 32 /ItalicAngle 0 "
                + "/Ascent 700 /Descent -200 /CapHeight 700 /StemV 80 /FontBBox [0 0 1000 1000] "
                + "/FontFile2 7 0 R >>",
            StreamObject("/Length1 12", "embeddedfont"),
            "<< /Type /StructTreeRoot /K [9 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [9 0 R]] >>",
        });

    /// <summary>
    /// ISO 14289-1 §7.21.4.1 requires every font to be embedded but exempts one used exclusively in
    /// an invisible rendering mode — which is exactly the shape of an OCR text layer behind a
    /// scanned image, the output of this repository's own OCR plugin.
    /// </summary>
    [Fact]
    public void Audit_FontUsedOnlyForInvisibleOcrText_IsExemptFromTheEmbeddingRequirement()
    {
        AccessibilityAuditReport report = Audit(OcrLayerPdf(textRenderMode: 3));

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA21");
    }

    /// <summary>
    /// The control: the same unembedded font painted visibly gets no exemption, so the rule above
    /// cannot be mistaken for UA21 having been switched off.
    /// </summary>
    [Fact]
    public void Audit_SameFontPaintedVisibly_IsStillRequiredToBeEmbedded()
    {
        AccessibilityAuditReport report = Audit(OcrLayerPdf(textRenderMode: 0));

        Assert.Contains(report.Issues, i => i.Code == "UA21");
    }

    /// <summary>A scanned page with a text layer drawn at the given text rendering mode.</summary>
    private static byte[] OcrLayerPdf(int textRenderMode)
    {
        string content =
            "q 200 0 0 100 10 600 cm /Im0 Do Q\n"
            + "/P <</MCID 0>> BDC\n"
            + "BT " + textRenderMode.ToString(CultureInfo.InvariantCulture)
            + " Tr /F1 12 Tf 72 700 Td (recognised text) Tj ET\nEMC\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> >>",
            StreamObject("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Image /Width 2 /Height 2 "
                + "/ColorSpace /DeviceGray /BitsPerComponent 8", "abcd"),
            "<< /Type /StructTreeRoot /K [8 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [8 0 R]] >>",
        });
    }

    // =====================================================================
    //  6. Honesty
    // =====================================================================

    /// <summary>
    /// /MarkInfo &lt;&lt;/Marked true&gt;&gt; asserts conformance to Tagged PDF conventions
    /// (ISO 32000-1 §14.8.1), which requires a structure tree. The artifact marker creates none,
    /// so setting the flag made the document claim something false — and the checker then
    /// reported the page as missing /StructParents, a finding manufactured by the previous step.
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_UntaggedDocument_DoesNotClaimTaggedPdf()
    {
        byte[] input = UntaggedTwoLinePdf();

        using var inMs  = new MemoryStream(input);
        using var outMs = new MemoryStream();
        ArtifactMarkDetail detail = PdfArtifactMarker.MarkHeadersAndFootersDetailed(inMs, outMs);

        Assert.True(detail.ArtifactsMarked > 0, "the fixture must have a header to mark");

        using var reopened = new MemoryStream(outMs.ToArray());
        using PdfDocument doc = PdfDocument.Open(reopened);

        PdfDictionary catalog = doc.Catalog?.Dictionary;
        Assert.NotNull(catalog);
        Assert.Null(catalog[new PdfName("StructTreeRoot")]);   // fixture self-check
        Assert.Null(catalog[new PdfName("MarkInfo")]);

        Assert.Contains(detail.Warnings,
            w => w.IndexOf("not a Tagged PDF", StringComparison.Ordinal) >= 0);
    }

    private static byte[] UntaggedTwoLinePdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 9 Tf 72 760 Td (Running header) Tj ET\n"
                           + "BT /F1 12 Tf 72 400 Td (Body text) Tj ET\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        });

    /// <summary>
    /// The clean summary is the one string a human actually sees — it is embedded verbatim in
    /// RemediationReport.ToDetailedString(). A bare "CONFORMING" there made every carefully
    /// worded XML caveat invisible at the moment it mattered.
    /// </summary>
    [Fact]
    public void AuditReport_WithNoIssues_DoesNotPrintABareConformanceClaim()
    {
        var report = new AccessibilityAuditReport(new List<AccessibilityIssue>());
        string text = report.ToString();

        Assert.True(report.IsConforming);
        Assert.DoesNotContain("— CONFORMING", text, StringComparison.Ordinal);
        Assert.Contains("NO BLOCKING ISSUES DETECTED", text, StringComparison.Ordinal);
        Assert.Contains("not a PDF/UA-1 conformance claim", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The non-conforming wording must be unchanged: it was never the overstatement.
    /// </summary>
    [Fact]
    public void AuditReport_WithAnError_StillSaysNonConforming()
    {
        var report = new AccessibilityAuditReport(new List<AccessibilityIssue>
        {
            new AccessibilityIssue(IssueSeverity.Error, "UA01", "message", "fix", IssueCategory.Tagging),
        });

        Assert.False(report.IsConforming);
        Assert.Contains("NON-CONFORMING", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// K1: every structure-path segment carries an index, so the path of an element no longer
    /// depends on how many siblings it happens to have. Before, a row with one cell produced
    /// "TR[2]/TD" and a row with two produced "TR[1]/TD[1]".
    /// </summary>
    [Fact]
    public void Audit_StructPath_IndexesEverySegmentEvenWhenTheRoleIsUnique()
    {
        AccessibilityAuditReport report = Audit(EmptyParagraphPdf());

        AccessibilityIssue located = Assert.Single(
            report.Issues.Where(i => i.Code == "UA16" && i.StructPath != null));

        // Every segment is "Role[n]"; none is a bare role.
        foreach (string segment in located.StructPath.Split('/'))
        {
            Assert.EndsWith("]", segment, StringComparison.Ordinal);
            Assert.Contains('[', segment);
        }
    }

    // =====================================================================
    //  7. Adversarial input for the stack's guards (CLAUDE.md)
    // =====================================================================

    /// <summary>
    /// <b>Process-kill regression.</b> A Type0 font whose /DescendantFonts points back at itself
    /// made PdfUaChecker.CheckFontEmbedded recurse without depth, visited set or budget. The
    /// parser returns the same cached instance every time, so the frame repeated forever and
    /// ended in a StackOverflowException — which .NET Core cannot catch, so the whole host
    /// process died, bypassing every catch in the file. A ~150-byte document must not do that.
    /// </summary>
    [Fact]
    public void Audit_SelfReferentialDescendantFonts_TerminatesInsteadOfKillingTheProcess()
    {
        byte[] pdf = SelfReferentialType0FontPdf();

        AccessibilityAuditReport report = RunWithinBudget(() => Audit(pdf));

        Assert.NotNull(report);

        // Terminating by aborting the whole audit would also "pass" a timeout check, so assert
        // the scan actually completed: UA00 marks an audit that could not be run at all.
        Assert.DoesNotContain(report.Issues, i => i.Code == "UA00");
    }

    private static byte[] SelfReferentialType0FontPdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 700 Td (Body) Tj ET\n"),
            // The trap: object 5's /DescendantFonts names object 5.
            "<< /Type /Font /Subtype /Type0 /BaseFont /Loop /Encoding /Identity-H "
                + "/DescendantFonts [5 0 R] >>",
        });

    /// <summary>
    /// A Form XObject whose own content stream invokes itself. Without the cycle guard the
    /// checker's new Form XObject descent recurses until the stack runs out.
    /// </summary>
    [Fact]
    public void Audit_SelfInvokingFormXObject_Terminates()
    {
        byte[] pdf = SelfInvokingFormPdf();

        AccessibilityAuditReport report = RunWithinBudget(() => Audit(pdf));

        Assert.NotNull(report);
        Assert.DoesNotContain(report.Issues, i => i.Code == "UA00");
    }

    private static byte[] SelfInvokingFormPdf()
    {
        const string formContent = "BT /F1 10 Tf 10 10 Td (inner) Tj ET\nq /Fm0 Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
            StreamObject("", "q /Fm0 Do Q\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            // The trap: the form's /Resources name the form itself.
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >>", formContent),
        });
    }

    /// <summary>
    /// <b>Unbounded graphics-state stack.</b> A content stream of nothing but "q " pushes one
    /// state per two bytes and never pops. Uncapped, a stream well inside the decoder's own size
    /// limit allocates gigabytes; the sibling reading-order analyzer already capped the identical
    /// construct at 4096.
    /// </summary>
    [Fact]
    public void Tagger_ContentStreamOfNothingButQ_IsRefusedRatherThanGrowingTheStack()
    {
        var sb = new StringBuilder(200_000);
        for (int i = 0; i < 100_000; i++) sb.Append("q ");
        sb.Append("BT /F1 10 Tf 72 400 Td (body) Tj ET\n");

        byte[] content = Ascii(sb.ToString());

        PageTagResult result = RunWithinBudget(() => ContentStreamTagger.TagPage(content, TagOptions()));

        Assert.False(result.Changed);
        Assert.Same(content, result.NewContent);
        Assert.Contains("graphics state", result.SkipReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Quadratic /K expansion.</b> The visited set bounds how many times a node is expanded,
    /// not the cost of expanding one. N structure elements that all point /K at a single shared
    /// N-entry array cost N per node over N nodes. The edge budget bounds that, and RawKids
    /// aliases the array instead of copying it per node (the copy alone was O(nodes x kids) of
    /// retained memory).
    /// </summary>
    [Fact]
    public void Walker_ElementsSharingOneHugeKidsArray_IsBoundedByTheEdgeBudget()
    {
        const int count = 2_000;

        byte[] pdf = SharedKidsArrayPdf(count);

        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);
        PdfObjectIndex index = PdfObjectIndex.Build(doc);
        PdfDictionary structTree = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;

        // No clock: the guard's evidence is the walk's own report. count x count slots exceed the
        // edge ceiling, so the walk stops there and says so (the shared array also chains the
        // elements past the depth cap, which is the truncation reason it names first); an
        // unguarded walk would report no edge ceiling. The reports of this thread alone are read.
        int thisThread = Environment.CurrentManagedThreadId;
        var reports = new List<string>();
        void Handler(string message)
        {
            if (Environment.CurrentManagedThreadId == thisThread) lock (reports) reports.Add(message);
        }
        AccessibilityDiagnostics.OnEvent += Handler;
        IReadOnlyList<StructNode> nodes;
        string truncationReason;
        try
        {
            nodes = StructTreeWalker.Collect(structTree, doc, index, out truncationReason);
        }
        finally
        {
            AccessibilityDiagnostics.OnEvent -= Handler;
        }

        Assert.NotNull(truncationReason);
        Assert.Contains(reports, r => r.Contains("-edge ceiling", StringComparison.Ordinal));

        // Every element is reachable and expanded at most once; the shared array is walked, not
        // copied, so this completes rather than allocating count x count entries.
        Assert.True(nodes.Count <= count + 1,
            "no element may be expanded twice: " + nodes.Count.ToString(CultureInfo.InvariantCulture));
        Assert.True(nodes.Count > 1, "the fixture must actually contain elements");
    }

    /// <summary>
    /// <paramref name="count"/> structure elements, every one of whose /K is the same shared
    /// array listing all of them.
    /// </summary>
    private static byte[] SharedKidsArrayPdf(int count)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 >>",
            StreamObject("", "BT ET\n"),
            "<< /Type /StructTreeRoot /K 6 0 R >>",
        };

        // Object 6 is the shared array; elements start at object 7.
        var shared = new StringBuilder("[");
        for (int i = 0; i < count; i++)
            shared.Append((7 + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
        shared.Append(']');
        bodies.Add(shared.ToString());

        for (int i = 0; i < count; i++)
            bodies.Add("<< /Type /StructElem /S /P /K 6 0 R >>");

        return BuildPdf(bodies);
    }

    // =====================================================================
    //  8. Reading order: a partial pass must say so
    // =====================================================================

    /// <summary>
    /// An element with no resolvable marked-content identifier is silently dropped from the
    /// comparison, so a tree where almost nothing resolves reported Analyzed = true,
    /// IsOrdered = true — "I examined a fraction of your document and it looked fine",
    /// indistinguishable from a real pass.
    /// </summary>
    [Fact]
    public void AnalyzeReadingOrder_TreeWhereMostElementsCannotBePlaced_ReportsThePassAsPartial()
    {
        using var input = new MemoryStream(MostlyUnplaceablePdf());
        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(input);

        Assert.True(analysis.Analyzed);
        Assert.True(analysis.IsPartial,
            "placed " + analysis.ElementsPlaced.ToString(CultureInfo.InvariantCulture) + " of " +
            analysis.ElementsTotal.ToString(CultureInfo.InvariantCulture));
        Assert.True(analysis.ElementsPlaced < analysis.ElementsTotal);
        Assert.Contains("PARTIAL", analysis.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: when every element resolves, the pass is complete and must not be labelled
    /// partial — otherwise the flag would be noise.
    /// </summary>
    [Fact]
    public void AnalyzeReadingOrder_TreeWhereEveryElementResolves_IsNotPartial()
    {
        using var input = new MemoryStream(FullyMarkedPdf());
        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(input);

        Assert.True(analysis.Analyzed);
        Assert.False(analysis.IsPartial);
        Assert.Equal(analysis.ElementsTotal, analysis.ElementsPlaced);
    }

    /// <summary>One element carries a real MCID; three carry nothing that can be located.</summary>
    private static byte[] MostlyUnplaceablePdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Placed) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R 8 0 R 9 0 R 10 0 R] /ParentTree 11 0 R "
                + "/ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 6 0 R >>",
            "<< /Type /StructElem /S /P /P 6 0 R >>",
            "<< /Type /StructElem /S /P /P 6 0 R >>",
            "<< /Nums [0 [7 0 R]] >>",
        });

    // =====================================================================
    //  Shared audit helper
    // =====================================================================

    private static AccessibilityAuditReport Audit(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAccessibility.Audit(ms);
    }

    // =====================================================================
    //  Plan 05 — end to end: tag a form, then audit the result
    // =====================================================================

    /// <summary>
    /// A document whose text lives inside a Form XObject audits with <b>no UA17</b> after
    /// <c>AutoTag</c>. That is the definition of done for form-XObject tagging.
    /// </summary>
    /// <remarks>
    /// <para>The other plan-05 tests assert the <i>shape</i> of what was written — a
    /// <c>/StructParents</c> on the form, an <c>/MCR</c> carrying <c>/Stm</c>, MCIDs restarting at
    /// 0. This one asserts the <b>point</b>: that an independent reader, the deep checker, stops
    /// finding untagged text. Shape and effect are different claims, and only this one would catch
    /// a tree that is well-formed but that the checker cannot follow.</para>
    /// <para>It works as a cross-check because the two sides were built independently — the checker
    /// already followed forms long before the tagger could mark them.</para>
    /// </remarks>
    /// <remarks>
    /// <para><b>Not asserted against the untagged input</b>, which would look like the obvious
    /// before/after. UA17 runs only when the document <i>claims</i> to be tagged
    /// (<c>CheckUnmarkedPageContent</c> returns early otherwise) — on a plainly untagged file UA01
    /// says so once and repeating it per page would drown the report. So the untagged original
    /// yields no UA17 to contrast with, and a "before" assertion on it fails for a reason that has
    /// nothing to do with forms.</para>
    /// <para>The contrast that isolates the behaviour is the pair of <b>tagged outputs</b> below:
    /// both claim tagged, both went through <c>AutoTag</c>, and they differ only in whether the
    /// form is shared.</para>
    /// </remarks>
    [Fact]
    public void AutoTagThenAudit_TextInsideAForm_NoLongerReportsUntaggedText()
    {
        AccessibilityAuditReport after = Audit(AutoTagBytesToArray(FormXObjectPagePdf()));

        Assert.DoesNotContain(after.Issues, i => i.Code == "UA17");
    }

    /// <summary>
    /// A <b>shared</b> form's text is marked too, now that each invoking page gets its own copy —
    /// so UA17 is no longer reported for it either.
    /// </summary>
    /// <remarks>
    /// The end-to-end proof of plan 08 item 49. It works because the checker side was already
    /// right: UA17 follows <c>/XObject /Subtype /Form</c> invocations recursively. Note what it
    /// cannot see — had both pages been left pointing at <i>one</i> copy, the text would still be
    /// marked and UA17 would still be silent, while the second page's content resolved through the
    /// first page's parent-tree entry. That half is carried by the structural assertions in
    /// <c>SharedFormTaggingTests</c>.
    /// </remarks>
    [Fact]
    public void AutoTagThenAudit_SharedFormText_NoLongerReportsUntaggedText()
    {
        AccessibilityAuditReport after = Audit(AutoTagBytesToArray(TwoPagesSharingOneFormPdf()));

        Assert.DoesNotContain(after.Issues, i => i.Code == "UA17");
    }

    /// <summary>
    /// The control: with duplication switched off, a shared form's text stays untagged and UA17 is
    /// still reported.
    /// </summary>
    /// <remarks>
    /// Without it, the two "does not report UA17" assertions above would also pass if
    /// <c>AutoTag</c> suppressed UA17 wholesale rather than earned its absence. Switching
    /// duplication off gives the control something real to contrast against.
    /// </remarks>
    [Fact]
    public void AutoTagThenAudit_SharedFormWithDuplicationDisabled_StillReportsUntaggedText()
    {
        byte[] tagged = AutoTagBytesToArray(TwoPagesSharingOneFormPdf(),
            new AutoTagOptions { DuplicateSharedFormXObjects = false });

        Assert.Contains(Audit(tagged).Issues, i => i.Code == "UA17");
    }
}
