// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The duplicate-merge pass hands back a subtree unchanged when nothing in it needs rewriting,
/// instead of deep-cloning every object on every round.
/// </summary>
/// <remarks>
/// <para><b>Why the liveness assertion is the whole point.</b> The fast path and the slow path
/// produce the <i>same hash by construction</i> — that is what makes it a safe optimisation, and it
/// is also why no assertion about the answer can tell whether it is running. Deleting it leaves
/// every other test in this project green while restoring the per-object, per-round deep clone it
/// exists to remove. CLAUDE.md's rule, and the same reason <c>CMapCodec.UsesCidRangeIndex</c>
/// exists.</para>
///
/// <para><b>The obvious version of this optimisation is wrong, and ordinary input cannot show
/// it.</b> <c>dedupMap</c> is seeded with <c>dedupMap[num] = num</c> for <i>every</i> object, and
/// the reference arm rewrites to <c>new PdfReference(canonical, <b>0</b>)</c> — so every reference
/// in a hashed object has its generation <b>normalised to zero</b> whether or not its object number
/// moved. Skipping the copy on <c>canonical == num</c> alone would preserve a non-zero generation
/// and change the hash.</para>
/// </remarks>
public sealed class CanonicalisationFastPathTests
{
    /// <summary>
    /// The fast path actually runs on a document with duplicates to merge.
    /// </summary>
    /// <remarks>
    /// Asserted as a strictly positive count rather than a boolean, so a future change narrowing
    /// the fast path to almost nothing is visible as a number rather than passing on one lucky hit.
    /// </remarks>
    [Fact]
    public void TheUnchangedSubtreeFastPathIsLive()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithDuplicateObjects();

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveDuplicates = true,
            RemoveUnusedObjects = false,
            CompressStreams = false
        });

        Assert.True(PdfOptimizer.LastUnchangedSubtreeCount > 0,
            "the duplicate-merge pass cloned every subtree; the unchanged-subtree fast path is "
            + "not running, and no assertion about the merged output would have said so");
    }

    /// <summary>
    /// Two otherwise-identical objects whose only difference is the <b>generation number</b> of a
    /// reference they hold are still merged.
    /// </summary>
    /// <remarks>
    /// <para><b>A characterisation test: it pins what this code does, not what it ought to do.</b>
    /// <c>CanonicaliseForHash</c> rewrites every reference to <c>new PdfReference(canonical, 0)</c>,
    /// so a hashed object's references have their generation normalised away and <c>9 0 R</c> and
    /// <c>9 1 R</c> become indistinguishable. <b>Whether that is right is a real question</b> —
    /// they name different objects — but it is the behaviour that shipped, and this test exists so
    /// the fast path above could be added without changing it.</para>
    ///
    /// <para><b>It was written because a mutation survived.</b> Dropping the
    /// <c>ReferenceGenerationNumber != 0</c> clause from <c>NeedsCanonicalisation</c> left this
    /// project at 250/250: no fixture anywhere held a reference with a non-zero generation, so the
    /// clause looked dead while actually being what preserves the normalisation. Reading the callee
    /// is what told the two apart — a redundant guard and a load-bearing one look identical from a
    /// green suite.</para>
    /// </remarks>
    [Fact]
    public void ObjectsDifferingOnlyInAReferencedGenerationAreStillMerged()
    {
        byte[] input = PdfWithGenerationDifferingDuplicates();
        int before = CountObjectDeclarations(input);

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveDuplicates = true,
            RemoveUnusedObjects = false,
            CompressStreams = false
        });

        int after = CountObjectDeclarations(output.ToArray());
        Assert.True(after < before,
            $"the two near-identical objects did not merge ({after} objects vs {before}); the "
            + "reference generation is no longer being normalised away");
    }

    /// <summary>
    /// The control: the merge still produces a readable document.
    /// </summary>
    /// <remarks>
    /// Paired with the liveness assertion deliberately. On its own, "the duplicates merged" is
    /// satisfied by both the fast and the slow path — which is the point — so it is evidence about
    /// correctness only, and the liveness test is the evidence about the optimisation.
    /// </remarks>
    [Fact]
    public void TheMergeKeepsTheDocumentReadable()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithDuplicateObjects();

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveDuplicates = true,
            RemoveUnusedObjects = false,
            CompressStreams = false
        });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(2, doc.Pages.Count);
    }

    // ------------------------------------------------------------------

    private static int CountObjectDeclarations(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        return Regex.Matches(text, @"(?m)^\d+\s+\d+\s+obj\b").Count;
    }

    /// <summary>
    /// Two pages whose font dictionaries are identical except that one holds <c>9 1 R</c> where the
    /// other holds <c>9 0 R</c>.
    /// </summary>
    private static byte[] PdfWithGenerationDifferingDuplicates()
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]"
            + " /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]"
            + " /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");

        // The pair: identical but for the generation of the /FontDescriptor reference.
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica"
            + " /FontDescriptor 7 0 R >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica"
            + " /FontDescriptor 7 1 R >>\nendobj\n");

        Obj("7 0 obj\n<< /Type /FontDescriptor /FontName /Helvetica /Flags 32 >>\nendobj\n");

        int xref = sb.Length;
        int size = offsets.Count + 1;
        sb.Append("xref\n0 " + size.ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size " + size.ToString(CultureInfo.InvariantCulture)
                  + " /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
