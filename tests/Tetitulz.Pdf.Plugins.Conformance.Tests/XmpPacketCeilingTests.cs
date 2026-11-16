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
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A <c>/Metadata</c> packet is decoded no further than the XMP packet ceiling (2 MB) by every
/// checker that reads it. Each decoded it with <c>GetDecodedData</c>, bounded only by the 512 MB
/// per-stream ceiling sized for images, and loaded the result into an <c>XmlDocument</c>: a few
/// kilobytes of flate were half a gigabyte of bytes and several of DOM.
/// </summary>
/// <remarks>
/// The packet is well-formed and declares what each checker looks for; only its padding takes it
/// past the ceiling. A checker that still decoded it whole would read it and report nothing about
/// it, so the refusal shows in the answer — and the control, the same packet padded under the
/// ceiling, is read.
/// </remarks>
public class XmpPacketCeilingTests
{
    private const int PastTheCeiling = 3 * 1024 * 1024;
    private const int UnderTheCeiling = 1024 * 1024;

    private const string PdfAIdentification =
        "xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"1\" pdfaid:conformance=\"B\"";
    private const string PdfUa1Identification =
        "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" pdfuaid:part=\"1\"";
    private const string PdfUa2Identification =
        "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" pdfuaid:part=\"2\" pdfuaid:rev=\"2024\"";

    [Fact]
    public void PdfA_AdversarialPacketInflatingPastTheCeiling_IsNotDecoded()
    {
        var result = PdfAValidator.Validate(new MemoryStream(Pdf(PdfAIdentification, PastTheCeiling)));

        Assert.Contains(result.Violations, v => v.Code == "A07"
            && v.Message.Contains("could not be decoded", StringComparison.Ordinal));
    }

    [Fact]
    public void PdfA_ThePacketUnderTheCeiling_IsRead()
    {
        var result = PdfAValidator.Validate(new MemoryStream(Pdf(PdfAIdentification, UnderTheCeiling)));

        Assert.DoesNotContain(result.Violations, v => v.Code == "A07");
    }

    /// <summary>
    /// UA03's identifier read and UA04's <c>dc:title</c> read both refuse it: the document has no
    /// <c>/Info</c> title, so a title read from the packet is the only thing between it and a UA04.
    /// </summary>
    [Fact]
    public void PdfUa_AdversarialPacketInflatingPastTheCeiling_IsNotDecoded()
    {
        var result = PdfUaChecker.Validate(new MemoryStream(Pdf(PdfUa1Identification, PastTheCeiling)));

        Assert.Contains(result.Violations, v => v.Code == "UA03"
            && v.Message.Contains("could not be decoded", StringComparison.Ordinal));
        Assert.Contains(result.Violations, v => v.Code == "UA04");
    }

