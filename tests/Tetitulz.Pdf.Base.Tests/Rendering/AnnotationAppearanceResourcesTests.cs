// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §12.7.3.3 — which resource dictionary an annotation's appearance stream runs against: its own
/// <c>/Resources</c>, else the AcroForm's <c>/DR</c>, else the page's.
/// </summary>
/// <remarks>
/// <para><b>The rule was implemented twice and tested zero times.</b> Both renderers carried the
/// same three-branch ladder verbatim; after merging them into
/// <c>AnnotationAppearance.ResolveResources</c>, deleting the <c>/DR</c> branch outright left the
/// whole Base suite — 3 104 tests — green. A widget whose appearance names a font only the AcroForm
/// declares is the most ordinary form-field shape there is, and nothing rendered one.</para>
///
/// <para><b>These assert the decision, not a rendered pixel, and the first draft did the
/// opposite.</b> That version showed text in a font reachable only through <c>/DR</c> and counted
/// dark pixels. Its own floor case — the font in <b>neither</b> place — then failed: the renderer
/// substitutes a default typeface for an unresolvable font name, so text draws either way and "ink
/// appeared" is not evidence that a resource was found. Two of the three tests were vacuous and the
/// third caught it. Extracting the ladder is what made the honest test possible: the decision is
/// now a function that can be asked directly.</para>
/// </remarks>
public class AnnotationAppearanceResourcesTests
{
    /// <summary>An appearance stream with its own <c>/Resources</c> uses them.</summary>
    [Fact]
    public void ItsOwnResourcesWin()
    {
        var (doc, page) = Open(ApResources: "/Resources 9 0 R");

        var resolved = AnnotationAppearance.ResolveResources(ApStream(doc), doc, page);

        Assert.NotNull(resolved);
        Assert.True(resolved.ContainsKey(PdfName.Font), "expected the AP stream's own /Resources");
        Assert.Equal("own", NameOf(resolved));
        doc.Dispose();
    }

    /// <summary>
    /// <b>The rule.</b> With no <c>/Resources</c> of its own the stream inherits the AcroForm's
    /// <c>/DR</c> — not the page's, which is the last resort and would silently supply a different
    /// font set.
    /// </summary>
    [Fact]
    public void WithNoResourcesTheAcroFormDrIsInherited()
    {
        var (doc, page) = Open(ApResources: null);

        var resolved = AnnotationAppearance.ResolveResources(ApStream(doc), doc, page);

        Assert.Equal("dr", NameOf(resolved));
        doc.Dispose();
    }

    /// <summary>
    /// The last resort, and the floor that stops the test above passing for an implementation that
    /// always returns the page's resources: with no <c>/DR</c> the page's are correct.
    /// </summary>
    [Fact]
    public void WithNoDrThePageResourcesAreUsed()
    {
        var (doc, page) = Open(ApResources: null, withDr: false);

        var resolved = AnnotationAppearance.ResolveResources(ApStream(doc), doc, page);

        Assert.Equal("page", NameOf(resolved));
        doc.Dispose();
    }

    /// <summary>
    /// A <c>/Resources</c> that is present but does not resolve to a dictionary yields <c>null</c>
    /// rather than falling through to <c>/DR</c>. The stream stated its resources and the statement
    /// is broken; substituting a different dictionary would draw with the wrong fonts instead of
    /// with none. Both original copies behaved this way — this pins it as a contract rather than
    /// leaving it as an accident.
    /// </summary>
    [Fact]
    public void AnUnusableResourcesEntryYieldsNullRatherThanTheDr()
    {
        var (doc, page) = Open(ApResources: "/Resources 42");

        Assert.Null(AnnotationAppearance.ResolveResources(ApStream(doc), doc, page));
        doc.Dispose();
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Each candidate dictionary carries a distinct <c>/Marker</c> name, so the assertions say
    /// <i>which</i> one came back rather than merely that something did.
    /// </summary>
    private static string NameOf(PdfDictionary d) => (d?[new PdfName("Marker")] as PdfName)?.Value;

    private static PdfDictionary ApStream(PdfDocument doc)
        => doc.ResolveObject(new PdfReference(7, 0)) as PdfDictionary;

    private static (PdfDocument doc, PdfPage page) Open(string ApResources, bool withDr = true)
    {
        var doc = PdfDocument.Open(new MemoryStream(Pdf(ApResources, withDr)));
        return (doc, doc.Pages[0]);
    }

    private static byte[] Pdf(string apResources, bool withDr)
    {
        string acroForm = withDr
            ? " /AcroForm << /Fields [6 0 R] /DR 10 0 R >>"
            : " /AcroForm << /Fields [6 0 R] >>";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R" + acroForm + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Resources 11 0 R /Annots [6 0 R] >>",
            StreamObj("\n"),
            "null",
            "<< /Type /Annot /Subtype /Widget /Rect [20 20 180 60] /AP << /N 7 0 R >> >>",
            StreamObj("\n", "/Type /XObject /Subtype /Form /BBox [0 0 160 40]"
                            + (apResources != null ? " " + apResources : "")),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Marker /own  /Font << /Helv 8 0 R >> >>",
            "<< /Marker /dr   /Font << /Helv 8 0 R >> >>",
            "<< /Marker /page /Font << /Helv 8 0 R >> >>");
    }

    private static string StreamObj(string data, string extraKeys = null) =>
        "<< " + (extraKeys != null ? extraKeys + " " : "") +
        "/Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
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
