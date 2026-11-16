// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG exporter names the font a <c>&lt;text&gt;</c> run should be drawn in.
/// </summary>
/// <remarks>
/// <para><b>It named none at all.</b> Every <c>&lt;text&gt;</c> element carried a matrix, a fill
/// and a size, and no <c>font-family</c> — so a viewer rendered it in whatever its default was. The
/// comment beside that code said the viewer's "own matching font" would render the run, which was
/// true of the raster path and not of this one: there was nothing to match on. A Times document came
/// out in the viewer's sans-serif, with the wrong shapes and the wrong widths inside every run.</para>
///
/// <para><b>Only non-embedded fonts reach the <c>&lt;text&gt;</c> path</b> — an embedded program is
/// drawn as vector outlines, which are the actual glyph shapes — so this is exactly the population
/// that depends on the viewer having been told what to look for. Every fixture here therefore uses a
/// font with no <c>/FontFile</c>.</para>
///
/// <para>Assertions parse the SVG as an <see cref="XDocument"/>, never as a substring: an attribute
/// appearing somewhere in the file and an attribute on the element that draws the run are different
/// claims.</para>
/// </remarks>
public sealed class SvgFontNamingTests
{
    /// <summary>A standard-14 font names the system family that actually has its metrics.</summary>
    /// <remarks>
    /// <b>The metric-compatible family comes first, and that is the point.</b> A PDF that says
    /// <c>Helvetica</c> means Helvetica's metrics, which on nearly every desktop are Arial's;
    /// asking a browser for "Helvetica" gets a substitute of its own choosing, and on Windows that
    /// is not a metric match. The mapping is the one <c>Standard14Names.SystemFamily</c> already
    /// owns for the renderer, not a second opinion.
    /// </remarks>
    [Theory]
    [InlineData("Helvetica", "Arial", "sans-serif")]
    [InlineData("Times-Roman", "Times New Roman", "serif")]
    [InlineData("Courier", "Courier New", "monospace")]
    public void AStandard14Font_NamesItsSystemFamilyAndAGeneric(
        string baseFont, string expectedFamily, string expectedGeneric)
    {
        string family = FamilyOf(RenderText(baseFont, string.Empty));

        Assert.Contains(expectedFamily, family, StringComparison.Ordinal);
        Assert.Equal(expectedGeneric, GenericOf(family));
    }

    /// <summary>A generic family always ends the stack, so an uninstalled font still has a shape.</summary>
    /// <remarks>
    /// The descriptor's flags (§9.8.2 Table 123) are what a viewer can act on when it has neither
    /// the named font nor any idea what it looks like: bit 1 is fixed-pitch, bit 2 is serif.
    /// Without a generic tail the fallback is the viewer's default, which is what this whole file
    /// is about.
    /// </remarks>
    [Theory]
    [InlineData(1, "monospace")]    // FixedPitch
    [InlineData(2, "serif")]        // Serif
    [InlineData(32, "sans-serif")]  // Nonsymbolic, neither of the above
    public void TheDescriptorFlagsChooseTheGenericFamily(int flags, string expected)
    {
        string family = FamilyOf(RenderText(
            "MadeUpFace",
            $"/FontDescriptor << /Type /FontDescriptor /FontName /MadeUpFace /Flags {flags} >>"));

        Assert.Equal(expected, GenericOf(family));
    }

    /// <summary>The font's own family token is named, ahead of the generic.</summary>
    [Fact]
    public void ANonStandardFont_NamesItsOwnFamily()
    {
        string family = FamilyOf(RenderText("Georgia-Bold", string.Empty));

        Assert.Contains("Georgia", family, StringComparison.Ordinal);

        // The style half of the PostScript name is not part of the family — it is the weight, and
        // CSS wants it as one.
        Assert.DoesNotContain("Bold", family, StringComparison.Ordinal);
    }

    /// <summary>A subset tag is not part of the family name.</summary>
    /// <remarks>
    /// <c>ABCDEF+</c> (§9.6.4) identifies the subset, not the typeface. Leaving it on asks the
    /// viewer for a family nothing has, which silently costs the whole stack up to the generic.
    /// </remarks>
    [Fact]
    public void ASubsetTagIsStrippedFromTheFamily()
    {
        string family = FamilyOf(RenderText("ABCDEF+Georgia", string.Empty));

        Assert.DoesNotContain("ABCDEF", family, StringComparison.Ordinal);
        Assert.Contains("Georgia", family, StringComparison.Ordinal);
    }

    /// <summary>Weight and slant come out as CSS, from the name or from the descriptor.</summary>
    /// <remarks>
    /// <b>Both sources, because a producer supplies one or the other.</b> A standard-14 has no
    /// descriptor at all and its style is in its name; an embedded-subset descriptor often carries
    /// <c>/FontWeight</c> and <c>/ItalicAngle</c> while the name is a meaningless subset spelling.
    /// </remarks>
    [Theory]
    [InlineData("Helvetica-Bold", "", "700", null)]
    [InlineData("Helvetica-Oblique", "", null, "italic")]
    [InlineData("Helvetica-BoldOblique", "", "700", "italic")]
    [InlineData("Plain", "/FontDescriptor << /Type /FontDescriptor /FontName /Plain /FontWeight 700 >>",
                "700", null)]
    [InlineData("Plain", "/FontDescriptor << /Type /FontDescriptor /FontName /Plain /ItalicAngle -12 >>",
                null, "italic")]
    // CSS takes 100–900 here: a weight past the top is clamped, one below the bottom says nothing.
    [InlineData("Plain", "/FontDescriptor << /Type /FontDescriptor /FontName /Plain /FontWeight 950 >>",
                "900", null)]
    [InlineData("Plain", "/FontDescriptor << /Type /FontDescriptor /FontName /Plain /FontWeight 50 >>",
                null, null)]
    // ForceBold (Table 123 bit 19) makes a plain name bold.
    [InlineData("Plain", "/FontDescriptor << /Type /FontDescriptor /FontName /Plain /Flags 262144 >>",
                "700", null)]
    public void WeightAndSlantAreWrittenAsCss(
        string baseFont, string descriptor, string expectedWeight, string expectedStyle)
    {
        XElement text = TextElement(RenderText(baseFont, descriptor));

        Assert.Equal(expectedWeight, text.Attribute("font-weight")?.Value);
        Assert.Equal(expectedStyle, text.Attribute("font-style")?.Value);
    }

