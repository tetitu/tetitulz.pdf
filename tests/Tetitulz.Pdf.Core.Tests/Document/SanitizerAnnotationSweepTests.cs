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
/// The <c>/Annots</c> sweep and the action edits around it — the decisions a
/// <c>PdfSanitizer</c> mutation run found were reached by no other test of the sanitizer, nor by
/// <c>Plugins.Maintenance</c>'s or <c>Plugins.Conformance</c>'s.
/// </summary>
/// <remarks>
/// <para><b>For a sanitizer, failing to strip is the harmful direction</b> — the caller believes the
/// document was cleaned — so every assertion below is that something specific is <i>gone</i>, paired
/// with a control that something benign <i>stayed</i>. Asserting only the removal would also pass on
/// a sanitizer that had emptied the page.</para>
/// </remarks>
public class SanitizerAnnotationSweepTests
{
    private const string JsPayload = "app.alert-annotation-js";

    /// <summary>
    /// <b>Four removable annotations side by side, and the number is load-bearing.</b> The
    /// <c>/Annots</c> loop counts <i>down</i> because it removes as it goes; walking up skips the
    /// element that slides into the index just vacated. Every existing fixture had at most one
    /// removable annotation, so the direction was free to be wrong.
    /// </summary>
    /// <remarks>
    /// <para><b>Two adjacent annotations do not catch it, and finding out why is the point.</b>
    /// Every page is swept <i>twice</i>: once as its own indirect object, and again when the
    /// <c>/Pages</c> node above it is sanitized and <c>SanitizeNameTree</c> descends <c>/Kids</c> —
    /// which a page tree uses as well as a name tree. A forward loop removes every other entry, so
    /// two passes clear any run of two or three and the bug hides. Four leaves one survivor
    /// (4 → 2 → 1), which is the smallest run that outlives both passes.</para>
    ///
    /// <para>So this fixture is sized to the traversal, and a deeper page tree would sweep more
    /// times still. That is a statement about the <i>sanitizer</i> worth keeping: the correctness
    /// of the sweep must not rest on being run more than once, which is exactly what the surviving
    /// mutation showed it was resting on.</para>
    /// </remarks>
    [Fact]
    public void AdjacentRichMediaAnnotationsAreAllRemoved()
    {
        byte[] pdf = WithAnnotations(
            "<< /Type /Annot /Subtype /RichMedia /Rect [0 0 10 10] >>",
            "<< /Type /Annot /Subtype /RichMedia /Rect [10 10 20 20] >>",
            "<< /Type /Annot /Subtype /RichMedia /Rect [20 20 30 30] >>",
            "<< /Type /Annot /Subtype /RichMedia /Rect [30 30 40 40] >>",
            "<< /Type /Annot /Subtype /Text /Contents (keep me) /Rect [40 40 50 50] >>");

        var subtypes = SubtypesAfterSanitize(pdf, new SanitizeOptions());

        Assert.DoesNotContain("RichMedia", subtypes);
        // The control: the sweep removed the two it was asked to and nothing else.
        Assert.Equal(new[] { "Text" }, subtypes);
    }

    /// <summary>
    /// The acceptance control for the option itself. With the switch off the same annotations stay,
    /// so the test above is pinning the sweep rather than a page that lost its annotations.
    /// </summary>
    [Fact]
    public void WithRichMediaRemovalDisabledTheAnnotationsSurvive()
    {
        byte[] pdf = WithAnnotations(
            "<< /Type /Annot /Subtype /RichMedia /Rect [0 0 10 10] >>",
            "<< /Type /Annot /Subtype /RichMedia /Rect [10 10 20 20] >>",
            "<< /Type /Annot /Subtype /RichMedia /Rect [20 20 30 30] >>",
            "<< /Type /Annot /Subtype /RichMedia /Rect [30 30 40 40] >>",
            "<< /Type /Annot /Subtype /Text /Contents (keep me) /Rect [40 40 50 50] >>");

        var subtypes = SubtypesAfterSanitize(
            pdf, new SanitizeOptions { RemoveRichMediaAnnotations = false });

        Assert.Equal(
            new[] { "RichMedia", "RichMedia", "RichMedia", "RichMedia", "Text" }, subtypes);
    }

