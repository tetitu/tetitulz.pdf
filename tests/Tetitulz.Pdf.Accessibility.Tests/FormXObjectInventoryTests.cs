// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Core.Document;
using Xunit;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The document-wide inventory the form tagger
/// needs before it rewrites anything.
/// </summary>
/// <remarks>
/// The decisive case is a <b>shared</b> form. A Form XObject has one stream dictionary and so one
/// <c>/StructParents</c>, meaning its marked content can belong to exactly one parent-tree entry —
/// a form invoked from two pages cannot carry structure for both. That is why the inventory is a
/// document-wide pre-pass rather than something the per-page loop could work out as it goes.
/// </remarks>
public sealed class FormXObjectInventoryTests
{
    private const int MaxFormBytes = 4 * 1024 * 1024;

    private static IReadOnlyList<FormUsage> Collect(byte[] pdf) => Inventory(pdf).Forms;

    /// <summary>
    /// Scans each page for its <c>Do</c> names and hands them to the inventory.
    /// </summary>
    /// <remarks>
    /// The inventory deliberately does <b>not</b> decode pages itself — in production the names
    /// come from the per-page tagging pass, which has already applied the content ceilings. An
    /// earlier draft had it re-read every page, which reintroduced the decompression-bomb
    /// inflation those ceilings exist to prevent. Doing the page scan here keeps the test honest
    /// about that split.
    /// </remarks>
    private static FormInventory Inventory(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);
        PdfObjectIndex index = PdfObjectIndex.Build(doc);

        var invocations = new List<PageInvocations>();
        for (int pi = 0; pi < doc.Pages.Count; pi++)
        {
            PdfPage page = doc.Pages[pi];
            ContentScanResult scan = ContentStreamScanner.ScanDetailed(page.GetContentStreamData());

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ContentToken token in scan.Tokens)
            {
                if (token.Operator != "Do" || token.Operands.Count == 0) continue;
                if (token.Operands[token.Operands.Count - 1] is Core.Objects.PdfName name && seen.Add(name.Value))
                    names.Add(name.Value);
            }

