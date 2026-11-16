// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// An action's <c>/S</c> must be followed on the <b>cloned</b> graph — the one graph in this
/// register where resolving against the source document would be actively wrong.
/// </summary>
/// <remarks>
/// <para><c>DeepClone</c> rewrites every reference into the output's numbering before either of
/// these walks runs, so an object number no longer means what it meant in the source. Both walkers
/// already had the means to follow it — the splitter's <c>Deref(…, byNum)</c>, used on the lines
/// either side of the defect, and the merger's <c>remappedObjects</c>, from which a lookup is built
/// once. Neither needed a new design.</para>
///
/// <para><b>The splitter kept a link to a page it had just dropped.</b> With <c>/S</c> read as null
/// the <c>GoTo</c> test failed, the action was not pruned, and the extract carried
/// <c>/A &lt;&lt; /S &lt;ref&gt; /D [null /Fit] &gt;&gt;</c> — a link whose target page did not
/// survive the split.</para>
/// </remarks>
public class IndirectActionTypeTests
{
    /// <summary>
    /// Extracting page 1 prunes a <c>GoTo</c> action aimed at the dropped page, whichever way the
    /// action's <c>/S</c> is spelled. <c>false</c> is the control.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /S /GoTo
    [InlineData(true)]   // /S 10 0 R
    public void ADanglingGoToIsPruned_WhicheverWaySIsSpelled(bool indirect)
    {
        using var input = new MemoryStream(LinkedPagesPdf(indirect));
        using var output = new MemoryStream();

        PdfSplitter.SplitRange(input, output, 1, 1);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        Assert.NotNull(annots);

        var widget = doc.ResolveObject(annots![0]) as PdfDictionary;
        Assert.NotNull(widget);

        // The action named a page the split dropped, so it must be gone entirely — not kept with a
        // destination pointing at null.
        Assert.Null(doc.ResolveObject(widget![PdfName.A]));
    }

    /// <summary>
    /// The floor: an action whose destination <i>did</i> survive must be kept, so the assertion
    /// above is a claim about dangling targets rather than about pruning every action.
    /// </summary>
    [Fact]
    public void AGoToAimedAtTheKeptPageSurvives()
    {
        using var input = new MemoryStream(LinkedPagesPdf(indirect: false, targetSecondPage: false));
        using var output = new MemoryStream();

        PdfSplitter.SplitRange(input, output, 1, 1);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        var widget = doc.ResolveObject(annots![0]) as PdfDictionary;

        // Not merely "an action survived" — the pattern-3 guard rejected that, and rightly: an
        // action kept with a destination pointing at nothing would satisfy it. This asserts the
        // action is still a GoTo and that its destination still names a real page.
        var action = doc.ResolveObject(widget![PdfName.A]) as PdfDictionary;
        Assert.NotNull(action);
        Assert.Equal("GoTo", (doc.ResolveObject(action![PdfName.S]) as PdfName)?.Value);

        var dest = doc.ResolveObject(action[PdfName.D]) as PdfArray;
        Assert.NotNull(dest);
        Assert.IsType<PdfDictionary>(doc.ResolveObject(dest![0]));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Two pages; page 1 carries a link with a <c>GoTo</c> action. Object 10 holds the action type
    /// when <paramref name="indirect"/>; <paramref name="targetSecondPage"/> chooses whether the
    /// destination is the page the split drops.
    /// </summary>
    private static byte[] LinkedPagesPdf(bool indirect, bool targetSecondPage = true)
    {
        string s = indirect ? "10 0 R" : "/GoTo";
        string target = targetSecondPage ? "5 0 R" : "3 0 R";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Annots [7 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 6 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A 8 0 R >>",
            "<< /Type /Action /S " + s + " /D [" + target + " /Fit] >>",
            "null",
            "/GoTo");
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
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
