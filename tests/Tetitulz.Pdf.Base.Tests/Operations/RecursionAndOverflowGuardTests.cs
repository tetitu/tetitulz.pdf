// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Adversarial inputs for two guards that were missing entirely.
///
/// <para>Per this repository's rules a DoS-guard test asserts the guard's own evidence — a reported
/// reason, a refusal, a bounded result — never a wall clock, because a guard test that finishes
/// fast has quite possibly not run.</para>
/// </summary>
public class RecursionAndOverflowGuardTests
{
    /// <summary>
    /// A Form XObject that draws itself. <c>PdfStructuredTextExtractor</c> had NO guard of any kind
    /// here — no depth cap, no visited set, no budget — while <c>Core.Text.PdfTextExtractor</c>,
    /// which does the same job, has both. The recursion ran until the stack overflowed, which is
    /// uncatchable and takes the whole process with it.
    ///
    /// <para>Note the form carries no <c>/Resources</c> of its own: a form without them inherits
    /// the caller's, so the page's own <c>/XObject</c> entry is all a self-reference needs.</para>
    /// </summary>
    [Fact]
    public void StructuredTextExtraction_OnASelfDrawingFormXObject_StopsAndReportsWhy()
    {
        byte[] pdf = SelfDrawingFormPdf();

        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0);

