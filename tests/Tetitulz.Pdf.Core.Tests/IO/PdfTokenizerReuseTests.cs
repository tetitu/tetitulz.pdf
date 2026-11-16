// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// The tokenizer's reused buffers and known keywords (GAPS.md §13b): one text builder and one name
/// buffer for every token, and the one string of each known keyword — each token's text still its
/// own, whatever came before it.
/// </summary>
public sealed class PdfTokenizerReuseTests
{
    private static List<PdfToken> Tokens(string text)
    {
        var tokenizer = new PdfTokenizer(new MemoryStream(Encoding.Latin1.GetBytes(text)));
        var tokens = new List<PdfToken>();
        while (true)
        {
            PdfToken t = tokenizer.NextToken();
            if (t.Type == PdfTokenType.Eof) return tokens;
            tokens.Add(t);
        }
    }

    /// <summary>Every kind of token after every other keeps its own text: the reused builder is cleared, never appended to.</summary>
    [Fact]
    public void EachTokenKeepsItsOwnText()
    {
        var t = Tokens("(first string) 12.5 BT <414243> /Name (second) -3 ET true null 0.25 Tj");
        Assert.Equal(new[] { "first string", "12.5", "BT", "414243", "Name", "second", "-3", "ET", "true", "null", "0.25", "Tj" },
                     t.Select(x => x.Value).ToArray());
        Assert.Equal(new[] { PdfTokenType.LiteralString, PdfTokenType.Number, PdfTokenType.Keyword, PdfTokenType.HexString,
                             PdfTokenType.Name, PdfTokenType.LiteralString, PdfTokenType.Number, PdfTokenType.Keyword,
                             PdfTokenType.Boolean, PdfTokenType.Null, PdfTokenType.Number, PdfTokenType.Keyword },
                     t.Select(x => x.Type).ToArray());
    }

    /// <summary>
    /// A token longer than what the builder keeps — a 10 000-character string, a 9 000-byte name —
    /// is whole, and the short tokens after it are themselves: the buffer it grew is let go or
    /// cleared, never read again.
    /// </summary>
    [Fact]
    public void ALongTokenAndTheShortOnesAfterIt_AreEachWhole()
    {
        string longText = new string('x', 10_000);
        string longName = new string('n', 9_000);
        string mediumName = new string('m', 100);
        var t = Tokens($"({longText}) /{longName} 1 /{mediumName} /a (b) re");
        Assert.Equal(longText, t[0].Value);
        Assert.Equal(longName, t[1].Value);
        Assert.Equal("1", t[2].Value);
        Assert.Equal(mediumName, t[3].Value);
        Assert.Equal("a", t[4].Value);
        Assert.Equal("b", t[5].Value);
        Assert.Equal("re", t[6].Value);
    }

    /// <summary>
    /// What a tokenizer keeps between tokens: the buffer a 100-byte name grew (so the next long
    /// name allocates nothing), and nothing past 4 096 — a 10 000-character string or a 9 000-byte
    /// name is not held for the tokenizer's life.
    /// </summary>
    [Fact]
    public void ATokenizerKeepsWhatAShortTokenGrew_NotWhatALongOneDid()
    {
        var tokenizer = new PdfTokenizer(new MemoryStream(Encoding.Latin1.GetBytes(
            "/" + new string('m', 100) + " (" + new string('x', 10_000) + ") /" + new string('n', 9_000)
            + " 1" + new string('0', 5_000) + " ")));
        Assert.Equal(32, tokenizer.RetainedNameCapacity);

        Assert.Equal(PdfTokenType.Name, tokenizer.NextToken().Type);
        Assert.Equal(128, tokenizer.RetainedNameCapacity);

        Assert.Equal(10_000, tokenizer.NextToken().Value.Length);
        Assert.InRange(tokenizer.RetainedTextCapacity, 1, 4096);

        Assert.Equal(9_000, tokenizer.NextToken().Value.Length);
        Assert.InRange(tokenizer.RetainedNameCapacity, 128, 4096);

        PdfToken number = tokenizer.NextToken();
        Assert.Equal((PdfTokenType.Number, 5_001), (number.Type, number.Value.Length));
        Assert.InRange(tokenizer.RetainedTextCapacity, 1, 4096);
    }

