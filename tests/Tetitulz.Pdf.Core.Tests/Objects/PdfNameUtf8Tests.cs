// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// Whether a name's bytes were UTF-8 (ISO 32000-2 §7.3.5), carried from the tokenizer to the
/// <see cref="PdfName"/>: the value cannot say it, since <c>é</c> reads from <c>C3 A9</c> and from a
/// lone <c>E9</c> alike, and PDF/A-4 checks it (ISO 19005-4 6.1.7).
/// </summary>
public sealed class PdfNameUtf8Tests
{
    [Theory]
    [InlineData(new byte[] { 0x43, 0x61, 0x66, 0xC3, 0xA9 }, "Café", false)]   // UTF-8
    [InlineData(new byte[] { 0x43, 0x61, 0x66, 0xE9 }, "Café", true)]          // Latin-1, the same value
    [InlineData(new byte[] { 0x52, 0xC2 }, "RÂ", true)]                    // a lead byte with nothing after it
    [InlineData(new byte[] { 0x54, 0x79, 0x70, 0x65 }, "Type", false)]          // ASCII
    public void Decode_SaysWhetherTheBytesWereUtf8(byte[] bytes, string value, bool notUtf8)
    {
        Assert.Equal(value, PdfNameEscape.Decode(bytes, bytes.Length, out bool flagged));
        Assert.Equal(notUtf8, flagged);
    }

    private static PdfDictionary Parse(string text)
        => Assert.IsType<PdfDictionary>(new PdfParser(new MemoryStream(Encoding.ASCII.GetBytes(text))).ParseDirectObject());

    /// <summary>A value name and a key name each carry their bytes' reading; the UTF-8 controls do not.</summary>
    [Fact]
    public void Parser_FlagsAValueAndAKeyWhoseBytesAreNotUtf8()
    {
        PdfDictionary dict = Parse("<< /V /Caf#E9 /Na#E9me 1 /U /Caf#C3#A9 >>");

        var value = Assert.IsType<PdfName>(dict[new PdfName("V")]);
        Assert.Equal("Café", value.Value);
        Assert.True(value.BytesNotUtf8);
        Assert.True(dict.Keys.Single(k => k.Value == "Naéme").BytesNotUtf8);
        Assert.False(Assert.IsType<PdfName>(dict[new PdfName("U")]).BytesNotUtf8);
        Assert.False(dict.Keys.Single(k => k.Value == "U").BytesNotUtf8);
    }

    /// <summary>A content stream's operand name is flagged the same way.</summary>
    [Fact]
    public void ContentStreamParser_FlagsAnOperandWhoseBytesAreNotUtf8()
    {
        var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes("/T#FF BMC EMC /OK BMC EMC"));

