// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Regressions for the assembly-scan findings against the two validators' resource walks:
/// <see cref="PdfAValidator"/>'s A06 font walk (which read the wrong dictionary, and was unbounded
/// in fan-out) and <see cref="PdfXValidator"/>'s page scan (bounded in nodes, unbounded in edges).
///
/// <para>The hostile-input tests assert the <b>guard's own evidence</b> — the reported refusal, the
/// X99 informational entry — never a wall clock.</para>
/// </summary>
public class ResourceWalkScanFindingsTests
{
    // ------------------------------------------------------------------
    //  A06 — a font reachable only through a Form XObject
    // ------------------------------------------------------------------

    [Fact]
    public void A06_NonEmbeddedFontInsideAFormXObject_IsReported()
    {
        // The page names no /Font at all; the text is drawn inside a form whose OWN /Resources
        // names a non-embedded Helvetica. The walk used to recurse with the form STREAM as the
        // resource dictionary, so it read xobj[/Font] — a key Table 95 puts under /Resources — and
        // found nothing, every time.
        var result = ValidateA(FormXObjectFontPdf(embedded: false));

        var a06 = Assert.Single(result.Violations.Where(v => v.Code == "A06"));
        Assert.Contains("Helvetica", a06.Message, StringComparison.Ordinal);
        Assert.False(result.IsConformant);
    }

    [Fact]
    public void A06_EmbeddedFontInsideAFormXObject_IsNotReported()
    {
        // The control: without it, an A06 that fired unconditionally on any nested font would pass
        // the test above.
        var result = ValidateA(FormXObjectFontPdf(embedded: true));

        Assert.DoesNotContain(result.Violations, v => v.Code == "A06");
    }

    [Fact]
    public void A06_FormXObjectCycle_IsWalkedOnceAndReportsTheFontOnce()
    {
        // A form whose own /Resources names it twice. A depth cap alone admits b^depth visits, so
        // this shape cost 2^50 recursive calls behind the cap of 50 — a hang, from ~300 bytes. The
        // visited set is what terminates it; the evidence is that validation completes and the
        // font inside the cycle is reported exactly once.
        var result = ValidateA(SelfReferencingFormPdf());

        var a06 = Assert.Single(result.Violations.Where(v => v.Code == "A06"));
        Assert.Contains("Helvetica", a06.Message, StringComparison.Ordinal);
        // Nothing was refused: the cycle was absorbed by the visited set, not by the budget.
        Assert.Null(PdfAValidator.LastValidationError);
    }

    [Fact]
    public void A06_OversizedResourceDictionary_ReportsThePartialWalkRatherThanASilentPass()
    {
        // One /XObject dictionary of 200 001 entries, all naming the same form. The visited set
        // stops the form being EXPANDED twice; it says nothing about the 200 001 resolve-and-probe
        // operations, which is the cost the budget bounds. The guard's evidence is the report.
        var result = ValidateA(WideResourceDictionaryPdf(entries: 200_001));

        Assert.NotNull(PdfAValidator.LastValidationError);
        Assert.Contains("resource traversal stopped", PdfAValidator.LastValidationError,
            StringComparison.Ordinal);

        // The A06 claim is asserted on the A99 entry, not on the slot. The slot holds ONE message
        // and the transparency walk, which runs after this one, spends the same budget on the same
        // dictionary — so what it holds last is that walk's. It used to contain "A06" anyway,
        // because that walk's message said "the A06 font-embedding check is partial" about itself.
        Assert.Contains(result.Informational, i => i.Code == "A99"
            && i.Message.Contains("A06 font-embedding check did not run in full", StringComparison.Ordinal));
    }

    [Fact]
    public void A06_OrdinaryDocument_ReportsNoTraversalGuard()
    {
        // The counterpart: the slot must be null after a document the walk finished, or "a guard
        // fired" carries no information. It also pins the reset — a previous document's refusal
        // read as this one's is the per-document-scope trap CLAUDE.md records.
        ValidateA(WideResourceDictionaryPdf(entries: 200_001));
        Assert.NotNull(PdfAValidator.LastValidationError);

        ValidateA(FormXObjectFontPdf(embedded: true));
        Assert.Null(PdfAValidator.LastValidationError);
    }