    /// <summary>The <c>d0</c>/<c>d1</c> rule reads a digit into <c>d</c> alone: <c>de5</c> is the keyword <c>de</c> and the number 5.</summary>
    [Fact]
    public void ADigitJoinsOnlyALoneD()
    {
        var t = Tokens("d1 de5 d0");
        Assert.Equal(new[] { "d1", "de", "5", "d0" }, t.Select(x => x.Value).ToArray());
        Assert.Equal(PdfTokenType.Number, t[2].Type);
    }

    /// <summary>A literal string whose escape is cut throws as before, and the tokenizer's next token after a reset is clean.</summary>
    [Fact]
    public void AFailedTokenLeavesNothingBehind()
    {
        var tokenizer = new PdfTokenizer(new MemoryStream(Encoding.Latin1.GetBytes("(abc\\")));
        Assert.Throws<PdfParseException>(() => tokenizer.NextToken());
        var again = new PdfTokenizer(new MemoryStream(Encoding.Latin1.GetBytes("(def) gh")));
        Assert.Equal("def", again.NextToken().Value);
        Assert.Equal("gh", again.NextToken().Value);
    }

    /// <summary>
    /// A known keyword is its one string — the same instance however often it is met, so a million
    /// operations hold one <c>"re"</c> — and any other keyword, a non-ASCII one, one longer than nine,
    /// is a string of its own with its own text.
    /// </summary>
    [Fact]
    public void AKnownKeywordIsItsOneString_AnyOtherIsItsOwn()
    {
        var t = Tokens("re re BDC BDC true true endstream startxref xyz xyz abé abcdefghij");
        Assert.Same(t[0].Value, t[1].Value);
        Assert.Same(t[2].Value, t[3].Value);
        Assert.Same(t[4].Value, t[5].Value);
        Assert.Equal("endstream", t[6].Value);
        Assert.Equal("startxref", t[7].Value);
        Assert.Same(t[6].Value, Tokens("endstream")[0].Value);

        Assert.Equal("xyz", t[8].Value);
        Assert.NotSame(t[8].Value, t[9].Value);   // unknown: its own
        Assert.Equal("abé", t[10].Value);
        Assert.Equal("abcdefghij", t[11].Value);

        // The packing's two bounds: a tenth character would push the first out past 63 bits
        // ("Xendstream" packing as "endstream"), and a character above 127 would set its
        // neighbour's lowest bit ("R" then U+00C3 packing as "SC": 0x52 becomes 0x53, 0xC3 0x43).
        Assert.Equal("Xendstream", Tokens("Xendstream")[0].Value);
        string rWithTilde = "R" + (char)0xC3;
        Assert.Equal(rWithTilde, Tokens(rWithTilde)[0].Value);
        Assert.Equal("SC", Tokens("SC")[0].Value);   // the control: the keyword it would pack as is known

        // Every content-stream operator is known, by the one set the parser reads.
        foreach (string op in ContentStreamOperators.All)
            Assert.Same(Tokens(op + " " + op)[0].Value, Tokens(op)[0].Value);
    }

    /// <summary>
    /// A vector page's parse allocates what its objects are and little more: 20 000 operations of
    /// paths, colours and widths under 450 bytes an operation (about 310 measured), where a builder
    /// and an iterator per token took about 680 (GAPS.md §13b).
    /// </summary>
    [Fact]
    public void AVectorPagesParse_AllocatesWhatItsObjectsAre()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
            sb.Append("q 0.2 0.4 0.6 rg 0.5 w 100.25 200.5 m 103.5 202 l 106.75 203.5 l 110 205 l 40.125 12.5 20 30 re f Q\n");
        byte[] data = Encoding.ASCII.GetBytes(sb.ToString());
        ContentStreamParser.Parse(data);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ops = ContentStreamParser.Parse(data);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2000 * 10, ops.Count);
        Assert.True(allocated < 450L * ops.Count, $"{allocated:N0} bytes for {ops.Count:N0} operations, {allocated / ops.Count} each");
    }
}
