// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// What a signature's excluded gap may hold, byte by byte, and what a revision carrying one catalog
/// entry may change besides it.
/// </summary>
public sealed class SignedGapAndProofRevisionTests
{
    private static readonly byte[] Contents = { 0xAB, 0xCD, 0x00 };

    private static bool Holds(string gap, byte[] contents = null)
    {
        byte[] bytes = Encoding.Latin1.GetBytes("xx" + gap + "yy");
        using var file = new MemoryStream(bytes);
        return SignedByteRanges.GapHoldsOnlyContents(file, 2, gap.Length, contents ?? Contents);
    }

    /// <summary>§7.3.4.3's hex string, whichever way §12.8.1's gap is drawn around it.</summary>
    [Theory]
    [InlineData("<ABCD00>")]
    [InlineData("<abcd00>")]
    [InlineData("ABCD00")]
    [InlineData("<AB CD\n00>")]
    [InlineData(" <ABCD00> ")]
    [InlineData("<ABCD00>\0\0")]
    public void TheSignaturesOwnHex_Holds(string gap)
    {
        Assert.True(Holds(gap));
    }

    [Fact]
    public void AnOddFinalDigit_HasAnAssumedZero()
    {
        Assert.True(Holds("<ABC>", new byte[] { 0xAB, 0xC0 }));
        Assert.False(Holds("<ABC>", new byte[] { 0xAB, 0xC1 }));
        Assert.False(Holds("<ABC>", new byte[] { 0xAB }));
    }

    /// <summary>Whatever else stands in a gap no digest covers.</summary>
    [Theory]
    [InlineData("<ABCD01>")]                     // another signature's bytes
    [InlineData("<ABCD>")]                       // too few
    [InlineData("<ABCD0000>")]                   // too many
    [InlineData("<ABCD00")]                      // an open delimiter
    [InlineData("<ABCD00x")]                     // a forger's byte where the close should be
    [InlineData("<ABCD00>x")]                    // a forger's bytes after it
    [InlineData("x<ABCD00>")]                    // and before it
    [InlineData("<ABCD00> 4 0 obj")]             // an object
    [InlineData("ABCD00>")]                      // a close with no open
    [InlineData("(«Í\0)")]             // the literal form, which leaves room
    [InlineData("")]                             // nothing
    public void AdversarialGap_HoldingAnythingElse_DoesNot(string gap)
    {
        Assert.False(Holds(gap));
    }

    [Fact]
    public void AGapPastTheFile_DoesNot()
    {
        using var file = new MemoryStream(new byte[4]);
        Assert.False(SignedByteRanges.GapHoldsOnlyContents(file, 2, 40, Contents));
        Assert.False(SignedByteRanges.GapHoldsOnlyContents(file, 2, -1, Contents));
    }

    // ---------------------------------------------------------------- the proof's revision

    private static readonly PdfName Entry = new PdfName("Proof");
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private static string Stream(string text, string dict = "") => $"<< {dict} /Length {text.Length} >>\nstream\n{text}\nendstream";

