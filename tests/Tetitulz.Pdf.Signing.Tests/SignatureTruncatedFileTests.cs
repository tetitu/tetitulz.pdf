// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A file that ends before its <c>/ByteRange</c> does is reported on the signature rather than
/// thrown out of the verifier.
/// </summary>
/// <remarks>
/// <para>The bounds check measures the range against the stream's <c>Length</c>, so the only way to
/// read short after passing it is a stream whose length says more than it holds — a file truncated
/// by another process after its length was taken, or a wrapper over a chunked source. The verifier's
/// private reader threw its own exception type there, and the catch around it caught
/// <c>IOException</c>, so the failure escaped the report it was written for.</para>
/// </remarks>
public sealed class SignatureTruncatedFileTests
{
    /// <summary>A stream that reports the whole file's length but holds all but its last bytes.</summary>
    private sealed class TruncatedStream : MemoryStream
    {
        private readonly long _declaredLength;

        public TruncatedStream(byte[] data, int missing)
            : base(data, 0, data.Length - missing, writable: false)
            => _declaredLength = data.Length;

        public override long Length => _declaredLength;
    }

    [Fact]
    public void ASignatureWhoseBytesAreNoLongerAllThere_IsReportedAsUnreadable()
    {
        var root = SigningTestCrypto.Ca("CN=Truncation Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Truncation TSA");
        byte[] pdf = SignedPdfFixtures.AppendDocumentTimestamp(
            SignedPdfFixtures.MinimalPdf(), tsa, DateTime.UtcNow, new[] { root.Cert },
            sigDictObjNum: 6, sigFieldObjNum: 7);

        using var doc = PdfDocument.Open(new TruncatedStream(pdf, missing: 1));
        var result = new PdfSignatureVerifier().VerifyAll(doc).Single();

        Assert.False(result.IsValid);
        Assert.Contains(result.ValidationErrors,
            e => e.Contains("the file ended before the signed bytes did", StringComparison.Ordinal));
    }
}
