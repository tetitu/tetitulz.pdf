// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Xml.Linq;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="XmlEscape.EscapeText"/> must produce something an XML parser will read — which is a
/// stronger claim than escaping the five entities, and a separate one.
/// </summary>
/// <remarks>
/// <para><b>The five-entity pass could not have fixed this.</b> XML 1.0 §2.2 defines <c>Char</c>
/// as <c>#x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]</c>, so a
/// character outside that set has no spelling at all — <c>&amp;#1;</c> is exactly as ill-formed as
/// a raw <c>0x01</c>. Escaping is about ambiguity; this is about representability.
/// </para>
///
/// <para><b>What it broke, and why it took this long to find.</b> One such byte makes the entire
/// document unparseable rather than just the element carrying it. The PDF→SVG export hit it on a
/// composite font with no <c>/ToUnicode</c>, where the decoded text is the raw code units and a
/// CID of 1 is an ordinary thing for a subset font to use. Every existing assertion about that
/// output was a substring search or a pixel comparison, and both are perfectly happy with a
/// document no XML reader will open.</para>
///
/// <para>Each test therefore parses the result rather than inspecting it: the claim is "an XML
/// parser accepts this", and only an XML parser can make it. Control characters are written as
/// <c>\uNNNN</c> escapes throughout — a source file carrying the raw bytes is one careless
/// re-save away from losing them silently.</para>
/// </remarks>
public sealed class XmlEscapeRepresentabilityTests
{
    /// <summary>A character XML cannot carry makes the document unreadable, so it is removed.</summary>
    [Theory]
    [InlineData('\u0000')]
    [InlineData('\u0001')]
    [InlineData('\u0008')]
    [InlineData('\u000B')]
    [InlineData('\u000C')]
    [InlineData('\u001F')]
    public void AControlCharacterIsRemovedRatherThanEscaped(char control)
    {
        string escaped = XmlEscape.EscapeText("a" + control + "b");

        Assert.Equal("ab", escaped);
        Assert.Equal("ab", Parse(escaped));
    }

