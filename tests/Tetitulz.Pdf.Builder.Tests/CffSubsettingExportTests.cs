// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// End-to-end proof that CFF charstring subsetting (plan 11 §5e slice 2) is <b>live in the export
/// path</b>, not merely present in Core.
///
/// <para><b>Why this file exists at all.</b> Wiring <c>CffSubsetter</c> into
/// <c>Core.Writing.Type0FontBuilder</c> broke no existing test in the solution — which is exactly
/// what a wiring that does nothing would also look like. The repository's own rule is that a
/// dispatch nobody asserts is indistinguishable from one that was removed, so the assertions here
/// read the embedded program back out of the produced PDF and say which charstrings survived.</para>
///
/// <para><b>The trap this file is written around: the PDF barely changes size.</b> Blanking
/// overwrites charstrings where they sit, so the uncompressed program is byte-for-byte as long as
/// the original and only Flate sees the win. <c>Assert.True(subsetPdf.Length &lt; wholePdf.Length)</c>
/// is a plausible-looking assertion that measures the wrong thing.</para>
/// </summary>
public class CffSubsettingExportTests
{
    /// <summary>
    /// The Cyrillic alphabet, upper and lower — 64 glyphs, so a provider font has plenty the
    /// document will not draw.
    ///
    /// <para><b>Not Latin, and that is load-bearing.</b> Builder only consults
    /// <see cref="PdfExportOptions.FontProvider"/> for text the standard-14 WinAnsi fonts cannot
    /// encode; a document of "abc" is drawn in Helvetica and embeds no program at all. Written with
    /// ASCII text, every test in this file failed on a null font program while the subsetter
    /// underneath was working perfectly — the fixture never reached the feature.</para>
    /// </summary>
    private static readonly string FullAlphabet =
        new string(Enumerable.Range(0x0410, 64).Select(c => (char)c).ToArray());

    /// <summary>Three characters of <see cref="FullAlphabet"/>, the only ones any document here draws.</summary>
    private static readonly string Drawn = FullAlphabet.Substring(0, 3);

