// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Three reads in <c>Core</c> whose null answer is not "absent" but a <b>different, wrong
/// answer</b>: a phantom page, a black gradient, and a page box the document did not ask for.
/// </summary>
/// <remarks>
/// <para><b><c>/Type</c> in <c>PdfPageTree</c> re-opens a defect that was already closed once.</b>
/// The leaf predicate's <c>type != PdfName.Pages</c> clause exists so that an emptied branch —
/// <c>&lt;&lt; /Type /Pages /Count 0 /MediaBox […] &gt;&gt;</c>, exactly what a page-removal tool
/// leaves behind — is not collected as a page. A null <c>type</c> makes that clause <i>hold</i>, so
/// the branch becomes a <b>phantom page</b> and every later page index is off by one. The code's
/// own comment describes the damage; it just could not survive an indirect <c>/Type</c>.</para>
///
/// <para><b><c>/FunctionType</c> was the one raw read left in a file explicitly converted to
/// reference-following reads.</b> Unresolved it becomes <c>-1</c>, which falls to <c>default:</c>
/// and returns <c>{0f}</c> <i>before</i> any evaluator runs — so <c>LastType0Failure</c> is left
/// null too. A shading whose function type is indirect evaluates to black everywhere and reports
/// nothing at all.</para>
///
/// <para><b><c>PdfViewerPreferences</c> could not resolve anything, by construction.</b> The type
/// held only a <c>PdfDictionary</c> and no <c>PdfDocument</c>, so every entry inside a resolved
/// <c>/ViewerPreferences</c> read as absent. That is not cosmetic: <c>/PrintArea</c> and
/// <c>/ViewArea</c> feed <c>PdfPageRenderer.ResolveRenderBox</c>, the single page-box decision both
/// renderers share, so an indirect one silently fell back to the CropBox default in the raster path
/// and the SVG export alike — agreeing with each other, which is why the parity suite is blind to
/// it.</para>
/// </remarks>
public class IndirectCoreReadTests
{
    /// <summary>
    /// An emptied <c>/Pages</c> branch is not collected as a page, whichever way its <c>/Type</c>
    /// is spelled. <c>false</c> is the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Type /Pages
    [InlineData(true)]   // /Type 9 0 R
    public void AnEmptiedBranchIsNotAPhantomPage_WhicheverWayTypeIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(EmptiedBranchPdf(indirect));
        using var doc = PdfDocument.Open(ms);

