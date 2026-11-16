// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The sanitizer must strip an action whose <c>/S</c> is written as an <b>indirect reference</b>.
/// </summary>
/// <remarks>
/// <para><b>A security property, not a correctness one.</b> Every action test in
/// <c>PdfSanitizer</c> read <c>/S</c> with <c>PdfDictionary.GetName</c>, which tests the raw value.
/// §7.3.10 permits <c>/S 12 0 R</c>, and whoever supplied the file chooses how to write it — so the
/// type could be made unreadable for free. <b>Every null branch in that class means "keep":</b>
/// <c>ShouldRemoveAction</c> returns <see langword="false"/>, and <c>SanitizeDictionary</c>'s own
/// <c>/S</c> branch passes its <c>ContainsKey</c> guard and then skips every removal. A sanitizer
/// that fails open is worse than none, because the caller believes the document was cleaned.</para>
///
/// <para><b>Why a Launch action and not JavaScript — this took two wrong fixtures to find.</b>
/// Inline JavaScript is removed by an <i>earlier, unconditional</i> branch
/// (<c>if (RemoveJavaScript &amp;&amp; dict.ContainsKey(PdfName.JS)) dict.Remove(PdfName.JS)</c>),
/// so a <c>/JS</c> payload dies whatever <c>/S</c> says. <c>Launch</c>, <c>SubmitForm</c> and
/// <c>URI</c> are the ones whose removal depends <b>entirely</b> on the <c>/S</c> classification:
/// nothing else touches a Launch action's <c>/F</c>.</para>
///
/// <para><b>And the action hangs off an annotation, not the catalog's <c>/OpenAction</c>.</b>
/// <c>RemoveOpenAction</c> is on by default and strips that entry whatever the action type is, so
/// an <c>/OpenAction</c> fixture passes with the defect intact. Both wrong fixtures were caught by
/// the mutation and by nothing else — each asserted something true that it had not tested.</para>
/// </remarks>
public class SanitizerIndirectActionTypeTests
{
    /// <summary>The Launch action's target. Nothing but the <c>/S</c> rule removes it.</summary>
    private const string Payload = "cmd.exe";

    /// <summary>
    /// A Launch action reached through an annotation is stripped whichever way <c>/S</c> is
    /// spelled. The direct spelling is the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /S 7 0 R
    [InlineData(false)]  // /S /Launch
    public void LaunchAction_IsStripped_WhicheverWaySIsSpelled(bool indirect)
    {
        Assert.False(AnnotationStillHasAction(LaunchActionPdf(indirect), null),
            "the annotation kept its Launch action after sanitisation");
    }

    /// <summary>
    /// The floor: with Launch removal switched off the annotation really does keep its action — so
    /// the assertion above cannot be met by an implementation that detaches every <c>/A</c>, and
    /// the fixture demonstrably carries the action as far as the writer.
    /// </summary>
    [Fact]
    public void WithLaunchRemovalDisabled_TheActionSurvives()
    {
        Assert.True(
            AnnotationStillHasAction(
                LaunchActionPdf(indirect: false),
                new SanitizeOptions { RemoveLaunchActions = false }));
    }

    /// <summary>
    /// Sanitises and reports whether page 0's annotation still carries an <c>/A</c>.
    /// </summary>
    /// <remarks>
    /// <b>Asserted on the structure, not on the output bytes.</b> Removal detaches the action from
    /// the annotation and clears its <c>/S</c>; it does not delete the now-orphaned action
    /// <i>object</i>, which the sanitizer still renumbers and writes out with its <c>/F</c> intact.
    /// So a byte search for the payload fails even when sanitisation worked — as the first version
    /// of this test discovered, by failing on its own control.
    /// </remarks>
    private static bool AnnotationStillHasAction(byte[] pdf, SanitizeOptions options)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, options);

        using var ms = new MemoryStream(output.ToArray());
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        var annots = doc.ResolveObject(
            doc.Pages[0].Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.Annots]) as Tetitulz.Pdf.Core.Objects.PdfArray;
        Assert.NotNull(annots);

        var annot = doc.ResolveObject(annots![0]) as Tetitulz.Pdf.Core.Objects.PdfDictionary;
        Assert.NotNull(annot);

        return annot![Tetitulz.Pdf.Core.Objects.PdfName.A] != null;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A link annotation whose <c>/A</c> is a Launch action. Object 7 carries the action type name
    /// when <paramref name="indirect"/>; it is the only difference between the two fixtures.
    /// </summary>
    private static byte[] LaunchActionPdf(bool indirect)
    {
        string s = indirect ? "7 0 R" : "/Launch";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Annots [6 0 R] >>",
            StreamObj("q Q\n"),
            "<< /Type /Action /S " + s + " /F (" + Payload + ") >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A 5 0 R >>",
            "/Launch");
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