    // ------------------------------------------------------------------
    //  PdfXValidator — the page scan spends per entry examined
    // ------------------------------------------------------------------

    [Fact]
    public void PdfX_OversizedResourceDictionary_ReportsX99RatherThanScanningItAll()
    {
        // Same shape, against the other walk: WalkNestedResources re-iterates every resource
        // dictionary collected so far on each round and enumerates the whole /XObject dictionary of
        // each, with a resolve per entry — the N×M cost neither the 4 096-dictionary cap nor the
        // decoded-byte ceiling bounds. X99 is the honest-reporting channel for a check that did not
        // run in full.
        using var ms = new MemoryStream(WideResourceDictionaryPdf(entries: 200_001));
        var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a);

        Assert.Contains(result.Informational, v => v.Code == "X99");
        Assert.Contains(result.Informational,
            v => v.Code == "X99" && v.Message.Contains("resource entries", StringComparison.Ordinal));
        Assert.True(result.IsPartial);
    }

    [Fact]
    public void PdfX_OrdinaryDocument_ReportsNoX99()
    {
        // Without this control, a scan that reported X99 unconditionally would pass the test above.
        using var ms = new MemoryStream(FormXObjectFontPdf(embedded: true));
        var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a);

        Assert.DoesNotContain(result.Informational, v => v.Code == "X99");
        Assert.False(result.IsPartial);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static PdfAValidationResult ValidateA(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, PdfAConformancePart.Part1);
    }

    private const string PageContent = "/Fm0 Do";
    private const string FormContent = "BT /F1 12 Tf (x) Tj ET";

    /// <summary>
    /// A page whose only resource is a form XObject; the font lives in the FORM's /Resources.
    /// </summary>
    private static byte[] FormXObjectFontPdf(bool embedded)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /XObject << /Fm0 5 0 R >> >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] " +
            "/Resources << /Font << /F1 6 0 R >> >> " +
            $"/Length {FormContent.Length} >>\nstream\n{FormContent}\nendstream",
            embedded
                ? "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FontDescriptor 7 0 R >>"
                : "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /FontDescriptor /FontName /Helvetica /Flags 32 /FontFile3 8 0 R >>",
            CffStream(),
        };
        return Assemble(bodies);
    }

    /// <summary>
    /// A real CFF program holding the glyph the form draws: the font checks that read the program (A84)
    /// then finish on this document too, which is what "ordinary" has to mean for the slot test below.
    /// </summary>
    private static string CffStream()
    {
        byte[] cff = Tetitulz.Pdf.TestHelpers.MinimalOpenTypeCff.BuildBareCff(Tetitulz.Pdf.TestHelpers.MinimalOpenTypeCff.NamedBoxGlyphs("x"));
        return "<< /Subtype /Type1C /Length " + cff.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
               + Encoding.Latin1.GetString(cff) + "\nendstream";
    }

    /// <summary>
    /// A form XObject whose own <c>/Resources /XObject</c> names it twice — branching factor two at
    /// every level, which a depth cap does not bound.
    /// </summary>
    private static byte[] SelfReferencingFormPdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /XObject << /Fm0 5 0 R >> >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] " +
            "/Resources << /Font << /F1 6 0 R >> /XObject << /A 5 0 R /B 5 0 R >> >> " +
            $"/Length {FormContent.Length} >>\nstream\n{FormContent}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        return Assemble(bodies);
    }

    /// <summary>
    /// One <c>/XObject</c> dictionary of <paramref name="entries"/> names, every one of them the
    /// same form. Distinct nodes: two. Edges to examine: <paramref name="entries"/>.
    /// </summary>
    private static byte[] WideResourceDictionaryPdf(int entries)
    {
        var xobjects = new StringBuilder("<< ");
        for (int i = 0; i < entries; i++)
            xobjects.Append("/X").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" 5 0 R ");
        xobjects.Append(">>");

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /XObject " + xobjects + " >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] " +
            "/Resources << /Font << /F1 6 0 R >> >> " +
            $"/Length {FormContent.Length} >>\nstream\n{FormContent}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        return Assemble(bodies);
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());   // Latin-1: a font program's bytes round-trip
    }
}
