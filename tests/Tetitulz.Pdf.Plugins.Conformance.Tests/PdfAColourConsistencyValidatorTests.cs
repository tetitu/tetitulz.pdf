// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAValidator"/>'s A15-A18 — output-intent colour consistency (ISO 19005-1:2005
/// §6.2.2/§6.2.3.3; the published profiles rules 6.2.2-2, 6.2.3.3-1/2/3). "Colour conversion" sounds like it needs
/// a CMM; these rules ask only whether device-dependent colour usage matches the OutputIntent's
/// declared class — a scan, not a transform. The scan itself is
/// <c>PdfXValidator.ScanPage</c> (reused, not duplicated); only the colour-family classification
/// is new.
/// </summary>
public class PdfAColourConsistencyValidatorTests
{
    // ------------------------------------------------------------------
    //  A16 — DeviceRGB needs an RGB-class OutputIntent
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_DeviceRgbContentOperator_WithCmykOutputIntent_ReportsA16()
    {
        var result = Validate(PdfWithContent("1 0 0 rg 10 10 100 100 re f", outputIntentN: 4));
        Assert.Contains(result.Violations, v => v.Code == "A16");
    }

    [Fact]
    public void Validate_DeviceRgbContentOperator_WithRgbOutputIntent_DoesNotReportA16()
    {
        var result = Validate(PdfWithContent("1 0 0 rg 10 10 100 100 re f", outputIntentN: 3));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A16");
    }

    [Fact]
    public void Validate_DeviceRgbNamedColorSpaceResource_WithCmykOutputIntent_ReportsA16()
    {
        var result = Validate(PdfWithResourceColorSpace("/DeviceRGB", outputIntentN: 4));
        Assert.Contains(result.Violations, v => v.Code == "A16");
    }

    [Fact]
    public void Validate_DeviceRgbImageXObject_WithCmykOutputIntent_ReportsA16()
    {
        var result = Validate(PdfWithImageColorSpace("/DeviceRGB", outputIntentN: 4));
        Assert.Contains(result.Violations, v => v.Code == "A16");
    }

    /// <summary>Exercises <c>ClassifyDeviceFamily</c>'s recursion into /Indexed's base space —
    /// the one path none of the direct-device-space tests above reach.</summary>
    [Fact]
    public void Validate_IndexedColorSpaceOverDeviceRgb_WithCmykOutputIntent_ReportsA16()
    {
        var result = Validate(PdfWithResourceColorSpace(
            "[/Indexed /DeviceRGB 1 <000000ffffff>]", outputIntentN: 4));
        Assert.Contains(result.Violations, v => v.Code == "A16");
    }

    /// <summary>
    /// An uncoloured tiling pattern paints in its base space (ISO 32000-1 §8.7.3.3), so
    /// <c>[/Pattern /DeviceRGB]</c> is DeviceRGB used; a coloured pattern, bare <c>/Pattern</c>,
    /// names no device family. PdfXValidator's X08 classifier descends the same way (Wave 17, R9.1).
    /// </summary>
    [Theory]
    [InlineData("[/Pattern /DeviceRGB]", true)]
    [InlineData("/Pattern", false)]
    public void Validate_PatternColorSpace_IsJudgedByItsBaseSpace_WithCmykOutputIntent(
        string colorSpace, bool reportsA16)
    {
        var result = Validate(PdfWithResourceColorSpace(colorSpace, outputIntentN: 4));
        Assert.Equal(reportsA16, result.Violations.Any(v => v.Code == "A16"));
    }

    // ------------------------------------------------------------------
    //  A17 — DeviceCMYK needs a CMYK-class OutputIntent
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_DeviceCmykContentOperator_WithRgbOutputIntent_ReportsA17()
    {
        var result = Validate(PdfWithContent("0 0 0 1 k 10 10 100 100 re f", outputIntentN: 3));
        Assert.Contains(result.Violations, v => v.Code == "A17");
    }

    [Fact]
    public void Validate_DeviceCmykContentOperator_WithCmykOutputIntent_DoesNotReportA17()
    {
        var result = Validate(PdfWithContent("0 0 0 1 k 10 10 100 100 re f", outputIntentN: 4));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A17");
    }

