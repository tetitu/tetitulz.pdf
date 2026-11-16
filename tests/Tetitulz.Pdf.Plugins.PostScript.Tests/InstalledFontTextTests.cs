// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>Registering a font directory changes process-global state: these tests run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FontSearchPathCollection
{
    public const string Name = "FontSearchPath";
}

/// <summary>
/// A driver's TrueType font that carries glyphs alone — the Microsoft PS Class Driver's Type 2
/// CIDFont under a made-up collection, with no <c>cmap</c>, no glyph names — given its text by the
/// installed font it was taken from (Core's <see cref="InstalledFontGlyphs"/>: the same <c>head</c>,
/// the same glyph bytes), so the PDF's text can be extracted. The installed font is written into a
/// directory registered on the search path.
/// </summary>
[Collection(FontSearchPathCollection.Name)]
public sealed class InstalledFontTextTests
{
    private const string Collection = "cpsFontConverter-Synthetic-Order";

    /// <summary>A Type 2 CIDFont of <paramref name="sfnts"/> under a collection no reader knows, composed with Identity-H; then <paramref name="after"/>.</summary>
    private static string DriverFont(byte[] sfnts, string extra, string after)
        => "20 dict begin /CIDFontName /DriverCID def /CIDFontType 2 def /FontType 42 def\n" +
           $"/CIDSystemInfo 3 dict dup begin /Registry (cpsFontConverter-Synthetic-Registry) def /Ordering ({Collection}) def /Supplement 1 def end def\n" +
           "/FontMatrix [1 0 0 1 0 0] def /FontBBox [0 0 1 1] def /PaintType 0 def\n" +
           "/Encoding StandardEncoding def /CharStrings 1 dict dup begin /.notdef 0 def end def\n" +
           "/CIDCount 3 def /GDBytes 2 def /CIDMap <000000010002> def\n" + extra +
           "/sfnts [" + Type42FontTests.Hex(sfnts) + "] def\n" +
           "/DriverCID currentdict end /CIDFont defineresource pop\n" +
           "/Driver /Identity-H [/DriverCID] composefont pop /Driver findfont 100 scalefont setfont\n" + after;

    // The driver's shape: no glyf, glyph 1 downloaded into /GlyphDirectory, then shown by its CID.
    private static string Downloaded(byte[] glyph)
        => DriverFont(Type42FontTests.WithoutGlyphs(), "/GlyphDirectory 4 dict def /Metrics 4 dict dup 1 [0 0.61] put def\n",
               "/DriverCID /CIDFont findresource /GlyphDirectory get 1 " + Type42FontTests.Hex(glyph) + " put\n0 0 moveto <0001> show");

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private static string ToUnicode(PostScriptConversionResult r)
    {
        var type0 = ((PdfDictionary)r.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>()
                        .Single(f => f[PdfName.Subtype] is PdfName { Value: "Type0" });
        return type0[PdfName.ToUnicode] is PdfStream stream ? Encoding.ASCII.GetString(stream.RawData) : "";
    }

    private static T Installed<T>(Func<T> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-ps-installed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "program.ttf"), Type42FontTests.Program);
        try
        {
            FontSearchPath.Add(dir);
            return test();
        }
        finally
        {
            FontSearchPath.Remove(dir);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// With the font installed, the downloaded glyph 1 is the installed font's glyph 1, 'A': the
    /// <c>/ToUnicode</c> maps it, and the conversion is clean.
    /// </summary>
    [Fact]
    public void ADownloadedGlyph_TakesItsTextFromTheInstalledFont()
    {
        var r = Installed(() => Convert(Downloaded(Type42FontTests.GlyphBytes(1))));
        Assert.Contains("<0001> <0041>", ToUnicode(r), StringComparison.Ordinal);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// Without it the glyph has no text: none is written, and that is said — the control for the
    /// case above.
    /// </summary>
    [Fact]
    public void WithoutTheInstalledFont_TheGlyphHasNoText_AndThatIsSaid()
    {
        var r = Convert(Downloaded(Type42FontTests.GlyphBytes(1)));
        Assert.DoesNotContain("<0001> <0041>", ToUnicode(r), StringComparison.Ordinal);
        Assert.Contains("stands for no known text", r.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A glyph whose bytes are not the installed glyph's — glyph 1 with its bounding box's xMin moved —
    /// gets no text, though the font is installed: the glyph number alone is no evidence.
    /// </summary>
    [Fact]
    public void AGlyphWithOtherBytes_GetsNoText()
    {
        byte[] other = Type42FontTests.GlyphBytes(1);
        other[3] ^= 1;
        var r = Installed(() => Convert(Downloaded(other)));
        Assert.DoesNotContain("<0001> <0041>", ToUnicode(r), StringComparison.Ordinal);
        Assert.Contains("stands for no known text", r.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A program that carries its own <c>glyf</c> is matched by the same rule: its glyph 1's bytes.</summary>
    [Fact]
    public void AProgramsOwnGlyph_TakesItsTextFromTheInstalledFont()
    {
        var r = Installed(() => Convert(DriverFont(Type42FontTests.Program, "", "0 0 moveto <0001> show")));
        Assert.Contains("<0001> <0041>", ToUnicode(r), StringComparison.Ordinal);
    }
}