    /// <summary>
    /// <b>§7.3.10 lets <c>/Subtype</c> be an indirect reference,</b> and whoever supplied the file
    /// chooses. A non-resolving read answers <see langword="null"/>, which this sweep reads as
    /// "not a RichMedia annotation" and keeps — a sanitizer failing open on attacker-controlled
    /// input. The resolving read was already in place; nothing exercised it.
    /// </summary>
    [Fact]
    public void ARichMediaAnnotationIsRemovedWhenItsSubtypeIsWrittenIndirectly()
    {
        byte[] pdf = WithAnnotations(
            new[] { "<< /Type /Annot /Subtype 9 0 R /Rect [0 0 10 10] >>",
                    "<< /Type /Annot /Subtype /Text /Contents (keep me) /Rect [20 20 30 30] >>" },
            extraObjects: new[] { "/RichMedia" });

        var subtypes = SubtypesAfterSanitize(pdf, new SanitizeOptions());

        Assert.Equal(new[] { "Text" }, subtypes);
    }

    /// <summary>
    /// <b>A script hidden beside a harmless action type.</b> <c>/S /Named</c> is not in the removal
    /// ladder, so the action survives — and is then recursed into, where the unconditional
    /// <c>/JS</c> strip takes the payload. That strip is now the <i>only</i> one: the copy that sat
    /// in the <c>/S /JavaScript</c> branch could never fire, because this one runs earlier in the
    /// same call under the same option.
    /// </summary>
    [Fact]
    public void AScriptBesideABenignActionTypeIsStrippedAndTheActionKept()
    {
        byte[] pdf = WithAnnotations(
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] " +
            "/A << /S /Named /N /NextPage /JS (" + JsPayload + ") >> >>");

        byte[] cleaned = Sanitize(pdf, new SanitizeOptions());

        Assert.DoesNotContain(JsPayload, Latin1(cleaned), System.StringComparison.Ordinal);

