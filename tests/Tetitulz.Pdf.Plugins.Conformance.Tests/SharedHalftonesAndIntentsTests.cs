// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// What many owners share, a validation reads once: a type 5 halftone many graphics states name
/// (<see cref="Halftones.Seen"/>; N states over M components cost N + M visits, not N × M), and an
/// <c>/OutputIntents</c> array many PDF/A-4 pages share. Each is reported at the first location that
/// reaches it, and still reported (the potency halves).
/// </summary>
public sealed class SharedHalftonesAndIntentsTests
{
    private const int States = 200;
    private const int Components = 50;

    [Fact]
    public void AdversarialSharedType5Halftone_IsVisitedOnce_NotOnceAState()
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(SharedHalftoneDocument(bad: false)));
        PdfDictionary states = ExtGStates(doc);

        var seen = new Halftones.Seen();
        int visits = 0;
        foreach (var kvp in states)
            Halftones.Visit(doc, ((PdfDictionary)doc.ResolveObject(kvp.Value))[PdfName.HT], "/" + kvp.Key.Value, seen,
                (_, _, _) => visits++);

        Assert.Equal(1 + Components, visits);       // the halftone and each component, once
        Assert.Equal(States - 1, seen.Skipped);     // every later state passed over at the halftone

        // The control: without one record across the states, each walks the whole halftone again.
        int unshared = 0;
        foreach (var kvp in states)
            Halftones.Visit(doc, ((PdfDictionary)doc.ResolveObject(kvp.Value))[PdfName.HT], "/" + kvp.Key.Value,
                new Halftones.Seen(), (_, _, _) => unshared++);
        Assert.Equal(States * (1 + Components), unshared);
    }

    [Fact]
    public void AComponentReachedAsAnotherColorant_IsVisitedAgain()
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(SharedHalftoneDocument(bad: false)));
        PdfDictionary component = (PdfDictionary)doc.ResolveObject(new PdfReference(6, 0));

        var seen = new Halftones.Seen();
        var colorants = new List<string?>();
        var one = new PdfDictionary { [PdfName.HalftoneType] = new PdfNumber(5), [new PdfName("Cyan")] = component };
        var other = new PdfDictionary { [PdfName.HalftoneType] = new PdfNumber(5), [new PdfName("Spot")] = component };
        Halftones.Visit(doc, one, "a", seen, (_, _, c) => colorants.Add(c));
        Halftones.Visit(doc, other, "b", seen, (_, _, c) => colorants.Add(c));

        Assert.Equal(new string?[] { null, "Cyan", null, "Spot" }, colorants);
        Assert.Equal(0, seen.Skipped);
    }

    [Fact]
    public void ASharedBadHalftone_IsReportedOnce_AtTheFirstStateThatNamesIt()
    {
        using var ms = new MemoryStream(SharedHalftoneDocument(bad: true));
        PdfAViolation a57 = Assert.Single(PdfAValidator.Validate(ms, PdfAConformancePart.Part2).Violations, v => v.Code == "A57");

        Assert.Contains("/G0 /HT /C0 /HalftoneType 6", a57.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/G1 /HT", a57.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedPageIntentsArray_IsReadOnce_AndReportedAtItsFirstPage()
    {
        using var ms = new MemoryStream(SharedIntentsDocument(pages: 40));
        long before = PdfAValidator.OutputIntentArraysWalked;
        var violations = PdfAValidator.Validate(ms, PdfAConformancePart.Part4).Violations;
        long walked = PdfAValidator.OutputIntentArraysWalked - before;
        PdfAViolation a48 = Assert.Single(violations, v => v.Code == "A48");

        Assert.Contains("page 1, intent 0", a48.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("page 2,", a48.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("more", a48.Message, StringComparison.Ordinal);

        // The colour rule's intent and the blending rule's (every page draws with transparency, and
        // the intent has no profile, so A41 asks each page): one walk of the array each, not forty.
        Assert.Contains(violations, v => v.Code == "A41");
        Assert.Equal(2, walked);
    }

    private static PdfDictionary ExtGStates(PdfDocument doc)
    {
        var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]);
        return (PdfDictionary)doc.ResolveObject(resources[PdfName.ExtGState]);
    }

    /// <summary>
    /// One page whose <see cref="States"/> graphics states all name object 5, a type 5 halftone of
    /// <see cref="Components"/> components; the first, object 6, is of type 6 when <paramref name="bad"/>.
    /// </summary>
    private static byte[] SharedHalftoneDocument(bool bad)
    {
        var gs = new StringBuilder("<<");
        for (int i = 0; i < States; i++) gs.Append(" /G").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" << /HT 5 0 R >>");
        gs.Append(" >>");

        var type5 = new StringBuilder("<< /Type /Halftone /HalftoneType 5");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /ExtGState " + gs + " >> >>",
            "<< /Length 6 >>\nstream\n/G0 gs\nendstream",
            null!,
        };
        for (int c = 0; c < Components; c++)
        {
            bool type6 = bad && c == 0;
            objects.Add(type6
                ? "<< /Type /Halftone /HalftoneType 6 /Width 1 /Height 1 >>"
                : "<< /Type /Halftone /HalftoneType 1 /Frequency 60 /Angle 45 /SpotFunction /Round >>");
            if (c > 0) type5.Append(" /C").Append(c.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(objects.Count).Append(" 0 R");
            else type5.Append(" /C0 6 0 R");
        }
        objects[4] = type5.Append(" >>").ToString();
        return Build(objects);
    }

    /// <summary><paramref name="pages"/> PDF/A-4 pages whose <c>/OutputIntents</c> is one array, its intent with a <c>/DestOutputProfileRef</c>.</summary>
    private static byte[] SharedIntentsDocument(int pages)
    {
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(p => (4 + p).ToString(CultureInfo.InvariantCulture) + " 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>",
            "[<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (x) /DestOutputProfileRef << /URLs [] >> >>]",
        };
        string content = (4 + pages).ToString(CultureInfo.InvariantCulture) + " 0 R";
        for (int p = 0; p < pages; p++)
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /OutputIntents 3 0 R /Contents " + content +
                        " /Resources << /ExtGState << /G0 << /ca 0.5 >> >> >> >>");
        objects.Add("<< /Length 21 >>\nstream\n/G0 gs 0 0 10 10 re f\nendstream");
        return Build(objects);
    }

    private static byte[] Build(IReadOnlyList<string> objects)
    {
        var sb = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
