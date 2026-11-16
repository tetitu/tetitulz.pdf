// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// §7.3.5 name escaping, and the round trip that makes it matter: the tokenizer <b>decodes</b>
/// <c>#XX</c> into a name's value, so any writer that emits the value raw un-escapes it.
/// </summary>
/// <remarks>
/// <para><c>PdfTemplateEngine</c> serialised a name as <c>"/" + n.Value</c>. A page whose content
/// stream contained <c>/Name#20With#20Space BDC</c> — or a resource name such as <c>/T1#2D0</c> —
/// was therefore re-emitted as <c>/Name With Space</c>: <b>three tokens where the file had one</b>,
/// on every templated page. And the result goes out through an ASCII writer, so a non-ASCII byte in
/// a name became <c>'?'</c>.</para>
///
/// <para>The rule now has one implementation, which <c>PdfWriter</c> also uses. That matters
/// because a name written by one and read back by the other has to survive the trip.</para>
/// </remarks>
public class PdfNameEscapeTests
{
    /// <summary>
    /// <b>The property that makes escaping load-bearing.</b> A name survives being written and
    /// parsed again, whatever it contains.
    /// </summary>
    [Theory]
    [InlineData("Simple")]
    [InlineData("Name With Space")]
    [InlineData("T1-0")]
    [InlineData("A#B")]
    [InlineData("Paren(s)")]
    [InlineData("Bracket[s]")]
    [InlineData("Slash/Inside")]
    [InlineData("Percent%Sign")]
    [InlineData("Euro€Sign")]
    [InlineData("עברית")]
    // Longer than either reader's initial byte buffer (32 and 16), so the growth path runs. The
    // second is non-ASCII across the resize, where a buffer bug and a decode bug would look alike.
    [InlineData("AVeryLongResourceNameThatExceedsThirtyTwoBytesInLength")]
    [InlineData("עבריתעבריתעבריתעברית")]
    public void ANameSurvivesAWriteAndReParse(string value)
    {
        // Escape it, put the escaped token in a real file, and let the parser decode it back.
        // Round-tripping through the tokenizer is the point: the tokenizer DECODES #XX, so a
        // writer that emits the decoded value un-escapes whatever escaping the file had.
        string escaped = PdfNameEscape.Escape(value);

        using var ms = new MemoryStream(DocumentNaming(escaped));
        using var doc = PdfDocument.Open(ms);

        var marker = doc.Catalog.Dictionary[new PdfName("Marker")] as PdfDictionary;
        Assert.True(marker != null, "the marker dictionary did not survive the round trip");

        Assert.True(marker.ContainsKey(new PdfName(value)),
            $"'{escaped}' parsed back to something other than '{value}'");

        // And re-escaping the parsed value reproduces the token, which is the invariant a writer
        // depends on.
        Assert.Equal(escaped, PdfNameEscape.Escape(value));
    }

    /// <summary>
    /// <b>The defect, stated directly.</b> A name containing a space must not be emitted as two
    /// tokens.
    /// </summary>
    [Theory]
    [InlineData("Name With Space", "Name#20With#20Space")]
    [InlineData("T1-0", "T1-0")]              // '-' is legal unescaped
    [InlineData("A#B", "A#23B")]
    [InlineData("Slash/Inside", "Slash#2FInside")]
    public void TheEscapedFormIsWhatTheSpecPrescribes(string value, string expected)
        => Assert.Equal(expected, PdfNameEscape.Escape(value));

    /// <summary>
    /// Non-ASCII is escaped from its <b>UTF-8 bytes</b>. Casting the char to a byte truncates the
    /// code unit — U+20AC € becomes 0xAC — and produces a name no reader maps back.
    /// </summary>
    [Fact]
    public void NonAsciiIsEscapedFromItsUtf8Bytes()
        => Assert.Equal("Euro#E2#82#ACSign", PdfNameEscape.Escape("Euro€Sign"));