    private static Document DocWith(string text)
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(new Paragraph { Runs = { new TextRun { Text = text } } });
        doc.Sections.Add(section);
        return doc;
    }

    private static PdfExportOptions CffProvider(string covered, int ceiling = 4 * 1024 * 1024) =>
        new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalOpenTypeCff.Build(covered, padTo: 120),
            MaxUnsubsettableFontBytes = ceiling,
        };

    // ================================================================
    //  The embedded program really is a subset
    // ================================================================

    /// <summary>
    /// The load-bearing test. A document drawing three letters from a 62-glyph CFF font must embed a
    /// program in which those three charstrings are intact and the other 59 are blanked.
    /// </summary>
    [Fact]
    public void ExportingWithACffFont_EmbedsAProgramWhoseUndrawnCharstringsAreBlanked()
    {
        byte[] pdf = DocWith(Drawn).ToPdf(CffProvider(FullAlphabet));

        byte[] program = PdfContentProbe.EmbeddedFontProgram(pdf, "FontFile3");
        Assert.NotNull(program);

        List<byte[]> charstrings = MinimalOpenTypeCff.ReadCharstrings(program!);
        int blanked = charstrings.Count(MinimalOpenTypeCff.IsBlanked);
        int intact = charstrings.Count - blanked;

        Assert.Equal(FullAlphabet.Length + 1, charstrings.Count);   // .notdef + 64

        // 'a', 'b', 'c' plus .notdef, whose own charstring in this fixture is a bare endchar and so
        // reads as blanked — hence the range rather than an exact 3.
        Assert.InRange(intact, 3, 4);
        Assert.True(blanked >= 61,
            $"only {blanked} of {charstrings.Count} charstrings were blanked; the document draws " +
            "three letters, so the rest are dead weight the subsetter was supposed to empty");
    }

    /// <summary>
    /// The glyphs the document <i>does</i> draw must come through byte for byte. A subsetter that
    /// blanked everything would satisfy the count assertion above and produce blank text.
    /// </summary>
    [Fact]
    public void TheDrawnGlyphsCharstringsAreUnchangedBySubsetting()
    {
        byte[] whole = MinimalOpenTypeCff.Build(FullAlphabet, padTo: 120);
        List<byte[]> original = MinimalOpenTypeCff.ReadCharstrings(whole);

        byte[] pdf = DocWith(Drawn).ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => whole,
        });

        byte[] embedded = PdfContentProbe.EmbeddedFontProgram(pdf, "FontFile3");
        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(embedded!);

        // GIDs follow the sorted, de-duplicated character order the fixture builds its cmap from.
        string sorted = new string(new SortedSet<char>(FullAlphabet).ToArray());
        foreach (char c in Drawn)
        {
            int gid = sorted.IndexOf(c) + 1;
            Assert.True(original[gid].SequenceEqual(after[gid]),
                $"the charstring for '{c}' (GID {gid}) was modified — the document draws it");
        }
    }

    /// <summary>
    /// ISO 32000-1 §9.6.4: a program holding only part of a font's glyphs <i>shall</i> be named
    /// <c>ABCDEF+</c>. This path wrote no tag at all before subsetting landed, because it was
    /// always embedding whole programs — so an untagged output now would be a lie about coverage.
    /// </summary>
    [Fact]
    public void ASubsettedCffFontCarriesTheSubsetTagOnEveryNameThatDeclaresIt()
    {
        byte[] pdf = DocWith(Drawn).ToPdf(CffProvider(FullAlphabet));

        List<string> baseFonts = PdfContentProbe.BaseFontNames(pdf);
        var tagged = baseFonts.Where(n => n.Length > 7 && n[6] == '+').ToList();
        Assert.NotEmpty(tagged);

        // §9.8.1 requires the descriptor's /FontName to equal the /BaseFont, and the Type0 and its
        // CIDFontType0 descendant both declare one — so all of them carry the tag or none does.
        string raw = PdfContentProbe.Raw(pdf);
        string tag = tagged[0].Substring(0, 7);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(raw, "/BaseFont\\s*/" + tag).Count);
        Assert.Contains("/FontName /" + tag, raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// The saving is real once the program is Flate-encoded, which is how it enters the file. This
    /// compares two complete exports of the same document, one whose font the subsetter can handle
    /// and one it must refuse, so the only difference is whether subsetting happened.
    /// </summary>
    [Fact]
    public void SubsettingMakesTheProducedPdfSubstantiallySmallerThanEmbeddingWhole()
    {
        byte[] cffFont = MinimalOpenTypeCff.Build(FullAlphabet, padTo: 400);

        byte[] subsetted = DocWith(Drawn).ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => cffFont,
        });

        // The same font with its CFF table relabelled CFF2: identical bytes, identical glyphs, but
        // the subsetter refuses it, so this export embeds the program whole. Any size difference
        // between the two files is therefore attributable to subsetting and to nothing else.
        byte[] refusedFont = (byte[])cffFont.Clone();
        DeclareCff2(refusedFont);
        byte[] whole = DocWith(Drawn).ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => refusedFont,
        });

        Assert.True(subsetted.Length * 2 < whole.Length,
            $"the subsetted export is {subsetted.Length} bytes against {whole.Length} embedding " +
            "whole; blanking 61 of 64 padded charstrings should compress away to far less than half");
    }

    // ================================================================
    //  The ceiling, which now means what its name says
    // ================================================================

    /// <summary>
    /// <c>MaxUnsubsettableFontBytes</c> is documented as the largest program to embed <b>when it
    /// cannot be subsetted</b>. A CFF font that can be subsetted must therefore be accepted however
    /// large it is — before this change the ceiling measured the whole program and the run fell
    /// back to a font that did not cover the text, which is worse output rather than a smaller file.
    /// </summary>
    [Fact]
    public void ACffFontOverTheCeilingIsNowEmbeddedBecauseItCanBeSubsetted()
    {
        var seen = new List<BuilderDiagnostic>();
        var options = CffProvider(FullAlphabet, ceiling: 64);   // far below the fixture's size
        options.OnDiagnostic = seen.Add;

        byte[] pdf = DocWith(Drawn).ToPdf(options);

        Assert.Contains("/FontFile3", PdfContentProbe.Raw(pdf));
        Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.FontFormatRejected);
    }

    /// <summary>
    /// The other half of that rule: a program the subsetter genuinely cannot handle is still
    /// refused at the ceiling, and the report names the reason rather than only the size.
    /// </summary>
    [Fact]
    public void ACff2FontOverTheCeilingIsStillRefusedAndTheReasonIsReported()
    {
        byte[] font = MinimalOpenTypeCff.Build(FullAlphabet, padTo: 120);
        DeclareCff2(font);

        var seen = new List<BuilderDiagnostic>();
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => font,
            MaxUnsubsettableFontBytes = 64,
            OnDiagnostic = seen.Add,
        };

        DocWith(Drawn).ToPdf(options);

        var rejected = seen.FirstOrDefault(d => d.Kind == BuilderDiagnosticKind.FontFormatRejected);
        Assert.NotNull(rejected);
        Assert.Contains("cannot be subsetted", rejected!.Detail, StringComparison.Ordinal);
        Assert.Contains("CFF2", rejected.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A glyf-outlined font must keep taking the TrueType path, which rebuilds the tables and really
    /// does shrink the program. Routing it through the CFF blanker would produce a valid but
    /// needlessly large font — and the two subsetters are chosen by outline format, not by container.
    /// </summary>
    [Fact]
    public void ATrueTypeFontStillTakesTheGlyfSubsetterAndItsFontFile2Path()
    {
        byte[] pdf = DocWith(Drawn).ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build(FullAlphabet),
        });

        string raw = PdfContentProbe.Raw(pdf);
        Assert.Contains("/FontFile2", raw);
        Assert.DoesNotContain("/FontFile3", raw);
    }

    /// <summary>
    /// Sets the <c>CFF </c> table's major version to 2, so the subsetter sees CFF2 and refuses.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as renaming the table to <c>CFF2</c>, which is what this tried first and
    /// why two tests here failed for the wrong reason.</b>
    /// <c>TrueTypeFontProgram.HasCff</c> tests for a table literally tagged <c>"CFF "</c>, so a
    /// renamed table makes the program read as glyf-outlined: Builder then takes the
    /// <c>/FontFile2</c> branch, never reaches the CFF ceiling, and never reports anything. Keeping
    /// the tag and changing the version byte keeps the whole route identical up to the one decision
    /// under test.
    /// </remarks>
    private static void DeclareCff2(byte[] sfnt)
    {
        int numTables = (sfnt[4] << 8) | sfnt[5];
        for (int i = 0; i < numTables; i++)
        {
            int at = 12 + i * 16;
            if (System.Text.Encoding.ASCII.GetString(sfnt, at, 4) != "CFF ") continue;
            int offset = (sfnt[at + 8] << 24) | (sfnt[at + 9] << 16) | (sfnt[at + 10] << 8) | sfnt[at + 11];
            sfnt[offset] = 2;
            return;
        }
        throw new InvalidOperationException("no \"CFF \" table");
    }
}
