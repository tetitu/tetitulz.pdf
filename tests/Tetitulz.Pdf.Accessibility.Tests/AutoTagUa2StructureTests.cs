// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Which PDF/UA-2 structural rules the auto-tagger's own output already satisfies.
/// </summary>
/// <remarks>
/// <para><b>These are the authoring side judged by the checking side</b>, which is the only
/// independent oracle available: the two share no code, so a rule the tagger breaks shows up as a
/// finding rather than as agreement between a writer and a reader that were written together.</para>
///
/// <para><b>What this pins is deliberately narrow.</b> It asserts the <i>structural</i> clauses —
/// the ones whose satisfaction is a property of the tree the tagger emits, not of whether it
/// understood the content: one <c>Document</c> as the structure tree root's only child
/// (§8.2.5.2), numbered headings rather than the bare <c>H</c> type (§8.2.5.12), and no
/// <c>Note</c> (§8.2.5.14.1). It does <b>not</b> assert PDF/UA-2 conformance, which needs semantic
/// roles no structural test can supply.</para>
/// </remarks>
public class AutoTagUa2StructureTests
{
    private static byte[] AutoTagged(byte[] pdf, PdfUaVersion target)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var tagged = new MemoryStream();
        PdfAccessibility.AutoTag(input, tagged, new AutoTagOptions { Target = target });
        return tagged.ToArray();
    }

    private static AccessibilityAuditReport AutoTagThenAuditAsUa2(byte[] pdf)
    {
        using var tagged = new MemoryStream(AutoTagged(pdf, PdfUaVersion.Ua2), writable: false);
        return PdfAccessibility.Audit(tagged, new AuditOptions { Target = PdfUaVersion.Ua2 });
    }

    private static string[] CodesStartingWith(AccessibilityAuditReport report, string prefix) =>
        report.Issues
              .Where(i => i.Code != null && i.Code.StartsWith(prefix, System.StringComparison.Ordinal))
              .Select(i => i.Code + ": " + i.Message)
              .ToArray();

    /// <summary>
    /// §8.2.5.2 — the structure tree root's only child is a single <c>Document</c> element.
    /// </summary>
    [Fact]
    public void AutoTaggedOutput_HasASingleDocumentRootChild()
    {
        AccessibilityAuditReport report = AutoTagThenAuditAsUa2(MinimalPdfBuilder.CreateMultiPagePdf(3));

        string[] found = CodesStartingWith(report, "UA2-8.2.5.2");
        Assert.True(found.Length == 0, string.Join("\n", found));
    }

    /// <summary>
    /// §8.2.5.12 — numbered heading types, never the bare <c>H</c>.
    /// </summary>
    [Fact]
    public void AutoTaggedOutput_UsesNumberedHeadingsNotBareH()
    {
        AccessibilityAuditReport report = AutoTagThenAuditAsUa2(MinimalPdfBuilder.CreateMultiPagePdf(3));

        string[] found = CodesStartingWith(report, "UA2-8.2.5.12");
        Assert.True(found.Length == 0, string.Join("\n", found));
    }

    /// <summary>
    /// §8.2.5.14.1 — <c>FENote</c> replaces <c>Note</c>, which must not appear.
    /// </summary>
    [Fact]
    public void AutoTaggedOutput_EmitsNoNoteStructureType()
    {
        AccessibilityAuditReport report = AutoTagThenAuditAsUa2(MinimalPdfBuilder.CreateMultiPagePdf(3));

        string[] found = CodesStartingWith(report, "UA2-8.2.5.14");
        Assert.True(found.Length == 0, string.Join("\n", found));
    }

    /// <summary>
    /// <b>Under PDF/UA-1 the namespace is not written.</b> A PDF/UA-1 document must not start
    /// carrying PDF 2.0 namespaces because the tagger learned how to write them — so the same
    /// document tagged for part 1 still fails the part-2 namespace check, and that failure is the
    /// evidence the switch does something.
    /// </summary>
    [Fact]
    public void TaggingForPart1_DoesNotWriteThePdf2Namespace()
    {
        using var tagged = new MemoryStream(
            AutoTagged(MinimalPdfBuilder.CreateMultiPagePdf(3), PdfUaVersion.Ua1), writable: false);

        AccessibilityAuditReport report =
            PdfAccessibility.Audit(tagged, new AuditOptions { Target = PdfUaVersion.Ua2 });

        // The finding must be the NAMESPACE one specifically. §8.2.5.2 also covers a missing
        // structure tree root and a wrong root-child count, so asserting merely that the clause
        // fired would pass on a tagger that emitted no tree at all.
        Assert.Contains(CodesStartingWith(report, "UA2-8.2.5.2"),
                        f => f.IndexOf("namespace", System.StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// The structure tree root <b>declares</b> the namespace, not merely the elements referencing
    /// it — ISO 32000-2 Table 354 makes <c>/Namespaces</c> the array of every namespace dictionary
    /// the tree uses.
    /// </summary>
    /// <remarks>
    /// <b>Asserted on the written object graph because no checker covers it.</b> Deleting the line
    /// that writes <c>/Namespaces</c> leaves every element pointing at a namespace dictionary the
    /// root never declares, and the PDF/UA-2 structure checker passes that document — it reads the
    /// <c>Document</c> element's own <c>/NS</c> and stops there. So the audit cannot be the oracle
    /// for this one, and a mutation removing the declaration survived until this test existed.
    /// </remarks>
    [Fact]
    public void TheStructureTreeRootDeclaresTheNamespace()
    {
        byte[] tagged = AutoTagged(MinimalPdfBuilder.CreateMultiPagePdf(3), PdfUaVersion.Ua2);

        using var stream = new MemoryStream(tagged, writable: false);
        using var doc = PdfDocument.Open(stream);

        var structTreeRoot =
            doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]) as PdfDictionary;
        Assert.NotNull(structTreeRoot);

        var namespaces = doc.ResolveObject(structTreeRoot[PdfName.Namespaces]) as PdfArray;
        Assert.NotNull(namespaces);

        bool declaresPdf2 = namespaces
            .Select(entry => doc.ResolveObject(entry) as PdfDictionary)
            .Any(ns => ns != null
                       && (ns[PdfName.NS] as PdfString)?.GetText() == "http://iso.org/pdf2/ssn");

        Assert.True(declaresPdf2,
                    "the structure tree root's /Namespaces does not declare the PDF 2.0 standard " +
                    "structure namespace, so every element's /NS points at an undeclared dictionary");
    }

    /// <summary>
    /// The control, without which the three above could all pass because the PDF/UA-2 checker never
    /// ran at all: the audit really is a PDF/UA-2 audit, and it really did examine this document.
    /// </summary>
    [Fact]
    public void TheAuditThatJudgesThemIsAPdfUa2Audit()
    {
        AccessibilityAuditReport report = AutoTagThenAuditAsUa2(MinimalPdfBuilder.CreateMultiPagePdf(3));

        Assert.Equal(PdfUaVersion.Ua2, report.ConformanceTarget);
        Assert.NotEmpty(report.Issues);
        Assert.Contains(report.Issues, i => i.Code != null
                                            && i.Code.StartsWith("UA2-", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// §8.2.2 and §8.3.1 over a page holding text, two filled paths, a shading, and an image XObject the caller
    /// describes: the tagged output leaves none of them outside the structure tree and outside an artifact —
    /// the paths and the shading are layout artifacts, the image a figure. The input, untagged, has the paths
    /// and the image reported: the control that the check reads this page.
    /// </summary>
    [Fact]
    public void AutoTaggedOutput_LeavesNoPaintedContentUntagged()
    {
        byte[] pdf = PaintedPage();

        string[] before = CodesStartingWith(PdfAccessibility.Audit(new MemoryStream(pdf), new AuditOptions { Target = PdfUaVersion.Ua2 }), "UA2-8.2.2");
        Assert.Contains(before, f => f.Contains("a path"));
        Assert.Contains(before, f => f.Contains("an image"));
        Assert.Contains(before, f => f.Contains("a shading"));

        using var input = new MemoryStream(pdf, writable: false);
        using var tagged = new MemoryStream();
        PdfAccessibility.AutoTag(input, tagged, new AutoTagOptions { Target = PdfUaVersion.Ua2, FigureAlternateText = _ => "A grey square" });
        tagged.Position = 0;
        string[] after = CodesStartingWith(PdfAccessibility.Audit(tagged, new AuditOptions { Target = PdfUaVersion.Ua2 }), "UA2-8.2.2");
        Assert.True(after.Length == 0, string.Join("\n", after));
    }

    /// <summary>
    /// An image the caller does not describe stays out of the tree, and is reported — the tagger does not call
    /// a picture decoration on its own — while the page's paths and shading are artifacts all the same.
    /// </summary>
    [Fact]
    public void AutoTaggedOutput_LeavesAnUndescribedImageReported_AndItsPathsArtifacts()
    {
        string[] after = CodesStartingWith(AutoTagThenAuditAsUa2(PaintedPage()), "UA2-8.2.2");
        Assert.Contains(after, f => f.Contains("an image"));
        Assert.DoesNotContain(after, f => f.Contains("a path") || f.Contains("a shading"));
    }

    /// <summary>Text, two paths painted one after the other, a shading under a clip, and an image XObject.</summary>
    private static byte[] PaintedPage()
    {
        const string content = "BT /F1 12 Tf 20 150 Td (Hello world) Tj ET 20 20 60 40 re f 1 0 0 RG 0 0 m 200 0 l S "
                             + "q 0 0 50 50 re W n /Sh0 sh Q "
                             + "q 40 0 0 40 120 20 cm /Im0 Do Q";
        return PdfUa2StructureCheckerTests.BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /XObject << /Im0 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\na\nendstream",
        });
    }
}