    /// <summary>
    /// <b>The legacy guarantee, and the reason changing the reader was safe to do at all.</b> Bytes
    /// that are not valid UTF-8 keep the byte-per-char reading they have always had.
    /// </summary>
    /// <remarks>
    /// <para>A Latin-1 producer writing <c>e</c>-acute emits <c>#E9</c>, and 0xE9 standing alone is
    /// not a valid UTF-8 sequence - there is no continuation byte after it. So the fallback fires
    /// and the name is still U+00E9, exactly as before. This is what makes the change unable to
    /// reach a legacy file: the two readings are defined on disjoint inputs.</para>
    ///
    /// <para>Paired with <c>Caf#C3#A9</c> below, which IS valid UTF-8 and must now take the other
    /// branch. A test of either one alone cannot see that a branch was chosen.</para>
    /// </remarks>
    [Fact]
    public void BytesThatAreNotValidUtf8KeepTheLatin1Reading()
    {
        using var ms = new MemoryStream(DocumentNaming("Caf#E9"));
        using var doc = PdfDocument.Open(ms);
        var marker = doc.Catalog.Dictionary[new PdfName("Marker")] as PdfDictionary;

        Assert.True(marker != null);
        Assert.True(marker.ContainsKey(new PdfName("Café")),
            "a lone 0xE9 is not valid UTF-8, so it must still read as Latin-1");
    }

    /// <summary>
    /// <b>The defect the old reader actually caused: a name GREW on every read-modify-write.</b>
    /// </summary>
    /// <remarks>
    /// <para>The reader is only half the trip. <c>/Caf#C3#A9</c> read as two Latin-1 characters
    /// re-escaped from <i>their</i> UTF-8 bytes to <c>/Caf#C3#83#C2#A9</c> - four escapes where the
    /// file had two - and the next cycle doubled it again. Nothing downstream noticed, because the
    /// document stayed internally consistent: every reference to the name went through the same
    /// reader, so lookups kept matching while the bytes on disk drifted.</para>
    ///
    /// <para>That is why this asserts the <b>re-escaped token</b> rather than only the decoded
    /// value. A test that stopped at "it parses to Cafe-acute" passes for a reader that is right
    /// and a writer that is wrong.</para>
    /// </remarks>
    [Fact]
    public void AParsedNameReEscapesToTheTokenItCameFrom()
    {
        using var ms = new MemoryStream(DocumentNaming("Caf#C3#A9"));
        using var doc = PdfDocument.Open(ms);
        var marker = doc.Catalog.Dictionary[new PdfName("Marker")] as PdfDictionary;

        Assert.True(marker != null);
        Assert.True(marker.ContainsKey(new PdfName("Café")),
            "C3 A9 is the UTF-8 for e-acute and must decode as one character");

        Assert.Equal("Caf#C3#A9", PdfNameEscape.Escape("Café"));
    }

    /// <summary>
    /// <b>The floor.</b> An ordinary name is left completely alone, so the escape cannot have become
    /// "escape everything" — which would round-trip correctly while making every file unreadable by
    /// anything that compares names literally.
    /// </summary>
    [Theory]
    [InlineData("Type")]
    [InlineData("FontFile2")]
    [InlineData("T1_0")]
    [InlineData("A.B-C")]
    public void AnOrdinaryNameIsUnchanged(string value)
        => Assert.Equal(value, PdfNameEscape.Escape(value));

    /// <summary>The escaped form is always pure printable ASCII, which is what lets it be written through an ASCII writer.</summary>
    [Theory]
    [InlineData("Euro€Sign")]
    [InlineData("עברית")]
    [InlineData("Name With Space")]
    public void TheEscapedFormIsPrintableAscii(string value)
    {
        foreach (char c in PdfNameEscape.Escape(value))
            Assert.InRange(c, (char)0x21, (char)0x7E);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A document whose catalog carries a dictionary keyed by the already-escaped
    /// <paramref name="escapedName"/> token, written as raw bytes so the token reaches the
    /// tokenizer exactly as spelled.
    /// </summary>
    private static byte[] DocumentNaming(string escapedName)
    {
        var objs = new[]
        {
            $"1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Marker << /{escapedName} 1 >> >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n",
        };

        var acc = new System.Collections.Generic.List<byte>();
        var offsets = new System.Collections.Generic.List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (string o in objs) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
