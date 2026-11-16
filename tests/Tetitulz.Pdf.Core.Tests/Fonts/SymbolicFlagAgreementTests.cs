// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// §9.6.6.4 asks one question of a simple font — <b>is it symbolic, and therefore which base
/// encoding applies when the dictionary names none?</b> — and both simple subtypes must answer it
/// the same way.
/// </summary>
/// <remarks>
/// <para><b>The decision was written twice and had drifted three ways.</b>
/// <c>PdfTrueTypeFont</c> tested Table 123's Symbolic bit (bit 3) alone and read <c>/Flags</c>
/// through an indirect reference; <c>PdfType1Font</c> tested "bit 3 set <i>and</i> bit 6 clear" and
/// read <c>/Flags</c> straight off the dictionary — under a comment claiming its sibling "makes the
/// same distinction". And where the <c>/Encoding</c> value is neither a name nor a dictionary, the
/// TrueType copy hardcoded WinAnsi while the Type 1 copy used the implicit base.</para>
///
/// <para><b>Why bit 3 alone is the rule both now apply.</b> Table 123 says Symbolic and Nonsymbolic
/// shall not both be set nor both be clear, so in any <i>conforming</i> file the extra clause is
/// dead code — the two forms can only differ for a descriptor that contradicts itself. The
/// two-clause form is therefore a policy about malformed input, not a spec rule, and nothing in
/// §9.6.6.4 makes such a policy per-subtype. Bit 3 also gates real glyph selection on the TrueType
/// side (<c>UsesBuiltInEncoding</c>), so unifying the other way would have changed what a symbol
/// font <i>draws</i> on malformed input, rather than only how its text extracts.</para>
///
/// <para><b>Extraction is the instrument</b>, as in <see cref="ImplicitBaseEncodingTests"/>: both
/// classes feed the same text extractor, and the code-to-character map is exactly what they
/// disagreed about. Byte <c>0x27</c> separates the two candidate bases — StandardEncoding maps it
/// to U+2019 RIGHT SINGLE QUOTATION MARK, WinAnsi leaves it at U+0027 APOSTROPHE — and <c>0x60</c>
/// separates them the same way (U+2018 against U+0060).</para>
/// </remarks>
public class SymbolicFlagAgreementTests
{
    /// <summary>Table 123 bit 3 alone: Symbolic, and (illegally) nothing about bit 6.</summary>
    private const string SymbolicOnly = "4";

    /// <summary>Table 123 bit 6 alone: Nonsymbolic. The conforming spelling.</summary>
    private const string NonsymbolicOnly = "32";

    /// <summary>
    /// Bits 3 <b>and</b> 6 together — which Table 123 forbids, and which is the only <c>/Flags</c>
    /// value the two subtypes' old tests could disagree about.
    /// </summary>
    private const string BothFlagsSet = "36";

    /// <summary>A reference to object 7, which holds the number 4 (Symbolic).</summary>
    private const string IndirectSymbolic = "7 0 R";

    // ------------------------------------------------------------------
    //  The divergence: a descriptor that sets both flags
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>The defect.</b> With both flags set, Type 1 answered "nonsymbolic" and TrueType answered
    /// "symbolic", so the same byte extracted as a different character depending only on the
    /// subtype the producer had written.
    /// </summary>
    [Theory]
    [InlineData(0x27)]
    [InlineData(0x60)]
    public void WithBothSymbolicAndNonsymbolicSet_TheTwoSubtypesAgree(int code)
        => Assert.Equal(Extract("TrueType", code, BothFlagsSet, null),
                        Extract("Type1", code, BothFlagsSet, null));

    /// <summary>
    /// <b>The value, not merely the agreement.</b> Bit 3 is set, so the font is symbolic and the
    /// extraction view is WinAnsi. Asserting the character stops the pair being satisfied by both
    /// sides moving to StandardEncoding instead.
    /// </summary>
    [Theory]
    [InlineData("Type1", 0x27, '\'')]
    [InlineData("Type1", 0x60, '`')]
    [InlineData("TrueType", 0x27, '\'')]
    [InlineData("TrueType", 0x60, '`')]
    public void WithBothSymbolicAndNonsymbolicSet_TheSymbolicBitWins(string subtype, int code, char expected)
        => Assert.Equal(expected.ToString(), Extract(subtype, code, BothFlagsSet, null));

    // ------------------------------------------------------------------
    //  The second drift: /Flags read non-resolving
    // ------------------------------------------------------------------

    /// <summary>
    /// A legal <c>/Flags 7 0 R</c> read as <b>absent</b> — i.e. 0, i.e. nonsymbolic — through
    /// <c>PdfType1Font</c>, while <c>PdfTrueTypeFont</c> resolved the same reference and answered
    /// the opposite.
    /// </summary>
    [Theory]
    [InlineData(0x27)]
    [InlineData(0x60)]
    public void WithAnIndirectFlagsEntry_TheTwoSubtypesAgree(int code)
        => Assert.Equal(Extract("TrueType", code, IndirectSymbolic, null),
                        Extract("Type1", code, IndirectSymbolic, null));

    /// <summary>And the resolved value is honoured, rather than both sides ignoring it.</summary>
    [Theory]
    [InlineData("Type1", 0x27, '\'')]
    [InlineData("TrueType", 0x27, '\'')]
    public void WithAnIndirectFlagsEntry_TheReferencedValueIsHonoured(string subtype, int code, char expected)
        => Assert.Equal(expected.ToString(), Extract(subtype, code, IndirectSymbolic, null));