    /// <summary>The three controls XML does allow survive unchanged.</summary>
    /// <remarks>
    /// <b>The acceptance control, and it is not a formality.</b> A newline in a text run and a tab
    /// in a table cell are ordinary content; a strip that took "below U+0020" as its rule would
    /// delete both, turning a parse fix into a data-loss bug no XML parser would ever complain
    /// about.
    /// </remarks>
    [Theory]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('\r')]
    public void TheThreeXmlLegalControlsAreKept(char allowed)
    {
        string escaped = XmlEscape.EscapeText("a" + allowed + "b");

        Assert.Equal(3, escaped.Length);
        Assert.Equal(allowed, escaped[1]);
    }

    /// <summary>The five entities are still escaped, and ordinary text is untouched.</summary>
    [Fact]
    public void OrdinaryTextIsUnaffectedAndTheFiveEntitiesStillEscape()
    {
        Assert.Equal("plain text", XmlEscape.EscapeText("plain text"));
        Assert.Equal("&amp;&lt;&gt;&quot;&apos;", XmlEscape.EscapeText("&<>\"'"));
        Assert.Equal("", XmlEscape.EscapeText(null));
    }

    /// <summary>A complete surrogate pair is one character and is kept.</summary>
    /// <remarks>
    /// <c>[#x10000-#x10FFFF]</c> is in the legal set, and .NET stores it as two code units. A
    /// filter testing code units in isolation would see two surrogates, judge each one alone and
    /// delete every astral character in the document — an emoji, a CJK extension-B ideograph, a
    /// musical symbol.
    /// </remarks>
    [Fact]
    public void AWellFormedSurrogatePairSurvives()
    {
        string astral = char.ConvertFromUtf32(0x1F600);   // two code units, one character

        Assert.Equal("a" + astral + "b", XmlEscape.EscapeText("a" + astral + "b"));
        Assert.Equal("a" + astral + "b", Parse(XmlEscape.EscapeText("a" + astral + "b")));
    }

    /// <summary>Half of a pair is not a character, and XML cannot carry it.</summary>
    /// <remarks>
    /// <para>A lone surrogate reaches here whenever a font's <c>/ToUnicode</c> maps a code to one,
    /// which a broken CMap does. It is not encodable in UTF-8 at all, so it fails at serialization
    /// rather than at parse. All three shapes are tested because keeping "a low surrogate after
    /// anything" and "a high surrogate before anything" are different mistakes, and the reversed
    /// pair is the one that catches a check written as "is the neighbour a surrogate".</para>
    ///
    /// <para><b>The strings are built here rather than passed as <c>[InlineData]</c>, and that is
    /// not a style choice.</b> xUnit serializes theory arguments for discovery, and a lone
    /// surrogate is exactly the thing that cannot survive a UTF-8 round trip — it arrived as
    /// U+FFFD, which is representable, so the theory asserted three times that the replacement
    /// character is not a surrogate. Two mutations that keep an orphan survived against it. The
    /// <c>int</c> selector keeps the three cases as three rows without putting a string through
    /// the serializer.</para>
    /// </remarks>
    [Theory]
    [InlineData(0)]   // a high surrogate with no low one after it
    [InlineData(1)]   // a low surrogate with no high one before it
    [InlineData(2)]   // a pair in the wrong order: two orphans, not one character
    public void AnOrphanedSurrogateIsRemoved(int shape)
    {
        const char High = '\uD83D';
        const char Low  = '\uDE00';

        string input = shape switch
        {
            0 => "a" + High + "b",
            1 => "a" + Low + "b",
            _ => string.Empty + Low + High,
        };

        // The fixture is only potent if it really carries an orphan; a value that arrived
        // through a serializer would not, and that is what made this test vacuous.
        Assert.Contains(input, char.IsSurrogate);

        string escaped = XmlEscape.EscapeText(input);

        foreach (char c in escaped)
            Assert.False(char.IsSurrogate(c), "an orphaned surrogate survived");

        Assert.Equal(escaped, Parse(escaped));
    }

    /// <summary>The two non-characters at the end of the BMP are removed.</summary>
    /// <remarks>
    /// U+FFFE and U+FFFF sit just past <c>[#xE000-#xFFFD]</c>. U+FFFE is in particular the byte
    /// order mark read with the wrong endianness, which is exactly what a PDF's UTF-16
    /// <c>/ToUnicode</c> can produce.
    /// </remarks>
    [Theory]
    [InlineData('\uFFFE')]
    [InlineData('\uFFFF')]
    public void ABmpNonCharacterIsRemoved(char nonCharacter)
        => Assert.Equal("ab", XmlEscape.EscapeText("a" + nonCharacter + "b"));

    /// <summary>U+FFFD itself is legal and is kept.</summary>
    /// <remarks>
    /// The boundary immediately below the pair above. It is the replacement character every
    /// decoder in this tree substitutes for a byte it could not read, so deleting it would discard
    /// the one signal that something was lost.
    /// </remarks>
    [Fact]
    public void TheReplacementCharacterIsKept()
        => Assert.Equal("a\uFFFDb", XmlEscape.EscapeText("a\uFFFDb"));

    /// <summary>A string that is already representable is returned as-is.</summary>
    /// <remarks>
    /// That is every string a document holds but a handful, so the scan must not allocate for
    /// them. Asserting reference identity is the only way to see it: an equal copy passes a value
    /// comparison and still costs an allocation per escaped run on a page with thousands.
    /// </remarks>
    [Fact]
    public void ARepresentableStringIsNotCopied()
    {
        // No entity in it either, so the five Replace calls each hand back the same instance too.
        string input = "nothing here needs changing";

        Assert.Same(input, XmlEscape.EscapeText(input));
    }

    /// <summary>Only the offending characters go; their neighbours stay, entities included.</summary>
    /// <remarks>
    /// The copy path takes the index of the first offender and re-walks from there; an off-by-one
    /// drops a neighbouring character, which no parse assertion would notice.
    /// </remarks>
    [Fact]
    public void OnlyTheUnrepresentableCharactersAreRemoved()
        => Assert.Equal("start&amp;end",
                        XmlEscape.EscapeText("start\u0001&\u0002\u0003end"));

    /// <summary>Round-trips escaped text through a real XML parser.</summary>
    private static string Parse(string escaped)
        => XDocument.Parse("<r>" + escaped + "</r>").Root.Value;
}
