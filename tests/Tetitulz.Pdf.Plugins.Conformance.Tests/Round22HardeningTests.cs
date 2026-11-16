// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Locks in Round 22: four bounded hardenings to API / encryption /
/// accessibility surfaces.  See GAPS.md for context.
/// </summary>
public class Round22HardeningTests
{
    // ----------------------------------------------------------------
    //  R22.A1 — PdfAccessibilityFixer string serialisation
    // ----------------------------------------------------------------
    //
    //  Round 22 hardened a hand-rolled `EscapeString` helper inside
    //  PdfAccessibilityFixer.  That whole hand-rolled writer has since been
    //  deleted: it encoded every object through Latin-1, so any character
    //  above U+00FF (Hebrew, Arabic, CJK) in a /TU tooltip or /Alt value was
    //  silently replaced with '?'.  Serialisation now delegates to Core's
    //  PdfWriter, which writes PdfString.RawBytes losslessly.
    //
    //  The property the original tests protected — a value can never
    //  terminate the literal string early or smuggle in content-stream
    //  operators — is retained and re-asserted below against the writer that
    //  actually performs the escaping now.

    private static string WriteObjectAsLatin1(PdfObject obj)
    {
        using var ms = new MemoryStream();
        new PdfWriter(ms).WriteObject(obj);
        // ISO-8859-1 maps bytes 0x00-0xFF 1:1 to code points, so the decoded
        // string is a faithful view of the bytes that were written.
        return Encoding.GetEncoding("iso-8859-1").GetString(ms.ToArray());
    }

    [Fact]
    public void AccessibilityFixer_StringSerialisation_EscapesLiteralDelimiters()
    {
        string written = WriteObjectAsLatin1(new PdfString(@"a(b)c\d"));
        Assert.Equal(@"(a\(b\)c\\d)", written);
    }

    [Fact]
    public void AccessibilityFixer_StringSerialisation_EscapesCarriageReturnAndLineFeed()
    {
        const char CR = (char)0x0D;
        const char LF = (char)0x0A;
        string written = WriteObjectAsLatin1(new PdfString("Tip" + CR + "and" + LF + "more"));

        Assert.Equal(@"(Tip\rand\nmore)", written);
        // The raw line-break bytes must not survive — an unescaped CR/LF inside a
        // literal string is what let a crafted tooltip break the object apart.
        Assert.DoesNotContain(CR, written);
        Assert.DoesNotContain(LF, written);
    }

    [Fact]
    public void AccessibilityFixer_StringSerialisation_PreservesEveryOtherByteVerbatim()
    {
        // Non-delimiter bytes are legal inside a literal string (PDF §7.3.4.2) and
        // MUST round-trip untouched: this is precisely what makes a Hebrew or CJK
        // UTF-16BE tooltip survive the write.
        byte[] raw = { 0xFE, 0xFF, 0x05, 0xD0, 0x05, 0xDC, 0x7F, 0x1F };
        string written = WriteObjectAsLatin1(new PdfString(raw));

        Assert.StartsWith("(", written, StringComparison.Ordinal);
        Assert.EndsWith(")", written, StringComparison.Ordinal);

        byte[] body = Encoding.GetEncoding("iso-8859-1").GetBytes(
            written.Substring(1, written.Length - 2));
        Assert.Equal(raw, body);
    }

    /// <summary>
    /// Round 22 caught a real production bug: PDF §7.3.4.2 requires
    /// <c>\ddd</c> to be OCTAL but the hardening used <c>{0:D3}</c>
    /// (decimal).  For 0x1F that produced <c>\031</c> which the parser
    /// reads as octal 031 = 0x19 (wrong byte); for 0x09 it produced
    /// <c>\009</c> where '9' is illegal in octal and aborts the escape.
    /// This regression test pins the now-correct octal output on
    /// PdfFormFiller — the original site of the bug, which still uses its
    /// own escaper.
    /// </summary>
    [Fact]
    public void FormFiller_EscapePdfString_UsesOctal_NotDecimal()
    {
        var mi = typeof(PdfFormFiller).GetMethod(
            "EscapePdfString",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(mi);

        string r1 = (string)mi.Invoke(null, new object[] { ((char)0x1F).ToString() });
        Assert.Equal(@"\037", r1);
        string r2 = (string)mi.Invoke(null, new object[] { ((char)0x7F).ToString() });
        Assert.Equal(@"\177", r2);
    }

    // ----------------------------------------------------------------
    //  R22.A2 — PdfAttachmentManager rejects empty filenames
    // ----------------------------------------------------------------

    [Fact]
    public void AttachmentManager_AddAttachment_EmptyFilename_Throws()
    {
        using var input  = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();
        var data = Encoding.ASCII.GetBytes("hello");

        var ex = Assert.Throws<ArgumentException>(() =>
            PdfAttachmentManager.AddAttachment(input, output, "", data));

        Assert.Equal("filename", ex.ParamName);
    }

    [Fact]
    public void AttachmentManager_AddAttachment_WhitespaceFilename_Throws()
    {
        using var input  = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();
        var data = Encoding.ASCII.GetBytes("hello");

        Assert.Throws<ArgumentException>(() =>
            PdfAttachmentManager.AddAttachment(input, output, "   ", data));
    }

    // ----------------------------------------------------------------
    //  R22.E1 / R22.E2 — Decryptor diagnostic surface
    // ----------------------------------------------------------------

    [Fact]
    public void Decryptor_HasV5KeyDerivationDiagnosticProperty()
    {
        // Property must exist with the expected shape — public static get.
        var prop = typeof(PdfDecryptor).GetProperty(
            "LastV5KeyDerivationIssue",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(prop);
        Assert.Equal(typeof(string), prop.PropertyType);
    }

    [Fact]
    public void Decryptor_HasAesCbcPaddingFallbackDiagnosticProperty()
    {
        var prop = typeof(PdfDecryptor).GetProperty(
            "LastAesCbcPaddingFallback",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(prop);
        Assert.Equal(typeof(string), prop.PropertyType);
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    private static byte[] MinimalPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        long obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        long obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        long xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
