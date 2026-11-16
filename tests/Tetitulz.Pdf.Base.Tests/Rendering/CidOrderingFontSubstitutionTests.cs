// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A non-embedded CID font's <c>/CIDSystemInfo /Ordering</c> selects the substitute face, so a
/// document whose <c>/BaseFont</c> name says nothing about the script still gets a face that has
/// the glyphs.
/// </summary>
/// <remarks>
/// <para><b>What was actually missing.</b> GAPS §11g said the ordering "never selects a substitute
/// face", which overstated it: <c>FontCache.ResolveCjkSystemFont</c> has existed for a while, with
/// per-script candidate lists and a U+4E00 verification. It was reached only through
/// <c>LooksLikeCjkFont</c>, which matches <b>substrings of the font name</b> — so a CID font with a
/// subset tag or an opaque name missed the CJK path entirely and fell into the Western
/// name-derivation heuristics, which return a DirectWrite substitute with no CJK glyphs at all.
/// And once inside, the script was guessed from that same name, defaulting to Japanese.</para>
///
/// <para><b>Traditional Chinese had no candidates whatsoever.</b> Every entry in the Chinese list
/// was a Simplified face, so <c>CNS1</c> could only ever land on a Simplified or Japanese font.
/// That is wrong in the quiet direction — a Simplified face renders Traditional text with the wrong
/// glyph shapes rather than with blanks, so nothing looks broken.</para>
///
/// <para><b>These are <see cref="SystemFontFactAttribute"/>-gated because the answer is a real
/// installed face.</b> A test that accepted any non-null typeface would pass everywhere and prove
/// nothing: <c>SKTypeface.FromFamilyName</c> answers Segoe UI on Windows and DejaVu Sans on Linux
/// for a family it does not have, so the assertion has to be on the family that came back.</para>
/// </remarks>
public sealed class CidOrderingFontSubstitutionTests
{
    /// <summary>
    /// The mapping itself: only the four Adobe CJK registries name a script.
    /// </summary>
    /// <remarks>
    /// <c>Identity</c> is the row that carries the risk. It declares that the CIDs <i>are</i> glyph
    /// indices and says nothing about the script, so treating it as CJK would push every
    /// Identity-encoded Latin CID font — the overwhelmingly common case — down the CJK substitution
    /// path.
    /// </remarks>
    [Theory]
    [InlineData("Japan1", "ja")]
    [InlineData("GB1", "zh-Hans")]
    [InlineData("CNS1", "zh-Hant")]
    [InlineData("Korea1", "ko")]
    [InlineData("Identity", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyTheFourCjkOrderingsNameAScript(string ordering, string expected)
        => Assert.Equal(expected, FontCache.CjkScriptFromOrdering(ordering));

    /// <summary>
    /// <c>/Ordering (CNS1)</c> resolves to an installed <b>Traditional</b> Chinese face, even
    /// though the font's name says nothing.
    /// </summary>
    /// <remarks>
    /// This is the sharpest of the four: before the ordering was consulted there were no
    /// Traditional candidates in the tree at all, so this case could not have been satisfied by
    /// accident. Microsoft JhengHei is the Windows Traditional UI face.
    /// </remarks>
    [SystemFontFact("Microsoft JhengHei")]
    public void TraditionalChineseOrderingResolvesToATraditionalFace()
    {
        using SKTypeface face = ResolveOpaqueCidFont("CNS1");

        Assert.Equal("Microsoft JhengHei", face.FamilyName);
    }

    /// <summary>
    /// <c>/Ordering (Korea1)</c> resolves to an installed Korean face.
    /// </summary>
    /// <remarks>
    /// The second script proves the ordering <i>selects</i> rather than merely enabling the CJK
    /// path: a single-script assertion would also pass against an implementation that always
    /// answered Japanese.
    /// </remarks>
    [SystemFontFact("Malgun Gothic")]
    public void KoreanOrderingResolvesToAKoreanFace()
    {
        using SKTypeface face = ResolveOpaqueCidFont("Korea1");

        Assert.Equal("Malgun Gothic", face.FamilyName);
    }

    /// <summary>
    /// An <c>/Ordering</c> written as its own object still selects the face.
    /// </summary>
    /// <remarks>
    /// Read raw, <c>/Ordering 8 0 R</c> was neither a string nor a name, so the font lost its only
    /// script signal and fell to the Western heuristics — the defect the ordering path exists to
    /// prevent, restored by a legal spelling (§7.3.10).
    /// </remarks>
    [SystemFontFact("Malgun Gothic")]
    public void AnIndirectOrderingStillSelectsTheFace()
    {
        using SKTypeface face = ResolveOpaqueCidFont("Korea1", indirectOrdering: true);

        Assert.Equal("Malgun Gothic", face.FamilyName);
    }

    /// <summary>
    /// The control: the identical font with <c>/Ordering (Identity)</c> does <b>not</b> take the
    /// CJK path.
    /// </summary>
    /// <remarks>
    /// Without this, both assertions above would be satisfied by an implementation that sent every
    /// non-embedded CID font to a CJK face — which would be a regression for every Identity-encoded
    /// Latin font, i.e. most of them. Asserted as "the face has no CJK glyphs" rather than by name,
    /// because which Western face the heuristics land on is a platform detail.
    /// </remarks>
    [SystemFontFact("Microsoft JhengHei")]
    public void AnIdentityOrderingDoesNotGetACjkSubstitute()
    {
        using SKTypeface face = ResolveOpaqueCidFont("Identity");

        Assert.Equal(0, GlyphIdResolver.Glyph(face, 0x4E00));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves the substitute for a non-embedded Type0 font whose <c>/BaseFont</c> deliberately
    /// carries no script hint, so the ordering is the only signal available.
    /// </summary>
    private static SKTypeface ResolveOpaqueCidFont(string ordering, bool indirectOrdering = false)
    {
        byte[] pdf = CidFontPdf(ordering, indirectOrdering);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        using var cache = new FontCache();

        var page = doc.Pages[0];
        var resources = page.Resources;
        Assert.NotNull(resources);

        SKTypeface face = cache.GetTypeface("F1", resources, doc);
        Assert.NotNull(face);

        // The cache disposes every typeface it hands out, so the family name is read here, inside
        // the scope, and the caller gets a face it owns.
        return SKTypeface.FromFamilyName(face.FamilyName);
    }

    /// <summary>
    /// One page, one Type0 resource with no <c>/FontFile</c> and an opaque <c>/BaseFont</c>.
    /// </summary>
    private static byte[] CidFontPdf(string ordering, bool indirectOrdering = false)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        const string cs = "BT /F1 24 Tf 72 700 Td <0041> Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        // /BaseFont carries a subset tag and a name with no script substring, which is exactly the
        // shape LooksLikeCjkFont cannot classify.
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+XQ0000"
                  + " /Encoding /Identity-H /DescendantFonts [6 0 R] >>\nendobj\n");

        int o6 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "6 0 obj\n<< /Type /Font /Subtype /CIDFontType0 /BaseFont /ABCDEF+XQ0000"
            + " /CIDSystemInfo << /Registry (Adobe) /Ordering {0} /Supplement 0 >>"
            + " /FontDescriptor 7 0 R /DW 1000 >>\nendobj\n", indirectOrdering ? "8 0 R" : "(" + ordering + ")");

        // No /FontFile* entry: this is the non-embedded case the substitution exists for.
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /FontDescriptor /FontName /ABCDEF+XQ0000 /Flags 4"
                  + " /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 900 /Descent -200"
                  + " /CapHeight 700 /StemV 80 >>\nendobj\n");

        // Object 8: the ordering string, used only when the font refers to it.
        int o8 = sb.Length;
        sb.Append("8 0 obj\n(").Append(ordering).Append(")\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5, o6, o7, o8 })
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
