// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Three reads whose null answer is <b>indistinguishable from a true negative</b>, which is what
/// makes them expensive: "this annotation is not a link", "this action has no type" and "this layer
/// is not hidden" are all the correct answer for most documents.
/// </summary>
/// <remarks>
/// <para><b><c>/Subtype</c> on an annotation drops the link outright.</b> The annotation is
/// resolved on the line above and <c>/Rect</c> on the line below goes through
/// <c>ResolveArray</c> — only the subtype was raw. The <c>continue</c> fires before the annotation
/// becomes a <c>PdfLink</c>, so the URL and its clickable region are lost to link checkers and to
/// PDF-to-HTML conversion.</para>
///
/// <para><b><c>/S</c> on the action reports the link as pointing nowhere.</b> The action dictionary
/// is resolved on the line above and <c>/URI</c> and <c>/D</c> below go through
/// <c>ResolveString</c>/<c>ResolveDest</c>. A null type matches neither branch, so the link is
/// reported with kind <c>Unknown</c> and no <c>Uri</c> while its <c>/Rect</c> is correct — a
/// clickable region that appears to lead nowhere is worse than one that is missing, because it
/// looks like a fact about the document.</para>
///
/// <para><b><c>/OFF</c> inverts the visibility of every layer.</b> <c>IsInOffList</c> was the one
/// read in <c>PdfOcgManager</c> still following nothing — its <c>/OCGs</c>, <c>/D</c> and
/// <c>/ON</c> siblings all resolve. A null makes it return <see langword="false"/> for <b>every</b>
/// OCG, so <c>ListLayers</c> reports every layer <c>DefaultVisible = true</c>, including the ones
/// the file hides. That is the opposite of the truth for a caller auditing hidden content.</para>
/// </remarks>
public class IndirectExtractionReadTests
{
    /// <summary>Which key the link fixture writes as an indirect reference.</summary>
    public enum LinkSpelling
    {
        /// <summary>Both inline — the control, which always worked.</summary>
        Direct,

        /// <summary>The annotation's <c>/Subtype 9 0 R</c>.</summary>
        IndirectSubtype,

        /// <summary>The action's <c>/S 10 0 R</c>.</summary>
        IndirectActionType,
    }

    [Theory]
    [InlineData(LinkSpelling.Direct)]
    [InlineData(LinkSpelling.IndirectSubtype)]
    [InlineData(LinkSpelling.IndirectActionType)]
    public void TheUriLinkIsExtracted_WhicheverWayItIsSpelled(LinkSpelling spelling)
    {
        using var ms = new MemoryStream(LinkPdf(spelling));
        IReadOnlyList<PdfLink> links = PdfLinkExtractor.ExtractLinks(ms);

        var link = Assert.Single(links);

        // The URL and the kind, not merely that a link was reported: an annotation reported with
        // kind Unknown and no Url is exactly what the /S defect produces.
        Assert.Equal(PdfLinkKind.Uri, link.Kind);
        Assert.Equal("https://example.invalid/target", link.Uri);
    }

    /// <summary>
    /// The floor: a page whose only annotation is not a link yields none, so the assertion above
    /// cannot be met by an extractor that reports every annotation.
    /// </summary>
    [Fact]
    public void ANonLinkAnnotation_IsNotExtracted()
    {
        using var ms = new MemoryStream(LinkPdf(LinkSpelling.Direct, subtype: "/Square"));
        IReadOnlyList<PdfLink> links = PdfLinkExtractor.ExtractLinks(ms);

        Assert.Empty(links);
    }

    /// <summary>
    /// A layer the document hides is reported hidden whichever way <c>/OFF</c> is spelled.
    /// </summary>
    /// <remarks>
    /// The direct case is the control. Note the failure direction: unresolved, the layer is
    /// reported <b>visible</b> — so a test asserting only that <c>ListLayers</c> returned the layer
    /// passes with the defect intact.
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /OFF [5 0 R]
    [InlineData(true)]   // /OFF 9 0 R
    public void AHiddenLayerIsReportedHidden_WhicheverWayOffIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(OcgPdf(indirect));
        IReadOnlyList<OcgInfo> layers = PdfOcgManager.ListLayers(ms);

        var hidden = Assert.Single(layers, l => l.Name == "Hidden");
        Assert.False(hidden.DefaultVisible,
            "the layer the document lists in /OFF was reported visible");
    }

    /// <summary>
    /// The floor for the layer test: a layer that is <i>not</i> in <c>/OFF</c> must be reported
    /// visible, so "reported hidden" is a claim about <c>/OFF</c> rather than a constant.
    /// </summary>
    [Fact]
    public void ALayerNotInTheOffList_IsReportedVisible()
    {
        using var ms = new MemoryStream(OcgPdf(indirect: false));
        IReadOnlyList<OcgInfo> layers = PdfOcgManager.ListLayers(ms);

        var shown = Assert.Single(layers, l => l.Name == "Shown");
        Assert.True(shown.DefaultVisible);
    }

    /// <summary>
    /// A layer whose <c>/Name</c> is its own object is listed, hidden and renamed by that name.
    /// </summary>
    /// <remarks>
    /// Listing, hiding and renaming each read the name raw, so this layer listed as "(unnamed)" and
    /// neither of the other two could find it. They now share one resolving read; each is asserted,
    /// since a helper nobody calls would satisfy one of them.
    /// </remarks>
    [Fact]
    public void ALayerWithAnIndirectName_IsListedHiddenAndRenamedByIt()
    {
        byte[] pdf = OcgPdf(indirect: false, indirectName: true);

        using (var ms = new MemoryStream(pdf))
            Assert.Single(PdfOcgManager.ListLayers(ms), l => l.Name == "Shown");

        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
            Assert.Equal(1, PdfOcgManager.SetLayerVisibility(input, output, "Shown", visible: false));

        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            Assert.Equal(1, PdfOcgManager.RenameLayer(input, output, "Shown", "Renamed"));
            output.Position = 0;
            Assert.Single(PdfOcgManager.ListLayers(output), l => l.Name == "Renamed");
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// One page carrying a single URI link annotation. Objects 9 and 10 hold the two names;
    /// <paramref name="spelling"/> selects which the document points at rather than writing inline.
    /// </summary>
    private static byte[] LinkPdf(LinkSpelling spelling, string subtype = "/Link")
    {
        string subtypeEntry = spelling == LinkSpelling.IndirectSubtype ? "9 0 R" : subtype;
        string actionType = spelling == LinkSpelling.IndirectActionType ? "10 0 R" : "/URI";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Annots [5 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype " + subtypeEntry + " /Rect [10 10 90 30] /A 6 0 R >>",
            "<< /Type /Action /S " + actionType + " /URI (https://example.invalid/target) >>",
            "null", "null",
            subtype,
            "/URI");
    }

    /// <summary>
    /// Two optional-content groups, one of which the default configuration hides. Object 9 holds
    /// the <c>/OFF</c> array when <paramref name="indirect"/> — the only difference.
    /// </summary>
    private static byte[] OcgPdf(bool indirect, bool indirectName = false)
    {
        string off = indirect ? "9 0 R" : "[5 0 R]";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R]"
                + " /D << /Order [5 0 R 6 0 R] /OFF " + off + " >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /OCG /Name (Hidden) >>",
            indirectName ? "<< /Type /OCG /Name 7 0 R >>" : "<< /Type /OCG /Name (Shown) >>",
            indirectName ? "(Shown)" : "null",
            "null",
            "[5 0 R]");
    }

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