        // The guard's evidence: it returned at all, said why, and still produced the page's text.
        Assert.NotNull(layout);
        Assert.Contains("draws itself", PdfStructuredTextExtractor.LastError ?? string.Empty);
        string text = string.Concat(layout.Lines.SelectMany(l => l.Words).Select(w => w.Text));
        Assert.Contains("A", text);
    }

    /// <summary>
    /// A form drawn twice side by side is not a cycle and must still be extracted twice — the
    /// control, so the guard above cannot pass by having started refusing every nested form.
    /// </summary>
    [Fact]
    public void StructuredTextExtraction_OnAFormDrawnTwice_ExtractsItBothTimes()
    {
        byte[] pdf = FormDrawnTwicePdf();

        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0);

        string text = string.Concat(layout.Lines.SelectMany(l => l.Words).Select(w => w.Text));
        Assert.Equal(2, text.Count(c => c == 'B'));
    }

    /// <summary>
    /// A chain of <b>distinct</b> forms, each drawing the next — the shape neither of the other two
    /// guards bounds.
    /// </summary>
    /// <remarks>
    /// <para><b>The depth cap was the one guard on this walk with no fixture, and it is not
    /// redundant.</b> The visited set is scoped to a branch and refuses only a <i>repeat</i>, which
    /// a chain of distinct forms never is. The per-page budget permits
    /// <see cref="PdfLimits.MaxFormXObjectInvocationsPerPage"/> = 100 000 invocations and a chain
    /// spends one per level, so 100 000 nested <c>InterpretOps</c> frames are reached long before it
    /// refuses anything. A stack overflow is uncatchable and takes the process down.</para>
    ///
    /// <para><b>One assertion carries the claim in both directions.</b> Exactly
    /// <see cref="PdfLimits.MaxFormXObjectDepth"/> glyphs come back: fewer would mean the walk
    /// stopped early for some other reason and the fixture never reached the cap; more would mean
    /// the cap did not fire. "No text past the cap" on its own is equally true of a walk that
    /// refused every form.</para>
    /// </remarks>
    [Fact]
    public void StructuredTextExtraction_OnAFormChainDeeperThanTheCap_StopsAtTheCap()
    {
        // Two levels past the cap, so the last forms are unambiguously outside it.
        byte[] pdf = FormChainPdf(PdfLimits.MaxFormXObjectDepth + 2);

        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0);

        Assert.Equal(PdfLimits.MaxFormXObjectDepth, layout.Chars.Count(c => c.Char == "A"));

        // And it says so. The cycle guard and the budget both report; this one truncated silently,
        // so a caller could not tell a short page from a complete one.
        Assert.Contains("nested deeper than", PdfStructuredTextExtractor.LastError ?? string.Empty,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// A form that draws the <b>same subform twice</b> must extract it twice — the nested case of
    /// "drawn twice side by side", and the only place the visited set's branch scoping is
    /// load-bearing.
    /// </summary>
    /// <remarks>
    /// <para><b>The page-level test above cannot see this, and a mutation said so.</b> Removing the
    /// <c>finally</c> that takes a form back out of the visited set left
    /// <see cref="StructuredTextExtraction_OnAFormDrawnTwice_ExtractsItBothTimes"/> — the test named
    /// for exactly that claim — passing, and was killed instead by an unrelated budget test.</para>
    ///
    /// <para>The reason is that <c>visited</c> is a by-value parameter: <c>visited ??= new
    /// HashSet&lt;int&gt;()</c> assigns the local copy and never reaches the caller, so page-level
    /// <c>InterpretOps</c> passes null and <b>every page-level <c>Do</c> mints its own set</b>. Two
    /// sibling invocations at the page level cannot collide however the <c>finally</c> behaves. One
    /// level down they share a set, and the removal is the whole of what keeps the second draw
    /// alive.</para>
    /// </remarks>
    [Fact]
    public void StructuredTextExtraction_OnANestedFormDrawnTwice_ExtractsItBothTimes()
    {
        byte[] pdf = NestedFormDrawnTwicePdf();

        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0);

        string text = string.Concat(layout.Lines.SelectMany(l => l.Words).Select(w => w.Text));
        Assert.Equal(2, text.Count(c => c == 'B'));
    }

    // ------------------------------------------------------------------

    /// <summary>Page draws /Fx; /Fx's content draws /Fx again, inheriting the page's resources.</summary>
    private static byte[] SelfDrawingFormPdf()
    {
        const string pageContent = "BT /F1 12 Tf 20 150 Td (A) Tj ET q /Fx Do Q";
        const string formContent = "q /Fx Do Q";

        return Build(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fx 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Length {formContent.Length} >>"
                + $"\nstream\n{formContent}\nendstream\nendobj\n",
        });
    }

    /// <summary>Page draws /Fb twice at different positions; /Fb writes one 'B'.</summary>
    private static byte[] FormDrawnTwicePdf()
    {
        const string pageContent = "q 1 0 0 1 0 0 cm /Fb Do Q q 1 0 0 1 0 100 cm /Fb Do Q";
        const string formContent = "BT /F1 12 Tf 20 20 Td (B) Tj ET";

        return Build(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fb 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Length {formContent.Length} >>"
                + $"\nstream\n{formContent}\nendstream\nendobj\n",
        });
    }

    /// <summary>
    /// Page draws /X0; form k draws one 'A' at its own position and then /X(k+1). Every form is a
    /// distinct object, so the cycle guard never applies and the depth cap is the only thing that
    /// can stop the walk.
    /// </summary>
    private static byte[] FormChainPdf(int levels)
    {
        const string pageContent = "q /X0 Do Q";

        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /X0 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        };

        for (int k = 0; k < levels; k++)
        {
            // A distinct position per level, so the duplicate-glyph rule keeps every one of them.
            string content = $"BT /F1 4 Tf {5 + k} {5 + k * 5} Td (A) Tj ET";
            string xobject = k + 1 < levels ? $" /XObject << /X{k + 1} {7 + k} 0 R >>" : string.Empty;
            if (xobject.Length > 0) content += $" q /X{k + 1} Do Q";

            objects.Add(
                $"{6 + k} 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + $"/Resources << /Font << /F1 5 0 R >>{xobject} >> "
                + $"/Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        return Build(objects.ToArray());
    }

    /// <summary>Page draws /X0 once; /X0 draws /X1 twice, at two positions; /X1 writes one 'B'.</summary>
    private static byte[] NestedFormDrawnTwicePdf()
    {
        const string pageContent  = "q /X0 Do Q";
        const string outerContent = "q /X1 Do Q q 1 0 0 1 0 60 cm /X1 Do Q";
        const string innerContent = "BT /F1 12 Tf 10 10 Td (B) Tj ET";

        return Build(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /X0 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /X1 7 0 R >> >> "
                + $"/Length {outerContent.Length} >>\nstream\n{outerContent}\nendstream\nendobj\n",
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Resources << /Font << /F1 5 0 R >> >> "
                + $"/Length {innerContent.Length} >>\nstream\n{innerContent}\nendstream\nendobj\n",
        });
    }

    private static byte[] Build(string[] objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
