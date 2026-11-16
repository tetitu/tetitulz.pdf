// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A Type 3 font's PDF dictionary is rebuilt only when a glyph is added. Every <c>show</c> rebuilt
/// the whole font — every CharProc stream, the differences and their sort, the widths — so a page of
/// N shows over G glyphs cost N × G.
/// </summary>
public sealed class Type3FontBuildCostTests
{
    private const string Font =
        "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
        "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /square put Encoding 66 /bar put " +
        "/CharProcs 2 dict def " +
        "CharProcs /square { 750 0 0 0 750 750 setcachedevice 0 0 750 750 rectfill } put " +
        "CharProcs /bar { 500 0 0 0 100 750 setcachedevice 0 0 100 750 rectfill } put " +
        "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
        "/T3 exch definefont pop /T3 findfont 12 scalefont setfont ";

    [Fact]
    public void AdversarialManyShowsOfOneGlyph_BuildTheFontOnce()
    {
        string shows = string.Concat(Enumerable.Repeat("0 0 moveto (A) show ", 200));

        PdfResourceCollector.Type3FontBuilds = 0;
        var r = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(Font + shows + "0 0 moveto (B) show"));

        Assert.Equal(2L, PdfResourceCollector.Type3FontBuilds);   // A, then B added
        var fonts = (PdfDictionary)r.Resources[PdfName.Font];
        var t3 = (PdfDictionary)fonts.Single().Value;
        var procs = (PdfDictionary)t3[PdfName.CharProcs];
        Assert.Equal(2, procs.Count);                              // the glyph added later is in it
    }

    /// <summary>
    /// An embedded program's resource is keyed by the program, not its /FontName: two programs a
    /// document defines under one name were one resource, and the second's strings drew in the
    /// first's glyphs. The same program asked again is the same resource.
    /// </summary>
    [Fact]
    public void TwoEmbeddedProgramsUnderOneName_AreTwoResources()
    {
        var collector = new PdfResourceCollector();
        var first = new PsEmbeddedFont { FontName = "Shared", Program = new byte[] { 1, 2, 3 } };
        var second = new PsEmbeddedFont { FontName = "Shared", Program = new byte[] { 4, 5, 6 } };

        string a = collector.GetOrAddEmbeddedFont(first, null);
        string b = collector.GetOrAddEmbeddedFont(second, null);
        string again = collector.GetOrAddEmbeddedFont(first, null);

        Assert.NotEqual(a, b);
        Assert.Equal(a, again);
    }

    /// <summary>
    /// The font's /Resources hold every glyph's resources under distinct names. Each glyph was built
    /// with a collector of its own, naming from /F1 again, and the font kept the last glyph's alone:
    /// the first glyph's /F1 then drew in the second glyph's face.
    /// </summary>
    [Fact]
    public void TwoGlyphsDrawingInDifferentFaces_KeepBothFaces()
    {
        const string font =
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /a put Encoding 66 /b put " +
            "/CharProcs 2 dict def " +
            "CharProcs /a { 600 0 setcharwidth /Helvetica findfont 500 scalefont setfont 0 0 moveto (x) show } put " +
            "CharProcs /b { 600 0 setcharwidth /Courier findfont 500 scalefont setfont 0 0 moveto (y) show } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
            "/T3 exch definefont pop /T3 findfont 12 scalefont setfont 0 0 moveto (AB) show";

        var r = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(font));

        var pageFonts = (PdfDictionary)r.Resources[PdfName.Font];
        var t3 = (PdfDictionary)pageFonts.Single(kv => kv.Value is PdfDictionary d && d[PdfName.Subtype] is PdfName { Value: "Type3" }).Value;
        var glyphFonts = (PdfDictionary)((PdfDictionary)t3[PdfName.Resources])[PdfName.Font];
        var faces = glyphFonts.Select(kv => ((PdfName)((PdfDictionary)kv.Value)[PdfName.BaseFont]).Value).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Courier", "Helvetica" }, faces);
    }
}