    [Fact]
    public void PdfUa_ThePacketUnderTheCeiling_IsRead()
    {
        var result = PdfUaChecker.Validate(new MemoryStream(Pdf(PdfUa1Identification, UnderTheCeiling)));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA03");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA04");
    }

    /// <summary>
    /// With no catalog <c>/Lang</c>, UA02 lists the packet's x-default text among what declares no
    /// language — when the packet is read. Past the ceiling it is not; under it, it is.
    /// </summary>
    [Fact]
    public void PdfUa_AdversarialPacketPastTheCeiling_IsNotReadForItsDefaultLanguageText()
    {
        var past = PdfUaChecker.Validate(new MemoryStream(Pdf(PdfUa1Identification, PastTheCeiling, lang: false)));
        var under = PdfUaChecker.Validate(new MemoryStream(Pdf(PdfUa1Identification, UnderTheCeiling, lang: false)));

        Assert.DoesNotContain(past.Violations, v => v.Code == "UA02"
            && v.Message.Contains("x-default", StringComparison.Ordinal));
        Assert.Contains(under.Violations, v => v.Code == "UA02"
            && v.Message.Contains("XMP metadata text in x-default", StringComparison.Ordinal));
    }

    /// <summary>
    /// PDF/A-2's A79 reads the packet's bytes for their encoding: a UTF-16 packet under the ceiling
    /// is reported as not UTF-8, and past it is not read at all — A07 says why.
    /// </summary>
    [Fact]
    public void PdfA2_AdversarialPacketPastTheCeiling_IsNotReadForItsEncoding()
    {
        var past = PdfAValidator.Validate(new MemoryStream(Pdf(PdfAIdentification, PastTheCeiling, utf16: true)),
                                          PdfAConformancePart.Part2);
        var under = PdfAValidator.Validate(new MemoryStream(Pdf(PdfAIdentification, UnderTheCeiling / 2, utf16: true)),
                                           PdfAConformancePart.Part2);   // two bytes a character

        Assert.DoesNotContain(past.Violations, v => v.Code == "A79");
        Assert.True(past.Violations.Any(v => v.Code == "A07"), string.Join(" | ", past.Violations.Select(v => v.Code + " " + v.Message)));
        Assert.True(under.Violations.Any(v => v.Code == "A79" && v.Message.Contains("UTF-16", StringComparison.Ordinal)),
                    string.Join(" | ", under.Violations.Select(v => v.Code + " " + v.Message)));
    }

    [Fact]
    public void PdfUa2_AdversarialPacketInflatingPastTheCeiling_IsNotDecoded()
    {
        var result = PdfUa2Checker.Validate(new MemoryStream(Pdf(PdfUa2Identification, PastTheCeiling)));

        Assert.Contains(result.Violations, v => v.Code == "UA2-5"
            && v.Message.Contains("could not be decoded", StringComparison.Ordinal));
    }

    [Fact]
    public void PdfUa2_ThePacketUnderTheCeiling_IsRead()
    {
        var result = PdfUa2Checker.Validate(new MemoryStream(Pdf(PdfUa2Identification, UnderTheCeiling)));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-5");
    }

    /// <summary>
    /// PDF/X skips its identifier check over a packet it cannot read and says so; the packet
    /// declares no <c>GTS_PDFXVersion</c>, so a packet that is read is an X03.
    /// </summary>
    [Fact]
    public void PdfX_AdversarialPacketInflatingPastTheCeiling_IsNotDecoded()
    {
        var result = PdfXValidator.Validate(new MemoryStream(Pdf(PdfAIdentification, PastTheCeiling)),
                                            PdfXConformanceLevel.X4);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X03");
        Assert.Contains("could not be decoded; the GTS_PDFXVersion check was skipped",
                        PdfXValidator.LastValidationError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void PdfX_ThePacketUnderTheCeiling_IsRead()
    {
        var result = PdfXValidator.Validate(new MemoryStream(Pdf(PdfAIdentification, UnderTheCeiling)),
                                            PdfXConformanceLevel.X4);

        Assert.Contains(result.Violations, v => v.Code == "X03"
            && v.Message.Contains("GTS_PDFXVersion", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A well-formed packet whose description carries <paramref name="identification"/>,
    /// padded with whitespace to <paramref name="paddedTo"/> characters, in UTF-8 or, for
    /// <paramref name="utf16"/>, UTF-16 with its byte-order mark.</summary>
    private static byte[] Packet(string identification, int paddedTo, bool utf16 = false)
    {
        string head =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
            " <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
            "  <rdf:Description rdf:about=\"\" " + identification + "/>\n" +
            "  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n" +
            "   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Title</rdf:li></rdf:Alt></dc:title>\n" +
            "  </rdf:Description>\n" +
            " </rdf:RDF>\n" +
            "</x:xmpmeta>\n";
        const string tail = "<?xpacket end=\"w\"?>";
        int padding = Math.Max(0, paddedTo - head.Length - tail.Length);
        string text = head + new string(' ', padding) + tail;
        if (!utf16) return Encoding.ASCII.GetBytes(text);
        byte[] bytes = Encoding.BigEndianUnicode.GetBytes(text);
        return new byte[] { 0xFE, 0xFF }.Concat(bytes).ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    /// <summary>A tagged one-page document whose catalog's <c>/Metadata</c> is the flated packet.</summary>
    private static byte[] Pdf(string identification, int paddedTo, bool lang = true, bool utf16 = false)
    {
        byte[] flated = Deflate(Packet(identification, paddedTo, utf16));
        const string content = "/P <</MCID 0>> BDC EMC";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R /StructTreeRoot 6 0 R " +
            "/MarkInfo << /Marked true >>" + (lang ? " /Lang (en)" : "") +
            " /ViewerPreferences << /DisplayDocTitle true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" +
            content + "\nendstream",
            "<< /Type /Metadata /Subtype /XML /Filter /FlateDecode /Length " +
            flated.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" +
            Encoding.Latin1.GetString(flated) + "\nendstream",
            "<< /Type /StructTreeRoot /K 7 0 R >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n")
              .Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
