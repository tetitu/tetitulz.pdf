// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// Review findings #2 and #3 for <see cref="PdfSanitizer"/>:
///   #2 — a /Subtype /FileAttachment annotation with a /FS → /EF embedded stream survived
///        sanitization (only the catalog /Names /EmbeddedFiles tree was swept).
///   #3 — a JavaScript action authored as a direct INLINE dict inside a /Names /JavaScript
///        name-tree leaf survived (the walk never descended /Names / /Kids name trees).
/// Both payloads must now be stripped.
/// </summary>
public class ReviewFixSanitizer_Tests
{
    private const string EmbeddedPayload = "EMBEDDED-FILE-SECRET-PAYLOAD";

    /// <summary>
    /// <b>No parenthesis, no backslash, and that is load-bearing.</b> This payload is a PDF literal
    /// <i>string</i> (a <c>/JS</c> value), and <c>PdfStringEscape.EscapeLiteral</c> backslash-escapes
    /// <c>(</c> and <c>)</c> unconditionally. The original payload was
    /// <c>app.alert('inline-name-tree-js')</c>, which the writer necessarily re-emits as
    /// <c>app.alert\('inline-name-tree-js'\)</c> — so searching the output bytes for the raw
    /// spelling could <b>never</b> find it, and the assertion below passed with the entire name-tree
    /// walk deleted. Deleting <c>SanitizeNameTree</c> is what exposed it: the suite stayed green.
    ///
    /// <para>The <c>EmbeddedPayload</c> above has the same shape but is safe for the opposite
    /// reason — it lives in a <i>stream</i>, whose bytes are written raw and never escaped.</para>
    /// </summary>
    private const string InlineJsPayload = "app.alert-inline-name-tree-js";

    [Fact]
    public void Sanitize_RemovesFileAttachmentEmbeddedFile()
    {
        byte[] pdf = BuildPdfWithFileAttachment();

        // The embedded payload is present before sanitization.
        Assert.Contains(EmbeddedPayload, Latin1(pdf));

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions { RemoveEmbeddedFiles = true });

        byte[] cleaned = output.ToArray();

        // The FileAttachment annotation and its embedded payload are gone from the bytes.
        Assert.DoesNotContain(EmbeddedPayload, Latin1(cleaned));

        // And the page no longer references a FileAttachment annotation.
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var page = doc.Pages[0];
        var annots = doc.ResolveObject(page.Dictionary[PdfName.Annots]) as PdfArray;
        if (annots != null)
        {
            for (int i = 0; i < annots.Count; i++)
            {
                var a = doc.ResolveObject(annots[i]) as PdfDictionary;
                var subtype = a?.GetName(PdfName.Subtype);
                Assert.False(subtype != null && subtype.Value == "FileAttachment",
                    "FileAttachment annotation must be removed when RemoveEmbeddedFiles is set.");
            }
        }
    }

    [Fact]
    public void Sanitize_StripsInlineJavaScriptInNamesTree()
    {
        byte[] pdf = BuildPdfWithInlineJsNameTree();

        // The control: the payload really is in the input, and survives the writer's escaping
        // unchanged, so its absence below means it was removed rather than merely re-spelled.
        Assert.Contains(InlineJsPayload, Latin1(pdf), System.StringComparison.Ordinal);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions { RemoveJavaScript = true });

        byte[] cleaned = output.ToArray();

        // The /JS string authored inline in the /Names /JavaScript leaf must be stripped.
        Assert.DoesNotContain(InlineJsPayload, Latin1(cleaned), System.StringComparison.Ordinal);

        // And on the parsed object, not only the bytes: the leaf's action dictionary must carry
        // neither the script nor the /S that names it. A byte search alone cannot tell "removed"
        // from "written differently", which is precisely how this test used to pass.
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var names = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]) as PdfDictionary;
        var jsRoot = doc.ResolveObject(names[new PdfName("JavaScript")]) as PdfDictionary;
        var leaf = doc.ResolveObject(jsRoot[PdfName.Names]) as PdfArray;
        var action = doc.ResolveObject(leaf[1]) as PdfDictionary;

        // The benign entry proves the leaf was reached and EDITED rather than dropped whole —
        // an empty or missing dictionary would satisfy "no /JS" for the wrong reason.
        Assert.Equal("Action", ((PdfName)action[PdfName.Type]).Value);
        Assert.Null(action[PdfName.JS]);
        Assert.Null(action[PdfName.S]);
    }

    private static string Latin1(byte[] data) => Encoding.GetEncoding("iso-8859-1").GetString(data);

    // ----------------------------------------------------------------
    //  Fixtures
    // ----------------------------------------------------------------

    // Page with a /Subtype /FileAttachment annotation whose /FS file-spec carries an
    // /EF embedded file stream — the catalog-level /Names sweep does NOT reach this.
    private static byte[] BuildPdfWithFileAttachment()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        const string content = "BT /F1 12 Tf 100 700 Td (hi) Tj ET";

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Contents 7 0 R /Annots [4 0 R] /Resources << >> >>\nendobj\n");

        // FileAttachment annotation → /FS file-spec (5 0 R).
        int off4 = pdf.Length;
        pdf.Append("4 0 obj\n<< /Type /Annot /Subtype /FileAttachment /Rect [100 100 120 120] " +
                   "/FS 5 0 R >>\nendobj\n");

        // File-spec dict → /EF /F embedded file stream (6 0 R).
        int off5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Filespec /F (secret.bin) /EF << /F 6 0 R >> >>\nendobj\n");

        // Embedded file stream carrying the secret payload.
        int off6 = pdf.Length;
        pdf.AppendFormat("6 0 obj\n<< /Type /EmbeddedFile /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            EmbeddedPayload.Length, EmbeddedPayload);

        int off7 = pdf.Length;
        pdf.AppendFormat("7 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);

        int xref = pdf.Length;
        pdf.Append("xref\n0 8\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.AppendFormat("{0:D10} 00000 n \n", off6);
        pdf.AppendFormat("{0:D10} 00000 n \n", off7);
        pdf.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // Catalog → /Names /JavaScript name tree whose leaf /Names array maps a name to an
    // INLINE JavaScript-action dictionary. No /A, /AA, /Next or /OpenAction is involved.
    private static byte[] BuildPdfWithInlineJsNameTree()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        // Catalog with an inline /Names dict → inline /JavaScript name-tree root → inline
        // /Names leaf array → inline JS action dict (all direct, not separate objects).
        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R " +
                   "/Names << /JavaScript << /Names [ (Doc) << /Type /Action /S /JavaScript /JS (" +
                   InlineJsPayload + ") >> ] >> >> >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        const string content = "BT /F1 12 Tf 100 700 Td (hi) Tj ET";

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Contents 4 0 R /Resources << >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);

        int xref = pdf.Length;
        pdf.Append("xref\n0 5\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
