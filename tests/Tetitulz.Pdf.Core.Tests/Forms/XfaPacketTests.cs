// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <c>/AcroForm /XFA</c> resolves in both of §12.7.8's shapes, and presence is a separate question
/// from readability.
/// </summary>
/// <remarks>
/// <para><b>The array form is the one that was never resolved.</b> Before this type, every site in
/// the tree that touched <c>/XFA</c> asked only whether the key existed — enough for a stripper, and
/// not enough for anything that has to read a packet. A common viewer writes the array, so it is the common
/// case: a reader that only understood the single-stream form would report the ordinary document as
/// having no packets.</para>
/// </remarks>
public class XfaPacketTests
{
    [Fact]
    public void TheArrayFormResolvesEveryNamedPacketInFileOrder()
    {
        using var doc = Open(ArrayFormPdf());
        var packet = XfaPacket.Read(AcroForm(doc), doc);

        Assert.NotNull(packet);
        Assert.True(packet!.IsPackaged);
        Assert.Equal(new[] { "template", "datasets" }, packet.PacketNames);
        Assert.Equal("<t/>", Ascii(packet.GetPacket("template")));
        Assert.Equal("<d/>", Ascii(packet.GetPacket(XfaPacket.DatasetsPacket)));
    }

    /// <summary>A single stream is the whole XDP, so there are no names to ask for.</summary>
    /// <remarks>
    /// <c>GetPacket("datasets")</c> answering null here is the honest answer, not a miss: the bytes
    /// are in the stream somewhere, and splitting an XDP into its packets is an XML problem this
    /// type does not take on. A caller that needs the distinction reads <see cref="XfaPacket.IsPackaged"/>.
    /// </remarks>
    [Fact]
    public void TheSingleStreamFormResolvesToOneUnnamedPacket()
    {
        using var doc = Open(SingleStreamPdf());
        var packet = XfaPacket.Read(AcroForm(doc), doc);

        Assert.NotNull(packet);
        Assert.False(packet!.IsPackaged);
        Assert.Empty(packet.PacketNames);
        Assert.Single(packet.Streams);
        Assert.Null(packet.GetPacket(XfaPacket.DatasetsPacket));
    }

    /// <summary>
    /// The stream handed back is the document's own, so its object number can be superseded.
    /// </summary>
    /// <remarks>
    /// This is what makes an incremental update possible: a copy would have object number 0 and the
    /// caller would have no way to replace the packet without rewriting the whole file.
    /// </remarks>
    [Fact]
    public void ThePacketStreamCarriesTheDocumentsObjectNumber()
    {
        using var doc = Open(ArrayFormPdf());
        var packet = XfaPacket.Read(AcroForm(doc), doc);

        PdfStream datasets = packet!.GetPacketStream(XfaPacket.DatasetsPacket);

        Assert.NotNull(datasets);
        Assert.Equal(7, datasets!.ObjectNumber);
    }

    /// <summary>
    /// A malformed array is read as far as it parses: the good pairs survive.
    /// </summary>
    /// <remarks>
    /// Refusing the whole entry over one bad pair turns a producer's sloppiness into "this document
    /// has no XFA", and the one thing every caller does with that answer is stop looking. The
    /// fixture carries a name with no stream, a stream with no name, and a trailing odd element.
    /// </remarks>
    [Fact]
    public void AMalformedArrayKeepsThePairsThatDidParse()
    {
        using var doc = Open(MalformedArrayPdf());
        var packet = XfaPacket.Read(AcroForm(doc), doc);

        Assert.NotNull(packet);
        Assert.Equal(new[] { "datasets" }, packet!.PacketNames);
        Assert.Equal("<d/>", Ascii(packet.GetPacket(XfaPacket.DatasetsPacket)));
    }

    /// <summary>
    /// Presence does not depend on anything resolving — a packet that cannot be read is still one
    /// that is there.
    /// </summary>
    /// <remarks>
    /// The two answers are deliberately different for this document: <c>IsPresent</c> is true and
    /// <c>Read</c> is null. A caller reporting "this document is an XFA form" must use the first,
    /// because answering false would send it on as though there were nothing to keep in step.
    /// </remarks>
    [Fact]
    public void AnUnreadableEntryIsStillPresent()
    {
        using var doc = Open(EmptyArrayPdf());
        PdfDictionary form = AcroForm(doc);

        Assert.True(XfaPacket.IsPresent(form));
        Assert.Null(XfaPacket.Read(form, doc));
    }

    /// <summary>The acceptance control: a form with no <c>/XFA</c> answers false and null.</summary>
    [Fact]
    public void AFormWithNoXfaIsNeitherPresentNorReadable()
    {
        using var doc = Open(NoXfaPdf());
        PdfDictionary form = AcroForm(doc);

        Assert.False(XfaPacket.IsPresent(form));
        Assert.Null(XfaPacket.Read(form, doc));
    }

    /// <summary>Null arguments answer rather than throw, because both are reachable states.</summary>
    /// <remarks>
    /// A document with no <c>/AcroForm</c> at all yields a null dictionary at every call site here,
    /// and making that an exception would push a null check into each of them.
    /// </remarks>
    [Fact]
    public void ANullAcroFormIsAnAnswerNotAnError()
    {
        Assert.False(XfaPacket.IsPresent(null));
        Assert.Null(XfaPacket.Read(null, null));
    }

    // ------------------------------------------------------------------

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static PdfDictionary AcroForm(PdfDocument doc)
        => doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.AcroForm]) as PdfDictionary;

    private static string Ascii(byte[] bytes) => bytes == null ? null : Encoding.ASCII.GetString(bytes);

    private static string Contents(string text) =>
        $"<< /Length {text.Length} >>\nstream\n{text}\nendstream";

    private const string Prefix =
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>";

    private static byte[] ArrayFormPdf() => Assemble(new List<string>
    {
        Prefix,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
        "<< /Fields [] /XFA [(template) 6 0 R (datasets) 7 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Contents("<t/>"),
        Contents("<d/>"),
    });

    private static byte[] SingleStreamPdf() => Assemble(new List<string>
    {
        Prefix,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
        "<< /Fields [] /XFA 5 0 R >>",
        Contents("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"/>"),
    });

    // A name whose partner is a dictionary rather than a stream, a stream with no preceding name,
    // one good pair, and a trailing element with nothing after it.
    private static byte[] MalformedArrayPdf() => Assemble(new List<string>
    {
        Prefix,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
        "<< /Fields [] /XFA [(config) 5 0 R (datasets) 6 0 R (orphan)] >>",
        "<< /NotAStream true >>",
        Contents("<d/>"),
    });

    private static byte[] EmptyArrayPdf() => Assemble(new List<string>
    {
        Prefix,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
        "<< /Fields [] /XFA [] >>",
    });

    private static byte[] NoXfaPdf() => Assemble(new List<string>
    {
        Prefix,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
        "<< /Fields [] >>",
    });

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