    private static byte[] Assemble()
    {
        string[] bodies =
        {
            Catalog,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            Stream("(notarized) Tj"),
            "<< /Note (unused) >>",
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
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static long StartXref(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.LastIndexOf("startxref", System.StringComparison.Ordinal);
        return long.Parse(text.Substring(at + 9, text.IndexOf("%%EOF", at, System.StringComparison.Ordinal) - at - 9).Trim(), CultureInfo.InvariantCulture);
    }

    private static byte[] Revise(byte[] pdf, IReadOnlyDictionary<int, string> objects, IReadOnlyDictionary<int, long> entries = null, string root = "1 0 R", int freed = 0)
    {
        var sb = new StringBuilder(Encoding.Latin1.GetString(pdf));
        var at = new SortedDictionary<int, long>();
        foreach (var o in objects)
        {
            at[o.Key] = sb.Length;
            sb.Append(o.Key).Append(" 0 obj\n").Append(o.Value).Append("\nendobj\n");
        }
        if (entries != null) foreach (var e in entries) at[e.Key] = e.Value;

        long xref = sb.Length;
        sb.Append("xref\n");
        foreach (var e in at) sb.Append(e.Key).Append(" 1\n").Append(e.Value.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        if (freed > 0) sb.Append(freed).Append(" 1\n0000000000 00001 f \n");
        sb.Append("trailer\n<< /Size 100 /Root ").Append(root).Append(" /Prev ").Append(StartXref(pdf)).Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static IReadOnlyList<string> Changes(byte[] pdf, long start)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        return DocMdpAnalyzer.ChangesBesidesCatalogEntry(doc, start, Entry);
    }

    private const string CatalogWithInline = "<< /Type /Catalog /Pages 2 0 R /Proof << /A (b) >> >>";

    [Fact]
    public void TheCatalogAndItsEntry_AreNoOtherChange_InlineOrIndirect()
    {
        byte[] prefix = Assemble();

        Assert.Empty(Changes(Revise(prefix, new Dictionary<int, string> { [1] = CatalogWithInline }), prefix.Length));
        Assert.Empty(Changes(Revise(prefix, new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Proof 6 0 R >>",
            [6] = "<< /A (b) >>",
        }), prefix.Length));
    }

    [Fact]
    public void AdversarialObject_RewrittenBesideTheEntry_IsNamed()
    {
        byte[] prefix = Assemble();
        var changes = Changes(Revise(prefix, new Dictionary<int, string>
        {
            [1] = CatalogWithInline,
            [4] = Stream("(forged) Tj"),
        }), prefix.Length);

        Assert.Contains("obj 4: rewritten", changes);
    }

    [Fact]
    public void AdversarialObject_AddedBesideTheEntry_IsNamed()
    {
        byte[] prefix = Assemble();
        Assert.Contains("obj 6: added", Changes(Revise(prefix, new Dictionary<int, string>
        {
            [1] = CatalogWithInline,
            [6] = "<< /Note (more) >>",
        }), prefix.Length));
    }

    /// <summary>An entry naming a stream is no dictionary of proof: it could be anything a reader draws.</summary>
    [Fact]
    public void AdversarialEntry_NamingAStream_IsNamed()
    {
        byte[] prefix = Assemble();
        Assert.Contains("obj 6: added", Changes(Revise(prefix, new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Proof 6 0 R >>",
            [6] = Stream("x"),
        }), prefix.Length));
    }

    [Fact]
    public void AdversarialCatalogKey_BesideTheEntry_IsNamed()
    {
        byte[] prefix = Assemble();
        var changes = Changes(Revise(prefix, new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Proof << /A (b) >> /OpenAction 4 0 R >>",
        }), prefix.Length);

        Assert.Contains(changes, c => c.StartsWith("catalog:", System.StringComparison.Ordinal) && c.Contains("OpenAction", System.StringComparison.Ordinal));
    }

    /// <summary>What writes no object: a table pointing at other bytes, a catalog changed by the trailer, a freed object.</summary>
    [Fact]
    public void AdversarialTable_PointingAtOtherBytes_IsNamed()
    {
        byte[] prefix = Assemble();
        long other = Encoding.Latin1.GetString(prefix).IndexOf("5 0 obj", System.StringComparison.Ordinal);

        var changes = Changes(Revise(prefix, new Dictionary<int, string> { [1] = CatalogWithInline }, new Dictionary<int, long> { [4] = other }), prefix.Length);

        Assert.Contains(changes, c => c.StartsWith("obj 4:", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AdversarialFreedObject_IsNamed()
    {
        byte[] prefix = Assemble();
        Assert.Contains("obj 4: freed", Changes(Revise(prefix, new Dictionary<int, string> { [1] = CatalogWithInline }, freed: 4), prefix.Length));
    }

    [Fact]
    public void AnUnreadablePrefix_IsSaid()
    {
        byte[] prefix = Assemble();
        var changes = Changes(Revise(prefix, new Dictionary<int, string> { [1] = CatalogWithInline }), 4);

        Assert.Contains(changes, c => c.Contains("could not be read", System.StringComparison.Ordinal));
    }
}
