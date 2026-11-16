// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// A packet is XML the readers build a tree of, at many times its bytes, and a kilobyte of double
/// deflate reaches the general decode ceiling: the packet's bytes and its nodes are each bounded
/// before any tree exists.
/// </summary>
public class XfaPacketBoundsTests
{
    private static PdfStream Flate(long bytes)
    {
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            var buffer = new byte[65536];
            Array.Fill(buffer, (byte)' ');
            for (long left = bytes; left > 0; left -= buffer.Length) z.Write(buffer, 0, (int)Math.Min(left, buffer.Length));
        }

        var stream = new PdfStream { RawData = deflated.ToArray() };
        stream[PdfName.Filter] = new PdfName("FlateDecode");
        return stream;
    }

    [Fact]
    public void AdversarialPacketPastTheCeiling_IsRefusedWithTheDecodersReason()
    {
        byte[] over = XfaPacket.BoundedBytes(Flate(XfaPacket.MaxPacketBytes + 1), out string refusal);

        Assert.Null(over);
        Assert.Contains("limit", refusal, StringComparison.Ordinal);
    }

    /// <summary>The control, at the ceiling exactly.</summary>
    [Fact]
    public void APacketAtTheCeiling_Decodes()
    {
        byte[] at = XfaPacket.BoundedBytes(Flate(XfaPacket.MaxPacketBytes), out string refusal);

        Assert.Null(refusal);
        Assert.Equal(XfaPacket.MaxPacketBytes, at.LongLength);
    }

    /// <summary>The public door asks the same question: a packet past the ceiling is no packet.</summary>
    [Fact]
    public void AdversarialGetPacket_AnswersNullPastTheCeiling()
    {
        using var doc = PdfDocument.Open(new MemoryStream(TwoPacketPdf()));
        var acroForm = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var packet = XfaPacket.Read(acroForm, doc);

        Assert.Null(packet.GetPacket("template"));
        Assert.Equal("<d/>", Encoding.ASCII.GetString(packet.GetPacket("datasets")));
    }

    private static XmlReaderSettings Settings() => new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    private static byte[] Elements(int count, string element)
    {
        var text = new StringBuilder("<r>", count * element.Length + 8);
        for (int i = 0; i < count; i++) text.Append(element);
        return Encoding.ASCII.GetBytes(text.Append("</r>").ToString());
    }

    /// <summary>
    /// <b>Four bytes a node.</b> The same count of elements fits bare and does not with two attributes
    /// each, so the attributes are counted, not just the elements.
    /// </summary>
    [Fact]
    public void AdversarialNodeFlood_IsOutOfBounds_CountingAttributes()
    {
        int elements = XfaDatasets.MaxXmlNodes / 3 + 1;

        Assert.True(XfaDatasets.IsWithinBounds(Elements(elements, "<a/>"), Settings()));
        Assert.False(XfaDatasets.IsWithinBounds(Elements(elements, "<a b=\"\" c=\"\"/>"), Settings()));
        Assert.False(XfaDatasets.IsWithinBounds(Elements(XfaDatasets.MaxXmlNodes + 1, "<a/>"), Settings()));
    }

    /// <summary>Bytes past the ceiling are refused before any is read: these are not XML, and a read would throw.</summary>
    [Fact]
    public void AdversarialBytesPastTheCeiling_AreOutOfBoundsUnread()
    {
        Assert.False(XfaDatasets.IsWithinBounds(new byte[XfaPacket.MaxPacketBytes + 1], Settings()));
    }

    [Fact]
    public void AdversarialNodeFlood_ReadsAsNoValues()
    {
        string data = "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><f><v>1</v>{0}</f></xfa:data></xfa:datasets>";
        byte[] flood = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, data,
            string.Concat(Enumerable.Repeat("<a/>", XfaDatasets.MaxXmlNodes + 1))));
        byte[] control = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, data, "<a/>"));

        Assert.Null(XfaDatasets.ReadValues(flood));
        IReadOnlyDictionary<string, string> values = XfaDatasets.ReadValues(control);
        Assert.Equal("1", values["f.v"]);
    }

    [Fact]
    public void AdversarialDepth_IsOutOfBounds_AndTheControlIsNot()
    {
        byte[] Nested(int depth) => Encoding.ASCII.GetBytes(
            string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth)));

        Assert.True(XfaDatasets.IsWithinBounds(Nested(XfaDatasets.MaxXmlDepth), Settings()));
        Assert.False(XfaDatasets.IsWithinBounds(Nested(XfaDatasets.MaxXmlDepth + 2), Settings()));
    }

    private static byte[] TwoPacketPdf()
    {
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            var buffer = new byte[65536];
            for (long left = XfaPacket.MaxPacketBytes + 1; left > 0; left -= buffer.Length)
                z.Write(buffer, 0, (int)Math.Min(left, buffer.Length));
        }
        string hex = Convert.ToHexString(deflated.ToArray()) + ">";

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA [(template) 4 0 R (datasets) 5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            $"<< /Filter [/ASCIIHexDecode /FlateDecode] /Length {hex.Length} >>\nstream\n{hex}\nendstream",
            "<< /Length 4 >>\nstream\n<d/>\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