    // ------------------------------------------------------------------
    //  A18 — an uncalibrated space (DeviceGray) needs SOME OutputIntent
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_DeviceGrayContentOperator_WithNoOutputIntent_ReportsA18()
    {
        var result = Validate(PdfWithContent("0.5 g 10 10 100 100 re f", outputIntentN: null));
        Assert.Contains(result.Violations, v => v.Code == "A18");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Validate_DeviceGrayContentOperator_WithAnyOutputIntent_DoesNotReportA18(int n)
    {
        var result = Validate(PdfWithContent("0.5 g 10 10 100 100 re f", outputIntentN: n));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A18");
    }

    // ------------------------------------------------------------------
    //  Negative control: no device colour usage at all
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_NoDeviceColourUsage_ReportsNeitherA16NorA17NorA18()
    {
        var result = Validate(PdfWithContent("BT ET", outputIntentN: null));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A16");
        Assert.DoesNotContain(result.Violations, v => v.Code == "A17");
        Assert.DoesNotContain(result.Violations, v => v.Code == "A18");
    }

    // ------------------------------------------------------------------
    //  A15 — multiple OutputIntents must share one /DestOutputProfile object
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_TwoOutputIntentsWithDifferentProfiles_ReportsA15()
    {
        var result = Validate(PdfWithTwoOutputIntents(sameProfile: false));
        Assert.Contains(result.Violations, v => v.Code == "A15");
    }

    [Fact]
    public void Validate_TwoOutputIntentsWithTheSameProfile_DoesNotReportA15()
    {
        var result = Validate(PdfWithTwoOutputIntents(sameProfile: true));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A15");
    }

    [Fact]
    public void Validate_OneOutputIntent_DoesNotReportA15()
    {
        var result = Validate(PdfWithContent("BT ET", outputIntentN: 3));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A15");
    }

    // ------------------------------------------------------------------
    //  Drawn, not named (ISO 19005-1 Corrigendum 2), and remapped through a Default colour space
    //  (ISO 32000-2 §8.6.5.6) — the conformance corpus 6-2-3-3-t03-pass-a to -k, each of which the
    //  validator failed
    // ------------------------------------------------------------------

    private static readonly string CmykForm = FormObject("<< >>", "0 0 0 1 k 0 0 5 5 re f");

    /// <summary>A named resource the content does not reference is not used (6-2-3-3-t03-pass-a).</summary>
    [Theory]
    [InlineData("BT ET", false)]
    [InlineData("/F0 Do", true)]
    public void AFormIsJudgedOnlyWhenTheContentDrawsIt(string content, bool reportsA17)
    {
        var result = Validate(Page("/XObject << /F0 7 0 R >>", content, outputIntentN: 3, CmykForm));
        Assert.Equal(reportsA17, Has(result, "A17"));
    }

    /// <summary>A form drawn only by another form that is itself not drawn is not drawn either.</summary>
    [Fact]
    public void AFormReachedOnlyThroughAnUndrawnFormIsNotUsed()
    {
        string outer = FormObject("<< /XObject << /F1 8 0 R >> >>", "/F1 Do");
        var undrawn = Validate(Page("/XObject << /F0 7 0 R >>", "BT ET", outputIntentN: 3, outer, CmykForm));
        var drawn = Validate(Page("/XObject << /F0 7 0 R >>", "/F0 Do", outputIntentN: 3, outer, CmykForm));

        Assert.False(Has(undrawn, "A17"));
        Assert.True(Has(drawn, "A17"));
    }

    /// <summary>Each device operator is remapped by its default (6-2-3-3-t03-pass-b, -c, -d).</summary>
    [Theory]
    [InlineData("/DefaultRGB [/CalRGB << /WhitePoint [0.95 1 1.09] >>]", "1 0 0 rg 0 0 5 5 re f", 4, "A16")]
    [InlineData("/DefaultRGB [/CalRGB << /WhitePoint [0.95 1 1.09] >>]", "1 0 0 RG 0 0 5 5 re S", 4, "A16")]
    [InlineData("/DefaultCMYK 7 0 R", "0 0 0 1 k 0 0 5 5 re f", 3, "A17")]
    [InlineData("/DefaultGray [/CalGray << /WhitePoint [0.95 1 1.09] >>]", "0.5 g 0 0 5 5 re f", null, "A18")]
    [InlineData("/DefaultGray [/CalGray << /WhitePoint [0.95 1 1.09] >>]", "/DeviceGray cs 0.5 sc 0 0 5 5 re f", null, "A18")]
    public void ADefaultColourSpaceRemapsTheDeviceSpace(string defaults, string content, int? outputIntentN, string code)
    {
        const string cmykProfile = "[/ICCBased 8 0 R]";
        string[] extra = { cmykProfile, IccProfileObject(4) };
        var remapped = Validate(Page("/ColorSpace << " + defaults + " >>", content, outputIntentN, extra));
        var plain = Validate(Page("", content, outputIntentN, extra));

        Assert.False(Has(remapped, code));
        Assert.True(Has(plain, code));
    }

    /// <summary>A default replaces a device space, not the space's class: DefaultRGB set to
    /// DeviceRGB is DeviceRGB used, and a default is not remapped again.</summary>
    [Fact]
    public void ADefaultThatIsItselfADeviceSpaceIsThatSpaceUsed()
    {
        var result = Validate(Page("/ColorSpace << /DefaultRGB /DeviceCMYK /DefaultCMYK /DeviceRGB >>",
            "1 0 0 rg 0 0 5 5 re f", outputIntentN: 3));

        Assert.True(Has(result, "A17"));
        Assert.False(Has(result, "A16"));
    }

    /// <summary>A default applies in the resource dictionary it sits in: a form with resources of its
    /// own is painted without the page's.</summary>
    [Theory]
    [InlineData("<< >>", true)]
    [InlineData("<< /ColorSpace << /DefaultRGB [/CalRGB << /WhitePoint [0.95 1 1.09] >>] >> >>", false)]
    public void APageDefaultDoesNotReachAFormWithItsOwnResources(string formResources, bool reportsA16)
    {
        string form = FormObject(formResources, "1 0 0 rg 0 0 5 5 re f");
        var result = Validate(Page(
            "/ColorSpace << /DefaultRGB [/CalRGB << /WhitePoint [0.95 1 1.09] >>] >> /XObject << /F0 7 0 R >>",
            "/F0 Do", outputIntentN: 4, form));

        Assert.Equal(reportsA16, Has(result, "A16"));
    }

    /// <summary>An image is remapped by the defaults of the stream that draws it (6-2-3-3-t03-pass-f,
    /// 6-2-4-t03-pass-a), and an undrawn image is not used.</summary>
    [Theory]
    [InlineData("/XObject << /Im 7 0 R >>", "/Im Do", true)]
    [InlineData("/XObject << /Im 7 0 R >>", "BT ET", false)]
    [InlineData("/XObject << /Im 7 0 R >> /ColorSpace << /DefaultRGB [/CalRGB << /WhitePoint [0.95 1 1.09] >>] >>", "/Im Do", false)]
    public void AnImageIsJudgedWhenDrawnAndRemapped(string resources, string content, bool reportsA16)
    {
        string image = StreamObject("/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB", "abc");
        Assert.Equal(reportsA16, Has(Validate(Page(resources, content, outputIntentN: 4, image)), "A16"));
    }

    /// <summary>An inline image's colour space, abbreviated or named from the resources, is remapped
    /// the same way (6-2-3-3-t03-pass-e).</summary>
    [Theory]
    [InlineData("", "BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI", true)]
    [InlineData("/ColorSpace << /DefaultRGB [/CalRGB << /WhitePoint [0.95 1 1.09] >>] >>", "BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI", false)]
    [InlineData("/ColorSpace << /C0 [/Indexed /DeviceRGB 1 <000000ffffff>] >>", "BI /W 1 /H 1 /CS /C0 /BPC 8 ID a EI", true)]
    public void AnInlineImageIsRemappedToo(string resources, string content, bool reportsA16)
    {
        Assert.Equal(reportsA16, Has(Validate(Page(resources, content, outputIntentN: 4)), "A16"));
    }

    /// <summary>§8.6.5.6 remaps an Indexed base, a Separation alternate and a Pattern's underlying
    /// space (6-2-3-3-t03-pass-k).</summary>
    [Theory]
    [InlineData("[/Indexed /DeviceRGB 1 <000000ffffff>]", "/C0 cs 0 sc 0 0 5 5 re f")]
    [InlineData("[/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 0] /N 1 >>]", "/C0 cs 1 sc 0 0 5 5 re f")]
    [InlineData("[/Pattern /DeviceRGB]", "/C0 cs 1 0 0 /P0 scn 0 0 5 5 re f")]
    public void ASpecialSpacesDeviceBaseIsRemapped(string space, string content)
    {
        const string defaultRgb = "/DefaultRGB [/CalRGB << /WhitePoint [0.95 1 1.09] >>]";
        var remapped = Validate(Page("/ColorSpace << /C0 " + space + " " + defaultRgb + " >>", content, outputIntentN: 4));
        var plain = Validate(Page("/ColorSpace << /C0 " + space + " >>", content, outputIntentN: 4));

        Assert.False(Has(remapped, "A16"));
        Assert.True(Has(plain, "A16"));
    }

    /// <summary>A colour space resource the content never selects is not used.</summary>
    [Fact]
    public void AColourSpaceResourceNoOperatorSelectsIsNotUsed()
    {
        var unselected = Validate(Page("/ColorSpace << /C0 /DeviceRGB >>", "0 0 5 5 re f", outputIntentN: 4));
        var selected = Validate(Page("/ColorSpace << /C0 /DeviceRGB >>", "/C0 cs 1 0 0 sc 0 0 5 5 re f", outputIntentN: 4));

        Assert.False(Has(unselected, "A16"));
        Assert.True(Has(selected, "A16"));
    }

    /// <summary>A tiling pattern is drawn when <c>scn</c> names it, with its own resources' defaults
    /// (6-2-3-3-t03-pass-g); an unnamed one is not drawn.</summary>
    [Theory]
    [InlineData("<< >>", "/Pattern cs /P0 scn 0 0 5 5 re f", true)]
    [InlineData("<< >>", "0 0 5 5 re f", false)]
    [InlineData("<< /ColorSpace << /DefaultCMYK [/ICCBased 8 0 R] >> >>", "/Pattern cs /P0 scn 0 0 5 5 re f", false)]
    public void ATilingPatternIsDrawnWhenNamedAndRemappedByItsOwnDefaults(string patternResources, string content, bool reportsA17)
    {
        string pattern = StreamObject("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 5 5] /XStep 5 /YStep 5 /Resources "
            + patternResources, "0 0 0 1 k 0 0 5 5 re f");
        var result = Validate(Page("/Pattern << /P0 7 0 R >>", content, outputIntentN: 3, pattern, IccProfileObject(4)));
        Assert.Equal(reportsA17, Has(result, "A17"));
    }

    private const string RgbShading = "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 5 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >> >>";

    /// <summary>A shading paints in its colour space — by <c>sh</c>, or as a shading pattern by
    /// <c>scn</c>; the scan reads both.</summary>
    [Theory]
    [InlineData("/Shading << /Sh0 7 0 R >>", "/Sh0 sh", true)]
    [InlineData("/Shading << /Sh0 7 0 R >>", "BT ET", false)]
    [InlineData("/Pattern << /P0 8 0 R >>", "/Pattern cs /P0 scn 0 0 5 5 re f", true)]
    [InlineData("/Shading << /Sh0 7 0 R >> /ColorSpace << /DefaultRGB [/CalRGB << /WhitePoint [0.95 1 1.09] >>] >>", "/Sh0 sh", false)]
    public void AShadingIsJudgedWhenPainted(string resources, string content, bool reportsA16)
    {
        var result = Validate(Page(resources, content, outputIntentN: 4,
            RgbShading, "<< /PatternType 2 /Shading 7 0 R >>"));
        Assert.Equal(reportsA16, Has(result, "A16"));
    }

    /// <summary>A Type 3 font's glyph procedures are drawn when <c>Tf</c> selects the font.</summary>
    [Theory]
    [InlineData("BT /T3 12 Tf (a) Tj ET", true)]
    [InlineData("BT ET", false)]
    public void ATypeThreeGlyphIsDrawnWhenTheFontIsSelected(string content, bool reportsA17)
    {
        const string font = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << /a 8 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1] >>";
        string glyph = StreamObject("", "1 0 d0 0 0 0 1 k 0 0 1 1 re f");
        var result = Validate(Page("/Font << /T3 7 0 R >>", content, outputIntentN: 3, font, glyph));
        Assert.Equal(reportsA17, Has(result, "A17"));
    }