        // One real page, and it is the REAL one: the phantom's /MediaBox is 999 wide, so asserting
        // the box distinguishes "the branch was skipped" from "the branch replaced the page".
        Assert.Single(doc.Pages);
        Assert.Equal(200, (int)((doc.Pages[0].MediaBox[2] as PdfNumber)!.DoubleValue));
    }

    /// <summary>
    /// The floor: the fixture's emptied branch really is reachable from the page tree, so "one
    /// page" is a claim about the predicate rather than about a tree that never contained it.
    /// </summary>
    [Fact]
    public void TheFixtureReallyContainsTheEmptiedBranch()
    {
        using var ms = new MemoryStream(EmptiedBranchPdf(indirect: false));
        using var doc = PdfDocument.Open(ms);

        var root = doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.Pages]) as PdfDictionary;
        var kids = doc.ResolveObject(root![PdfName.Kids]) as PdfArray;
        Assert.Equal(2, kids!.Count);
    }

    /// <summary>
    /// The viewer preferences' names are read whichever way they are spelled.
    /// </summary>
    /// <remarks>
    /// <b>The dictionary itself may be indirect too, and that is a separate read.</b> The entries
    /// are resolved by <c>PdfViewerPreferences</c>; <c>/ViewerPreferences</c> is resolved by
    /// <c>PdfCatalog</c>, one level up. A sweep found the outer read reached by nothing — in Core's
    /// suite or in Base's, where <c>/PrintArea</c> and <c>/ViewArea</c> feed
    /// <c>PdfPageRenderer.ResolveRenderBox</c> — because every fixture wrote the dictionary inline.
    /// The four cases below are the two reads crossed, so neither can hide the other.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]  // /ViewerPreferences << /PrintArea /MediaBox … >>
    [InlineData(true, false)]   // /ViewerPreferences << /PrintArea 9 0 R … >>
    [InlineData(false, true)]   // /ViewerPreferences 12 0 R, entries inline
    [InlineData(true, true)]    // both the dictionary and its entries indirect
    public void ViewerPreferencesAreRead_WhicheverWayTheyAreSpelled(bool indirect, bool dictionaryIndirect)
    {
        using var ms = new MemoryStream(ViewerPrefsPdf(indirect, dictionaryIndirect));
        using var doc = PdfDocument.Open(ms);

        var prefs = doc.Catalog!.GetViewerPreferences();

        Assert.Equal("MediaBox", prefs.PrintArea);
        Assert.Equal(3, prefs.NumCopies);
        Assert.True(prefs.DisplayDocTitle);

        // /PrintPageRange had no caller and no test at all — kept because it is one of §12.2's
        // eighteen preferences and removing a single member of a complete set makes the API
        // arbitrary rather than smaller, but pinned here so it is no longer merely present.
        Assert.NotNull(prefs.PrintPageRange);
        Assert.Equal(2, prefs.PrintPageRange!.Count);
    }

    /// <summary>
    /// A page's <c>/Annots</c> is read whichever way it is spelled.
    /// </summary>
    /// <remarks>
    /// <para>Unresolved, the page reads as having <b>no annotations at all</b> — which is a legal
    /// state, so nothing can report it. Links stop working, form fields and signature widgets
    /// vanish from every walk that starts at the page, and the document still renders. A producer
    /// writing <c>/Annots</c> as a reference is ordinary: the array is built once and shared.</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Annots [6 0 R]
    [InlineData(true)]   // /Annots 5 0 R
    public void AnnotationsAreRead_WhicheverWayAnnotsIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(AnnotsPdf(indirect));
        using var doc = PdfDocument.Open(ms);

        PdfArray annots = doc.Pages[0].GetAnnotations();

        Assert.NotNull(annots);
        Assert.Single(annots);
    }

    /// <summary>
    /// The honest negative: a page that really has no <c>/Annots</c> answers null, so the test
    /// above is pinning the resolution rather than "always returns an array".
    /// </summary>
    [Fact]
    public void APageWithNoAnnots_AnswersNull()
    {
        using var ms = new MemoryStream(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"));
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.Pages[0].GetAnnotations());
    }

    /// <summary>
    /// A function evaluates whichever way its <c>/FunctionType</c> is spelled.
    /// </summary>
    /// <remarks>
    /// <para>Asserted through the evaluator directly rather than through a render. The failure also
    /// has <b>no diagnostic</b> — <c>-1</c> falls to <c>default:</c> and returns <c>{0f}</c> before
    /// any evaluator runs, so <c>LastType0Failure</c> stays null — but that is not asserted here
    /// and saying so is the honest version: the slot is written only on the Type 0 path, so its
    /// being null proves nothing about a Type 2 function. The claim under test is the value.</para>
    ///
    /// <para>The function is a Type 2 exponential from 0 to 1, so a correct evaluation at
    /// <c>t = 0.5</c> is <c>0.5</c> and the broken one is <c>0</c> — which is also a legitimate
    /// output value, hence the second sample at <c>t = 1</c>.</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /FunctionType 2
    [InlineData(true)]   // /FunctionType <indirect>
    public void AFunctionEvaluates_WhicheverWayFunctionTypeIsSpelled(bool indirect)
    {
        var fn = new PdfDictionary();
        fn[PdfName.FunctionType] = indirect ? new PdfReference(9, 0) : (PdfObject)new PdfNumber(2);
        fn[PdfName.Domain] = Numbers(0, 1);
        fn[PdfName.C0] = Numbers(0);
        fn[PdfName.C1] = Numbers(1);
        fn[PdfName.N] = new PdfNumber(1);

        PdfObject Resolve(PdfObject o) =>
            o is PdfReference r && r.ReferenceObjectNumber == 9 ? new PdfNumber(2) : o;

        float[] half = Tetitulz.Pdf.Core.Functions.PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f }, Resolve);
        float[] one = Tetitulz.Pdf.Core.Functions.PdfFunctionEvaluator.Evaluate(fn, new[] { 1f }, Resolve);

        Assert.Equal(0.5, half[0], 3);
        Assert.Equal(1.0, one[0], 3);
    }

    // ------------------------------------------------------------------

    private static PdfArray Numbers(params double[] values)
    {
        var a = new PdfArray();
        foreach (double v in values) a.Add(new PdfNumber(v));
        return a;
    }

    /// <summary>
    /// A page tree whose root has two kids: one real page, and an emptied <c>/Pages</c> branch with
    /// no <c>/Kids</c> and its own <c>/MediaBox</c> — the shape a page-removal tool leaves behind.
    /// Object 9 holds the branch's <c>/Type</c> when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] EmptiedBranchPdf(bool indirect)
    {
        string type = indirect ? "9 0 R" : "/Pages";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type " + type + " /Parent 2 0 R /Count 0 /MediaBox [0 0 999 999] >>",
            "null", "null", "null",
            "/Pages");
    }

    /// <summary>
    /// A catalog carrying viewer preferences. Objects 9, 10 and 11 hold three of the values;
    /// <paramref name="indirect"/> points the dictionary at them instead of writing them inline.
    /// </summary>
    private static byte[] ViewerPrefsPdf(bool indirect, bool dictionaryIndirect = false)
    {
        string printArea = indirect ? "9 0 R" : "/MediaBox";
        string numCopies = indirect ? "10 0 R" : "3";
        string displayTitle = indirect ? "11 0 R" : "true";

        string prefs = "<< /PrintArea " + printArea + " /NumCopies " + numCopies
                       + " /DisplayDocTitle " + displayTitle + " /PrintPageRange [1 4] >>";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences "
                + (dictionaryIndirect ? "12 0 R" : prefs) + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "null", "null", "null", "null",
            "/MediaBox",
            "3",
            "true",
            // Object 12: the preferences dictionary itself, when the catalog points at it.
            dictionaryIndirect ? prefs : "null");
    }

    /// <summary>
    /// A one-page file whose <c>/Annots</c> is written inline or as object 5, which holds the same
    /// one-element array. Object 6 is the annotation.
    /// </summary>
    private static byte[] AnnotsPdf(bool indirect) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots "
            + (indirect ? "5 0 R" : "[6 0 R]") + " >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "[6 0 R]",
        "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>");

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
