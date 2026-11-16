// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// <see cref="RedactorPlugin"/> — the <see cref="IRedactPlugin"/> contract — must actually redact.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-08-12 it called <c>PdfRedactor.CoverRegions</c>, which draws an opaque box and
/// leaves the text underneath extractable, while every name on the path (<c>IRedactPlugin</c>,
/// <c>Redact</c>, <c>RedactionRegion</c>, plugin id <c>Tetitulz.Plugins.Redactor</c>) said
/// otherwise. <b>There were no tests on this type at all</b>, which is how the mismatch survived —
/// the cover-only behaviour was pinned for <c>PdfRedactor.CoverRegions</c> but never for the
/// contract that callers reach through <c>PdfPluginHost</c>.
/// </para>
/// <para>
/// These assert the data is <i>gone</i>, not that a call returned without throwing: a document with
/// a black box over the secret passes any "did it produce a PDF" check while still leaking.
/// </para>
/// </remarks>
public class RedactorPluginContractTests
{
    private const string PublicWord = "PUBLICHEADER";
    private const string SecretWord = "SECRETSSN12345";

    private static byte[] BuildTwoLinePdf()
    {
        string content =
            $"BT /F1 12 Tf 1 0 0 1 100 700 Tm ({PublicWord}) Tj " +
            $"1 0 0 1 100 660 Tm ({SecretWord}) Tj ET";
        return MinimalPdfBuilder.CreateMinimalPdf("ignored", content);
    }

    private static RedactionRegion SecretLine() => new RedactionRegion
    {
        PageIndex = 0, X = 95, Y = 652, Width = 140, Height = 22,
    };

    [Fact]
    public void Redact_RemovesTheSecretFromTheExtractableText()
    {
        using var input = new MemoryStream(BuildTwoLinePdf());
        using var output = new MemoryStream();

        new RedactorPlugin().Redact(input, output, new[] { SecretLine() });

        output.Position = 0;
        string text = PdfOperations.ExtractText(output);

        Assert.DoesNotContain(SecretWord, text);
        // The surrounding line survives, so this is redaction rather than "destroyed the page".
        Assert.Contains(PublicWord, text);
    }

    /// <summary>
    /// The stronger claim, and the one that separates redaction from a cover box: the secret's
    /// bytes are absent from the whole output file, not merely from what a text extractor reports.
    /// </summary>
    [Fact]
    public void Redact_LeavesNoResidualSecretBytesAnywhereInTheFile()
    {
        using var input = new MemoryStream(BuildTwoLinePdf());
        using var output = new MemoryStream();

        new RedactorPlugin().Redact(input, output, new[] { SecretLine() });

        byte[] bytes = output.ToArray();
        Assert.False(ContainsAscii(bytes, SecretWord),
            "The redacted string still appears in the output bytes; a cover box would also pass " +
            "the text-extraction check above, which is why this assertion exists.");
        // Fixture-validity control: the marker IS present before redaction, so the assertion above
        // cannot pass vacuously.
        Assert.True(ContainsAscii(BuildTwoLinePdf(), SecretWord),
            "The fixture never contained the secret, so this test proves nothing.");
    }

    /// <summary>
    /// A secret inside a Form XObject shared by two pages: the plugin redacts the page it was given
    /// by substituting a scrubbed clone there, and leaves the other page's copy alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Renamed from <c>Redact_WhenRemovalCannotBeProvenSafe_ThrowsRatherThanLeaking</c>, which
    /// asserted a refusal. Plan 08 item 46 replaced that refusal with cloning, so the name changed
    /// with the behaviour instead of the assertion being inverted underneath it.
    /// </para>
    /// <para>
    /// <b>The engine still fails closed elsewhere</b> — an encrypted document, or a form nested
    /// deeper than the depth cap — so this is a narrowing of the refusal, not its removal.
    /// </para>
    /// </remarks>
    [Fact]
    public void Redact_SecretInASharedForm_ScrubsTheGivenPageAndKeepsTheOther()
    {
        byte[] pdf = BuildSecretInsideAFormUsedByTwoPages();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        new RedactorPlugin().Redact(input, output, new[] { SecretLine() });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.DoesNotContain(SecretWord, PdfTextExtractor.ExtractText(doc.Pages[0]));
        // Page 1 was never named, so its draw of the original form is untouched.
        Assert.Contains(SecretWord, PdfTextExtractor.ExtractText(doc.Pages[1]));
    }

    [Fact]
    public void Redact_NullArguments_Throw()
    {
        var plugin = new RedactorPlugin();
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => plugin.Redact(null, ms, Array.Empty<RedactionRegion>()));
        Assert.Throws<ArgumentNullException>(() => plugin.Redact(ms, null, Array.Empty<RedactionRegion>()));
        Assert.Throws<ArgumentNullException>(() => plugin.Redact(ms, ms, null));
    }

    /// <summary>
    /// One Form XObject carrying the secret, drawn on <b>two</b> pages. A single in-place rewrite of
    /// that form cannot remove the secret from one page without altering the other, so the engine
    /// redacts a clone at the named page and keeps the original for the other, saying so in
    /// <c>LastSharedFormWarning</c>. Copied rather than shared with <c>TrueRedaction_Tests</c>: tests build their own
    /// in-memory PDFs, and cross-file helper sharing is what lets a fixture drift away from the
    /// claim it supports.
    /// </summary>
    private static byte[] BuildSecretInsideAFormUsedByTwoPages()
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        const string pageContent = "q 1 0 0 1 0 0 cm /Fm0 Do Q";
        string formContent =
            $"BT /F1 12 Tf 1 0 0 1 100 700 Tm ({PublicWord}) Tj " +
            $"1 0 0 1 100 660 Tm ({SecretWord}) Tj ET";

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << /XObject << /Fm0 6 0 R >> /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj($"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 5 0 R >> >> /Length {formContent.Length} >>\nstream\n{formContent}\nendstream\nendobj\n");
        Obj("7 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 8 0 R /Resources << /XObject << /Fm0 6 0 R >> /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"8 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");

        int total = offsets.Count + 1;
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", total);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", total);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + n.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < n.Length; j++)
                if (haystack[i + j] != n[j]) { hit = false; break; }
            if (hit) return true;
        }
        return false;
    }
}