    // ------------------------------------------------------------------
    //  The third drift: an /Encoding value that is neither a name nor a dictionary
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>/Encoding 42</c> is unusable, which is the same state as absent — so it must reach the
    /// same implicit base. The TrueType copy hardcoded WinAnsi in this branch, so a
    /// <b>nonsymbolic</b> font with an unusable <c>/Encoding</c> extracted differently through the
    /// two subtypes.
    /// </summary>
    [Theory]
    [InlineData(0x27)]
    [InlineData(0x60)]
    public void WithAnEncodingValueThatIsNeitherANameNorADictionary_TheTwoSubtypesAgree(int code)
        => Assert.Equal(Extract("TrueType", code, NonsymbolicOnly, " /Encoding 42"),
                        Extract("Type1", code, NonsymbolicOnly, " /Encoding 42"));

    /// <summary>
    /// And the base they agree on is the implicit one — StandardEncoding for a nonsymbolic font —
    /// not WinAnsi.
    /// </summary>
    [Theory]
    [InlineData("Type1", 0x27, '’')]
    [InlineData("TrueType", 0x27, '’')]
    [InlineData("Type1", 0x60, '‘')]
    [InlineData("TrueType", 0x60, '‘')]
    public void WithAnUnusableEncodingValue_TheImplicitBaseApplies(string subtype, int code, char expected)
        => Assert.Equal(expected.ToString(), Extract(subtype, code, NonsymbolicOnly, " /Encoding 42"));

    // ------------------------------------------------------------------
    //  The floors: the two cases that were already right must stay right
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>The floor against "always symbolic".</b> A conforming nonsymbolic descriptor keeps
    /// StandardEncoding on both sides — otherwise the fix would be "treat every font as symbolic",
    /// which satisfies every agreement assertion above and is wrong for the common case.
    /// </summary>
    [Theory]
    [InlineData("Type1", 0x27, '’')]
    [InlineData("TrueType", 0x27, '’')]
    [InlineData("Type1", 0x60, '‘')]
    [InlineData("TrueType", 0x60, '‘')]
    public void WithNonsymbolicFlags_BothSubtypesUseStandardEncoding(string subtype, int code, char expected)
        => Assert.Equal(expected.ToString(), Extract(subtype, code, NonsymbolicOnly, null));

    /// <summary>
    /// <b>The floor against "always StandardEncoding".</b> The conforming symbolic spelling — bit 3
    /// set, bit 6 clear — is the one value both old rules already agreed on, and it must still
    /// select the symbolic extraction view.
    /// </summary>
    [Theory]
    [InlineData("Type1", 0x27, '\'')]
    [InlineData("TrueType", 0x27, '\'')]
    public void WithTheConformingSymbolicFlags_BothSubtypesUseWinAnsi(string subtype, int code, char expected)
        => Assert.Equal(expected.ToString(), Extract(subtype, code, SymbolicOnly, null));

    /// <summary>
    /// <b>The floor against "the implicit base is all that matters".</b> An explicit
    /// <c>/WinAnsiEncoding</c> beats the flag in both directions, on both subtypes — the Symbolic
    /// bit decides only what happens when the dictionary names no encoding.
    /// </summary>
    [Theory]
    [InlineData("Type1", NonsymbolicOnly)]
    [InlineData("TrueType", NonsymbolicOnly)]
    [InlineData("Type1", BothFlagsSet)]
    [InlineData("TrueType", BothFlagsSet)]
    public void AnExplicitBaseEncodingBeatsTheSymbolicFlag(string subtype, string flags)
        => Assert.Equal("'", Extract(subtype, 0x27, flags, " /Encoding /WinAnsiEncoding"));

    // ------------------------------------------------------------------

    private static string Extract(string subtype, int code, string flags, string encodingEntry)
    {
        using var ms = new MemoryStream(Pdf(subtype, code, flags, encodingEntry));
        using var doc = PdfDocument.Open(ms);
        return PdfTextExtractor.ExtractText(doc.Pages[0]).Trim();
    }

    /// <summary>
    /// One page showing a single byte through a simple font of the given subtype, whose descriptor
    /// carries the given <c>/Flags</c> spelling. No <c>/ToUnicode</c>: it would override the
    /// encoding, which is the whole thing under test. The <c>/BaseFont</c> is not one of the
    /// standard 14, so no built-in-encoding special case applies.
    /// </summary>
    private static byte[] Pdf(string subtype, int code, string flags, string encodingEntry)
    {
        string content = $"BT /F0 12 Tf 50 700 Td <{code:X2}> Tj ET";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 800] /Contents 4 0 R "
                + "/Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /Font /Subtype /{subtype} /BaseFont /Custom "
                + $"/FirstChar 0 /LastChar 255 /FontDescriptor 6 0 R{encodingEntry} >>\nendobj\n",
            $"6 0 obj\n<< /Type /FontDescriptor /FontName /Custom /Flags {flags} /ItalicAngle 0 "
                + "/Ascent 700 /Descent -200 /CapHeight 700 /StemV 80 "
                + "/FontBBox [0 -200 1000 700] >>\nendobj\n",
            // The referent of /Flags 7 0 R: Symbolic. Written unconditionally so the two fixtures
            // differ only in the /Flags spelling.
            "7 0 obj\n4\nendobj\n",
        };
        return Build(objs);
    }

    private static byte[] Build(List<string> objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
