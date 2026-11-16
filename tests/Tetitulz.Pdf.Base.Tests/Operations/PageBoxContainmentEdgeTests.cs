// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §14.11.2 containment at its <b>edges</b> — the half of each test the existing fixtures never
/// decide, and the shapes a <i>stored</i> rectangle can take that stop it being readable at all.
/// </summary>
/// <remarks>
/// <para><b>Why a whole file of edges.</b> A mutation run over <c>PdfPageManipulator</c> left eleven
/// decisions standing against the <i>entire</i> <c>Base.Tests</c> suite, and they have one cause in
/// common: containment is a conjunction of four comparisons, and every existing fixture violates
/// <b>several</b> of them at once. <c>SetPageBox_RefusesASubBoxOutsideTheMediaBox</c> passes
/// <c>(-50, -50, 800, 900)</c>, which is outside on the low side <i>and</i> the high side — so
/// deleting the two low-side comparisons changes nothing it can see. The same is true in the
/// reverse direction, where the existing fixture only ever shrinks the high side.</para>
///
/// <para><b>Why it matters that these are silent.</b> A renderer intersects the boxes rather than
/// complaining (§14.11.2), so a containment rule that fails open produces a file which draws as
/// though the call never happened. There is no exception to see and no visible corruption — the
/// page is simply cropped to something the caller did not ask for.</para>
///
/// <para>The <c>/Parent</c> ring guard is deliberately <b>not</b> pinned here. With the depth cap in
/// place a ring walks to the cap and answers <c>false</c>; with the visited set it answers
/// <c>false</c> at once. The answer is identical and only the work differs, which this repository
/// does not accept a wall clock as proof of.</para>
/// </remarks>
public class PageBoxContainmentEdgeTests
{
    // ── the low side of each comparison ───────────────────────────────────────────

