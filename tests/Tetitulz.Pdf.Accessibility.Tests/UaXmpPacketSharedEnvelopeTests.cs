// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Tetitulz.Pdf.Accessibility.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfUaXmpWriter"/> takes the <c>xpacket</c> wrapper — both processing instructions,
/// the fixed packet id and the byte-order mark — from <see cref="XmpPacket"/> in Core, as its
/// sibling packet builder in <c>Plugins.Conformance</c> does, rather than restating it. What these
/// tests exist for is what makes sharing the constants <i>safe</i>.
///
/// <para><b>Two invariants ride on the emitted bytes, and neither is visible from the source
/// diff.</b> The packet is the document's PDF/UA conformance assertion, so a checker has to find
/// <c>pdfuaid:part</c> in it after the round trip; and <c>SetPdfUaIdentifier</c> is documented as
/// idempotent, so a second run over its own output must append nothing and produce the same bytes,
/// with <see cref="PdfUaXmpWriter.LastCallAppendedRevision"/> telling the two cases apart. A change
/// to the envelope that moved a single byte would leave both claims untested by everything else in
/// this project — the existing byte-identity test compares two runs over the <i>same</i> input,
/// which agrees with itself whatever the envelope says.</para>
///
/// <para><b>The padding is deliberately not shared.</b> <see cref="XmpPacket.Wrap"/> pads on an
/// 80-column grid and adds a newline of its own; this writer pads on a 64-column one to exactly
/// 2048 characters. Nothing in <c>src/</c> calls <c>Wrap</c>, and the Conformance sibling pads not
/// at all — padding is a per-writer choice, so routing through <c>Wrap</c> would have changed every
/// remediated document's metadata for no gain. <see cref="EmittedPacket_PadsWithItsOwn2048CharacterBlock_NotTheSharedWrapGrid"/>
/// pins that decision so a later "finish the consolidation" edit has to be deliberate.</para>
///
/// <para>Every document is built in memory (CLAUDE.md: no test reads a PDF from disk).</para>
/// </summary>
public class UaXmpPacketSharedEnvelopeTests
{
    /// <summary>The padding block <c>PdfUaXmpWriter.SerializePacket</c> writes, in characters.</summary>
    private const int PaddingLength = 2048;

    /// <summary>The column at which that block breaks its lines.</summary>
    private const int PaddingLineLength = 64;

    // ===============================================================
    //  Fixture — a classic-xref PDF with no /Metadata at all
    // ===============================================================

    private static byte[] ClassicPdfWithoutMetadata()
        => BuildClassicPdf(new[]
        {
            null,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        });

