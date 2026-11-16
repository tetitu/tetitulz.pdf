// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// True (content-removal) redaction via <see cref="PdfRedactor.RedactRegions"/>.
/// Unlike the cover-only <see cref="PdfRedactor.CoverRegions"/>, the covered glyphs/images
/// are physically removed and the output is a full single-revision PDF, so the data is
/// recoverable from no revision. These tests assert the data is genuinely GONE (not just
/// hidden), surrounding content survives, and the engine fails closed when it cannot
/// guarantee removal.
/// </summary>
public class TrueRedaction_Tests
{
    // PUBLIC stays (y=700); SECRET is redacted (y=660). Latin-1 unique markers.
    private const string PublicWord = "PUBLICHEADER";
    private const string SecretWord = "SECRETSSN12345";

    private static byte[] BuildTwoLinePdf()
    {
        string content =
            $"BT /F1 12 Tf 1 0 0 1 100 700 Tm ({PublicWord}) Tj " +
            $"1 0 0 1 100 660 Tm ({SecretWord}) Tj ET";
        return MinimalPdfBuilder.CreateMinimalPdf("ignored", content);
    }

    [Fact]
    public void RedactRegions_RemovesText_KeepsSurrounding_AndLeavesNoResidualBytes()
    {
        byte[] pdf = BuildTwoLinePdf();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.RedactRegions(input, output, new[]
        {
            // Covers the SECRET line (baseline y=660) but not the PUBLIC line (y=700).
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 140, Height = 22 },
        });

        byte[] outBytes = output.ToArray();

        // 1. The secret is no longer extractable...
        output.Position = 0;
        string text = PdfOperations.ExtractText(output);
        Assert.DoesNotContain(SecretWord, text);
        // 2. ...and the surrounding text survives.
        Assert.Contains(PublicWord, text);

        // 3. The secret bytes appear NOWHERE in the output file (no superseded revision).
        Assert.False(ContainsAscii(outBytes, SecretWord),
            "Redacted text must not survive anywhere in the output bytes.");
        Assert.True(ContainsAscii(outBytes, PublicWord));
    }

    [Fact]
    public void RedactRegions_RemovesImagePixelData()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(100, 100); // image at [100,200]x[600,700]

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 100, Y = 600, Width = 100, Height = 100 },
        });

        byte[] outBytes = output.ToArray();

        // The embedded JPEG (SOI marker FF D8 FF) must be gone from the output.
        Assert.True(ContainsSequence(pdf, new byte[] { 0xFF, 0xD8, 0xFF }), "fixture should contain a JPEG");
        Assert.False(ContainsSequence(outBytes, new byte[] { 0xFF, 0xD8, 0xFF }),
            "Redacted image pixel data must not survive in the output.");

        // The unrelated caption text (y=500) is preserved.
        output.Position = 0;
        Assert.Contains("Image Test", PdfOperations.ExtractText(output));
    }

    [Fact]
    public void RedactRegions_PreservesOtherPages()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(2); // "Page 1" @ idx0, "Page 2" @ idx1

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 695, Width = 140, Height = 22 },
        });

        output.Position = 0;
        var layouts = PdfOperations.ExtractStructuredText(output);
        Assert.Equal(2, layouts.Count); // page count preserved

        output.Position = 0;
        string text = PdfOperations.ExtractText(output);
        Assert.DoesNotContain("Page 1", text);
        Assert.Contains("Page 2", text);
    }

    [Fact]
    public void RedactRegions_RedactsContentInsideSingleUseForm()
    {
        // A page whose body is wrapped in its own form, invoked exactly once → the form's
        // content stream is rewritten in place (single-use forms are supported).
        byte[] pdf = BuildFormWrappedPdf(invokeOnSecondPageToo: false);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 140, Height = 22 },
        });

        byte[] outBytes = output.ToArray();
        output.Position = 0;
        string text = PdfOperations.ExtractText(output);
        Assert.DoesNotContain(SecretWord, text);
        Assert.Contains(PublicWord, text);
        Assert.False(ContainsAscii(outBytes, SecretWord),
            "Form-internal secret must not survive in the output bytes.");
    }

    /// <summary>
    /// The same form drawn on TWO pages. The engine no longer refuses: it substitutes a redacted
    /// <b>clone</b> at page 0's invocation and leaves the original for page 1, which the caller
    /// never asked to redact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces <c>RedactRegions_FailsClosed_WhenSecretIsInsideASharedForm</c>, renamed rather
    /// than re-asserted because the behaviour it named genuinely changed (plan 08 item 46).
    /// </para>
    /// <para>
    /// <b>Read the second assertion carefully — it is the cost of this feature.</b> The secret is
    /// gone from page 0, but it is still in the file, because page 1 still draws the original form
    /// and page 1 was not redacted. A caller who needs the bytes gone everywhere must redact every
    /// page that draws the form. That is a real reduction from the old refusal, which at least made
    /// the situation impossible to miss.
    /// </para>
    /// </remarks>
    [Fact]
    public void RedactRegions_SharedForm_RedactsTheNamedPageAndLeavesTheOtherIntact()
    {
        byte[] pdf = BuildFormWrappedPdf(invokeOnSecondPageToo: true);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 140, Height = 22 },
        });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Equal(2, doc.Pages.Count);

        // Page 0 - the page the caller named - no longer shows the secret...
        string page0 = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc.Pages[0]);
        Assert.DoesNotContain(SecretWord, page0);
        Assert.Contains(PublicWord, page0);

        // ...while page 1, which was never redacted, still draws the original form in full.
        string page1 = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc.Pages[1]);
        Assert.Contains(SecretWord, page1);
    }

    [Fact]
    public void RedactRegions_NoRegions_CopiesInputThrough()
    {
        byte[] pdf = BuildTwoLinePdf();
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.RedactRegions(input, output, Array.Empty<RedactionRegion>());

        output.Position = 0;
        string text = PdfOperations.ExtractText(output);
        Assert.Contains(PublicWord, text);
        Assert.Contains(SecretWord, text); // nothing redacted
    }

    // ---- helpers ----

    private static bool ContainsAscii(byte[] haystack, string ascii)
        => ContainsSequence(haystack, Encoding.ASCII.GetBytes(ascii));

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    // A form (obj 6) holding PUBLIC (y=700) + SECRET (y=660), drawn via "q /Fm0 Do Q".
    // When invokeOnSecondPageToo is true the same form is also drawn on a second page, so it
    // is invoked twice (shared) and can no longer be redacted in place.
    private static byte[] BuildFormWrappedPdf(bool invokeOnSecondPageToo)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string kids = invokeOnSecondPageToo ? "3 0 R 7 0 R" : "3 0 R";
        int count = invokeOnSecondPageToo ? 2 : 1;
        const string pageContent = "q 1 0 0 1 0 0 cm /Fm0 Do Q";
        string formContent =
            $"BT /F1 12 Tf 1 0 0 1 100 700 Tm ({PublicWord}) Tj " +
            $"1 0 0 1 100 660 Tm ({SecretWord}) Tj ET";

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {count} >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << /XObject << /Fm0 6 0 R >> /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj($"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 5 0 R >> >> /Length {formContent.Length} >>\nstream\n{formContent}\nendstream\nendobj\n");
        if (invokeOnSecondPageToo)
        {
            Obj("7 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 8 0 R /Resources << /XObject << /Fm0 6 0 R >> /Font << /F1 5 0 R >> >> >>\nendobj\n");
            Obj($"8 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");
        }

        int total = offsets.Count + 1; // + free object 0
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", total);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", total);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
