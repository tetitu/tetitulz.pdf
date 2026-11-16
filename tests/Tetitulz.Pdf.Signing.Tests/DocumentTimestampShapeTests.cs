// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// What the document timestamper writes: ISO 32000-2 Table 255 makes a document timestamp's
/// <c>/Type</c> <c>DocTimeStamp</c>, never <c>Sig</c>; and its widget names the page at the page's own
/// generation — a page written as <c>3 1 obj</c> named <c>3 0 R</c> resolved to nothing.
/// </summary>
public sealed class DocumentTimestampShapeTests
{
    private static byte[] Timestamp(byte[] pdf)
    {
        var root = SigningTestCrypto.Ca("CN=DocTS Shape Root");
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfDocumentTimestamper.AddTimestamp(input, output, tsa.Url,
            new SigningNetworkOptions { Proxy = new WebProxy() });
        return output.ToArray();
    }

    /// <summary>A one-page document whose page object is at generation 1.</summary>
    private static byte[] PageAtGenerationOne()
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 1 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length; sb.Append("3 1 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>\nendobj\n");
        int x = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n")
          .Append(o1.ToString("D10")).Append(" 00000 n \n")
          .Append(o2.ToString("D10")).Append(" 00000 n \n")
          .Append(o3.ToString("D10")).Append(" 00001 n \n")
          .Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void ADocumentTimestamp_IsTypedDocTimeStamp_AndNamesItsPageAtThePagesGeneration()
    {
        byte[] original = PageAtGenerationOne();
        byte[] stamped = Timestamp(original);
        string appended = Encoding.Latin1.GetString(stamped)[original.Length..];

        Assert.Contains("/Type /DocTimeStamp", appended, System.StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /Sig", appended, System.StringComparison.Ordinal);
        Assert.Matches(new Regex(@"/P 3 1 R"), appended);

        // And it is still read as a document timestamp that verifies.
        using var doc = PdfDocument.Open(new MemoryStream(stamped));
        var result = new PdfSignatureVerifier().VerifyAll(doc).Single();
        Assert.True(result.IsIntegrityValid, string.Join(" | ", result.ValidationErrors));
    }
}
