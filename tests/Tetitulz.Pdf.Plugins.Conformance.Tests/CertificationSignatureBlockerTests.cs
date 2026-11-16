// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A certification signature reachable only through <c>/Perms /DocMDP</c> still blocks the PDF/A
/// and PDF/X converters.
/// </summary>
/// <remarks>
/// <para>Both converters rewrite the whole file through <c>PdfSanitizer.Sanitize</c>, which
/// renumbers every object — so a signed input's <c>/ByteRange</c> stops covering what was signed
/// and the signature verifies as <b>corrupt</b>. They therefore refuse a signed document, and the
/// refusal is only as good as the detection behind it.</para>
///
/// <para><b>The plugin's own walk began at <c>/AcroForm /Fields</c> and returned false when that
/// was absent.</b> §12.8.1 does not require a certification signature's field to appear there, and
/// the catalog's <c>/Perms /DocMDP</c> names it directly — so such a document was reported
/// <b>unsigned</b>, the blocker was skipped, and the signature was destroyed with no warning.
/// <c>PdfSignatureDetection</c> in Core — already used by <c>PdfOptimizer</c> and
/// <c>PdfEncryptedRoundTrip</c> — tests <c>/Perms</c> before the field walk runs at all.</para>
/// </remarks>
public class CertificationSignatureBlockerTests
{
    /// <summary>
    /// PDF/A must report the signature as a blocker rather than converting.
    /// </summary>
    [Fact]
    public void PdfAConversionIsBlockedByAPermsOnlyCertificationSignature()
    {
        var result = ConvertToPdfA(PermsOnlySignedPdf());

        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>PDF/X takes the same route and must give the same answer.</summary>
    [Fact]
    public void PdfXConversionIsBlockedByAPermsOnlyCertificationSignature()
    {
        var result = ConvertToPdfX(PermsOnlySignedPdf());

        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>The control.</b> The same document without <c>/Perms</c> converts — so the blocker above
    /// cannot pass by the converter having started to refuse everything, and the only difference
    /// between the two fixtures is the key under test.
    /// </summary>
    [Fact]
    public void TheSameDocumentWithoutPermsStillConverts()
    {
        var result = ConvertToPdfA(UnsignedPdf());

        Assert.DoesNotContain(result.Blockers,
            b => b.Contains("signature", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.OutputWritten,
            "the control must convert, or the blocker case proves nothing: "
            + string.Join(" | ", result.Blockers));
    }

    // ------------------------------------------------------------------

    private static PdfAConversionResult ConvertToPdfA(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        return PdfAConverter.ConvertToPdfA1b(input, output, null);
    }

    private static PdfXConversionResult ConvertToPdfX(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        return PdfXConverter.Convert(input, output, null);
    }

    /// <summary>
    /// A certification-signed document whose signature field is named <b>only</b> by
    /// <c>/Perms /DocMDP</c> — there is no <c>/AcroForm</c> at all, which is exactly the shape the
    /// field walk could not see.
    /// </summary>
    private static byte[] PermsOnlySignedPdf() => Build(catalogExtra: "/Perms << /DocMDP 6 0 R >>");

    /// <summary>The same document with no <c>/Perms</c>.</summary>
    private static byte[] UnsignedPdf() => Build(catalogExtra: "");

    private static byte[] Build(string catalogExtra)
    {
        const string content = "q Q\n";

        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
          + "/Resources << >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Unused true >>\nendobj\n");
        // The signature dictionary /Perms /DocMDP points at. Never reachable from /AcroForm.
        Obj("6 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
          + "/ByteRange [0 0 0 0] /Contents <00> >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