        PdfDictionary action = FirstAnnotationAction(cleaned);
        Assert.Null(action[PdfName.JS]);
        // The control: only the script went. A sanitizer that dropped the whole action would also
        // satisfy "no /JS", and would break a document it was asked to clean rather than gut.
        Assert.Equal("Named", ((PdfName)action[PdfName.S]).Value);
    }

    /// <summary>
    /// An <c>/AA</c> whose every entry was stripped is removed rather than left behind empty — the
    /// behaviour the code states, and which nothing asserted.
    /// </summary>
    [Fact]
    public void AnAdditionalActionsDictionaryEmptiedByTheSweepIsRemoved()
    {
        byte[] pdf = WithAnnotations(
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] " +
            "/AA << /E << /S /JavaScript /JS (" + JsPayload + ") >> >> >>");

        byte[] cleaned = Sanitize(pdf, new SanitizeOptions());

        PdfDictionary annot = FirstAnnotation(cleaned);
        Assert.Null(annot[PdfName.AA]);
        // The control: the annotation itself is still there, so /AA went and the page did not.
        Assert.Equal("Text", ((PdfName)annot[PdfName.Subtype]).Value);
    }

    /// <summary>
    /// The output is a single revision, so the input's <c>/Prev</c> must not be carried into its
    /// trailer: it would name an offset in a file layout that no longer exists, and a reader
    /// following it sees a revision chain the document does not have.
    /// </summary>
    [Fact]
    public void TheOutputTrailerDoesNotCarryTheInputsPrev()
    {
        byte[] pdf = WithAppendedRevision(
            WithAnnotations("<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>"));

        // The control: the input really does declare one.
        using (var before = new MemoryStream(pdf))
        using (var doc = PdfDocument.Open(before))
            Assert.NotNull(doc.Trailer[PdfName.Prev]);

        byte[] cleaned = Sanitize(pdf, new SanitizeOptions());

        using var after = new MemoryStream(cleaned);
        using var cleanedDoc = PdfDocument.Open(after);
        Assert.Null(cleanedDoc.Trailer[PdfName.Prev]);
        Assert.NotNull(cleanedDoc.Trailer[PdfName.Root]);
    }

    // ------------------------------------------------------------------ helpers

    private static string Latin1(byte[] data) => Encoding.Latin1.GetString(data);

    private static byte[] Sanitize(byte[] pdf, SanitizeOptions options)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, options);
        return output.ToArray();
    }

    private static List<string> SubtypesAfterSanitize(byte[] pdf, SanitizeOptions options)
    {
        byte[] cleaned = Sanitize(pdf, options);
        using var ms = new MemoryStream(cleaned);
        using var doc = PdfDocument.Open(ms);

        var result = new List<string>();
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        if (annots != null)
            for (int i = 0; i < annots.Count; i++)
            {
                var a = doc.ResolveObject(annots[i]) as PdfDictionary;
                var subtype = doc.ResolveObject(a?[PdfName.Subtype]) as PdfName;
                if (subtype != null) result.Add(subtype.Value);
            }
        return result;
    }

    private static PdfDictionary FirstAnnotation(byte[] cleaned)
    {
        using var ms = new MemoryStream(cleaned);
        using var doc = PdfDocument.Open(ms);
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]);
        return (PdfDictionary)doc.ResolveObject(annots[0]);
    }

    private static PdfDictionary FirstAnnotationAction(byte[] cleaned)
    {
        using var ms = new MemoryStream(cleaned);
        using var doc = PdfDocument.Open(ms);
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]);
        var annot = (PdfDictionary)doc.ResolveObject(annots[0]);
        return (PdfDictionary)doc.ResolveObject(annot[PdfName.A]);
    }

    private static byte[] WithAnnotations(params string[] annotations)
        => WithAnnotations(annotations, extraObjects: new string[0]);

    /// <summary>
    /// A one-page document whose <c>/Annots</c> holds <paramref name="annotations"/> as objects
    /// 4..n, followed by <paramref name="extraObjects"/> — anything an annotation refers to
    /// indirectly, numbered from 9 so a fixture can name it without counting.
    /// </summary>
    private static byte[] WithAnnotations(string[] annotations, string[] extraObjects)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            null, // the page, filled in once the annotation object numbers are known
        };

        var refs = new List<string>();
        for (int i = 0; i < annotations.Length; i++)
        {
            bodies.Add(annotations[i]);
            refs.Add((bodies.Count) + " 0 R");
        }
        bodies[2] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots ["
                    + string.Join(" ", refs) + "] >>";

        // Pad to object 9 so an indirect /Subtype can be written as `9 0 R` in the fixture text.
        while (bodies.Count < 8) bodies.Add("<< /Type /Filler >>");
        foreach (string extra in extraObjects) bodies.Add(extra);

        return Assemble(bodies);
    }

    private static byte[] Assemble(List<string> bodies)
    {
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

    /// <summary>Appends a revision that rewrites the page, so the trailer carries a <c>/Prev</c>.</summary>
    private static byte[] WithAppendedRevision(byte[] original)
    {
        string text = Latin1(original);
        int firstXref = int.Parse(text.Substring(
            text.LastIndexOf("startxref\n", System.StringComparison.Ordinal) + 10,
            text.LastIndexOf("\n%%EOF", System.StringComparison.Ordinal)
                - text.LastIndexOf("startxref\n", System.StringComparison.Ordinal) - 10));

        var sb = new StringBuilder();
        int objectOffset = original.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>\nendobj\n");
        int xref = original.Length + sb.Length;
        sb.Append("xref\n3 1\n").AppendFormat("{0:D10} 00000 n \n", objectOffset)
          .Append("trailer\n<< /Size 9 /Root 1 0 R /Prev ").Append(firstXref).Append(" >>\n")
          .Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        byte[] tail = Encoding.Latin1.GetBytes(sb.ToString());
        var updated = new byte[original.Length + tail.Length];
        System.Buffer.BlockCopy(original, 0, updated, 0, original.Length);
        System.Buffer.BlockCopy(tail, 0, updated, original.Length, tail.Length);
        return updated;
    }
}
