// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Metadata;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A stream's decode is what its size is learned from, so a guard behind the decode has already paid:
/// each of these decodes under what is left, a refused decode spends the budget too, and what a file's
/// numbers can make a pass write stays in the range the pass counts in.
/// </summary>
public sealed class HostileDecodeCostTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static byte[] Zlib(string text, long repeat)
    {
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] unit = Encoding.ASCII.GetBytes(text);
            var buffer = new byte[65536 - 65536 % unit.Length];
            for (int i = 0; i < buffer.Length; i++) buffer[i] = unit[i % unit.Length];
            for (long left = repeat * unit.Length; left > 0; left -= buffer.Length)
                z.Write(buffer, 0, (int)Math.Min(left, buffer.Length));
        }
        return deflated.ToArray();
    }

    private static string FlateForm(string text, long repeat)
    {
        byte[] data = Zlib(text, repeat);
        return $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Filter /FlateDecode /Length {data.Length} >>\nstream\n{Latin1(data)}\nendstream";
    }

    /// <summary>A stream refused on its declared length, at no cost of its own: only the charge is left to see.</summary>
    private static string DeclaredHugeForm() =>
        "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Filter /ASCIIHexDecode /DL 999999999999 /Length 3 >>\nstream\n20>\nendstream";

    private static string Plain(string content) =>
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    /// <summary>Objects numbered from 1 in order; the first is the catalog.</summary>
    private static byte[] Pdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>A page that draws each form in turn; the forms are objects 5 up.</summary>
    private static byte[] PageDrawing(IReadOnlyList<string> forms)
    {
        string names = string.Concat(forms.Select((_, i) => $"/F{i} {5 + i} 0 R "));
        string ops = string.Concat(forms.Select((_, i) => $"/F{i} Do\n"));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << {names} >> >> >>",
            Plain(ops),
        };
        bodies.AddRange(forms);
        return Pdf(bodies);
    }

    private static List<string> Reported(Action act)
    {
        var events = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Sink(string text) { if (Environment.CurrentManagedThreadId == thread) lock (events) events.Add(text); }
        AccessibilityDiagnostics.OnEvent += Sink;
        try { act(); }
        finally { AccessibilityDiagnostics.OnEvent -= Sink; }
        return events;
    }

    // ---------------------------------------------------------------- the form inventory

    private static FormInventory Inventory(byte[] pdf, int maxFormBytes)
    {
        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);
        PdfObjectIndex index = PdfObjectIndex.Build(doc);
        PdfPage page = doc.Pages[0];
        var names = new List<string>();
        foreach (ContentToken token in ContentStreamScanner.ScanDetailed(page.GetContentStreamData()).Tokens)
            if (token.Operator == "Do" && token.Operands[token.Operands.Count - 1] is PdfName name) names.Add(name.Value);

        return FormXObjectInventory.Collect(doc, index, new List<PageInvocations>
        {
            new PageInvocations
            {
                PageIndex        = 0,
                PageObjectNumber = index.GetObjectNumber(page.Dictionary),
                Resources        = page.Resources,
                PageDictionary   = page.Dictionary,
                Names            = names,
                PageWasReMarked  = true,
            },
        }, maxFormBytes);
    }

    /// <summary>
    /// <b>A form past the limit is stopped there and spends half of what is left.</b> Each of sixty
    /// costs a kilobyte under a kilobyte's ceiling, so the budget of 32 MiB falls below the ceiling after
    /// fifteen and decides the rest. A pass that decoded each whole first, or charged nothing, would report
    /// the tagging limit to the last.
    /// </summary>
    [Fact]
    public void AdversarialFormsPastTheLimit_SpendTheBudgetAndAreNotDecodedWhole()
    {
        byte[] pdf = PageDrawing(Enumerable.Range(0, 60).Select(_ => FlateForm(" ", 4096)).ToList());

        FormInventory inventory = null;
        List<string> events = Reported(() => inventory = Inventory(pdf, 1024));

        List<FormUsage> forms = inventory.Forms.OrderBy(f => f.ObjectNumber).ToList();
        Assert.Equal(60, forms.Count);
        Assert.All(forms, f => Assert.True(f.TextUndetermined));
        Assert.All(forms, f => Assert.Null(f.Content));
        Assert.Contains("tagging limit", forms[0].SkipReason, StringComparison.Ordinal);
        Assert.Contains("form-scanning budget", forms[59].SkipReason, StringComparison.Ordinal);
        Assert.Equal(44, forms.Count(f => f.SkipReason.Contains("form-scanning budget", StringComparison.Ordinal)));

        // The decoder's own refusal: the measurement behind a whole decode would not have raised one.
        Assert.Contains(events, e => e.Contains("was not decoded", StringComparison.Ordinal) && e.Contains("limit", StringComparison.Ordinal));
    }

    /// <summary>The control: forms that fit are read, and spend only what they hold.</summary>
    [Fact]
    public void FormsThatFit_AreReadAndSpendOnlyWhatTheyHold()
    {
        byte[] pdf = PageDrawing(Enumerable.Range(0, 60).Select(_ => FlateForm("BT ET ", 16)).ToList());

        FormInventory inventory = Inventory(pdf, 1024);

        Assert.Equal(60, inventory.Forms.Count);
        Assert.All(inventory.Forms, f => Assert.NotNull(f.Content));
        Assert.All(inventory.Forms, f => Assert.DoesNotContain("limit", f.SkipReason ?? "", StringComparison.Ordinal));
        Assert.All(inventory.Forms, f => Assert.DoesNotContain("budget", f.SkipReason ?? "", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the deep audit

    private static int RefusedFormScans(byte[] pdf)
    {
        List<string> events = Reported(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfAccessibility.Audit(ms);
        });
        return events.Count(e => e.Contains("bytes the audit has left for forms", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>A form that will not decode spends half the audit's form budget</b>, its cost being the
    /// decoder's to know: the second form, 20 MiB, would fit in the 32 MiB that was there and does not fit
    /// in what a refusal leaves. <b>One past the budget ends the scanning of forms</b>, so the second is
    /// not decoded at all. The control is the second alone.
    /// </summary>
    [Fact]
    public void AdversarialAuditForm_RefusedSpendsTheBudgetForTheNext()
    {
        const string Corrupt =
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Filter /FlateDecode /Length 6 >>\nstream\nvvvvvv\nendstream";
        string over = FlateForm(" ", 33L * 1024 * 1024);
        string fits = FlateForm(" ", 20L * 1024 * 1024);

        Assert.Equal(0, RefusedFormScans(PageDrawing(new[] { fits })));
        Assert.Equal(1, RefusedFormScans(PageDrawing(new[] { Corrupt })));
        Assert.Equal(2, RefusedFormScans(PageDrawing(new[] { Corrupt, fits })));
        Assert.Equal(1, RefusedFormScans(PageDrawing(new[] { over, fits })));
    }

    // ---------------------------------------------------------------- PDF/UA-2's references into appearances

    private static PdfUa2StructureResult Ua2(string appearance, string kids)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en) /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Stamp /Rect [0 0 10 10] /AP << /N 5 0 R >> >>",
            appearance,
            "<< /Type /StructTreeRoot /K 7 0 R >>",
            "<< /Type /StructElem /S /Annot /P 6 0 R /Pg 3 0 R /K " + kids + " >>",
        };

        using var ms = new MemoryStream(Pdf(bodies));
        return PdfUa2StructureChecker.Validate(ms);
    }

    /// <summary>
    /// <b>§8.9.2.1 is judged from the structure tree, so no appearance stream is decoded for it.</b>
    /// The appearance's data is not Flate data and will not decode; a reference into it is reported,
    /// and no part of the check is said to be missing. The control names the same stream from an
    /// <c>/OBJR</c>, which includes the annotation whole.
    /// </summary>
    [Fact]
    public void AdversarialAppearanceThatWillNotDecode_IsJudgedByTheTreeAlone()
    {
        const string Corrupt =
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Filter /FlateDecode /Length 6 >>\nstream\nvvvvvv\nendstream";

        var hostile = Ua2(Corrupt, "<< /Type /MCR /Stm 5 0 R /MCID 0 >>");
        Assert.Contains(hostile.Violations, v => v.Code == "UA2-8.9.2.1" && v.Message.Contains("marked-content reference", StringComparison.Ordinal));
        Assert.DoesNotContain(hostile.Violations, v => v.Code == "UA2-99" && v.Message.Contains("§8.9.2.1", StringComparison.Ordinal));

        var control = Ua2(Corrupt, "<< /Type /OBJR /Obj 4 0 R >>");
        Assert.DoesNotContain(control.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    // ---------------------------------------------------------------- the font scan

    /// <summary>
    /// <b>A refused decode spends half of what the glyph scan has left</b>, each named here by a declared
    /// length past any budget, so thirty of them run out twenty million steps; three do not.
    /// </summary>
    [Theory]
    [InlineData(30, true)]
    [InlineData(3, false)]
    public void AdversarialRefusedStreams_SpendTheGlyphScansBudget(int forms, bool truncated)
    {
        byte[] pdf = PageDrawing(Enumerable.Range(0, forms).Select(_ => DeclaredHugeForm()).ToList());
        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);

        var scan = global::Tetitulz.Pdf.Accessibility.Fonts.PdfFontEmbeddingRemediator.CollectUsedCharacterCodes(doc);

        Assert.Equal(truncated, scan.Truncated);
    }

    // ---------------------------------------------------------------- the tagger's keys

    private static byte[] Tagged(string treeEntries, string nums, string structParents = "")
    {
        return Pdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> {structParents} >>",
            Plain("BT /F1 12 Tf 10 100 Td (Hello) Tj ET\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /StructTreeRoot /K 8 0 R /ParentTree 7 0 R {treeEntries} >>",
            $"<< /Nums [{nums}] >>",
            "<< /Type /StructElem /S /P /P 6 0 R >>",
        });
    }

    private static (AutoTagResult Result, string Output) Tag(byte[] pdf, AutoTagOptions options)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(input, output, options);
        return (result, Encoding.Latin1.GetString(output.ToArray()));
    }

    /// <summary>
    /// <b>A claim near the top of the range leaves no keys to count up from.</b> Carrying on writes
    /// negative ones. The bound is half the range: one below it tags, and the key it writes is the base.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1073741824)]
    public void AdversarialParentTreeNextKey_NearTheTop_LeavesTheDocumentUntagged(int next)
    {
        var (result, output) = Tag(Tagged($"/ParentTreeNextKey {next}", "0 [null]"), new AutoTagOptions());

        Assert.Equal(1, result.PagesSkipped);
        Assert.Equal(0, result.ElementsAdded);
        Assert.Contains(result.Warnings, w => w.Contains("leaves no room", StringComparison.Ordinal));
        Assert.DoesNotContain("/StructParents", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/ParentTreeNextKey -", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AParentTreeNextKeyAtTheBound_IsCountedFrom()
    {
        const int Bound = int.MaxValue / 2;

        var (result, output) = Tag(Tagged($"/ParentTreeNextKey {Bound}", "0 [null]"), new AutoTagOptions());

        Assert.Equal(0, result.PagesSkipped);
        Assert.True(result.ElementsAdded > 0);
        Assert.Contains("/StructParents " + Bound, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The keys in a tree being merged into are read as <c>long</c>, past the range of the
    /// <c>int</c> a narrowing cast would wrap to a small positive base.
    /// </summary>
    [Theory]
    [InlineData("2147483647 [null]")]
    [InlineData("4294967296 [null] 1 [8 0 R]")]
    public void AdversarialMergeWithAHighKey_LeavesTheDocumentUntagged(string nums)
    {
        var (result, output) = Tag(Tagged("", nums), new AutoTagOptions { MergeIntoExistingTree = true });

        Assert.Equal(1, result.PagesSkipped);
        Assert.Contains(result.Warnings, w => w.Contains("leaves no room", StringComparison.Ordinal));
        Assert.DoesNotContain("/StructParents", output, StringComparison.Ordinal);
    }

    /// <summary>A real number is no key: the page holding one is not the tree's, though its integer part is one.</summary>
    [Theory]
    [InlineData("/StructParents 1.5", false)]
    [InlineData("/StructParents 1", true)]
    public void OnlyAnIntegerKeyClaimsAPage(string entry, bool claimed)
    {
        using var ms = new MemoryStream(Tagged("", "1 [8 0 R]", entry));
        using PdfDocument doc = PdfDocument.Open(ms);

        ExistingStructureTree tree = ExistingStructureTree.Read(doc, doc.Catalog.Dictionary);

        Assert.Equal(claimed, tree.ClaimsPage(doc, doc.Pages[0].Dictionary));
    }

    // ---------------------------------------------------------------- the XMP packet

    /// <summary>
    /// <b>A title holding a character XML forbids is a packet, not an exception</b>; it goes through a
    /// serializer that throws on one. The packet parses and the rest of the title is there.
    /// </summary>
    [Fact]
    public void AdversarialTitleWithAControlCharacter_MakesAPacketAndSaysSo()
    {
        string packet = null;
        List<string> events = Reported(() => packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(null, 1, "a" + (char)1 + "b", "en" + (char)2));

        XDocument parsed = XDocument.Parse(packet);
        Assert.Contains(parsed.Descendants(), e => e.Value == "a" + (char)0xFFFD + "b");
        Assert.Contains(events, e => e.Contains("title held a character XML forbids", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Contains("language held a character XML forbids", StringComparison.Ordinal));
    }

    [Fact]
    public void ATitleXmlAllows_IsWrittenAsItIsAndNothingIsSaid()
    {
        string title = "T" + char.ConvertFromUtf32(0x1F600);
        string packet = null;
        List<string> events = Reported(() => packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(null, 1, title, "en"));

        Assert.Contains(XDocument.Parse(packet).Descendants(), e => e.Value == title);
        Assert.DoesNotContain(events, e => e.Contains("XML forbids", StringComparison.Ordinal));
    }
}
