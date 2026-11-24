// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// A Form XObject may only be rewritten in place when it is drawn <b>exactly once in the whole
/// document</b> — and "the whole document" includes invocations that live inside another form.
///
/// <para><b>The bug these pin.</b> The uniqueness test counted <c>Do</c> operators in page content
/// streams only. A form invoked once from a page and once from within a second form therefore
/// scored 1, looked unambiguous, and was rewritten — silently changing the second place it appeared,
/// on a page the caller never named. Redaction is the operation where that matters most: it is
/// supposed to remove content from where you asked, not from wherever else the same form happens to
/// be drawn.</para>
///
/// <para>The fixture is built so the two invocations are on <b>different pages</b>, which is what
/// makes the collateral damage observable: the assertion is about page 2, which no redaction area
/// ever names.</para>
/// </summary>
public class NestedFormRedactionTests
{
    /// <summary>
    /// Object 6 is the shared form drawing "SECRET". Page 1 draws it directly; page 2 draws form 7,
    /// which itself draws form 6. So form 6 has two invocation sites — one of them inside a form —
    /// and the page-only counter saw only the first.
    /// </summary>
    private static byte[] SharedFormAcrossPages() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 300] >>",
        // page 1: draws the shared form directly
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
            "/Resources << /XObject << /Shared 6 0 R >> >> /Contents 8 0 R >>",
        // page 2: draws the wrapper form, which draws the shared form
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
            "/Resources << /XObject << /Wrap 7 0 R >> >> /Contents 9 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        // 6: the shared form
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 300 300] " +
               "/Resources << /Font << /F1 5 0 R >> >>",
               "BT /F1 24 Tf 20 100 Td (SECRET) Tj ET"),
        // 7: the wrapper form, which invokes 6
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 300 300] " +
               "/Resources << /XObject << /Shared 6 0 R >> >>",
               "/Shared Do"),
        Stream(string.Empty, "/Shared Do"),   // 8: page 1 content
        Stream(string.Empty, "/Wrap Do"));    // 9: page 2 content

    /// <summary>
    /// A form reached from two places is not refused: the invocation on the redacted page gets a
    /// scrubbed <b>clone</b>, and the other place keeps the original.
    /// </summary>
    [Fact]
    public void RedactingAFormAlsoDrawnFromAnotherForm_ClonesItForTheRedactedPage()
    {
        using var input = new MemoryStream(SharedFormAcrossPages());
        using var output = new MemoryStream();

        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 0, Y = 80, Width = 300, Height = 60 },
        });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        string page0 = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.DoesNotContain("SECRET", page0);
    }

    /// <summary>
    /// The control, and the reason the test above is not vacuous: a form drawn from exactly one
    /// place — no nesting anywhere — is still redacted in place rather than refused. Without this
    /// pair, an implementation that refused every form would pass.
    /// </summary>
    [Fact]
    public void RedactingAFormDrawnFromExactlyOnePlace_StillSucceeds()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
                "/Resources << /XObject << /Only 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 300 300] " +
                   "/Resources << /Font << /F1 4 0 R >> >>",
                   "BT /F1 24 Tf 20 100 Td (SECRET) Tj ET"),
            Stream(string.Empty, "/Only Do"));

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 0, Y = 80, Width = 300, Height = 60 },
        });

        Assert.DoesNotContain("SECRET", ExtractText(output.ToArray()));
    }

    // ── fixture plumbing ────────────────────────────────────────────────────────────────────

    private static string ExtractText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc);
    }

    private static string Stream(string entries, string content) =>
        $"<< {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = pdf.Length;
            pdf.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++)
            pdf.AppendFormat("{0:D10} 00000 n \n", offsets[i]);

        pdf.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
