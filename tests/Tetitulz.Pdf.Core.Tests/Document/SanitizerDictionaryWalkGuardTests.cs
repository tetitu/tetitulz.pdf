// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The two DoS guards on <c>PdfSanitizer</c>'s <b>dictionary</b> walk — the depth cap and the
/// visited set — as distinct from the <c>/Next</c> chain guards that
/// <see cref="SanitizerDosGuardTests"/> covers.
/// </summary>
/// <remarks>
/// <para><b>Both survived a mutation run, and neither hung — because they mask each other.</b>
/// Deleting the visited set leaves the depth cap, which stops a <i>simple</i> cycle at 100 frames,
/// so nothing overflows and nothing loops; deleting the depth cap leaves the visited set, which
/// does nothing at all for a chain of <i>distinct</i> dictionaries. Each guard is therefore
/// invisible to any fixture the other one also handles, and the whole suite stayed green with
/// either removed.</para>
///
/// <para>Separating them needs two shapes, one per guard:</para>
/// <list type="bullet">
///   <item><b>Only the depth cap</b> stops a long chain of <i>unique</i> dictionaries — the visited
///   set never fires, because no dictionary repeats.</item>
///   <item><b>Only the visited set</b> stops a <i>branching</i> cycle. This is the case
///   <c>CLAUDE.md</c> states in the general: a depth cap bounds one branch, never the number of
///   branches. Two back-edges under a cap of 100 is on the order of 2^50 walks — it does not
///   overflow, it simply never finishes, which is why a depth cap reads as sufficient right up
///   until it isn't.</item>
/// </list>
///
/// <para><b>Completion is the assertion, and that is the behaviour under test</b> — these are
/// hostile inputs, so a non-throw is the claim rather than an empty one. Each also reads a
/// post-condition off the output, so a sanitizer that returned by doing nothing at all would not
/// pass. The fixtures' potency is not asserted by a clock: it was measured by removing each guard
/// under <c>.claude/sweep-sanitizer-dos.ps1</c>, which carries its own timeout and reports
/// "did not return" as the kill.</para>
/// </remarks>
public class SanitizerDictionaryWalkGuardTests
{
    /// <summary>
    /// A chain of ten thousand <b>distinct</b> annotation dictionaries, each holding the next in its
    /// own <c>/Annots</c>. Nothing repeats, so the visited set never fires and the depth cap is the
    /// only thing between this and ten thousand stack frames.
    /// </summary>
    [Fact]
    public void ADictionaryChainOfUniqueNodes_DoesNotOverflowTheStack()
    {
        byte[] pdf = Chain(10_000);

        byte[] cleaned = Sanitize(pdf);

        // The walk was clipped, not abandoned: the document still comes back whole.
        Assert.Equal(1, PageCount(cleaned));
    }

    /// <summary>
    /// A cycle with <b>two</b> back edges: X draws Y and Z, and both lead to X again. The depth cap
    /// bounds each branch at 100 and does nothing about there being 2^50 of them.
    /// </summary>
    [Fact]
    public void ABranchingAnnotationCycle_DoesNotHang()
    {
        byte[] pdf = BranchingCycle();

        byte[] cleaned = Sanitize(pdf);

        Assert.Equal(1, PageCount(cleaned));
    }

    /// <summary>
    /// The acceptance control for both: a cyclic graph does not cost the sanitizer its job. The
    /// script in the cycle is still stripped, so the guards are breaking the walk rather than
    /// abandoning the document at the first back edge.
    /// </summary>
    [Fact]
    public void AScriptInsideACycleIsStillStripped()
    {
        byte[] pdf = BranchingCycle(scriptOnZ: true);
        Assert.Contains(Payload, Encoding.Latin1.GetString(pdf), System.StringComparison.Ordinal);

        byte[] cleaned = Sanitize(pdf);

        Assert.DoesNotContain(Payload, Encoding.Latin1.GetString(cleaned), System.StringComparison.Ordinal);
        Assert.Equal(1, PageCount(cleaned));
    }

    // ------------------------------------------------------------------ helpers

    private const string Payload = "app.alert-inside-a-cycle";

    private static byte[] Sanitize(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions());
        return output.ToArray();
    }

    private static int PageCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return doc.Pages.Count;
    }

    /// <summary>Objects 4..(3+n), each naming the next in its own <c>/Annots</c>.</summary>
    private static byte[] Chain(int n)
    {
        var bodies = new List<string>();
        for (int i = 0; i < n; i++)
        {
            string next = i == n - 1 ? "" : " /Annots [" + (5 + i) + " 0 R]";
            bodies.Add("<< /Type /Annot /Subtype /Text /Rect [0 0 1 1]" + next + " >>");
        }
        return Assemble(bodies);
    }

    /// <summary>X (object 4) → Y and Z (5, 6); Y → X and Z → X.</summary>
    private static byte[] BranchingCycle(bool scriptOnZ = false)
    {
        string script = scriptOnZ
            ? " /A << /S /Named /N /NextPage /JS (" + Payload + ") >>"
            : "";
        return Assemble(new List<string>
        {
            "<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Annots [5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Annots [4 0 R]" + script + " >>",
        });
    }

    /// <summary>
    /// Catalog, page tree and a one-page document whose <c>/Annots</c> names object 4;
    /// <paramref name="annotations"/> become objects 4 onwards.
    /// </summary>
    private static byte[] Assemble(List<string> annotations)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
        };
        bodies.AddRange(annotations);

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
