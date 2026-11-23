// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <c>VisualSignatureBuilder.Sign(…, SignatureAppearanceOptions, …)</c> — a visible signature that
/// draws <b>text</b>.
/// </summary>
/// <remarks>
/// <para><b>The gap this closes had two halves and only one of them was a missing feature.</b>
/// `VisualSignatureBuilder` emitted `q W 0 0 H 0 0 cm /SigImg Do Q` and nothing else, so a caller
/// signing through `PdfSigner` had to rasterise the block themselves — while
/// `SignatureAppearanceGenerator`, which draws signer name, date, reason, location and composes
/// Hebrew and Arabic through `TextRunComposer`, had <b>no production caller at all</b>. The two
/// could not meet: `Signing → Base` would put SkiaSharp in every Signing consumer's graph and
/// `Base → Signing` would put BouncyCastle in every Base consumer's. Moving the generator down into
/// Core is what let them.</para>
///
/// <para>So these tests are as much about the objects <i>landing in the file</i> as about the
/// content stream. A form XObject that names a font nobody wrote is a perfectly valid stream that
/// draws nothing, and reading the content alone cannot tell the two apart.</para>
/// </remarks>
public sealed class VisualSignatureTextAppearanceTests
{
    private const string Hebrew = "שלום";
    private const string FieldPrefix = "VisualSig";