    /// <summary>An ordinary run carries neither attribute, so the common case costs one attribute.</summary>
    /// <remarks>
    /// The output is one element per text-showing operator; three attributes on every one of them
    /// is a measurable share of the file, and <c>font-weight="400" font-style="normal"</c> says
    /// exactly what CSS already assumes.
    /// </remarks>
    [Fact]
    public void ANormalRun_OmitsWeightAndStyle()
    {
        XElement text = TextElement(RenderText("Helvetica", string.Empty));

        Assert.Null(text.Attribute("font-weight"));
        Assert.Null(text.Attribute("font-style"));
        Assert.NotNull(text.Attribute("font-family"));
    }

    /// <summary>A generic family is written unquoted; a real family is quoted.</summary>
    /// <remarks>
    /// CSS reads a quoted <c>'serif'</c> as a family literally called "serif", which nobody has —
    /// so quoting the generic silently deletes the fallback that is the whole reason it is there.
    /// A family containing a space needs the opposite treatment.
    /// </remarks>
    [Fact]
    public void TheGenericIsUnquotedAndARealFamilyIsQuoted()
    {
        string family = FamilyOf(RenderText("Times-Roman", string.Empty));

        Assert.Equal("serif", GenericOf(family));
        Assert.DoesNotContain("'serif'", family, StringComparison.Ordinal);
        Assert.Contains("'Times New Roman'", family, StringComparison.Ordinal);
    }

    /// <summary>An indirect <c>/BaseFont</c> names the same family as a direct one.</summary>
    /// <remarks>
    /// The family comes from Core's <c>PdfFont.BaseFont</c>, which read the name raw, while the
    /// raster path's <c>FontCache</c> resolves it — so <c>/BaseFont 6 0 R</c> drew the page image
    /// in Times and gave the SVG no Times at all.
    /// </remarks>
    [Fact]
    public void AnIndirectBaseFont_NamesTheSameFamily()
    {
        string family = FamilyOf(RenderText("Times-Roman", string.Empty, indirectBaseFont: true));

        Assert.Contains("'Times New Roman'", family, StringComparison.Ordinal);
        Assert.Equal("serif", GenericOf(family));
    }

    /// <summary>
    /// A backslash in the font's name (<c>#5C</c>) does not reach the quoted CSS family, where it
    /// would escape the closing quote and void the whole declaration — generic fallback included.
    /// </summary>
    [Fact]
    public void ABackslashInTheNameDoesNotBreakTheQuotedFamily()
    {
        string family = FamilyOf(RenderText("Geo#5Crgia", string.Empty));

        Assert.DoesNotContain("\\", family, StringComparison.Ordinal);
        Assert.Contains("'Georgia'", family, StringComparison.Ordinal);
        Assert.Equal("sans-serif", GenericOf(family));   // the raw name says nothing serif
    }

    // ==================================================================== helpers

    /// <summary>The last entry of a family stack, which is the generic that ends it.</summary>
    /// <remarks>
    /// <b>A suffix match cannot tell <c>serif</c> from <c>sans-serif</c>.</b> Written as
    /// <c>EndsWith("serif")</c> this file passed with the descriptor's serif flag deleted, because
    /// the fallthrough answer <c>sans-serif</c> ends in the string being asserted. Comparing the
    /// final comma-separated entry is the same claim without the overlap.
    /// </remarks>
    private static string GenericOf(string family)
    {
        string[] parts = family.Split(new[] { ", " }, StringSplitOptions.None);
        return parts[parts.Length - 1];
    }

    /// <summary>The <c>font-family</c> of the one text element the fixture draws.</summary>
    private static string FamilyOf(string svg)
    {
        string family = TextElement(svg).Attribute("font-family")?.Value;
        Assert.False(string.IsNullOrEmpty(family),
            "the <text> element carries no font-family, so the viewer picks its own default");
        return family!;
    }

    private static XElement TextElement(string svg)
    {
        XElement[] texts = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "text")
            .ToArray();

        Assert.True(texts.Length > 0, "the export drew no <text> element:" + Environment.NewLine + svg);
        return texts[0];
    }

    /// <summary>
    /// One page drawing one run in a font with no <c>/FontFile</c>, so it takes the
    /// <c>&lt;text&gt;</c> path on every platform.
    /// </summary>
    private static string RenderText(string baseFont, string descriptor, bool indirectBaseFont = false)
    {
        string content = "BT /F1 24 Tf 72 700 Td (Sample) Tj ET";

        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont " + (indirectBaseFont ? "6 0 R" : "/" + baseFont)
            + " " + descriptor + " >>",
            "/" + baseFont);

        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
