// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The page annotations one validation reads, all its checks together (<see cref="ScanReuse.AdmitAnnotations"/>).
/// Pages may share one <c>/Annots</c> array, so a whole-document walk reads pages × entries — six thousand
/// pages naming one array of six thousand references took 13.5 s through PDF/A. The ceiling is lowered for
/// the test on its own thread, and the evidence is the count the scope admitted, beside the notice.
/// </summary>
public class AnnotationCeilingTests
{
    /// <summary>A document whose pages all name one /Annots array of one annotation listed <paramref name="entries"/> times.</summary>
    internal static byte[] SharedAnnots(int pages, int entries)
    {
        var kids = new StringBuilder();
        for (int i = 0; i < pages; i++) kids.Append(5 + i).Append(" 0 R ");
        var refs = new StringBuilder();
        for (int i = 0; i < entries; i++) refs.Append("4 0 R ");
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + kids + "] /Count " + pages + " /MediaBox [0 0 100 100] >>",
            "[" + refs + "]",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>",
        };
        for (int i = 0; i < pages; i++) bodies.Add("<< /Type /Page /Parent 2 0 R /Annots 3 0 R >>");
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static T Under<T>(long ceiling, Func<T> validate)
    {
        ScanReuse.TestMaxAnnotationVisits = ceiling;
        try { return validate(); }
        finally { ScanReuse.TestMaxAnnotationVisits = 0; }
    }

    private const long Ceiling = 1_000;

    /// <summary>
    /// <b>Adversarial: forty pages sharing one array of forty entries, under a ceiling of a thousand.</b>
    /// Each validator stops reading at the ceiling and says so; with the default ceiling the same
    /// document is read through, more than a thousand entries, and says nothing.
    /// </summary>
    [Fact]
    public void AdversarialSharedAnnotationArrays_StopAtTheCeiling_InEveryValidator()
    {
        byte[] pdf = SharedAnnots(40, 40);
        const string Notice = "Annotations were read for at most 1000 page entries";

        var a = Under(Ceiling, () => PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2));
        Assert.InRange(ScanReuse.ClosedAnnotationVisits, 1, Ceiling);
        Assert.Contains(a.Informational, i => i.Code == "A99" && i.Message.StartsWith(Notice, StringComparison.Ordinal));

        var x = Under(Ceiling, () => PdfXValidator.Validate(new MemoryStream(pdf), PdfXConformanceLevel.X4));
        Assert.InRange(ScanReuse.ClosedAnnotationVisits, 1, Ceiling);
        Assert.Contains(x.Informational, i => i.Code == "X99" && i.Message.StartsWith(Notice, StringComparison.Ordinal));

        var u = Under(Ceiling, () => PdfUaChecker.Validate(new MemoryStream(pdf)));
        Assert.InRange(ScanReuse.ClosedAnnotationVisits, 1, Ceiling);
        Assert.Contains(u.Violations, i => i.Code == "UA99" && i.Message.StartsWith(Notice, StringComparison.Ordinal));

        // Control: the default ceiling reads past a thousand entries and reports no stop.
        var aFull = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2);
        Assert.True(ScanReuse.ClosedAnnotationVisits > Ceiling);
        Assert.DoesNotContain(aFull.Informational, i => i.Message.StartsWith("Annotations were read for at most", StringComparison.Ordinal));
        var uFull = PdfUaChecker.Validate(new MemoryStream(pdf));
        Assert.True(ScanReuse.ClosedAnnotationVisits > Ceiling);
        Assert.DoesNotContain(uFull.Violations, i => i.Message.StartsWith("Annotations were read for at most", StringComparison.Ordinal));
    }

    /// <summary>
    /// Each walk asks the ceiling itself: with a ceiling under one page's array nothing is read, so the
    /// annotation's own findings — PDF/A's missing /F (A38), PDF/UA's missing /Contents (UA27) — are
    /// not made; at the default both are.
    /// </summary>
    [Fact]
    public void UnderACeilingBelowOneArray_TheAnnotationChecksReadNothing()
    {
        byte[] pdf = SharedAnnots(40, 40);

        var a = Under(10, () => PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2));
        Assert.Equal(0, ScanReuse.ClosedAnnotationVisits);
        Assert.DoesNotContain(a.Violations, i => i.Code == "A38");
        Assert.Contains(PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2).Violations, i => i.Code == "A38");

        var u = Under(10, () => PdfUaChecker.Validate(new MemoryStream(pdf)));
        Assert.Equal(0, ScanReuse.ClosedAnnotationVisits);
        Assert.DoesNotContain(u.Violations, i => i.Code == "UA27");
        Assert.Contains(PdfUaChecker.Validate(new MemoryStream(pdf)).Violations, i => i.Code == "UA27");
    }

    /// <summary>
    /// The PDF/A annotation repair revises an array pages share, and an annotation it lists more than
    /// once, once: forty pages naming one array of forty references to one annotation with no /F set one
    /// Print flag, not sixteen hundred.
    /// </summary>
    [Fact]
    public void TheAnnotationRepair_RevisesASharedArrayOnce()
    {
        byte[] pdf = SharedAnnots(40, 40);
        var outcome = PdfAAnnotationRepair.Repair(pdf, new MemoryStream());
        Assert.Equal(1, outcome.PrintFlagsSet);
        Assert.Equal(1, outcome.ArraysRead);   // forty pages, one array read

        // Potency: two annotations of their own on one page set two.
        string two = Encoding.ASCII.GetString(SharedAnnots(1, 1)).Replace("[4 0 R ]", "[<< /Subtype /Text /Rect [0 0 1 1] >> << /Subtype /Text /Rect [0 0 1 1] >>]", StringComparison.Ordinal);
        Assert.Equal(2, PdfAAnnotationRepair.Repair(Encoding.ASCII.GetBytes(two), new MemoryStream()).PrintFlagsSet);
    }
}