    /// <summary>
    /// A sub-box wholly <b>below and left</b> of the <c>/MediaBox</c> origin, and inside it on the
    /// high side. Only the two low-side comparisons can refuse this.
    /// </summary>
    [Fact]
    public void SetPageBox_RefusesASubBoxThatFallsBelowTheMediaBoxOrigin()
    {
        using var input = new MemoryStream(Letter());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.TrimBox, -50, -50, 100, 100));

        Assert.Contains("14.11.2", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The reverse direction's low side: a new <c>/MediaBox</c> whose <b>origin rises above</b> an
    /// existing sub-box. The existing fixture only ever shrinks the far edge.
    /// </summary>
    [Fact]
    public void SetPageBox_RefusesAMediaBoxWhoseOriginRisesAboveAnExistingSubBox()
    {
        byte[] withTrim = Set(Letter(), 0, PdfPageBoxKind.TrimBox, 36, 36, 540, 720);

        using var input = new MemoryStream(withTrim);
        using var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.MediaBox, 100, 100, 612, 792));

        Assert.Contains("TrimBox", ex.Message);
    }

    /// <summary>
    /// <c>/CropBox</c> is constrained like the other three. It is the one sub-box a reader also
    /// falls back to, so dropping it from the reverse check is the easiest of the four to not
    /// notice.
    /// </summary>
    [Fact]
    public void SetPageBox_RefusesAMediaBoxThatWouldNoLongerContainTheCropBox()
    {
        byte[] withCrop = Set(Letter(), 0, PdfPageBoxKind.CropBox, 36, 36, 540, 720);

        using var input = new MemoryStream(withCrop);
        using var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.MediaBox, 0, 0, 200, 200));

        Assert.Contains("CropBox", ex.Message);
    }

    // ── what a STORED rectangle may look like ─────────────────────────────────────

    /// <summary>
    /// §7.9.5: either corner pair may come first, so a stored <c>/CropBox</c> written
    /// <c>[576 756 36 36]</c> denotes the same rectangle as <c>[36 36 576 756]</c> and constrains
    /// the <c>/MediaBox</c> exactly as much. Reading it as written leaves it degenerate, and a
    /// degenerate box is skipped — so the containment rule silently stops applying.
    /// </summary>
    [Fact]
    public void SetPageBox_NormalisesAStoredBoxWrittenCornersReversed()
    {
        byte[] pdf = PageWith("/CropBox [576 756 36 36]");

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.MediaBox, 0, 0, 200, 200));

        Assert.Contains("CropBox", ex.Message);
    }

    /// <summary>
    /// A stored box with <b>zero area</b> constrains nothing — it cannot be drawn and cannot be
    /// violated. Treating it as a real rectangle would refuse a perfectly ordinary
    /// <c>/MediaBox</c> because of a box sitting at 1000,1000 that occupies no space at all.
    /// </summary>
    [Fact]
    public void SetPageBox_AStoredZeroAreaBoxDoesNotConstrainTheMediaBox()
    {
        byte[] pdf = PageWith("/CropBox [1000 1000 1000 1000]");

        byte[] result = Set(pdf, 0, PdfPageBoxKind.MediaBox, 0, 0, 612, 792);

        Assert.Equal(new double[] { 0, 0, 612, 792 }, Rect(result, 0, "MediaBox"));
    }

    /// <summary>
    /// <b>Malformed input, two guards at once.</b> A stored rectangle with three numbers is not a
    /// rectangle: it is skipped, rather than read past its end or measured against a zero default.
    /// Loosening the length check indexes the fourth element and throws; removing the "no usable
    /// <c>/MediaBox</c>" escape measures the new box against <c>(0,0,0,0)</c> and refuses it.
    /// </summary>
    [Fact]
    public void SetPageBox_AMalformedThreeNumberMediaBoxIsSkippedNotReadPastOrTreatedAsEmpty()
    {
        byte[] pdf = PageWith("/MediaBox [0 0 612]");

        byte[] result = Set(pdf, 0, PdfPageBoxKind.TrimBox, 0, 0, 5000, 5000);

        Assert.Equal(new double[] { 0, 0, 5000, 5000 }, Rect(result, 0, "TrimBox"));
    }

    // ── §7.7.3.4 inheritance ──────────────────────────────────────────────────────

    /// <summary>
    /// The ancestor's <c>/MediaBox</c> may be an indirect reference (§7.3.10), and a non-resolving
    /// read answers <see langword="null"/> — indistinguishable from "no ancestor supplies one". The
    /// page would then be refused a removal that is perfectly legal.
    /// </summary>
    [Fact]
    public void RemovePageBox_AllowsAMediaBoxTheParentSuppliesIndirectly()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "[0 0 612 792]");

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPageManipulator.RemovePageBox(input, output, 0, PdfPageBoxKind.MediaBox);

        Assert.Null(Rect(output.ToArray(), 0, "MediaBox"));
    }

    /// <summary>
    /// <b>Adversarial: a <c>/Parent</c> chain past the depth cap.</b> <c>/Parent</c> is
    /// caller-controlled and need not agree with <c>/Kids</c>, so the chain here is long while the
    /// page tree itself stays shallow enough to enumerate. The walk stops at
    /// <c>PdfLimits.MaxPageTreeDepth</c> and therefore answers "no ancestor supplies one", which
    /// refuses the removal — the safe direction, since the alternative is an unbounded walk over a
    /// structure the file chose.
    /// </summary>
    [Fact]
    public void RemovePageBox_RefusesWhenTheOnlyAncestorMediaBoxLiesBeyondTheParentDepthCap()
    {
        const int ChainLength = 1030;   // MaxPageTreeDepth is 1024

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // The page's /Parent deliberately points into the long chain, not at object 2.
            "<< /Type /Page /Parent 6 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Unused true >>",
        };
        // Objects 6 .. 6+ChainLength-1, each naming the next; only the last carries a /MediaBox.
        for (int i = 0; i < ChainLength; i++)
            bodies.Add(i == ChainLength - 1
                ? "<< /Type /Pages /MediaBox [0 0 612 792] >>"
                : "<< /Type /Pages /Parent " + (7 + i).ToString(CultureInfo.InvariantCulture) + " 0 R >>");

        using var input = new MemoryStream(Build(bodies.ToArray()));
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.RemovePageBox(input, output, 0, PdfPageBoxKind.MediaBox));

        Assert.Contains("MediaBox", ex.Message);
    }

    /// <summary>
    /// The acceptance control for the test above: the same removal <i>is</i> allowed when the
    /// ancestor sits within the cap, so the refusal is about the depth and not about the shape of
    /// the fixture.
    /// </summary>
    [Fact]
    public void RemovePageBox_AllowsItWhenTheAncestorIsWithinTheCap()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 0 >>\nstream\n\nendstream");

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPageManipulator.RemovePageBox(input, output, 0, PdfPageBoxKind.MediaBox);

        Assert.Null(Rect(output.ToArray(), 0, "MediaBox"));
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Set(byte[] pdf, int pageIndex, PdfPageBoxKind box,
                              double x, double y, double w, double h)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPageManipulator.SetPageBox(input, output, pageIndex, box, x, y, w, h);
        return output.ToArray();
    }

    private static double[] Rect(byte[] pdf, int pageIndex, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var arr = doc.ResolveObject(doc.Pages[pageIndex].Dictionary[new PdfName(key)]) as PdfArray;
        if (arr == null) return null;

        var values = new double[arr.Count];
        for (int i = 0; i < arr.Count; i++) values[i] = ((PdfNumber)arr[i]).DoubleValue;
        return values;
    }

    private static byte[] Letter() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        "<< /Length 0 >>\nstream\n\nendstream");

    /// <summary>A one-page document whose page dictionary also carries <paramref name="extra"/>.</summary>
    private static byte[] PageWith(string extra) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " + extra +
        " /Contents 4 0 R /Resources << >> >>",
        "<< /Length 0 >>\nstream\n\nendstream");

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
          .Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