    /// <summary>
    /// Assembles a classic-xref PDF from bodies indexed 1..N (index 0 is unused). Everything is
    /// ASCII, so <c>StringBuilder.Length</c> is a byte offset.
    /// </summary>
    private static byte[] BuildClassicPdf(IReadOnlyList<string> bodies)
    {
        int count = bodies.Count - 1;
        var sb = new StringBuilder(2048);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int n = 1; n <= count; n++)
        {
            offsets[n] = sb.Length;
            sb.Append(n.ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[n]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        for (int n = 1; n <= count; n++)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[n]);

        sb.Append("trailer\n<< /Size ").Append((count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ===============================================================
    //  The envelope really is the shared one
    // ===============================================================

    /// <summary>
    /// The emitted packet opens with <see cref="XmpPacket.Header"/> and closes with
    /// <see cref="XmpPacket.Trailer"/> — the constants, not a private restatement of them. The
    /// byte-order mark is checked as a character at its own offset rather than through the
    /// constant, because the defect the shared header exists to prevent was a builder that wrote
    /// the twelve literal characters <c>\xEF\xBB\xBF</c> where a BOM belongs: an assertion phrased
    /// only in terms of <c>XmpPacket.Header</c> would have accepted that.
    /// </summary>
    [Fact]
    public void EmittedPacket_OpensAndClosesWithTheSharedCoreXpacketInstructions()
    {
        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(null, part: 1);

        Assert.StartsWith(XmpPacket.Header, packet, StringComparison.Ordinal);
        Assert.EndsWith(XmpPacket.Trailer, packet, StringComparison.Ordinal);

        const string BeginPrefix = "<?xpacket begin=\"";
        Assert.StartsWith(BeginPrefix, packet, StringComparison.Ordinal);
        // Written as an escape: the source file carries no invisible character, which is the same
        // reason PdfUaXmpWriter takes the mark from XmpPacket rather than typing it.
        Assert.Equal('\uFEFF', packet[BeginPrefix.Length]);
        Assert.Equal('"', packet[BeginPrefix.Length + 1]);
    }

    /// <summary>
    /// The padding block stays this writer's own: 2048 characters, a newline every 64th, which is
    /// not what <see cref="XmpPacket.Wrap"/> produces. Routing the wrapper through the shared
    /// constants must not drag the shared padding grid along with it — that would move bytes in
    /// every remediated document's metadata for no behavioural gain.
    /// </summary>
    [Fact]
    public void EmittedPacket_PadsWithItsOwn2048CharacterBlock_NotTheSharedWrapGrid()
    {
        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(null, part: 1);

        int paddingStart = packet.Length - XmpPacket.Trailer.Length - PaddingLength;
        Assert.True(paddingStart > 0, "the packet is shorter than its own padding block");

        string padding = packet.Substring(paddingStart, PaddingLength);
        for (int i = 0; i < padding.Length; i++)
        {
            char expected = (i + 1) % PaddingLineLength == 0 ? '\n' : ' ';
            Assert.Equal(expected, padding[i]);
        }

        // The character immediately before the block belongs to the body's trailing newline, not
        // to a second padding run — i.e. the block is exactly this long, not merely this long at
        // the end.
        Assert.Equal('\n', packet[paddingStart - 1]);
        Assert.NotEqual(' ', packet[paddingStart - 2]);
    }

    // ===============================================================
    //  The emitted BYTES round-trip through the real reader
    // ===============================================================

    /// <summary>
    /// Write the identifier, then read the produced file back with the checker that decides UA03 —
    /// the shipped reader, over the bytes that actually landed in the PDF, not over the string the
    /// builder returned. The untouched fixture is audited alongside as a control: without it the
    /// assertion would pass just as happily on a document the writer never reached.
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_EmittedBytes_AreReadBackAsPdfUaPartOneByTheChecker()
    {
        byte[] original = ClassicPdfWithoutMetadata();
        byte[] produced = Apply(original);

        // Control: the fixture really is missing the declaration, so the assertion below is a
        // claim about the writer rather than about a document that was already conformant.
        Assert.Contains("UA03", CodesOf(original));
        Assert.DoesNotContain("UA03", CodesOf(produced));

        // And the packet itself says part 1 — read namespace-aware, so a pdfuaid:part in the
        // wrong namespace (or an unparseable packet) cannot satisfy it.
        Assert.Equal("1", PdfUaPartOf(XmpOf(produced)));
    }

    /// <summary>
    /// The documented idempotency contract, asserted on the emitted bytes: a second run over the
    /// writer's own output appends nothing, produces a byte-identical file, and still returns
    /// <c>true</c> — with <see cref="PdfUaXmpWriter.LastCallAppendedRevision"/> reporting
    /// <c>false</c> so the caller can tell "already correct" from "written".
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_ReRunOverItsOwnOutput_IsByteIdenticalAndAppendsNothing()
    {
        byte[] once = Apply(ClassicPdfWithoutMetadata());
        Assert.True(PdfUaXmpWriter.LastCallAppendedRevision,
            "the first run had to append the /Metadata revision");

        using var input = new MemoryStream(once, writable: false);
        using var output = new MemoryStream();
        bool ok = PdfUaXmpWriter.SetPdfUaIdentifier(input, output, part: 1);
        byte[] twice = output.ToArray();

        Assert.True(ok, PdfUaXmpWriter.LastError);
        Assert.False(PdfUaXmpWriter.LastCallAppendedRevision);
        Assert.Equal(once, twice);

        // Idempotent *and* still correct: a second run that quietly stopped declaring the
        // identifier would satisfy byte-identity against nothing worth having.
        Assert.Equal("1", PdfUaPartOf(XmpOf(twice)));
    }

    // ===============================================================
    //  Helpers
    // ===============================================================

    private static byte[] Apply(byte[] pdf, int part = 1)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        Assert.True(PdfUaXmpWriter.SetPdfUaIdentifier(input, output, part),
            PdfUaXmpWriter.LastError);
        return output.ToArray();
    }

    /// <summary>The audit codes the shipped checkers report for a document.</summary>
    private static IReadOnlyList<string> CodesOf(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        return PdfAccessibility.Audit(stream).Issues.Select(i => i.Code).ToList();
    }

    /// <summary>The decoded text of the catalog's <c>/Metadata</c> stream.</summary>
    private static string XmpOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfDictionary catalog = doc.Catalog?.Dictionary;
        Assert.NotNull(catalog);

        var stream = doc.ResolveObject(catalog.Get(PdfName.Metadata)) as PdfStream;
        Assert.NotNull(stream);
        return XmpPacket.DecodeText(stream.GetDecodedData());
    }

    /// <summary>
    /// The value of <c>pdfuaid:part</c> in a packet, located by namespace, or <c>null</c>. Reading
    /// the whole packet — <c>xpacket</c> instructions, padding and all — through an
    /// <see cref="XmlReader"/> is itself part of the claim: the envelope has to stay something a
    /// conforming parser accepts.
    /// </summary>
    private static string PdfUaPartOf(string packet)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        using var text = new StringReader(packet);
        using var reader = XmlReader.Create(text, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && string.Equals(reader.LocalName, "part", StringComparison.Ordinal)
                && string.Equals(reader.NamespaceURI, PdfUaXmpWriter.PdfUaIdNamespace,
                                 StringComparison.Ordinal))
            {
                return reader.ReadElementContentAsString();
            }
        }
        return null;
    }
}
