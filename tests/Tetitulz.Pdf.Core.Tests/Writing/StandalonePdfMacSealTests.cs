// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="StandalonePdfMacSeal"/> — ISO/TS 32004 §6.5.1's file-level placement — and its use by
/// <see cref="IncrementalUpdateWriter"/> for an appended revision, in both cross-reference forms. The
/// token here is a stand-in (the first bytes of the digest it is handed), so what is asserted is the
/// placement: <c>/ByteRange</c> covers the whole file except the <c>/MAC</c> value, and the token was
/// built over the SHA-256 of exactly those bytes.
/// </summary>
public class StandalonePdfMacSealTests
{
    /// <summary>A token of <see cref="TokenLength"/> bytes: the head of the digest it was given.</summary>
    private sealed class DigestHeadSeal : StandalonePdfMacSeal
    {
        private readonly int _length;
        internal DigestHeadSeal(int length = 12) => _length = length;
        public override int TokenLength => _length;
        public override byte[] BuildToken(byte[] dataDigest) => dataDigest.Take(_length).ToArray();
    }

    private sealed class WrongLengthSeal : StandalonePdfMacSeal
    {
        public override int TokenLength => 12;
        public override byte[] BuildToken(byte[] dataDigest) => new byte[11];
    }

    private static byte[] Append(XrefSectionFormat format, StandalonePdfMacSeal seal, bool withObject = true)
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("mac-seal");
        using var output = new MemoryStream();
        var objects = withObject
            ? new[] { new IndirectObject(100, 0, new PdfString("appended")) }
            : Array.Empty<IndirectObject>();
        IncrementalUpdateWriter.WriteUpdate(original, output, objects,
            new IncrementalUpdateOptions { Format = format, Size = 101, PdfMac = seal });
        return output.ToArray();
    }

    /// <summary>
    /// Reads the last <c>/AuthCode</c> in <paramref name="pdf"/> straight from the bytes — a parser's
    /// trailer merge is not what a validator of the newest revision reads — and checks its coverage and
    /// token against an independent SHA-256.
    /// </summary>
    private static void AssertSealedOverTheWholeFile(byte[] pdf, int tokenLength)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int authCode = text.LastIndexOf("/AuthCode", StringComparison.Ordinal);
        Assert.True(authCode >= 0, "no /AuthCode was written");

        int open = text.IndexOf('[', text.IndexOf("/ByteRange", authCode, StringComparison.Ordinal));
        int close = text.IndexOf(']', open);
        long[] range = text.Substring(open + 1, close - open - 1)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();

        Assert.Equal(4, range.Length);
        Assert.Equal(0, range[0]);
        Assert.Equal((byte)'<', pdf[range[1]]);
        Assert.Equal((byte)'>', pdf[range[2] - 1]);
        Assert.Equal(pdf.Length, range[2] + range[3]);
        Assert.Equal(1 + 2 * tokenLength + 1, range[2] - range[1]);

        byte[] covered = pdf.Take((int)range[1]).Concat(pdf.Skip((int)range[2])).ToArray();
        byte[] expectedToken = SHA256.HashData(covered).Take(tokenLength).ToArray();
        string mac = text.Substring((int)range[1] + 1, 2 * tokenLength);
        Assert.Equal(Convert.ToHexString(expectedToken), mac);
    }

    [Theory]
    [InlineData(XrefSectionFormat.ClassicTable)]
    [InlineData(XrefSectionFormat.CrossReferenceStream)]
    public void AppendedRevision_IsSealedOverTheWholeFile(XrefSectionFormat format)
    {
        byte[] pdf = Append(format, new DigestHeadSeal());

        AssertSealedOverTheWholeFile(pdf, 12);

        // Still a readable document, whose newest trailer carries the dictionary.
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal("appended", (doc.ResolveObject(new PdfReference(100, 0)) as PdfString)?.GetText());
        var authCode = doc.Trailer[PdfName.AuthCode] as PdfDictionary;
        Assert.Equal(PdfName.Standalone, authCode?[PdfName.MACLocation]);
    }

    /// <summary>
    /// <b>A caller-supplied <c>AuthCode</c> is written verbatim, in both cross-reference forms.</b>
    /// </summary>
    /// <remarks>
    /// <para>ISO/TS 32004 §6.5.2's <c>AttachedToSig</c> dictionary is finished before the write
    /// begins — Table 6 forbids <c>/ByteRange</c> and <c>/MAC</c> there, leaving two entries the
    /// caller already knows — so unlike <see cref="IncrementalUpdateOptions.PdfMac"/> it needs no
    /// placeholder and no second pass over the output.</para>
    ///
    /// <para><b>Both forms are asserted for the reason the standalone pair above is.</b> The
    /// classic trailer and the cross-reference stream dictionary are written by different methods,
    /// so an entry added to one is simply absent from the other — and a document written with an
    /// xref stream would then carry a token nothing points at. A mutation deleting the stream-side
    /// write survived every test in the tree until this one existed.</para>
    /// </remarks>
    [Theory]
    [InlineData(XrefSectionFormat.ClassicTable)]
    [InlineData(XrefSectionFormat.CrossReferenceStream)]
    public void ACallerSuppliedAuthCode_IsWrittenIntoTheAppendedRevision(XrefSectionFormat format)
    {
        var supplied = new PdfDictionary();
        supplied.Set(PdfName.MACLocation, PdfName.AttachedToSig);
        supplied.Set(PdfName.SigObjRef, new PdfReference(100, 0));

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(
            MinimalPdfBuilder.CreateMinimalPdf("mac-authcode"), output,
            new[] { new IndirectObject(100, 0, new PdfString("appended")) },
            new IncrementalUpdateOptions { Format = format, Size = 101, AuthCode = supplied });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var authCode = Assert.IsType<PdfDictionary>(doc.Trailer![PdfName.AuthCode]);

        Assert.Equal(PdfName.AttachedToSig, authCode[PdfName.MACLocation]);
        Assert.Equal(100, Assert.IsType<PdfReference>(authCode[PdfName.SigObjRef]).ReferenceObjectNumber);

        // Verbatim means verbatim: nothing of the standalone placement leaked in.
        Assert.Null(authCode[PdfName.ByteRange]);
        Assert.Null(authCode[PdfName.MAC]);
    }

    /// <summary>
    /// <b>The two placements are refused together rather than resolved by precedence.</b>
    /// </summary>
    /// <remarks>
    /// A revision carries one <c>AuthCode</c> dictionary and Table 6 makes the two forms mutually
    /// exclusive — <c>/ByteRange</c> and <c>/MAC</c> are required by one and forbidden by the other.
    /// Whichever a precedence rule dropped, the document would come out claiming a protection it
    /// does not have, which is the one outcome worse than an exception.
    /// </remarks>
    [Fact]
    public void SettingBothPlacements_IsRefusedBeforeAnythingIsWritten()
    {
        var authCode = new PdfDictionary();
        authCode.Set(PdfName.MACLocation, PdfName.AttachedToSig);

        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentException>(() => IncrementalUpdateWriter.WriteUpdate(
            MinimalPdfBuilder.CreateMinimalPdf("both"), output,
            new[] { new IndirectObject(100, 0, new PdfString("x")) },
            new IncrementalUpdateOptions
            {
                Size = 101,
                PdfMac = new DigestHeadSeal(),
                AuthCode = authCode,
            }));

        Assert.Contains("not both", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void WithoutASeal_TheAppendedTrailerCarriesNoAuthCode()
    {
        byte[] pdf = Append(XrefSectionFormat.ClassicTable, seal: null);
        Assert.DoesNotContain("/AuthCode", Encoding.ASCII.GetString(pdf));
    }

    [Fact]
    public void NothingAppended_WritesNoSeal()
    {
        byte[] pdf = Append(XrefSectionFormat.ClassicTable, new DigestHeadSeal(), withObject: false);
        Assert.Equal(MinimalPdfBuilder.CreateMinimalPdf("mac-seal"), pdf);
    }

    /// <summary>A stream that cannot be read back: the seal has to reread the file it covers.</summary>
    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }

    [Fact]
    public void AnOutputThatCannotBeReadBack_IsRefusedBeforeAnythingIsWritten()
    {
        using var output = new WriteOnlyStream();
        var ex = Assert.Throws<ArgumentException>(() => IncrementalUpdateWriter.WriteUpdate(
            MinimalPdfBuilder.CreateMinimalPdf("refused"), output,
            new[] { new IndirectObject(100, 0, new PdfString("x")) },
            new IncrementalUpdateOptions { Size = 101, PdfMac = new DigestHeadSeal() }));

        Assert.Contains("readable", ex.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Seal_WithNoPlaceholderAfterTheOffset_IsRefused()
    {
        using var file = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("no-placeholder"));
        file.Position = file.Length;
        Assert.Throws<PdfException>(() => new DigestHeadSeal().Seal(file, 0));
    }

    [Fact]
    public void Seal_WhoseTokenIsNotTheAllocatedLength_IsRefused()
    {
        var seal = new WrongLengthSeal();
        using var file = new MemoryStream();
        var writer = new PdfWriter(file);
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.AuthCode, seal.CreatePlaceholder());
        writer.WriteTrailer(trailer, 0);

        Assert.Throws<PdfException>(() => seal.Seal(file, 0));
    }
}
