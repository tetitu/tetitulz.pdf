// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Merging carries page labels and output intents across — the last two entries of GAPS §19d's
/// structure-preserving-merge row.
/// </summary>
/// <remarks>
/// <para><b>The label assertions read the labels back through
/// <see cref="PdfOperations.GetPageLabels(System.IO.Stream)"/>.</b> That expands the ranges the way a viewer does, so
/// it can see the thing the raw tree cannot show: a range whose key was not shifted silently
/// relabels the pages after it rather than producing anything malformed.</para>
///
/// <para><b>Each case pairs the two sources' labels.</b> A merge that dropped the second source's
/// tree, and one that let the second source's range start at key 0 and swallow the first, both
/// produce a well-formed document — and each is caught only by asserting what the FIRST document's
/// pages are called after the merge as well as the second's.</para>
/// </remarks>
public sealed class PageLabelAndOutputIntentMergeTests
{
    /// <summary>
    /// A two-page document, optionally labelled with <paramref name="prefix"/> and optionally
    /// declaring an output intent of subtype <paramref name="intentSubtype"/>.
    /// </summary>
    private static byte[] Document(
        string prefix = null, string intentSubtype = null, string condition = "Coated")
    {
        string labels = prefix == null
            ? ""
            : $" /PageLabels << /Nums [0 << /S /D /P ({prefix}) >>] >>";

        string intents = intentSubtype == null
            ? ""
            : $" /OutputIntents [<< /Type /OutputIntent /S /{intentSubtype} " +
              $"/OutputConditionIdentifier ({condition}) >>]";

        const string content = "BT /F1 12 Tf 72 700 Td (page) Tj ET\n";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + labels + intents + " >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Merge(params byte[][] inputs)
    {
        var streams = new List<Stream>();
        foreach (byte[] pdf in inputs) streams.Add(new MemoryStream(pdf, writable: false));
        try
        {
            using var output = new MemoryStream();
            PdfMerger.Merge(output, streams.ToArray());
            return output.ToArray();
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    private static IReadOnlyList<string> Labels(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfOperations.GetPageLabels(ms);
    }

    // ================================================================ page labels

    /// <summary>
    /// Each source's pages keep the labels that source gave them.
    /// </summary>
    /// <remarks>
    /// The second source's range is keyed 0 in its own document. Carried unshifted it would start
    /// at merged page 0 and relabel the FIRST document's pages as well — a well-formed tree that
    /// renames pages nobody asked about, which is why the first two entries are asserted too.
    /// </remarks>
    [Fact]
    public void EachSourcesPagesKeepItsOwnLabels()
    {
        var labels = Labels(Merge(Document("A-"), Document("B-")));

        Assert.Equal(new[] { "A-1", "A-2", "B-1", "B-2" }, labels);
    }

    /// <summary>
    /// An unlabelled source ahead of a labelled one gets the default numbering, and the labelled
    /// one still starts where its pages start.
    /// </summary>
    /// <remarks>
    /// §12.4.2 requires an entry for page index 0. Without the synthesised default the merged tree
    /// would begin at key 2, which is malformed — and read by a viewer as anything from "ignore the
    /// labels" to "apply the second document's range from page 0", the second of which mislabels
    /// pages that have no labels at all.
    /// </remarks>
    [Fact]
    public void AnUnlabelledSourceGetsDefaultNumberingRatherThanTheNextSourcesLabels()
    {
        var labels = Labels(Merge(Document(), Document("B-")));

        Assert.Equal(new[] { "1", "2", "B-1", "B-2" }, labels);
    }

    /// <summary>
    /// Labels that repeat after a merge are kept as written and reported.
    /// </summary>
    /// <remarks>
    /// Renumbering them would discard what each source said, which is the thing a merged bundle's
    /// labels exist to preserve. What a caller cannot see from the output is that "go to page 1" is
    /// now ambiguous, so that is what the warning says.
    /// </remarks>
    [Fact]
    public void RepeatedLabelsAreKeptAndReported()
    {
        var labels = Labels(Merge(Document("Doc "), Document("Doc ")));

        Assert.Equal(new[] { "Doc 1", "Doc 2", "Doc 1", "Doc 2" }, labels);
        Assert.NotNull(PdfMerger.LastPageLabelWarning);
        Assert.Contains("twice", PdfMerger.LastPageLabelWarning);
    }

    /// <summary>
    /// Merging unlabelled documents writes no page-label tree and reports nothing.
    /// </summary>
    /// <remarks>
    /// A tree holding nothing but synthesised defaults says exactly what its absence says, and
    /// writing one would make every merge of ordinary documents look like a relabelling.
    /// </remarks>
    [Fact]
    public void MergingUnlabelledDocumentsWritesNoPageLabelTree()
    {
        byte[] merged = Merge(Document(), Document());

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.Catalog.Dictionary[PdfName.PageLabels]);
        Assert.Null(PdfMerger.LastPageLabelWarning);
    }

    /// <summary>A single labelled source comes through unchanged and reports nothing.</summary>
    [Fact]
    public void ASingleLabelledSourceIsUnchanged()
    {
        Assert.Equal(new[] { "A-1", "A-2" }, Labels(Merge(Document("A-"))));
        Assert.Null(PdfMerger.LastPageLabelWarning);
    }

    // ================================================================ output intents

    /// <summary>Output intents of different subtypes are both carried.</summary>
    [Fact]
    public void OutputIntentsOfDifferentSubtypesAreBothCarried()
    {
        byte[] merged = Merge(
            Document(intentSubtype: "GTS_PDFX", condition: "FOGRA39"),
            Document(intentSubtype: "GTS_PDFA1", condition: "sRGB"));

        Assert.Equal(new[] { "FOGRA39", "sRGB" }, Conditions(merged));
        Assert.Null(PdfMerger.LastOutputIntentWarning);
    }

    /// <summary>
    /// A second intent of the same subtype is dropped, with the first kept, and the caller is told
    /// what went.
    /// </summary>
    /// <remarks>
    /// A PDF/X file has exactly one <c>GTS_PDFX</c> intent and it names the printing condition the
    /// whole document was prepared for. Two sources prepared for different conditions cannot both
    /// be right about the merged document; emitting both produces a file no validator accepts and
    /// that a prepress workflow may colour-manage with either.
    /// </remarks>
    [Fact]
    public void ASecondIntentOfTheSameSubtypeIsDroppedAndReported()
    {
        byte[] merged = Merge(
            Document(intentSubtype: "GTS_PDFX", condition: "FOGRA39"),
            Document(intentSubtype: "GTS_PDFX", condition: "SWOP"));

        Assert.Equal(new[] { "FOGRA39" }, Conditions(merged));
        Assert.NotNull(PdfMerger.LastOutputIntentWarning);
        Assert.Contains("GTS_PDFX", PdfMerger.LastOutputIntentWarning);
    }

    /// <summary>No source declares an intent, so the merged catalog has none.</summary>
    [Fact]
    public void NoIntentsMeansNoOutputIntentsEntry()
    {
        byte[] merged = Merge(Document(), Document());

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.Catalog.Dictionary[PdfName.OutputIntents]);
        Assert.Null(PdfMerger.LastOutputIntentWarning);
    }

    private static string[] Conditions(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        var intents = (PdfArray)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OutputIntents]);
        var conditions = new string[intents.Count];
        for (int i = 0; i < intents.Count; i++)
        {
            var intent = (PdfDictionary)doc.ResolveObject(intents[i]);
            conditions[i] = ((PdfString)doc.ResolveObject(
                intent[new PdfName("OutputConditionIdentifier")])).GetText();
        }
        return conditions;
    }
}
