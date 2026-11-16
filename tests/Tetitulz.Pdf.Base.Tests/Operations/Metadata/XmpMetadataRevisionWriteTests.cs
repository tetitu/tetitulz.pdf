// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Metadata;

/// <summary>
/// <see cref="XmpMetadataRevision.Write"/>, the pass-through write the declarations writer and the
/// PDF/UA identifier share: the plan's packet appended as the document's <c>/Metadata</c>, a plan
/// with nothing to write answered with the input's own bytes, and every refusal writing the input
/// unchanged with its reason.
/// </summary>
public class XmpMetadataRevisionWriteTests
{
    private const string Packet =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">MARKER</x:xmpmeta><?xpacket end=\"w\"?>";

    private static byte[] Document() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
        .Finish();

    private static string MetadataOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var stream = (PdfStream)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata]);
        return Encoding.UTF8.GetString(stream.GetDecodedData());
    }

    [Fact]
    public void ThePlansPacket_IsAppendedAsTheMetadata_AndThePlanSeesTheExistingOne()
    {
        byte[] original = Document();
        var seen = new List<string>();
        var failures = new List<string>();

        using var first = new MemoryStream();
        bool written = XmpMetadataRevision.Write(new MemoryStream(original), first,
            (doc, existing) => { seen.Add(existing); return new XmpMetadataRevision.PacketPlan(Packet); },
            (message, _) => failures.Add(message), null, out bool appended);

        Assert.True(written);
        Assert.True(appended);
        Assert.Empty(failures);
        Assert.True(first.Length > original.Length);
        Assert.Equal(Packet, MetadataOf(first.ToArray()));

        using var second = new MemoryStream();
        XmpMetadataRevision.Write(new MemoryStream(first.ToArray()), second,
            (doc, existing) => { seen.Add(existing); return new XmpMetadataRevision.PacketPlan(null); },
            (message, _) => failures.Add(message), null, out bool appendedAgain);

        Assert.Null(seen[0]);
        Assert.Equal(Packet, seen[1]);
        Assert.False(appendedAgain);
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void APlanThatThrows_IsRefused_AndTheInputPassesThrough()
    {
        byte[] original = Document();
        string reason = null;
        Exception cause = null;

        using var output = new MemoryStream();
        bool written = XmpMetadataRevision.Write(new MemoryStream(original), output,
            (doc, existing) => throw new InvalidDataException("no packet"),
            (message, ex) => { reason = message; cause = ex; }, null, out bool appended);

        Assert.False(written);
        Assert.False(appended);
        Assert.Equal("The XMP packet could not be built.", reason);
        Assert.Equal("no packet", cause.Message);
        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void Hostile_InputThatIsNoPdf_IsRefusedAsUnparsable_AndPassesThrough()
    {
        byte[] original = Encoding.ASCII.GetBytes("not a PDF at all");
        string reason = null;
        bool planned = false;

        using var output = new MemoryStream();
        bool written = XmpMetadataRevision.Write(new MemoryStream(original), output,
            (doc, existing) => { planned = true; return new XmpMetadataRevision.PacketPlan(Packet); },
            (message, _) => reason = message, null, out _);

        Assert.False(written);
        Assert.False(planned);
        Assert.Equal("The document could not be parsed.", reason);
        Assert.Equal(original, output.ToArray());
    }

    /// <summary>
    /// A <c>/Metadata</c> entry that is no stream is read past and said through the report — the
    /// packet is then written as new — while a refusal is not made of it.
    /// </summary>
    [Fact]
    public void AMetadataEntryThatIsNoStream_IsReported_AndANewPacketWritten()
    {
        byte[] original = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
            .Obj(4, "<< /NotAStream true >>")
            .Finish();
        var reports = new List<string>();
        string seen = "unset";

        using var output = new MemoryStream();
        bool written = XmpMetadataRevision.Write(new MemoryStream(original), output,
            (doc, existing) => { seen = existing; return new XmpMetadataRevision.PacketPlan(Packet); },
            (message, _) => throw new Xunit.Sdk.XunitException("refused: " + message),
            (message, _) => reports.Add(message), out bool appended);

        Assert.True(written);
        Assert.True(appended);
        Assert.Null(seen);
        Assert.Contains(reports, r => r.Contains("does not point to a stream", StringComparison.Ordinal));
    }

    /// <summary>
    /// A document whose <c>/Root</c> is no dictionary does not open — <c>PdfDocument</c> refuses it —
    /// so it is refused as unparsable, the plan never asked and the input passed through.
    /// </summary>
    [Fact]
    public void Hostile_ADocumentWithNoCatalogDictionary_IsRefusedAsUnparsable()
    {
        byte[] original = new PdfFixtureBuilder().Header()
            .Obj(1, "[1 2 3]")
            .Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Finish();
        string reason = null;
        bool planned = false;

        using var output = new MemoryStream();
        bool written = XmpMetadataRevision.Write(new MemoryStream(original), output,
            (doc, existing) => { planned = true; return new XmpMetadataRevision.PacketPlan(Packet); },
            (message, _) => reason = message, null, out _);

        Assert.False(written);
        Assert.False(planned);
        Assert.Equal("The document could not be parsed.", reason);
        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void AnEmptyInput_IsRefused_AndNothingIsWritten()
    {
        string reason = null;

        using var output = new MemoryStream();
        bool written = XmpMetadataRevision.Write(new MemoryStream(), output,
            (doc, existing) => new XmpMetadataRevision.PacketPlan(Packet),
            (message, _) => reason = message, null, out _);

        Assert.False(written);
        Assert.Equal("The input stream is empty; there is no PDF to update.", reason);
        Assert.Equal(0, output.Length);
    }
}