    /// <summary>A soft mask's group is drawn when <c>gs</c> selects its graphics state.</summary>
    [Theory]
    [InlineData("/GS0 gs 0 0 5 5 re f", true)]
    [InlineData("0 0 5 5 re f", false)]
    public void ASoftMaskGroupIsDrawnWhenItsStateIsSelected(string content, bool reportsA17)
    {
        var result = Validate(Page("/ExtGState << /GS0 << /SMask << /S /Luminosity /G 7 0 R >> >> >>",
            content, outputIntentN: 3, CmykForm));
        Assert.Equal(reportsA17, Has(result, "A17"));
    }

    /// <summary>An annotation's appearance is drawn with the page.</summary>
    [Fact]
    public void AnAnnotationAppearanceIsDrawn()
    {
        var result = Validate(Assemble("", "BT ET", 3, "/Annots [8 0 R]", new[]
        {
            CmykForm, "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /F 4 /AP << /N 7 0 R >> >>",
        }));
        Assert.True(Has(result, "A17"));
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static bool Has(PdfAValidationResult result, string code) => result.Violations.Any(v => v.Code == code);

    /// <summary>
    /// A page with <paramref name="resources"/> painting <paramref name="content"/>: objects 1–4
    /// (catalog, pages, page, content), 5–6 the output intent and its profile (free when there is
    /// none), and <paramref name="extra"/> from 7.
    /// </summary>
    private static byte[] Page(string resources, string content, int? outputIntentN, params string[] extra)
        => Assemble(resources, content, outputIntentN, "", extra);

    private static byte[] Assemble(string resources, string content, int? outputIntentN, string pageEntries, string[] extra)
    {
        var objects = new List<string>
        {
            outputIntentN.HasValue
                ? "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R] >>"
                : "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + pageEntries + " /Resources << " + resources + " >> >>",
            StreamObject("", content),
            outputIntentN.HasValue ? "<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 6 0 R >>" : "",
            outputIntentN.HasValue ? IccProfileObject(outputIntentN.Value) : "",
        };
        objects.AddRange(extra);
        return Build(objects.ToArray());
    }

    private static string FormObject(string resources, string content)
        => StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources " + resources, content);

    private static PdfAValidationResult Validate(byte[] pdf)
        => PdfAValidator.Validate(new MemoryStream(pdf));

    /// <summary>A one-page document painting <paramref name="content"/>, with an optional
    /// GTS_PDFA1 OutputIntent declaring <paramref name="outputIntentN"/> components.</summary>
    private static byte[] PdfWithContent(string content, int? outputIntentN)
    {
        var objects = new List<string>
        {
            outputIntentN.HasValue
                ? "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R] >>"
                : "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << >> >>",
            StreamObject("", content),
        };
        if (outputIntentN.HasValue)
        {
            objects.Add("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 6 0 R >>");
            objects.Add(IccProfileObject(outputIntentN.Value));
        }
        return Build(objects.ToArray());
    }

    private static byte[] PdfWithResourceColorSpace(string colorSpace, int? outputIntentN)
    {
        var objects = new List<string>
        {
            outputIntentN.HasValue
                ? "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R] >>"
                : "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /ColorSpace << /CS1 " + colorSpace + " >> >> >>",
            StreamObject("", "/CS1 cs 1 0 0 1 sc 10 10 100 100 re f"),
        };
        if (outputIntentN.HasValue)
        {
            objects.Add("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 6 0 R >>");
            objects.Add(IccProfileObject(outputIntentN.Value));
        }
        return Build(objects.ToArray());
    }

    private static byte[] PdfWithImageColorSpace(string colorSpace, int? outputIntentN)
    {
        var objects = new List<string>
        {
            outputIntentN.HasValue
                ? "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R] >>"
                : "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /XObject << /Im1 7 0 R >> >> >>",
            StreamObject("", "/Im1 Do"),
        };
        if (outputIntentN.HasValue)
        {
            objects.Add("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 6 0 R >>");
            objects.Add(IccProfileObject(outputIntentN.Value));
        }
        else
        {
            objects.Add(""); // keep object 6 numbering stable even when unused
        }
        objects.Add(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace " + colorSpace + " /Length 1 >>\nstream\n\x00\nendstream");
        return Build(objects.ToArray());
    }

    private static byte[] PdfWithTwoOutputIntents(bool sameProfile)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OutputIntents [5 0 R 6 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << >> >>",
            StreamObject("", "BT ET"),
            "<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 7 0 R >>",
            sameProfile
                ? "<< /Type /OutputIntent /S /GTS_PDFX /DestOutputProfile 7 0 R >>"
                : "<< /Type /OutputIntent /S /GTS_PDFX /DestOutputProfile 8 0 R >>",
            IccProfileObject(3),
        };
        if (!sameProfile) objects.Add(IccProfileObject(4));
        return Build(objects.ToArray());
    }

    private static string IccProfileObject(int n)
        => "<< /N " + n + " /Length 3 >>\nstream\nfoo\nendstream";

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    /// <summary>Objects are numbered 1..N in <paramref name="objects"/> order; an empty entry still
    /// claims its slot (a free-list entry), for fixtures that need to skip an unused number.</summary>
    private static byte[] Build(string[] objects)
    {
        var acc = new List<byte>();
        var offsets = new int[objects.Length];
        var free = new bool[objects.Length];
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));

        Emit("%PDF-1.7\n");
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i].Length == 0) { free[i] = true; continue; }
            offsets[i] = acc.Count;
            Emit($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = acc.Count;
        Emit($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 0; i < objects.Length; i++)
            Emit(free[i] ? "0000000000 65535 f \n" : $"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
