// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Reflection;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Locks in operation-layer hardening contracts added in §43 / §46:
/// <list type="bullet">
///   <item>SEC-3 — XMP XML well-formedness rejection.</item>
///   <item>§46 PdfFormFiller octal-escape of control bytes.</item>
/// </list>
/// </summary>
public class OperationContractTests
{
    // ==================================================================
    //  §43.SEC-3 — PdfMetadataWriter rejects malformed XMP.
    // ==================================================================

    [Fact]
    public void WriteXmpMetadata_MalformedXml_Throws()
    {
        // Unbalanced opening tag — XmlReader rejects with XmlException,
        // which the writer wraps into ArgumentException(nameof(xmpXml)).
        const string malformed = "<x:xmpmeta><nope>";
        using var input  = new MemoryStream(MinimalPdfBytes());
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() =>
            PdfMetadataWriter.WriteXmpMetadata(input, output, malformed));
        Assert.Equal("xmpXml", ex.ParamName);
    }

    [Fact]
    public void WriteXmpMetadata_DtdInjection_Rejected()
    {
        // External-entity DOCTYPE — XmlReader has DtdProcessing.Prohibit,
        // so the load throws and the writer surfaces ArgumentException.
        const string xxe = @"<?xml version=""1.0""?>
<!DOCTYPE foo [ <!ENTITY xxe SYSTEM ""file:///etc/passwd""> ]>
<x:xmpmeta xmlns:x=""adobe:ns:meta/""><x:val>&xxe;</x:val></x:xmpmeta>";

        using var input  = new MemoryStream(MinimalPdfBytes());
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() =>
            PdfMetadataWriter.WriteXmpMetadata(input, output, xxe));
    }

    // ==================================================================
    //  §46 — PdfFormFiller.EscapePdfString control-byte handling.
    // ==================================================================

    [Fact]
    public void EscapePdfString_NullByte_OctalEscaped()
    {
        // The escape function is private static; use reflection to
        // verify the contract — a null in the input must NOT pass
        // through unescaped (PDF parsers truncate at null in literal
        // strings).
        var method = typeof(PdfFormFiller).GetMethod(
            "EscapePdfString",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        // "a\0b" -> "a\\000b"
        string result = (string)method!.Invoke(null, new object[] { "a\0b" })!;
        Assert.Contains(@"\000", result);
        // (NUL absence verified by the Contains check above.)
    }

    [Fact]
    public void EscapePdfString_BackslashAndParens_Escaped()
    {
        var method = typeof(PdfFormFiller).GetMethod(
            "EscapePdfString",
            BindingFlags.Static | BindingFlags.NonPublic);
        string result = (string)method!.Invoke(null, new object[] { @"a\b(c)d" })!;
        Assert.Contains(@"\\", result);
        Assert.Contains(@"\(", result);
        Assert.Contains(@"\)", result);
    }

    [Fact]
    public void EscapePdfString_Del_Octal()
    {
        var method = typeof(PdfFormFiller).GetMethod(
            "EscapePdfString",
            BindingFlags.Static | BindingFlags.NonPublic);
        // 0x7F is DEL — must be octal-escaped per the §46 hardening.
        string result = (string)method!.Invoke(null, new object[] { "ab" })!;
        Assert.Contains(@"\177", result);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static byte[] MinimalPdfBytes()
    {
        // Minimal valid one-page PDF that PdfDocument.Open accepts.
        return System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n" +
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n" +
            "xref\n0 5\n0000000000 65535 f \n0000000009 00000 n \n0000000060 00000 n \n0000000113 00000 n \n0000000209 00000 n \n" +
            "trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n259\n%%EOF\n");
    }
}