    /// <summary>The claim: the signer's name is drawn, as text, in the widget's appearance.</summary>
    [Fact]
    public void TheSignerNameIsDrawnInTheAppearance()
    {
        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace",
            SigningDate = "2026-09-03",
            Width = 200,
            Height = 60,
        });

        Assert.Contains("(Ada Lovelace) Tj", AppearanceContent(signed), StringComparison.Ordinal);
    }

    /// <summary>
    /// The control that stops the test above from passing for the wrong reason: the <b>image</b>
    /// overload, same document, draws the image — through the inner form its appearance names —
    /// and no text at all. Without it, "the appearance contains text" could be true of a builder
    /// that ignored the options entirely and happened to leave the signer name somewhere in the file.
    /// </summary>
    [Fact]
    public void TheImageOverloadDrawsOnlyTheImage_ThroughItsInnerForm()
    {
        byte[] signed = SignWithImage(Jpeg());

        string content = AppearanceContent(signed);
        Assert.Equal("q /SigForm Do Q", content);

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var inner = InnerForm(doc);
        string drawn = Encoding.Latin1.GetString(inner.GetDecodedData());
        Assert.Equal("q 200.00 0 0 60.00 0 0 cm /SigImg Do Q", drawn);
        Assert.DoesNotContain("Tj", drawn, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reason and location are drawn too — the three text rows are separate emissions, so asserting
    /// only the name would leave two of them free to disappear.
    /// </summary>
    [Fact]
    public void ReasonAndLocationAreDrawnAsWell()
    {
        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace",
            Reason = "Approved",
            Location = "Tel Aviv",
            Width = 240,
            Height = 80,
        });

        string content = AppearanceContent(signed);
        Assert.Contains("Approved", content, StringComparison.Ordinal);
        Assert.Contains("Tel Aviv", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Every text row lands inside the form's <c>/BBox</c>.</b> Emitting the text is not drawing
    /// it: a row positioned outside the box is clipped away by §8.10.2 and never appears.
    /// </summary>
    /// <remarks>
    /// <para>Written because the generator positioned each row with <c>Td</c>, which §9.4.2 defines
    /// as an offset from the start of the <i>current</i> line rather than a position. The offsets
    /// compounded, so row 2 landed at (2·textX, y1+y2) and rows 2–4 sat above a 60 pt box — the
    /// date, the reason and the location were written into the content stream and clipped, leaving
    /// only the signer's name visible.</para>
    ///
    /// <para><b>Nothing in this file could see it</b>, and that is the lesson: every other
    /// assertion here checks that a row's text <i>is in the stream</i>, which it was. Only a check
    /// on where the rows are placed separates "emitted" from "drawn".</para>
    /// </remarks>
    [Fact]
    public void EveryTextRowIsPositionedInsideTheAppearanceBox()
    {
        const double width = 240, height = 80;

        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace",
            SigningDate = "2026-09-03",
            Reason = "Approved",
            Location = "Tel Aviv",
            Width = width,
            Height = height,
        });

        string content = AppearanceContent(signed);

        // Absolute text-matrix placements: "1 0 0 1 x y Tm".
        var placements = Regex.Matches(content, @"1 0 0 1 (-?[\d.]+) (-?[\d.]+) Tm");
        Assert.Equal(4, placements.Count);

        foreach (Match m in placements)
        {
            double x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

            Assert.InRange(x, 0, width);
            Assert.InRange(y, 0, height);
        }

        // …and they descend, one row under the next, rather than stacking on one line.
        var ys = placements.Cast<Match>()
                           .Select(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                           .ToList();
        for (int i = 1; i < ys.Count; i++)
            Assert.True(ys[i] < ys[i - 1], $"row {i} is not below row {i - 1} ({ys[i]} vs {ys[i - 1]})");
    }

    /// <summary>
    /// <b>The font the appearance names is actually in the file.</b> The generator allocates its own
    /// object numbers and hands back side objects for the caller to write; dropping them leaves a
    /// content stream that still says <c>/F1 … Tf</c> and a <c>/Resources</c> that still names
    /// <c>/F1</c>, pointing at an object that was never written — which every reader draws as
    /// nothing and no assertion about the content stream can see.
    /// </summary>
    [Fact]
    public void TheAppearancesFontResolvesInTheOutput()
    {
        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace", Width = 200, Height = 60,
        });

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var appearance = Appearance(doc);
        var resources = doc.ResolveObject(appearance[PdfName.Resources]) as PdfDictionary;
        var fonts = doc.ResolveObject(resources?[PdfName.Font]) as PdfDictionary;

        Assert.NotNull(fonts);
        Assert.NotEmpty(fonts!);

        foreach (var entry in fonts!)
        {
            var font = doc.ResolveObject(entry.Value) as PdfDictionary;
            Assert.True(font != null,
                $"the appearance names /{entry.Key.Value} but it resolves to nothing in the output");
            Assert.Equal("Font", (font![PdfName.Type] as PdfName)?.Value);
        }
    }

    // ------------------------------------------------------------------
    //  Hebrew — the reason the generator exists
    // ------------------------------------------------------------------

    /// <summary>
    /// Hebrew is drawn as an embedded composite font, not as <c>?</c>. A synthetic font covers
    /// exactly these characters, so this asserts the pipeline rather than the host's font list.
    /// </summary>
    [Fact]
    public void AHebrewSignerName_IsDrawnThroughAnEmbeddedCompositeFont()
    {
        byte[] font = MinimalTrueType.Build(Hebrew);

        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = Hebrew,
            FontProvider = _ => font,
            Width = 200,
            Height = 60,
        });

        // Composite text is shown as a hex string of 4-digit glyph ids — one per character, and
        // never the Latin-1 literal the old code emitted.
        string content = AppearanceContent(signed);
        int open = content.IndexOf('<');
        int close = open < 0 ? -1 : content.IndexOf('>', open + 1);
        Assert.True(open >= 0 && close > open, "no hex glyph run in: " + content);
        Assert.Equal(Hebrew.Length, (close - open - 1) / 4);

        // …and the program travelled with it, or the glyph ids name nothing.
        Assert.True(HasEmbeddedFontProgram(signed),
            "the appearance uses a composite font but no /FontFile2 or /FontFile3 reached the output");
    }

    /// <summary>
    /// <b>The opt-in the refusal names is reachable from this path too.</b>
    /// <c>SignatureAppearanceOptions.AllowRestrictedSystemFonts</c> lets a deployment that owns its
    /// installed fonts use one whose <c>fsType</c> forbids embedding.
    /// </summary>
    /// <remarks>
    /// The generator's own <c>error</c> string ends "set
    /// <c>FontResolutionRequest.AllowRestrictedSystemFonts</c>…", which until 2026-09-04 named a
    /// property no signature caller could set — the remedy was unactionable from the one API that
    /// reports it. Asserted through <b>drawn glyphs</b>, because a signature whose block is present
    /// but empty is exactly what a still-refusing gate would leave behind.
    /// </remarks>
    [Fact]
    public void AllowRestrictedSystemFonts_ReachesTheSignatureAppearanceGate()
    {
        byte[] restricted = MinimalTrueType.BuildWithFsType(Hebrew, 0x0002);
        int next = 10;

        // Supplied through the provider deliberately: the flag governs the SYSTEM search, so this
        // asserts the property exists, compiles and travels — the host-font arm is covered by
        // Base.Tests' AppearanceFontLicenceTests, which can control the machine's font directory.
        var (ap, _) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions
            {
                SignerName = Hebrew,
                FontProvider = _ => restricted,
                AllowRestrictedSystemFonts = true,
            },
            ref next, out string error);

        Assert.Null(error);

        // The composite run is a hex string of 4-digit glyph ids, one per character — the same
        // shape AHebrewSignerName_IsDrawnThroughAnEmbeddedCompositeFont asserts.
        string content = Encoding.Latin1.GetString(ap.GetDecodedData());
        int open = content.IndexOf('<');
        int close = open < 0 ? -1 : content.IndexOf('>', open + 1);
        Assert.True(open >= 0 && close > open, "no hex glyph run in: " + content);
        Assert.Equal(Hebrew.Length, (close - open - 1) / 4);
    }

    /// <summary>
    /// <b>Refused, not drawn wrong — and nothing is signed.</b> Hebrew with no font source cannot be
    /// drawn, and a signature whose visible block is blank or shows the wrong text is worse than a
    /// refusal, because the block is the half a human reads.
    /// </summary>
    [Fact]
    public void HebrewWithNoFontSource_IsRefusedAndNothingIsSigned()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => Sign(
            new SignatureAppearanceOptions { SignerName = Hebrew, Width = 200, Height = 60 },
            into: output));

        Assert.Contains("appearance", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    // ------------------------------------------------------------------
    //  Logos
    // ------------------------------------------------------------------

    /// <summary>
    /// A PNG logo keeps its transparency: the image XObject gets a real <c>/SMask</c> whose samples
    /// are the alpha plane.
    /// </summary>
    /// <remarks>
    /// <para>This is the half of Phase 14 item 4 that the text work left open. <c>LogoJpegBytes</c>
    /// goes through as <c>/DCTDecode</c> — three opaque channels, nowhere to put an alpha — so a
    /// corporate mark with a transparent background had to be pre-flattened against whatever colour
    /// the caller guessed the block would be, and came out as a coloured rectangle wherever that
    /// guess was wrong.</para>
    ///
    /// <para><b>Asserts the mask's samples, not its presence.</b> An <c>/SMask</c> that exists and
    /// is uniformly opaque is exactly what a decoder that dropped the alpha would leave behind, and
    /// it is indistinguishable from a correct one by any structural check.</para>
    /// </remarks>
    [Fact]
    public void APngLogo_KeepsItsTransparencyAsASoftMask()
    {
        // Left half transparent, right half opaque — a mask that is uniformly anything fails.
        byte[] png = PngWithAlpha(4, 2, x => x < 2 ? (byte)0 : (byte)255);

        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace", LogoPngBytes = png, Width = 200, Height = 60,
        });

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var image = LogoXObject(doc);
        var mask = doc.ResolveObject(image[PdfName.SMask]) as PdfStream;
        Assert.True(mask != null, "the PNG logo was embedded without an /SMask, so its alpha was lost");

        Assert.Equal("DeviceGray", (doc.ResolveObject(mask![PdfName.ColorSpace]) as PdfName)?.Value);

        byte[] alpha = mask.GetDecodedData();
        Assert.Equal(8, alpha.Length);
        for (int y = 0; y < 2; y++)
        {
            Assert.Equal(0, alpha[y * 4 + 0]);
            Assert.Equal(0, alpha[y * 4 + 1]);
            Assert.Equal(255, alpha[y * 4 + 2]);
            Assert.Equal(255, alpha[y * 4 + 3]);
        }
    }

    /// <summary>
    /// An opaque PNG gets <b>no</b> <c>/SMask</c> — the decoder reports no alpha plane for a colour
    /// type that cannot carry one, and writing an all-255 mask would cost a second image for nothing.
    /// </summary>
    [Fact]
    public void AnOpaquePngLogo_GetsNoSoftMask()
    {
        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace", LogoPngBytes = OpaquePng(4, 2), Width = 200, Height = 60,
        });

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        Assert.Null(LogoXObject(doc)[PdfName.SMask]);
    }

    /// <summary>
    /// A PNG that will not decode leaves the signature alone: the text still draws and the document
    /// is still signed. Refusing to sign because a decoration would not parse is the wrong trade.
    /// </summary>
    /// <remarks>
    /// <b>Both halves of the name are asserted.</b> "Rather than failing the signature" is the
    /// second half and it is the easy one — the first half, <i>dropped</i>, is what separates this
    /// from an implementation that embedded the unparseable bytes as an image XObject and drew
    /// them: that produces a signed document with a text row in it too, so a test that checked only
    /// the text would pass over the exact defect it is named for.
    /// </remarks>
    [Fact]
    public void AnUndecodablePngLogo_IsDroppedRatherThanFailingTheSignature()
    {
        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace",
            LogoPngBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 9, 9, 9 },
            Width = 200,
            Height = 60,
        });

        string content = AppearanceContent(signed);
        Assert.Contains("(Ada Lovelace) Tj", content, StringComparison.Ordinal);

        // Dropped: nothing draws it, and no image resource exists for it to draw.
        Assert.DoesNotContain(" Do", content, StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var resources = doc.ResolveObject(Appearance(doc)[PdfName.Resources]) as PdfDictionary;
        Assert.Null(resources?[PdfName.XObject]);
    }

    /// <summary>
    /// <b>A JPEG logo is described by the colour space its frame header declares</b>, not by an
    /// assumption.
    /// </summary>
    /// <remarks>
    /// <para><c>/DCTDecode</c> passes the caller's bytes through untouched, so the image dictionary
    /// is the <i>only</i> thing telling a reader how to interpret them. The generator wrote
    /// <c>/ColorSpace /DeviceRGB</c> over whatever arrived without ever reading the header: a
    /// 1-component grayscale logo — a scanned ink signature, the common case for this feature — had
    /// its samples read as RGB triples and rendered as noise, and a CMYK logo failed the same way.
    /// <c>VisualSignatureBuilder</c>'s image-only path had already fixed exactly this on its own
    /// side; making the generator reachable from production made the defect live again on the
    /// other.</para>
    ///
    /// <para>The dimensions are asserted alongside, and the options deliberately carry <i>wrong</i>
    /// ones: <c>/Width</c> and <c>/Height</c> that disagree with the frame describe a corrupt image
    /// just as surely as a wrong <c>/ColorSpace</c> does, and a caller-supplied number is the one
    /// input that can disagree with the samples.</para>
    /// </remarks>
    [Theory]
    [InlineData(1, "DeviceGray")]
    [InlineData(3, "DeviceRGB")]
    public void AJpegLogo_IsDescribedWithTheColourSpaceItsFrameHeaderDeclares(
        int components, string expectedColorSpace)
    {
        byte[] jpeg = Jpeg(components, width: 40, height: 20);

        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace",
            LogoJpegBytes = jpeg,
            LogoWidthPx = 7, LogoHeightPx = 3,   // wrong on purpose — the header is the authority
            Width = 200, Height = 60,
        });

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var logo = LogoXObject(doc);

        Assert.Equal(expectedColorSpace, (doc.ResolveObject(logo[PdfName.ColorSpace]) as PdfName)?.Value);
        Assert.Equal("DCTDecode", (doc.ResolveObject(logo[PdfName.Filter]) as PdfName)?.Value);
        Assert.Equal(40, (doc.ResolveObject(logo[PdfName.Width]) as PdfNumber)?.IntValue);
        Assert.Equal(20, (doc.ResolveObject(logo[PdfName.Height]) as PdfNumber)?.IntValue);

        // …and the bytes the dictionary describes are the caller's, unaltered.
        Assert.Equal(jpeg, ((PdfStream)logo).RawData);
    }

    /// <summary>
    /// A four-component JPEG is CMYK only under Adobe's APP14 marker, and Adobe's CMYK samples are
    /// inverted: the logo and the image-only stamp alike are written as <c>/DeviceCMYK</c> with the
    /// inverting <c>/Decode</c> (the one rule, <c>Core.Imaging.JpegColorSpace</c>). Without it the
    /// stamp rendered as a negative.
    /// </summary>
    [Fact]
    public void AnAdobeCmykJpeg_IsDescribedAsCmykWithTheInvertingDecode()
    {
        byte[] jpeg = AdobeCmykJpeg(width: 40, height: 20);

        using var logoDoc = PdfDocument.Open(new MemoryStream(Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace", LogoJpegBytes = jpeg, Width = 200, Height = 60,
        })));
        AssertCmykWithDecode(logoDoc, LogoXObject(logoDoc));

        using var stampDoc = PdfDocument.Open(new MemoryStream(SignWithImage(jpeg)));
        AssertCmykWithDecode(stampDoc, LogoXObject(stampDoc));
    }

    private static void AssertCmykWithDecode(PdfDocument doc, PdfDictionary image)
    {
        Assert.Equal("DeviceCMYK", (doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName)?.Value);
        var decode = Assert.IsType<PdfArray>(doc.ResolveObject(image[PdfName.Decode]));
        Assert.Equal(new double[] { 1, 0, 1, 0, 1, 0, 1, 0 },
            Enumerable.Range(0, decode.Count).Select(i => ((PdfNumber)doc.ResolveObject(decode[i])).DoubleValue));
    }

    /// <summary>
    /// A four-component JPEG without Adobe's marker has no identifiable colour space, so it is
    /// refused on both paths rather than embedded as a guess.
    /// </summary>
    [Fact]
    public void AFourComponentJpegWithoutAdobesMarker_IsRefused()
    {
        byte[] jpeg = Jpeg(components: 4, width: 40, height: 20);

        var logo = Assert.Throws<PdfException>(() => Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace", LogoJpegBytes = jpeg, Width = 200, Height = 60,
        }));
        Assert.Contains("Adobe", logo.Message, StringComparison.Ordinal);

        var stamp = Assert.Throws<PdfException>(() => SignWithImage(jpeg));
        Assert.Contains("Adobe", stamp.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Bytes that are not a readable JPEG are refused, naming the logo</b> — never written into
    /// a <c>/DCTDecode</c> stream.
    /// </summary>
    /// <remarks>
    /// <para>Embedding them costs nothing at write time and produces a file whose image no reader
    /// can decode, with no error anywhere in the pipeline. Unlike <c>LogoPngBytes</c>, which is
    /// dropped when it will not decode, <c>/DCTDecode</c> means the caller has asserted the byte
    /// format, so the honest answer is to tell them it is wrong.</para>
    ///
    /// <para><b>The reason is asserted, not just the refusal.</b> This generator refuses for a
    /// second reason entirely — text that needs a composite font with no font source — and both
    /// arrive as the same <c>PdfException</c> from the same call. The fixture's signer name is
    /// plain ASCII precisely so the font guard cannot fire, and the message assertion is what
    /// stops the test passing if it ever did.</para>
    /// </remarks>
    [Fact]
    public void ALogoThatIsNotAJpeg_IsRefusedRatherThanEmbeddedAsDctDecode()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => Sign(
            new SignatureAppearanceOptions
            {
                SignerName = "Ada Lovelace",
                LogoJpegBytes = Encoding.ASCII.GetBytes("GIF89a-not-a-jpeg-at-all"),
                Width = 200, Height = 60,
            },
            into: output));

        Assert.Contains("LogoJpegBytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a readable JPEG", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// A JPEG whose frame declares a component count no PDF device space describes is refused with
    /// that count, rather than rounded to the nearest space that exists.
    /// </summary>
    [Fact]
    public void AJpegLogoWithAnUndescribableComponentCount_IsRefusedWithTheCount()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => Sign(
            new SignatureAppearanceOptions
            {
                SignerName = "Ada Lovelace",
                LogoJpegBytes = Jpeg(components: 2, width: 40, height: 20),
                Width = 200, Height = 60,
            },
            into: output));

        Assert.Contains("2 components", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// §7.5.5 — every trailer's <c>/Size</c> exceeds the largest object number defined before it.
    /// </summary>
    /// <remarks>
    /// <para><b>Read out of the raw bytes, because a parser cannot report this.</b> A <c>/Size</c>
    /// smaller than the objects the revision actually wrote still opens here — the xref subsection
    /// headers carry the real entries and <c>PdfDocument</c> uses those — so every assertion made
    /// through a parsed document is blind to it. Same reason
    /// <c>SignatureDictionaryKeyUniquenessTests</c> reads bytes: where a writer emits its own
    /// trailer, something has to check the trailer.</para>
    ///
    /// <para>It uses the <b>Hebrew</b> appearance deliberately. The ASCII one draws with a
    /// Standard-14 simple font and allocates exactly as many objects as the image path did, so a
    /// <c>/Size</c> computed the old fixed way is accidentally right for it — a fixture that cannot
    /// discriminate. A composite font brings a descendant, a descriptor, the program and a
    /// <c>/ToUnicode</c> with it, and the two answers separate.</para>
    /// </remarks>
    [Fact]
    public void EveryTrailerSizeCoversTheObjectsWrittenBeforeIt()
    {
        byte[] font = MinimalTrueType.Build(Hebrew);
        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = Hebrew, FontProvider = _ => font, Width = 200, Height = 60,
        });

        string raw = Encoding.Latin1.GetString(signed);

        var objects = Regex.Matches(raw, @"(?m)^(\d+) 0 obj");
        var trailers = Regex.Matches(raw, @"/Size (\d+)");
        Assert.NotEmpty(trailers);

        // BOTH match sets are guarded against vacuity, and the object one is the load-bearing
        // half: an empty `objects` leaves `highestSoFar` at 0 for every trailer and degrades the
        // §7.5.5 comparison to "declared > 0", which passes for any /Size a writer could emit. The
        // bar is the input's own object count — the appearance revision must define objects the
        // unsigned fixture did not, or the file under test is not the one this test describes.
        int baselineObjects = Regex.Matches(
            Encoding.Latin1.GetString(SignedPdfFixtures.MinimalPdf()), @"(?m)^(\d+) 0 obj").Count;
        Assert.True(objects.Count > baselineObjects,
            $"the signed file matched {objects.Count} object definitions against the unsigned " +
            $"fixture's {baselineObjects}: the appearance revision's objects were not found, so " +
            "every /Size below would be compared against nothing");

        int trailersComparedAgainstRealObjects = 0;

        foreach (Match trailer in trailers)
        {
            int declared = int.Parse(trailer.Groups[1].Value, CultureInfo.InvariantCulture);

            int highestSoFar = 0;
            foreach (Match obj in objects)
            {
                if (obj.Index > trailer.Index) break;
                highestSoFar = Math.Max(highestSoFar,
                    int.Parse(obj.Groups[1].Value, CultureInfo.InvariantCulture));
            }

            if (highestSoFar > 0) trailersComparedAgainstRealObjects++;

            Assert.True(declared > highestSoFar,
                $"a trailer at byte {trailer.Index} declares /Size {declared}, but object " +
                $"{highestSoFar} is defined before it — §7.5.5 requires /Size to be one greater " +
                "than the largest object number in the file");
        }

        Assert.True(trailersComparedAgainstRealObjects > 0,
            "no trailer had a single object definition before it, so nothing above was actually " +
            "compared");
    }

    /// <summary>
    /// The signature still verifies. The appearance adds objects to the same revision the signature
    /// covers, so a numbering collision or a malformed xref would show up here and nowhere else.
    /// </summary>
    [Fact]
    public void TheSignatureOverATextAppearanceStillVerifies()
    {
        byte[] signed = Sign(new SignatureAppearanceOptions
        {
            SignerName = "Ada Lovelace", Width = 200, Height = 60,
        });

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        string fieldName = SignatureFieldName(doc);

        var result = new PdfSignatureVerifier().Verify(doc, fieldName);

        Assert.True(result.IsIntegrityValid, "the signature did not verify over the signed byte range");
    }

    // ------------------------------------------------------------------

    private static byte[] Sign(SignatureAppearanceOptions appearance, MemoryStream into = null)
        => WithPfx((pfxPath, pw) =>
        {
            using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
            var output = into ?? new MemoryStream();
            VisualSignatureBuilder.Sign(input, output, pfxPath, pw, appearance,
                pageNumber: 1, x: 50, y: 50);
            return output.ToArray();
        });

    private static byte[] SignWithImage(byte[] jpeg)
        => WithPfx((pfxPath, pw) =>
        {
            using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
            using var output = new MemoryStream();
            VisualSignatureBuilder.Sign(input, output, pfxPath, pw, jpeg,
                pageNumber: 1, x: 50, y: 50, width: 200, height: 60);
            return output.ToArray();
        });

    private static byte[] WithPfx(Func<string, string, byte[]> sign)
    {
        var root = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(root);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        string pfxPath = Path.Combine(Path.GetTempPath(), $"vsig-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfxPath, pfx);
        try { return sign(pfxPath, pw); }
        finally { try { File.Delete(pfxPath); } catch (IOException) { /* best effort */ } }
    }

    /// <summary>The signature widget's <c>/AP /N</c> stream, decoded.</summary>
    internal static string AppearanceContent(byte[] signed)
    {
        using var doc = PdfDocument.Open(new MemoryStream(signed));
        return Encoding.Latin1.GetString(Appearance(doc).GetDecodedData());
    }

    private static PdfStream Appearance(PdfDocument doc)
    {
        var widget = SignatureWidget(doc);
        var ap = doc.ResolveObject(widget[PdfName.AP]) as PdfDictionary;
        var normal = doc.ResolveObject(ap?[PdfName.N]) as PdfStream;
        Assert.True(normal != null, "the signature widget has no /AP /N appearance stream");
        return normal!;
    }

    private static PdfDictionary SignatureWidget(PdfDocument doc)
    {
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        Assert.True(annots != null && annots.Count > 0, "the page carries no annotations");

        for (int i = 0; i < annots!.Count; i++)
        {
            if (doc.ResolveObject(annots[i]) is PdfDictionary a &&
                (doc.ResolveObject(a[PdfName.FT]) as PdfName)?.Value == "Sig")
                return a;
        }

        throw new Xunit.Sdk.XunitException("no /FT /Sig widget on the page");
    }

    private static string SignatureFieldName(PdfDocument doc)
        => (doc.ResolveObject(SignatureWidget(doc)[PdfName.T]) as PdfString)?.GetText()
           ?? throw new Xunit.Sdk.XunitException("the signature widget has no /T");

    /// <summary>Whether any font descriptor in the output carries an embedded program.</summary>
    private static bool HasEmbeddedFontProgram(byte[] signed)
    {
        string raw = Encoding.Latin1.GetString(signed);
        return raw.Contains("/FontFile2", StringComparison.Ordinal)
            || raw.Contains("/FontFile3", StringComparison.Ordinal);
    }

    /// <summary>
    /// The inner form the appearance names under <c>/XObject</c> — the one entry there, a form
    /// XObject, which draws the image.
    /// </summary>
    private static PdfStream InnerForm(PdfDocument doc)
    {
        var resources = doc.ResolveObject(Appearance(doc)[PdfName.Resources]) as PdfDictionary;
        var xobjects = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
        Assert.True(xobjects != null && xobjects.Count == 1,
            "the appearance should name exactly one XObject, its inner form");

        var form = doc.ResolveObject(xobjects![new PdfName("SigForm")]) as PdfStream;
        Assert.True(form != null, "the appearance's /SigForm does not resolve to a stream");
        Assert.Equal("Form", (doc.ResolveObject(form![PdfName.Subtype]) as PdfName)?.Value);
        return form;
    }

    /// <summary>The logo image XObject, from the resources of the inner form the appearance names.</summary>
    private static PdfDictionary LogoXObject(PdfDocument doc)
    {
        var resources = doc.ResolveObject(InnerForm(doc)[PdfName.Resources]) as PdfDictionary;
        var xobjects = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
        Assert.True(xobjects != null && xobjects.Count > 0,
            "the inner form declares no XObject resources, so no logo was embedded");

        foreach (var entry in xobjects!)
            if (doc.ResolveObject(entry.Value) is PdfStream image) return image;

        throw new Xunit.Sdk.XunitException("the inner form's XObject resource does not resolve");
    }

    /// <summary>A colour-type-6 PNG whose alpha is a function of x.</summary>
    private static byte[] PngWithAlpha(int w, int h, Func<int, byte> alphaAt)
    {
        var raw = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            raw.Add(0);                                   // filter: None
            for (int x = 0; x < w; x++)
            {
                raw.Add(200); raw.Add(100); raw.Add(50);
                raw.Add(alphaAt(x));
            }
        }
        return AssemblePng(w, h, colorType: 6, raw.ToArray());
    }

    /// <summary>A colour-type-2 PNG, which cannot carry transparency at all.</summary>
    private static byte[] OpaquePng(int w, int h)
    {
        var raw = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            raw.Add(0);
            for (int x = 0; x < w; x++) { raw.Add(10); raw.Add(20); raw.Add(30); }
        }
        return AssemblePng(w, h, colorType: 2, raw.ToArray());
    }

    private static byte[] AssemblePng(int w, int h, byte colorType, byte[] raw)
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };

        var ihdr = new List<byte>();
        ihdr.AddRange(Be(w));
        ihdr.AddRange(Be(h));
        ihdr.Add(8);            // bit depth
        ihdr.Add(colorType);
        ihdr.Add(0); ihdr.Add(0); ihdr.Add(0);   // compression, filter, interlace
        png.AddRange(PngChunk("IHDR", ihdr.ToArray()));
        png.AddRange(PngChunk("IDAT", FlateEncodeFilter.Encode(raw, CompressionLevel.Optimal)));
        png.AddRange(PngChunk("IEND", Array.Empty<byte>()));
        return png.ToArray();
    }

    private static byte[] PngChunk(string type, byte[] data)
    {
        var c = new List<byte>(Be(data.Length));
        byte[] typed = Encoding.ASCII.GetBytes(type);
        c.AddRange(typed);
        c.AddRange(data);

        var crcIn = new List<byte>(typed);
        crcIn.AddRange(data);
        c.AddRange(Be(unchecked((int)Crc32(crcIn.ToArray()))));
        return c.ToArray();
    }

    private static byte[] Be(int v)
        => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)(-(int)(crc & 1)));
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>A tiny baseline JPEG, for the image-overload control.</summary>
    private static byte[] Jpeg() => Jpeg(components: 3, width: 40, height: 20);

    /// <summary>A four-component frame header preceded by Adobe's APP14 marker (transform 0: CMYK).</summary>
    private static byte[] AdobeCmykJpeg(int width, int height)
    {
        byte[] plain = Jpeg(components: 4, width, height);
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xEE, 0x00, 0x0E };   // SOI, APP14, length 14
        bytes.AddRange(Encoding.ASCII.GetBytes("Adobe"));
        bytes.AddRange(new byte[] { 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00 }); // version, flags0, flags1, transform
        bytes.AddRange(plain.Skip(2));                                           // the SOF0 and EOI
        return bytes.ToArray();
    }

    /// <summary>
    /// A baseline JPEG carrying nothing but its SOF0 frame header. That is the whole of what a
    /// <c>/DCTDecode</c> embedder reads — the scan is passed through, never decoded — so the
    /// component count and the dimensions here are exactly the inputs under test.
    /// </summary>
    private static byte[] Jpeg(int components, int width, int height)
    {
        // length(2) + precision(1) + height(2) + width(2) + count(1) + 3 bytes per component.
        int segLen = 8 + 3 * components;
        var bytes = new List<byte>
        {
            0xFF, 0xD8,                             // SOI
            0xFF, 0xC0,                             // SOF0 — baseline
            (byte)(segLen >> 8), (byte)segLen,
            8,                                      // sample precision
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            (byte)components,
        };
        for (int c = 1; c <= components; c++) bytes.AddRange(new byte[] { (byte)c, 0x11, 0x00 });
        bytes.AddRange(new byte[] { 0xFF, 0xD9 });  // EOI
        return bytes.ToArray();
    }
}
