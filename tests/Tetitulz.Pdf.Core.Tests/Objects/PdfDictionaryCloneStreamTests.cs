// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// <see cref="PdfDictionary.CloneShallow"/> and <see cref="PdfDictionary.DeepClone"/> applied to a
/// <see cref="PdfStream"/>.
///
/// <para><c>PdfStream</c> derives from <c>PdfDictionary</c>, so passing one compiles, and unguarded
/// would return a plain dictionary carrying the stream's keys — a lying <c>/Length</c> included —
/// and no data. <c>CloneValue</c> guards the case where a stream is a nested <i>value</i>; the entry
/// points guard it too, which matters because ~20 <c>CloneDictionary</c>
/// shims across Base, Signing, Accessibility and the Conformance plugin forward to them and are
/// routinely handed a resolved object that turns out to be an appearance stream or a Form
/// XObject.</para>
/// </summary>
public class PdfDictionaryCloneStreamTests
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("q 1 0 0 1 0 0 cm Q\n");

    private static PdfStream SampleStream()
    {
        var stream = new PdfStream();
        stream.Set(PdfName.Type, PdfName.XObject);
        stream.Set(PdfName.Subtype, PdfName.Form);
        stream.Set(PdfName.Length, new PdfNumber(Payload.Length));
        stream.RawData = Payload;
        stream.StreamOffset = 4242;
        return stream;
    }

    [Fact]
    public void CloneShallow_OfAStream_StaysAStreamAndKeepsItsData()
    {
        var clone = PdfDictionary.CloneShallow(SampleStream());

        var asStream = Assert.IsType<PdfStream>(clone);
        Assert.Equal(Payload, asStream.RawData);
        Assert.Equal(4242, asStream.StreamOffset);
        Assert.Equal(PdfName.Form, asStream.GetName(PdfName.Subtype));
    }

    [Fact]
    public void DeepClone_OfAStream_StaysAStreamAndKeepsItsData()
    {
        var clone = PdfDictionary.DeepClone(SampleStream());

        var asStream = Assert.IsType<PdfStream>(clone);
        Assert.Equal(Payload, asStream.RawData);
        Assert.Equal(PdfName.XObject, asStream.GetName(PdfName.Type));
    }

    /// <summary>
    /// The failure the downgrade actually caused, end to end: the clone is written as an indirect
    /// object and the bytes come back. <see cref="PdfWriter"/> emits <c>stream</c>/<c>endstream</c>
    /// only for a <see cref="PdfStream"/>, so a downgraded clone wrote a dictionary whose
    /// <c>/Length</c> claimed bytes that were not there — with no exception and no diagnostic.
    /// </summary>
    [Fact]
    public void ClonedStream_WrittenAsAnIndirectObject_RoundTripsItsBytes()
    {
        var clone = PdfDictionary.CloneShallow(SampleStream());

        using var ms = new MemoryStream();
        new PdfWriter(ms).WriteIndirectObject(11, 0, clone);

        var parser = new PdfParser(new MemoryStream(ms.ToArray()));
        var parsed = Assert.IsType<PdfStream>(parser.ParseIndirectObject());
        Assert.Equal(Payload, parsed.RawData);
    }

    /// <summary>
    /// The container is new even for a stream: setting a key on the clone must not reach the
    /// original, which is the entire point of cloning.
    /// </summary>
    [Fact]
    public void CloneShallow_OfAStream_DoesNotShareItsEntryTable()
    {
        var original = SampleStream();
        var clone = PdfDictionary.CloneShallow(original);
        clone.Set(PdfName.Subtype, PdfName.Image);

        Assert.Equal(PdfName.Form, original.GetName(PdfName.Subtype));
        Assert.Equal(PdfName.Image, clone.GetName(PdfName.Subtype));
    }

    /// <summary>An ordinary dictionary is unaffected — the stream branch is a branch, not a swap.</summary>
    [Fact]
    public void CloneShallow_OfAPlainDictionary_IsStillAPlainDictionary()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Page);

        var clone = PdfDictionary.CloneShallow(dict);

        Assert.IsType<PdfDictionary>(clone);
        Assert.False(clone is PdfStream);
        Assert.Equal(PdfName.Page, clone.GetName(PdfName.Type));
    }
}
