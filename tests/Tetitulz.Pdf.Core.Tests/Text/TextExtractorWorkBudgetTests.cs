// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Adversarial-input guard for the Form-XObject <b>work</b> budget in
/// <see cref="PdfTextExtractor"/>.
///
/// <para><c>MaxFormXObjectDepth</c> bounds depth and nothing else. The cycle guard is a
/// per-branch visited set <b>copied at every <c>Do</c></b>, so a name already on the current
/// branch is refused while the same name is re-entered freely on every sibling branch: with N
/// mutually-referencing forms the traversal visits N!/(N−D)! nodes, and each node re-parses a
/// content stream and re-loads a font dictionary. Twelve one-line forms — about 3 KB of PDF —
/// is 12! ≈ 4.8×10<sup>8</sup> invocations and <c>ExtractText</c> never returns.</para>
///
/// <para>The assertions are the guard's own evidence: the call comes back, and the refusal is
/// reported on <see cref="PdfTextExtractor.LastFormXObjectError"/> so a caller can tell
/// truncated text from absent text. Never a wall clock.</para>
/// </summary>
public class TextExtractorWorkBudgetTests
{
    [Fact]
    public void ExtractText_MutuallyReferencingFormXObjects_ReportsTheBudgetRefusal()
    {
        byte[] pdf = BuildPdfWithMutuallyReferencingForms(12);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.NotNull(PdfTextExtractor.LastFormXObjectError);
        Assert.Contains("budget", PdfTextExtractor.LastFormXObjectError!,
            System.StringComparison.OrdinalIgnoreCase);
        // Bounded output: the forms carry one short Tj each, so a traversal that really did visit
        // 12! nodes would return hundreds of megabytes of text.
        Assert.InRange(text.Length, 0, 4 * 1024 * 1024);
    }

    [Fact]
    public void LastFormXObjectError_IsClearedByASubsequentOrdinaryPage()
    {
        // The slot must be reset at the START of the operation that writes it. Before, a page
        // with no Form XObject at all reported whatever the previous page had left behind.
        using (var hostileMs = new MemoryStream(BuildPdfWithMutuallyReferencingForms(12)))
        using (var hostileDoc = PdfDocument.Open(hostileMs))
        {
            PdfTextExtractor.ExtractText(hostileDoc.Pages[0]);
            Assert.NotNull(PdfTextExtractor.LastFormXObjectError);
        }

        using var ms = new MemoryStream(BuildPlainTextPdf());
        using var doc = PdfDocument.Open(ms);
        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.Null(PdfTextExtractor.LastFormXObjectError);
        Assert.Contains("Hi", text);
    }

    [Fact]
    public void ExtractText_ModestFormXObjectFanOut_IsNotRefused()
    {
        // The budget must not become a cap on legitimate documents: three forms, each drawn once,
        // is ordinary and must extract in full with no refusal reported.
        byte[] pdf = BuildPdfWithSequentialForms(3);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.Null(PdfTextExtractor.LastFormXObjectError);
        Assert.Contains("F0", text);
        Assert.Contains("F2", text);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// A page whose /XObject resources declare N Form XObjects, each of whose content streams
    /// invokes ALL of them. Every form is reachable from every other, so only the per-branch
    /// visited set limits re-entry — which is exactly the factorial fan-out.
    /// </summary>
    private static byte[] BuildPdfWithMutuallyReferencingForms(int n)
    {
        var invokeAll = new StringBuilder();
        for (int i = 0; i < n; i++) invokeAll.Append($"/F{i} Do ");

        var xobjects = new StringBuilder();
        for (int i = 0; i < n; i++) xobjects.Append($"/F{i} {5 + i} 0 R ");

        var objects = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                $"/Resources << /XObject << {xobjects}>> >> >>",
            Stream(invokeAll.ToString()),
        };

        for (int i = 0; i < n; i++)
        {
            string body = $"BT (F{i}) Tj ET " + invokeAll;
            objects.Add(FormStream(body, xobjects.ToString()));
        }

        return Assemble(objects);
    }

    /// <summary>N forms, each invoked once from the page and referencing nothing.</summary>
    private static byte[] BuildPdfWithSequentialForms(int n)
    {
        var invokeAll = new StringBuilder();
        for (int i = 0; i < n; i++) invokeAll.Append($"/F{i} Do ");

        var xobjects = new StringBuilder();
        for (int i = 0; i < n; i++) xobjects.Append($"/F{i} {5 + i} 0 R ");

        var objects = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                $"/Resources << /XObject << {xobjects}>> >> >>",
            Stream(invokeAll.ToString()),
        };

        for (int i = 0; i < n; i++)
            objects.Add(FormStream($"BT (F{i}) Tj ET", string.Empty));

        return Assemble(objects);
    }

    private static byte[] BuildPlainTextPdf() => Assemble(new System.Collections.Generic.List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        Stream("BT (Hi) Tj ET"),
    });

    private static string Stream(string content) =>
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static string FormStream(string content, string xobjects)
    {
        string res = xobjects.Length == 0
            ? "<< >>"
            : $"<< /XObject << {xobjects}>> >>";
        return $"<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources {res} " +
               $"/Length {content.Length} >>\nstream\n{content}\nendstream";
    }

    private static byte[] Assemble(System.Collections.Generic.List<string> objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