            invocations.Add(new PageInvocations
            {
                PageIndex        = pi,
                PageObjectNumber = index.GetObjectNumber(page.Dictionary),

                // The page's EFFECTIVE resources, matching what the tagger passes: a page
                // inheriting from a /Pages node has no own /Resources key, and reading only that
                // key left every such page's forms uninventoried.
                Resources        = page.Resources,
                PageDictionary   = page.Dictionary,
                Names            = names,

                // These fixtures all model a page the tagger successfully re-marked.
                PageWasReMarked  = true,
            });
        }

        return FormXObjectInventory.Collect(doc, index, invocations, MaxFormBytes);
    }

    [Fact]
    public void UnresolvableInvocation_IsRecordedAsUnreadable()
    {
        // A name that resolves to nothing may have been a form full of text. Silence would read
        // as "there was none", so it is reported rather than skipped.
        FormInventory inventory = Inventory(DanglingInvocationPdf());

        Assert.Empty(inventory.Forms);
        KeyValuePair<int, string> entry = Assert.Single(inventory.Unreadable);
        Assert.Equal(0, entry.Key);
        Assert.Contains("does not resolve", entry.Value, StringComparison.Ordinal);
    }

    private static byte[] DanglingInvocationPdf() => Build(
        pages: new[] { "q /Missing Do Q" },
        pageXObjects: new[] { "/Fm0 {EXTRA0} 0 R" },
        extraObjects: new[]
        {
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50]", "0 0 1 rg"),
        });

    [Fact]
    public void FormWithTextOnOnePage_IsACandidate()
    {
        FormUsage form = Assert.Single(Collect(OnePageOneFormPdf()));

        Assert.True(form.HasText);
        Assert.Null(form.SkipReason);
        Assert.True(form.IsCandidate);
        Assert.Equal(new[] { 0 }, form.InvokingPageIndexes);
        Assert.True(form.PageObjectNumber > 0, "the single invoking page must be identified");
    }

    /// <summary>
    /// A form drawn by two pages is recorded as <b>two invocations</b>, each naming the page, the
    /// resource name it was reached under, and that it was reached directly rather than through
    /// another form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inventory records facts and the duplicator decides, because a shared form <i>can</i> be
    /// tagged when every page can be given its own copy; <c>SharedFormTaggingTests</c> holds the
    /// decision.
    /// </para>
    /// <para>
    /// <c>DirectFromPage</c> and the recorded names are what make duplication possible at all: a
    /// copy is substituted by repointing the invoking page's own <c>/XObject</c> entry, which can
    /// only be done for a form the page reaches through its own resources.
    /// </para>
    /// </remarks>
    [Fact]
    public void FormInvokedFromTwoPages_RecordsBothInvocationsAsDirectFromTheirPage()
    {
        FormUsage form = Assert.Single(Collect(TwoPagesSharingOneFormPdf()));

        Assert.Equal(new[] { 0, 1 }, form.InvokingPageIndexes);
        Assert.Equal(2, form.Invocations.Count);

        Assert.All(form.Invocations, inv =>
        {
            Assert.True(inv.DirectFromPage);
            Assert.False(inv.ReachedThroughAnotherForm);
            Assert.Equal(new[] { "Fm0" }, inv.DirectNames);
            Assert.True(inv.PageObjectNumber > 0, "each invoking page must be identified");
        });
    }

    [Fact]
    public void FormWithoutText_IsNotACandidate()
    {
        // Nothing to tag. Reported as a non-candidate rather than as a skip: there is no gap.
        FormUsage form = Assert.Single(Collect(VectorOnlyFormPdf()));

        Assert.False(form.HasText);
        Assert.Null(form.SkipReason);
        Assert.False(form.IsCandidate);
    }

    [Fact]
    public void FormAlreadyCarryingMarkedContent_IsSkipped()
    {
        // Double-marking is worse than not marking: the MCIDs would collide with the existing ones.
        FormUsage form = Assert.Single(Collect(AlreadyMarkedFormPdf()));

        Assert.False(form.IsCandidate);
        Assert.Contains("already carries marked content", form.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void OneFormInvokedTwiceFromTheSamePage_IsCountedOnce()
    {
        // Keyed by object number, not by resource name, and the page recorded once — otherwise the
        // same stream would be spliced twice.
        FormUsage form = Assert.Single(Collect(OnePageInvokingOneFormTwicePdf()));

        Assert.Equal(new[] { 0 }, form.InvokingPageIndexes);
        Assert.True(form.IsCandidate);
    }

    [Fact]
    public void SelfReferencingForm_TerminatesAndIsStillInventoried()
    {
        FormUsage form = Assert.Single(Collect(SelfReferencingFormPdf()));
        Assert.Equal(new[] { 0 }, form.InvokingPageIndexes);
    }

    [Fact]
    public void NestedForm_IsAttributedToTheInvokingPage()
    {
        var forms = Collect(NestedFormPdf()).OrderBy(f => f.ObjectNumber).ToList();

        Assert.Equal(2, forms.Count);
        Assert.All(forms, f => Assert.Equal(new[] { 0 }, f.InvokingPageIndexes));
        Assert.Contains(forms, f => f.HasText);   // the inner one carries the text
    }

    [Fact]
    public void NestingBeyondTheDepthCap_StopsAndIsRecordedAsUnreadable()
    {
        // The cap lived on Internal.FormXObjectTextProbe, a type with no callers at all: the walk
        // shipped here instead and the probe survived only because this constant was aliased from
        // it. Deleting it moved the literal, so the refusal is pinned here — silently walking
        // deeper (or refusing sooner) is exactly what a moved constant gets wrong, and neither
        // shows up in a compile.
        FormInventory inventory = Inventory(DeepFormChainPdf(ChainLength));

        // Refused *at* depth MaxDepth + 1, so everything shallower was still inventoried.
        Assert.Equal(FormXObjectInventory.MaxDepth + 1, inventory.Forms.Count);
        Assert.True(inventory.Forms.Count < ChainLength,
            "the fixture must be deeper than the cap, or the refusal is never reached");

        KeyValuePair<int, string> refusal = Assert.Single(inventory.Unreadable);
        Assert.Equal(0, refusal.Key);
        Assert.Contains("nesting exceeded 12 levels", refusal.Value, StringComparison.Ordinal);

        // The deepest form is the only one carrying text, and it is on the far side of the cap:
        // the refusal is reported precisely because content like this went unexamined.
        Assert.DoesNotContain(inventory.Forms, f => f.HasText);
    }

    /// <summary>Comfortably past <see cref="FormXObjectInventory.MaxDepth"/>, so the cap decides.</summary>
    private const int ChainLength = FormXObjectInventory.MaxDepth + 3;

    /// <summary>
    /// A page invoking form 0, which invokes form 1, … <paramref name="length"/> deep. Only the
    /// last form draws text, so a walk that quietly ran past the cap would be visible as a
    /// text-carrying form in the inventory.
    /// </summary>
    private static byte[] DeepFormChainPdf(int length)
    {
        var forms = new string[length];
        for (int i = 0; i < length; i++)
        {
            bool last = i == length - 1;
            forms[i] = last
                ? Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] " +
                         "/Resources << /Font << /F1 {FONT} 0 R >> >>",
                         "BT /F1 10 Tf 10 10 Td (bottom of the chain) Tj ET")
                : Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] " +
                         "/Resources << /XObject << /Fm" + (i + 1) + " {EXTRA" + (i + 1) + "} 0 R >> >>",
                         "q /Fm" + (i + 1) + " Do Q");
        }

        return Build(
            pages: new[] { "q /Fm0 Do Q" },
            pageXObjects: new[] { "/Fm0 {EXTRA0} 0 R" },
            extraObjects: forms);
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static byte[] OnePageOneFormPdf() => Build(
        pages: new[] { "q /Fm0 Do Q" },
        pageXObjects: new[] { "/Fm0 {EXTRA0} 0 R" },
        extraObjects: new[]
        {
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /Font << /F1 {FONT} 0 R >> >>",
                   "BT /F1 10 Tf 10 10 Td (inside the form) Tj ET"),
        });

    private static byte[] TwoPagesSharingOneFormPdf() => Build(
        pages: new[] { "q /Fm0 Do Q", "q /Fm0 Do Q" },
        pageXObjects: new[] { "/Fm0 {EXTRA0} 0 R" },
        extraObjects: new[]
        {
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /Font << /F1 {FONT} 0 R >> >>",
                   "BT /F1 10 Tf 10 10 Td (shared) Tj ET"),
        });

    private static byte[] VectorOnlyFormPdf() => Build(
        pages: new[] { "q /Fm0 Do Q" },
        pageXObjects: new[] { "/Fm0 {EXTRA0} 0 R" },
        extraObjects: new[]
        {
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50]", "0 0 1 rg 0 0 10 10 re f"),
        });

    private static byte[] AlreadyMarkedFormPdf() => Build(
        pages: new[] { "q /Fm0 Do Q" },
        pageXObjects: new[] { "/Fm0 {EXTRA0} 0 R" },
        extraObjects: new[]
        {
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /Font << /F1 {FONT} 0 R >> >>",
                   "/P <</MCID 0>> BDC BT /F1 10 Tf 10 10 Td (marked) Tj ET EMC"),
        });

    private static byte[] OnePageInvokingOneFormTwicePdf() => Build(
        pages: new[] { "q /Fm0 Do Q q 1 0 0 1 0 60 cm /Fm0 Do Q" },
        pageXObjects: new[] { "/Fm0 {EXTRA0} 0 R" },
        extraObjects: new[]
        {
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /Font << /F1 {FONT} 0 R >> >>",
                   "BT /F1 10 Tf 10 10 Td (twice) Tj ET"),
        });

    private static byte[] SelfReferencingFormPdf() => Build(
        pages: new[] { "q /Fm0 Do Q" },
        pageXObjects: new[] { "/Fm0 {EXTRA0} 0 R" },
        extraObjects: new[]
        {
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] " +
                   "/Resources << /Font << /F1 {FONT} 0 R >> /XObject << /Fm0 {EXTRA0} 0 R >> >>",
                   "BT /F1 10 Tf 10 10 Td (loop) Tj ET q /Fm0 Do Q"),
        });

    private static byte[] NestedFormPdf() => Build(
        pages: new[] { "q /Outer Do Q" },
        pageXObjects: new[] { "/Outer {EXTRA0} 0 R" },
        extraObjects: new[]
        {
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /XObject << /Inner {EXTRA1} 0 R >> >>",
                   "0 0 1 rg q /Inner Do Q"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /Font << /F1 {FONT} 0 R >> >>",
                   "BT /F1 10 Tf 10 10 Td (deep) Tj ET"),
        });

    // ------------------------------------------------------------------

    private static string Stream(string dictEntries, string data)
        => $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>
    /// Builds a document and substitutes <c>{FONT}</c> and <c>{EXTRA0}</c>, <c>{EXTRA1}</c>, … for
    /// the object numbers those objects actually receive.
    /// </summary>
    /// <remarks>
    /// The first draft hardcoded the numbers, which silently broke the moment a fixture used two
    /// pages: the layout shifts, <c>/Fm0 {EXTRA0} 0 R</c> started pointing at a content stream, and the
    /// inventory correctly found no forms — a fixture failure that reads exactly like a code
    /// failure. Numbers that depend on the layout have to be computed by whatever owns the layout.
    /// </remarks>
    private static byte[] Build(string[] pages, string[] pageXObjects, string[] extraObjects)
    {
        int n = pages.Length;
        var bodies = new List<string>();

        int firstPage = 3;
        int firstContent = firstPage + n;
        int fontNumber = firstContent + n;

        var kids = new StringBuilder();
        for (int i = 0; i < n; i++) kids.Append(firstPage + i).Append(" 0 R ");

        // Every placeholder any fixture may use, resolved from the layout computed above.
        string Fix(string s)
        {
            if (s == null) return null;
            s = s.Replace("{FONT}", fontNumber.ToString());
            for (int e = 0; e < extraObjects.Length; e++)
                s = s.Replace("{EXTRA" + e + "}", (fontNumber + 1 + e).ToString());
            return s;
        }

        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {n} >>");

        for (int i = 0; i < n; i++)
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                       $"/Contents {firstContent + i} 0 R /Resources << /Font << /F1 {fontNumber} 0 R >> " +
                       $"/XObject << {Fix(string.Join(" ", pageXObjects))} >> >> >>");

        for (int i = 0; i < n; i++) bodies.Add(Stream("", pages[i]));

        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (int e = 0; e < extraObjects.Length; e++) bodies.Add(Fix(extraObjects[e]));

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 1; i <= bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i).Append(" 0 obj\n").Append(bodies[i - 1]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