        Assert.True(Assert.IsType<PdfName>(ops[0].Operands[0]).BytesNotUtf8);
        Assert.False(Assert.IsType<PdfName>(ops[2].Operands[0]).BytesNotUtf8);
    }

    /// <summary>
    /// An inline image's keys are names too: one of the image dictionary's, and one inside its
    /// <c>/DP</c> dictionary, each flagged; the plain keys beside them not.
    /// </summary>
    [Fact]
    public void ContentStreamParser_FlagsAnInlineImagesKeys()
    {
        var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes(
            "BI /W 1 /H 1 /BPC 8 /CS /G /K#E9 1 /DP << /P#E9 1 /Q 2 >> ID x EI"));

        var image = Assert.IsAssignableFrom<PdfDictionary>(ops.Single(o => o.Operator == "BI").Operands[0]);
        Assert.True(Flagged(image, "Ké"));
        Assert.False(Flagged(image, "W"));
        var parameters = Assert.IsType<PdfDictionary>(image[new PdfName("DP")]);
        Assert.True(Flagged(parameters, "Pé"));
        Assert.False(Flagged(parameters, "Q"));
    }

    private static bool Flagged(PdfDictionary dict, string key) => dict.Keys.Single(k => k.Value == key).BytesNotUtf8;

    /// <summary>A key set again from a name of no such bytes is that name's: the writer writes it as UTF-8.</summary>
    [Fact]
    public void Dictionary_AKeySetAgainFromAPlainName_IsNotFlagged()
    {
        PdfDictionary dict = Parse("<< /Caf#E9 1 >>");
        Assert.True(Flagged(dict, "Café"));

        dict.Set(new PdfName("Café"), new PdfNumber(2));

        Assert.False(Flagged(dict, "Café"));
    }

    /// <summary>Both clones keep which keys were read from non-UTF-8 bytes, and the enumerator says so too.</summary>
    [Fact]
    public void Dictionary_Clones_KeepTheKeysEncoding()
    {
        PdfDictionary dict = Parse("<< /Caf#E9 1 /Plain 2 >>");

        Assert.True(Flagged(PdfDictionary.CloneShallow(dict), "Café"));
        Assert.True(Flagged(PdfDictionary.DeepClone(dict), "Café"));
        Assert.False(Flagged(PdfDictionary.DeepClone(dict), "Plain"));
        Assert.True(dict.Single(kv => kv.Key.Value == "Café").Key.BytesNotUtf8);
    }

    /// <summary>
    /// A removed key, and a key set through the internal string indexer, forget the old spelling: set
    /// again afterwards it is not flagged.
    /// </summary>
    [Fact]
    public void Dictionary_RemoveAndTheStringIndexer_ForgetTheOldSpelling()
    {
        PdfDictionary removed = Parse("<< /Caf#E9 1 >>");
        removed.Remove(new PdfName("Café"));
        removed["Café"] = new PdfNumber(3);

        PdfDictionary removedByString = Parse("<< /Caf#E9 1 >>");
        removedByString.Remove("Café");
        removedByString["Café"] = new PdfNumber(3);

        PdfDictionary overwritten = Parse("<< /Caf#E9 1 >>");
        overwritten["Café"] = new PdfNumber(4);

        Assert.False(Flagged(removed, "Café"));
        Assert.False(Flagged(removedByString, "Café"));
        Assert.False(Flagged(overwritten, "Café"));
    }

    /// <summary>
    /// The flag is not part of the name's identity: a key read from a Latin-1 spelling still finds the
    /// entry a lookup by its value names, and the two names are equal.
    /// </summary>
    [Fact]
    public void Equality_IgnoresHowTheBytesWereSpelled()
    {
        PdfDictionary dict = Parse("<< /Caf#E9 1 >>");

        Assert.Equal(new PdfName("Café"), new PdfName("Café", bytesNotUtf8: true));
        Assert.Equal(new PdfName("Café").GetHashCode(), new PdfName("Café", bytesNotUtf8: true).GetHashCode());
        Assert.Equal(1, Assert.IsType<PdfNumber>(dict[new PdfName("Café")]).IntValue);
    }

    /// <summary>
    /// The writer writes a parsed name — a key and a value alike — from its own bytes: a name read
    /// from Latin-1 bytes comes out as those bytes, since the content streams copied beside it still
    /// name the resource by them and §7.3.5 makes <c>/F#C3#A9</c> another name; one read from UTF-8
    /// comes out as UTF-8.
    /// </summary>
    [Theory]
    [InlineData("<< /F#E9 /Caf#E9 >>", "/F#E9 /Caf#E9", "#C3#A9")]
    [InlineData("<< /F#C3#A9 /Caf#C3#A9 >>", "/F#C3#A9 /Caf#C3#A9", "#E9")]
    public void Writer_WritesAParsedNameFromItsOwnBytes(string source, string written, string notWritten)
    {
        PdfDictionary dict = Parse(source);

        using var output = new MemoryStream();
        new PdfWriter(output).WriteObject(dict);
        string text = Encoding.ASCII.GetString(output.ToArray());

        Assert.Contains(written, text, System.StringComparison.Ordinal);
        Assert.DoesNotContain(notWritten, text, System.StringComparison.Ordinal);
    }
}
