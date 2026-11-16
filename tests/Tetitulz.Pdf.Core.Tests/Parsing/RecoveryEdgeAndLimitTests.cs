// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Two edges of the parser that only a caller driving <see cref="PdfParser"/> directly can reach:
/// the recovery scan's treatment of an object header at the very start of the buffer, and the
/// indirect-reference chain cap.
///
/// <para>Neither is reachable through <see cref="Tetitulz.Pdf.Core.Document.PdfDocument.Open"/> in
/// the first case — the <c>%PDF-</c> header occupies the opening bytes — but a caller pointing the
/// parser at a fragment, an extracted revision or an embedded stream lands exactly there.</para>
/// </summary>
public class RecoveryEdgeAndLimitTests
{
    // ================================================================
    //  Recovery: the object number may start at buffer offset 0
    // ================================================================

    /// <summary>
    /// A buffer beginning <c>12 0 obj</c>. The backward digit walk used to exit without testing
    /// index 0 and then increment unconditionally, so the leading digit was dropped from BOTH the
    /// parsed number and the recorded offset: object <b>2</b> was registered at offset <b>1</b>,
    /// one byte into the header, and object 12 was absent entirely.
    /// </summary>
    [Fact]
    public void RecoveryScan_ObjectNumberAtBufferOffsetZero_KeepsEveryDigit()
    {
        byte[] fragment = Encoding.ASCII.GetBytes(
            "12 0 obj\n<< /Type /Page /Marker 12 >>\nendobj\n");

        var parser = new PdfParser(new MemoryStream(fragment));
        parser.ParseCrossReferences();

        var entry = parser.CrossReferenceTable.GetEntry(12);
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Offset);
        Assert.Null(parser.CrossReferenceTable.GetEntry(2));

        var dict = Assert.IsType<PdfDictionary>(parser.GetObject(12));
        Assert.Equal(12, dict.GetInteger(new PdfName("Marker")));
    }

    /// <summary>
    /// The other half of the same walk: a digit run that is the <i>tail</i> of something else is
    /// not an object header. Recovering <c>x12 0 obj</c> as object 12 at offset 1 mints a
    /// plausible-looking wrong entry rather than leaving a missing one.
    /// </summary>
    [Fact]
    public void RecoveryScan_DigitsPrecededByANonWhitespaceByte_AreNotAnObjectHeader()
    {
        byte[] fragment = Encoding.ASCII.GetBytes(
            "x12 0 obj\n<< /Type /Page >>\nendobj\n");

        var parser = new PdfParser(new MemoryStream(fragment));
        parser.ParseCrossReferences();

        Assert.Null(parser.CrossReferenceTable.GetEntry(12));
        Assert.Equal(0, parser.CrossReferenceTable.Count);
    }

    // ================================================================
    //  The indirect-reference chain cap lives in PdfLimits
    // ================================================================

    /// <summary>
    /// A chain exactly as long as the cap allows resolves to its terminal object…
    /// </summary>
    [Fact]
    public void ResolveObject_ChainAtTheLimit_ResolvesToTheTerminalObject()
    {
        var parser = ParserOver(BuildReferenceChain(PdfLimits.MaxIndirectReferenceChain));

        var resolved = parser.ResolveObject(new PdfReference(1, 0));
        var dict = Assert.IsType<PdfDictionary>(resolved);
        Assert.Equal(PdfName.Page, dict.GetName(PdfName.Type));
    }

    /// <summary>
    /// …and one hop further is abandoned. Asserted against
    /// <see cref="PdfLimits.MaxIndirectReferenceChain"/> rather than against the literal 64, so
    /// that a maintainer tuning the shared constant — which is the whole reason the value moved
    /// out of the parser — cannot leave the guard reading a stale local copy.
    /// </summary>
    [Fact]
    public void ResolveObject_ChainOneHopPastTheLimit_IsAbandoned()
    {
        var parser = ParserOver(BuildReferenceChain(PdfLimits.MaxIndirectReferenceChain + 1));

        Assert.IsType<PdfNull>(parser.ResolveObject(new PdfReference(1, 0)));
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static PdfParser ParserOver(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    /// <summary>
    /// Objects 1..<paramref name="length"/>-1 each hold a reference to the next; object
    /// <paramref name="length"/> is a page dictionary. Resolving a reference to object 1 therefore
    /// costs exactly <paramref name="length"/> hops.
    /// </summary>
    private static byte[] BuildReferenceChain(int length)
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new long[length + 1];
        for (int n = 1; n <= length; n++)
        {
            offsets[n] = file.Position;
            string body = n < length
                ? (n + 1).ToString(CultureInfo.InvariantCulture) + " 0 R"
                : "<< /Type /Page >>";
            Write(n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + body + "\nendobj\n");
        }

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 ").Append((length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        for (int n = 1; n <= length; n++)
            xs.Append(offsets[n].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size ").Append((length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }
}
