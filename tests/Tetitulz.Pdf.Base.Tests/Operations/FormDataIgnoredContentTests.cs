// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What an FDF or XFDF file carries beyond its fields, and the fact that the reader says so.
/// </summary>
/// <remarks>
/// <para><b>Annotations are imported now, so this file is about what is still left behind.</b>
/// It used to be about annotations too — an export from a comment review is mostly
/// <c>&lt;annots&gt;</c> with no fields at all, and reading one produced an empty
/// <see cref="FormData"/> a caller could not tell from an empty file. That was closed by importing
/// them (<see cref="AnnotationExchangeTests"/>), and the tests asserting they were <i>reported</i>
/// were rewritten rather than re-pointed: the behaviour genuinely changed.</para>
///
/// <para><b>What is still reported and not imported</b> is FDF's <c>/JavaScript</c>,
/// <c>/Pages</c>, <c>/Differences</c> and <c>/EmbeddedFDFs</c>. Each is named individually, because
/// dropped document-level JavaScript and a dropped page-differences dictionary are different
/// losses, and "something was ignored" cannot be acted on.</para>
/// </remarks>
public class FormDataIgnoredContentTests
{
    private static FormData ReadXfdf(string xml)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return PdfFormDataExchange.ReadXfdf(ms);
    }

    private static FormData ReadFdf(string fdf)
    {
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(fdf));
        return PdfFormDataExchange.ReadFdf(ms);
    }

    private const string XfdfHeader =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\">\n";

    // ================================================================ the control

    /// <summary>A file that is only fields reports nothing ignored.</summary>
    /// <remarks>
    /// The control: without it, an assertion that the slot is non-null elsewhere would pass for a
    /// reader that complains about every file.
    /// </remarks>
    [Fact]
    public void AFileOfOnlyFieldsReportsNothingIgnored()
    {
        FormData data = ReadXfdf(XfdfHeader +
            "<fields><field name=\"name\"><value>Ada</value></field></fields>\n</xfdf>");

        Assert.Equal("Ada", Assert.Single(data.Fields).Value);
        Assert.Null(PdfFormDataExchange.LastIgnoredContent);
    }

    /// <summary>An empty <c>&lt;annots/&gt;</c> carries nothing, so it is not reported.</summary>
    /// <remarks>
    /// Presence is not content. A reader that fired on the element rather than on what is inside it
    /// would complain about a file that lost nothing — the channel a caller learns to ignore.
    /// </remarks>
    [Fact]
    public void AnEmptyAnnotsElementIsNotReported()
    {
        ReadXfdf(XfdfHeader +
            "<annots/>\n<fields><field name=\"a\"><value>1</value></field></fields>\n</xfdf>");

        Assert.Null(PdfFormDataExchange.LastIgnoredContent);
    }

    // ================================================================ XFDF

    /// <summary>
    /// <b>An XFDF carrying annotations imports them, and reports nothing ignored.</b>
    /// </summary>
    /// <remarks>
    /// Replaces <c>AnXfdfWithAnnotationsReportsHowManyWereIgnored</c>, which asserted the opposite
    /// and was correct when it was written. The second half is the one worth keeping: a diagnostic
    /// that still fired would now be reporting a loss that did not happen, and a channel that cries
    /// wolf is one a caller stops reading.
    /// </remarks>
    [Fact]
    public void AnXfdfWithAnnotationsImportsThemAndReportsNothingIgnored()
    {
        FormData data = ReadXfdf(XfdfHeader +
            "<annots>\n" +
            "  <highlight page=\"0\" rect=\"1,2,3,4\"/>\n" +
            "  <text page=\"0\" rect=\"5,6,7,8\"/>\n" +
            "  <square page=\"1\" rect=\"9,9,9,9\"/>\n" +
            "</annots>\n" +
            "<fields><field name=\"a\"><value>1</value></field></fields>\n</xfdf>");

        Assert.Equal("1", Assert.Single(data.Fields).Value);
        Assert.Equal(3, data.Annotations.Count);

        Assert.Null(PdfFormDataExchange.LastIgnoredContent);
    }

    /// <summary>
    /// <b>The case the whole thing was for: an annotations-only export.</b>
    /// </summary>
    /// <remarks>
    /// No fields at all, which is what a comment review looks like. It used to come back as an
    /// empty <see cref="FormData"/> with a diagnostic saying what had been dropped; now the
    /// annotations are simply there, and the two files differ in their content rather than in a
    /// side channel. That is the better answer, and this test asserts it in the same place the
    /// weaker one used to.
    /// </remarks>
    [Fact]
    public void AnAnnotationsOnlyXfdfIsDistinguishableFromAnEmptyOne()
    {
        FormData withAnnots = ReadXfdf(XfdfHeader +
            "<annots><highlight page=\"0\" rect=\"1,2,3,4\"/></annots>\n</xfdf>");

        FormData empty = ReadXfdf(XfdfHeader + "<fields/>\n</xfdf>");

        Assert.Empty(withAnnots.Fields);
        Assert.Empty(empty.Fields);

        Assert.Single(withAnnots.Annotations);
        Assert.Empty(empty.Annotations);
        Assert.Null(PdfFormDataExchange.LastIgnoredContent);
    }

    // ================================================================ FDF

    /// <summary>An FDF carrying <c>/Annots</c> imports them and reports nothing.</summary>
    [Fact]
    public void AnFdfWithAnnotationsImportsThemAndReportsNothingIgnored()
    {
        FormData data = ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [2 0 R] /Annots [3 0 R 4 0 R] >> >>\nendobj\n" +
            "2 0 obj\n<< /T (a) /V (1) >>\nendobj\n" +
            "3 0 obj\n<< /Subtype /Highlight /Page 0 >>\nendobj\n" +
            "4 0 obj\n<< /Subtype /Text /Page 0 >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal("1", Assert.Single(data.Fields).Value);
        Assert.Equal(2, data.Annotations.Count);
        Assert.Null(PdfFormDataExchange.LastIgnoredContent);
    }

    /// <summary>
    /// <b>The other <c>/FDF</c> entries are named individually</b>, because they are different
    /// losses.
    /// </summary>
    /// <remarks>
    /// §12.7.7.2 Table 243 has thirteen entries and three are read. A caller told only "something
    /// was ignored" cannot tell a dropped comment from dropped document-level JavaScript.
    /// </remarks>
    [Fact]
    public void TheOtherFdfEntriesAreNamedIndividually()
    {
        ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [2 0 R] /JavaScript << /Before (x) >> " +
            "/Pages [3 0 R] /Differences 4 0 R >> >>\nendobj\n" +
            "2 0 obj\n<< /T (a) /V (1) >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page >>\nendobj\n" +
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        string reported = PdfFormDataExchange.LastIgnoredContent;

        Assert.Contains("/JavaScript", reported);
        Assert.Contains("/Pages", reported);
        Assert.Contains("/Differences", reported);
        Assert.DoesNotContain("annotation", reported);
    }

    /// <summary>An FDF of only fields reports nothing.</summary>
    [Fact]
    public void AnFdfOfOnlyFieldsReportsNothingIgnored()
    {
        FormData data = ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [2 0 R] >> >>\nendobj\n" +
            "2 0 obj\n<< /T (a) /V (1) >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Single(data.Fields);
        Assert.Null(PdfFormDataExchange.LastIgnoredContent);
    }

    // ================================================================ the slot's own contract

    /// <summary>The report does not survive into the next read.</summary>
    /// <remarks>
    /// The trigger is FDF's <c>/JavaScript</c> rather than an annotation, because annotations are
    /// imported now and no longer set this. It is the last <c>/FDF</c> entry that both reports and
    /// is easy to write, which is why it stands in for the family.
    /// </remarks>
    [Fact]
    public void TheReportDoesNotSurviveIntoTheNextRead()
    {
        ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /JavaScript << /Before (app.alert\\(1\\)) >> >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");
        Assert.NotNull(PdfFormDataExchange.LastIgnoredContent);

        ReadXfdf(XfdfHeader + "<fields><field name=\"a\"><value>1</value></field></fields>\n</xfdf>");

        Assert.Null(PdfFormDataExchange.LastIgnoredContent);
    }

    /// <summary>It is cleared before the read can throw, so a failure leaves no stale answer.</summary>
    [Fact]
    public void TheReportIsClearedEvenWhenTheReadThrows()
    {
        ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /JavaScript << /Before (app.alert\\(1\\)) >> >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");
        Assert.NotNull(PdfFormDataExchange.LastIgnoredContent);

        Assert.ThrowsAny<Exception>(() => ReadXfdf("this is not xml at all"));

        Assert.Null(PdfFormDataExchange.LastIgnoredContent);
    }

    /// <summary>
    /// <b>Only FDF has anything left to report, and the slot says nothing for XFDF.</b>
    /// </summary>
    /// <remarks>
    /// <para>This replaces <c>BothFormatsReportThroughTheSameSlot</c>, whose claim stopped being
    /// true: XFDF's four sections — <c>&lt;f&gt;</c>, <c>&lt;ids&gt;</c>, <c>&lt;fields&gt;</c> and
    /// <c>&lt;annots&gt;</c> — are all read now, so there is nothing left for it to skip. FDF's
    /// Table 243 still has nine entries this reader does not take.</para>
    /// <para><b>The asymmetry is asserted rather than left implicit</b>, because a null from XFDF
    /// now means "nothing was dropped" and used to mean "nothing was dropped <i>that this reader
    /// notices</i>". Those are different promises and only the first is worth making.</para>
    /// </remarks>
    [Fact]
    public void OnlyFdfHasContentLeftToReport()
    {
        ReadXfdf(XfdfHeader +
            "<annots><text page=\"0\" rect=\"1,2,3,4\"/></annots>\n" +
            "<fields><field name=\"a\"><value>1</value></field></fields>\n</xfdf>");

        Assert.Null(PdfFormDataExchange.LastIgnoredContent);

        ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /JavaScript << /Before (app.alert\\(1\\)) >> >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Contains("/JavaScript", PdfFormDataExchange.LastIgnoredContent);
    }
}
