// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The PDF/X rules taken from public sources (GAPS.md §12b): the device-space rule against the
/// output intent, the X-4 blending space, and the brochure's core rules X14-X19. Each check is
/// asserted by its own code, beside a control the same fixture passes, so a check that stopped
/// running cannot pass for one that found nothing.
/// </summary>
public class PdfXPublicSourceRulesTests
{
    // ------------------------------------------------------------------
    //  X08 — a device space only where the output intent is in it
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>rg</c> was reported without the policy, so an RGB-intent X-4 file using DeviceRGB, which
    /// X-3 and X-4 allow, failed. The CMYK intent is the control.
    /// </summary>
    [Fact]
    public void RgbOperator_AgainstAnRgbIntent_IsAllowed_AndAgainstCmykIsNot()
    {
        Assert.DoesNotContain(Codes(new Fixture { Content = "1 0 0 rg", IntentComponents = 3 }), c => c == "X08");

        var x08 = Single("X08", new Fixture { Content = "1 0 0 rg", IntentComponents = 4 });
        Assert.Contains("DeviceRGB set by the rg operator", x08.Message);
    }

    [Theory]
    [InlineData("0 0 0 1 k", 3, "DeviceCMYK set by the k operator")]
    [InlineData("0 0 0 1 K", 1, "DeviceCMYK set by the K operator")]
    [InlineData("0 g", 3, "DeviceGray set by the g operator")]
    [InlineData("0 G", 3, "DeviceGray set by the G operator")]
    public void DeviceOperator_AgainstAnIntentOfAnotherSpace_ReportsX08(string content, int intent, string expected)
    {
        var x08 = Single("X08", new Fixture { Content = content, IntentComponents = intent });
        Assert.Contains(expected, x08.Message);
    }

    /// <summary>The acceptances: CMYK with a CMYK intent, Gray with a Gray or CMYK one.</summary>
    [Theory]
    [InlineData("0 0 0 1 k", 4)]
    [InlineData("0 g", 4)]
    [InlineData("0 g", 1)]
    public void DeviceOperator_AgainstItsOwnIntent_IsAllowed(string content, int intent)
        => Assert.DoesNotContain(Codes(new Fixture { Content = content, IntentComponents = intent }),
                                 c => c == "X08");

    /// <summary>
    /// No profile to read — X-3 allows a registered condition name instead — refuses DeviceRGB
    /// and admits the conventional press spaces.
    /// </summary>
    [Fact]
    public void UnknownIntent_RefusesOnlyDeviceRgb()
    {
        Assert.DoesNotContain(Codes(new Fixture { Content = "0 0 0 1 k 0 g", IntentComponents = null },
                                    PdfXConformanceLevel.X3), c => c == "X08");
        var x08 = Single("X08", new Fixture { Content = "1 0 0 rg", IntentComponents = null },
                         PdfXConformanceLevel.X3);
        Assert.Contains("no Gray, RGB or CMYK profile", x08.Message);
    }

    /// <summary>X-1a's rule is its own and admits CMYK and Gray whatever the intent says.</summary>
    [Fact]
    public void X1a_CmykAndGray_AreAllowed()
        => Assert.DoesNotContain(Codes(new Fixture { Content = "0 0 0 1 k 0 g", IntentComponents = 3 },
                                       PdfXConformanceLevel.X1a), c => c == "X08");

    [Theory]
    [InlineData("/G", "DeviceGray")]
    [InlineData("/CMYK", "DeviceCMYK")]
    public void InlineImageAbbreviation_IsExpanded(string cs, string expected)
    {
        var x08 = Single("X08", new Fixture
        {
            Content = $"BI /W 1 /H 1 /CS {cs} /BPC 8 ID \x00\x00\x00\x00 EI",
            IntentComponents = 3,
        });
        Assert.Contains($"a {expected} inline image", x08.Message);
    }

    /// <summary>
    /// A <c>cs</c> operand is not an inline image's: <c>/G</c> there names a resource, here an
    /// ICC-based space X-4 allows. The device name in the same place is the control.
    /// </summary>
    [Fact]
    public void CsOperand_IsNotExpanded()
    {
        var resources = "/ColorSpace << /G [/ICCBased 10 0 R] >>";
        var icc = (10, "<< /N 3 /Length 1 >>\nstream\n \nendstream");

        Assert.DoesNotContain(Codes(new Fixture { Content = "/G cs", PageResources = resources,
                                                  IntentComponents = 3, Objects = { icc } }), c => c == "X08");
        Assert.Contains(Codes(new Fixture { Content = "/DeviceGray cs", PageResources = resources,
                                            IntentComponents = 3, Objects = { icc } }), c => c == "X08");
    }

    /// <summary>A preflight vendor: "CMYK used for alt. color but PDF/X OutputIntent not CMYK".</summary>
    [Fact]
    public void SpotAlternate_InAnotherSpace_ReportsX08()
    {
        var x08 = Single("X08", new Fixture
        {
            PageResources = "/ColorSpace << /CS0 [/Separation /Spot /DeviceCMYK " + Tint4("1 0 0 0") + "] >>",
            IntentComponents = 3,
        });
        Assert.Contains("DeviceCMYK colour space (resource /CS0)", x08.Message);
    }

    // ------------------------------------------------------------------
    //  X08 at X-4 — the transparency blending space
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> BlendingSpaceHosts() => new[]
    {
        new object[] { "page" }, new object[] { "form" }, new object[] { "appearance" }, new object[] { "softmask" },
    };

    /// <summary>
    /// A preflight vendor: "RGB used for transparency blend color space but OutputIntent not RGB". Each place a
    /// group's <c>/CS</c> can sit, with a DeviceCMYK space in the same place as the control.
    /// </summary>
    [Theory]
    [MemberData(nameof(BlendingSpaceHosts))]
    public void BlendingSpace_IsCheckedAgainstTheIntent(string host)
    {
        var x08 = Single("X08", BlendingSpaceFixture(host, "/DeviceRGB"));
        Assert.Contains("DeviceRGB transparency blending space", x08.Message);

        Assert.DoesNotContain(Codes(BlendingSpaceFixture(host, "/DeviceCMYK")), c => c == "X08");
    }

    private static Fixture BlendingSpaceFixture(string host, string cs)
    {
        string group = $"/Group << /S /Transparency /CS {cs} >>";
        string form = $"<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] {group} /Length 0 >>\nstream\n\nendstream";
        return host switch
        {
            "page" => new Fixture { PageExtra = "/TrimBox [0 0 200 200] " + group },
            "form" => new Fixture { PageResources = "/XObject << /Fm 10 0 R >>", Objects = { (10, form) } },
            "appearance" => new Fixture
            {
                PageExtra = "/TrimBox [0 0 200 200] /Annots [<< /Type /Annot /Subtype /Stamp " +
                            "/Rect [0 0 10 10] /F 4 /AP << /N 10 0 R >> >>]",
                Objects = { (10, form) },
            },
            _ => new Fixture
            {
                PageResources = "/ExtGState << /GS0 << /SMask << /S /Luminosity /G 10 0 R >> >> >>",
                Objects = { (10, form) },
            },
        };
    }

    // ------------------------------------------------------------------
    //  X14 — no transfer curves
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/TR << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 2 >>", "/TR")]
    [InlineData("/TR /Identity", "/TR")]
    [InlineData("/TR2 << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 2 >>", "/TR2")]
    public void TransferFunction_ReportsX14(string entry, string key)
    {
        var x14 = Single("X14", new Fixture { PageResources = $"/ExtGState << /GS0 << {entry} >> >>" });
        Assert.Contains($"/ExtGState /GS0 on page 1 sets {key}", x14.Message);
    }

    [Fact]
    public void Tr2Default_IsNotATransferCurve()
        => Assert.DoesNotContain(Codes(new Fixture { PageResources = "/ExtGState << /GS0 << /TR2 /Default >> >>" }),
                                 c => c == "X14");

    /// <summary>The scan's reach: a graphics state only a form XObject's own resources name.</summary>
    [Fact]
    public void TransferFunction_InANestedForm_ReportsX14()
        => Assert.Contains("/ExtGState /GSn on page 1 sets /TR", Single("X14", new Fixture
        {
            PageResources = "/XObject << /Fm 10 0 R >>",
            Objects =
            {
                (10, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                     "/Resources << /ExtGState << /GSn << /TR /Identity >> >> >> /Length 0 >>\nstream\n\nendstream"),
            },
        }).Message);

    // ------------------------------------------------------------------
    //  X15 — no LZW compression
    // ------------------------------------------------------------------

    /// <summary>
    /// Every stream the cross-reference table lists, used or not, and a filter chain's later
    /// entries. Flate is the control.
    /// </summary>
    [Theory]
    [InlineData("/LZWDecode", true)]
    [InlineData("[/ASCIIHexDecode /LZWDecode]", true)]
    [InlineData("/FlateDecode", false)]
    public void StreamFilter_LzwReportsX15(string filter, bool reported)
    {
        var codes = Codes(new Fixture { Objects = { (10, $"<< /Filter {filter} /Length 1 >>\nstream\nx\nendstream") } });
        Assert.Equal(reported, codes.Contains("X15"));
    }

    [Theory]
    [InlineData("/F /LZW", true)]
    [InlineData("/Filter /LZWDecode", true)]
    [InlineData("/F [/AHx /LZW]", true)]
    [InlineData("/F /AHx", false)]
    public void InlineImageFilter_LzwReportsX15(string filter, bool reported)
    {
        var codes = Codes(new Fixture { Content = $"BI /W 1 /H 1 /CS /G /BPC 8 {filter} ID 00> EI" });
        Assert.Equal(reported, codes.Contains("X15"));
        if (reported)
            Assert.Contains("an inline image on page 1", Single("X15",
                new Fixture { Content = $"BI /W 1 /H 1 /CS /G /BPC 8 {filter} ID 00> EI" }).Message);
    }

    // ------------------------------------------------------------------
    //  X16 — no embedded files
    // ------------------------------------------------------------------

    [Fact]
    public void EmbeddedFilesNameTree_ReportsX16()
    {
        var x16 = Single("X16", new Fixture
        {
            CatalogExtra = "/Names << /EmbeddedFiles << /Names [(a.txt) 10 0 R] >> >>",
            Objects = { (10, "<< /Type /Filespec /F (a.txt) >>") },
        });
        Assert.Contains("the catalog's /Names /EmbeddedFiles", x16.Message);
    }

    [Fact]
    public void FileAttachmentAnnotation_ReportsX16()
    {
        var x16 = Single("X16", new Fixture
        {
            PageExtra = "/TrimBox [0 0 200 200] /Annots [<< /Type /Annot /Subtype /FileAttachment /Rect [0 0 10 10] >>]",
        });
        Assert.Contains("a /FileAttachment annotation on page 1", x16.Message);
    }

    /// <summary>A file the name tree and the annotations do not reach is still in the file.</summary>
    [Fact]
    public void EmbeddedFileStreamAlone_ReportsX16()
    {
        var x16 = Single("X16", new Fixture
        {
            Objects = { (10, "<< /Type /EmbeddedFile /Length 5 >>\nstream\nhello\nendstream") },
        });
        Assert.Contains("an embedded file stream (object 10)", x16.Message);
    }

    [Fact]
    public void PlainFile_HasNoX15OrX16()
    {
        var codes = Codes(new Fixture());
        Assert.DoesNotContain("X15", codes);
        Assert.DoesNotContain("X16", codes);
        Assert.Empty(codes);
    }

    // ------------------------------------------------------------------
    //  X17 — the BleedBox encloses the TrimBox
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/BleedBox [5 5 195 195]", false)]
    [InlineData("/BleedBox [10 10 190 190]", false)]           // equal: no bleed, still encloses
    [InlineData("/BleedBox [195 195 5 5]", false)]             // corners in either order (§7.9.5)
    [InlineData("/BleedBox [10.0005 10 190 190]", false)]      // inside the tolerance
    [InlineData("/BleedBox 10 0 R", false)]                    // indirect
    [InlineData("/BleedBox [12 5 195 195]", true)]
    [InlineData("/BleedBox [5 5 195 189]", true)]
    [InlineData("/BleedBox [5 11 195 195]", true)]
    [InlineData("/BleedBox [5 5 189 195]", true)]
    public void BleedBox_MustEncloseTheTrimBox(string bleed, bool reported)
    {
        var fixture = new Fixture { PageExtra = "/TrimBox [10 10 190 190] " + bleed, Objects = { (10, "[5 5 195 195]") } };
        Assert.Equal(reported, Codes(fixture).Contains("X17"));
        if (reported) Assert.Contains("page(s): 1", Single("X17", fixture).Message);
    }

    /// <summary>No BleedBox, no bleed zone defined: nothing to judge.</summary>
    [Fact]
    public void NoBleedBox_IsNotJudged()
        => Assert.DoesNotContain("X17", Codes(new Fixture { PageExtra = "/TrimBox [10 10 190 190]" }));

    // ------------------------------------------------------------------
    //  X18 — one alternate per spot colour
    // ------------------------------------------------------------------

    [Fact]
    public void SpotColour_WithTwoTintTransforms_ReportsX18()
    {
        var x18 = Single("X18", Spots(
            "[/Separation /Spot /DeviceCMYK " + Tint4("0 1 0 0") + "]",
            "[/Separation /Spot /DeviceCMYK " + Tint4("1 0 0 0") + "]"));
        Assert.Contains("/Spot (page 1)", x18.Message);
    }

    [Fact]
    public void SpotColour_WithTwoAlternateSpaces_ReportsX18()
        => Assert.Contains("/Spot (page 1)", Single("X18", Spots(
            "[/Separation /Spot /DeviceCMYK " + Tint4("0 0 0 1") + "]",
            "[/Separation /Spot /DeviceGray << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>]")).Message);

    /// <summary>
    /// The same ink defined a second time as an uncoloured pattern's base space
    /// (<c>[/Pattern [/Separation …]]</c>, §8.7.3.3) is the same ink, and a different tint there is
    /// the same inconsistency. The base was not descended until Wave 17 R9.1.
    /// </summary>
    [Fact]
    public void SpotColour_DefinedAgainInsideAPatternBase_ReportsX18()
        => Assert.Contains("/Spot (page 1)", Single("X18", Spots(
            "[/Separation /Spot /DeviceCMYK " + Tint4("0 1 0 0") + "]",
            "[/Pattern [/Separation /Spot /DeviceCMYK " + Tint4("1 0 0 0") + "]]")).Message);

    /// <summary>
    /// The same definition written twice — once direct, once through references, the numbers in
    /// another spelling — is one definition. ISO 19005-2 6.2.4.4: direct or indirect is ignored.
    /// </summary>
    [Fact]
    public void SpotColour_SameDefinitionWrittenTwice_IsConsistent()
    {
        var f = Spots(
            "[/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 0 0] /N 1 >>]",
            "[/Separation /Spot 11 0 R 12 0 R]");
        f.Objects.Add((11, "/DeviceCMYK"));
        f.Objects.Add((12, "<< /N 1.0 /C1 [0 1.0 0 0] /Domain [0 1] /FunctionType 2 /C0 [0 0 0 0] >>"));
        Assert.DoesNotContain("X18", Codes(f));
    }

    /// <summary>
    /// A tint transform with one entry more — a <c>/Range</c> the other lacks — is a different
    /// object, whatever it evaluates to: the objects are compared, not their results.
    /// </summary>
    [Fact]
    public void SpotColour_AnExtraEntry_IsADifferentDefinition()
        => Assert.Contains("/Spot (page 1)", Single("X18", Spots(
            "[/Separation /Spot /DeviceCMYK " + Tint4("0 1 0 0") + "]",
            "[/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 0 0] " +
            "/N 1 /Range [0 1 0 1 0 1 0 1] >>]")).Message);

    /// <summary>
    /// "Compression … shall be ignored": a PostScript function written plain and Flate-compressed
    /// is one function. A different program is the control.
    /// </summary>
    [Fact]
    public void SpotColour_CompressionIsIgnored()
    {
        Assert.DoesNotContain("X18", Codes(CompressedTintFixture("{ pop 0 1 0 0 }")));
        Assert.Contains("X18", Codes(CompressedTintFixture("{ pop 1 0 0 0 }")));
    }

    private static Fixture CompressedTintFixture(string secondProgram)
    {
        const string head = "/FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1 0 1]";
        var f = Spots("[/Separation /Spot /DeviceCMYK 10 0 R]", "[/Separation /Spot /DeviceCMYK 11 0 R]");
        const string program = "{ pop 0 1 0 0 }";
        f.Objects.Add((10, $"<< {head} /Length {program.Length} >>\nstream\n{program}\nendstream"));
        byte[] packed = Deflate(Encoding.ASCII.GetBytes(secondProgram));
        f.RawObjects.Add((11, Concat($"<< {head} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n", packed, "\nendstream")));
        return f;
    }

    [Fact]
    public void SpotColour_InADeviceNColorants_IsCompared()
        => Assert.Contains("/Spot (page 1)", Single("X18", Spots(
            "[/Separation /Spot /DeviceCMYK " + Tint4("0 1 0 0") + "]",
            "[/DeviceN [/Spot] /DeviceCMYK " + Tint4("0 1 0 0") +
            " << /Colorants << /Spot [/Separation /Spot /DeviceCMYK " + Tint4("1 0 0 0") + "] >> >>]")).Message);

    /// <summary>The Separation inside an <c>/Indexed</c> base is a definition too.</summary>
    [Fact]
    public void SpotColour_AsAnIndexedBase_IsCompared()
        => Assert.Contains("/Spot (page 1)", Single("X18", Spots(
            "[/Separation /Spot /DeviceCMYK " + Tint4("0 1 0 0") + "]",
            "[/Indexed [/Separation /Spot /DeviceCMYK " + Tint4("1 0 0 0") + "] 0 <00>]")).Message);

    /// <summary><c>/All</c> and <c>/None</c> are not spot colours.</summary>
    [Theory]
    [InlineData("All")]
    [InlineData("None")]
    public void AllAndNone_AreNotSpotColours(string name)
        => Assert.DoesNotContain("X18", Codes(Spots(
            $"[/Separation /{name} /DeviceCMYK " + Tint4("0 1 0 0") + "]",
            $"[/Separation /{name} /DeviceCMYK " + Tint4("1 0 0 0") + "]")));

    /// <summary>
    /// Two tint transforms that will not decode cannot be compared: neither a violation nor
    /// silence, but an X99 naming the check.
    /// </summary>
    [Fact]
    public void SpotColour_UndecodableTintTransform_IsReportedAsUndecided()
    {
        const string head = "/FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] /Filter /FlateDecode /Length 4";
        var f = Spots("[/Separation /Spot /DeviceCMYK 10 0 R]", "[/Separation /Spot /DeviceCMYK 11 0 R]");
        f.Objects.Add((10, $"<< {head} >>\nstream\nnope\nendstream"));
        f.Objects.Add((11, $"<< {head} >>\nstream\nnah!\nendstream"));

        var result = Validate(f);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X18");
        Assert.Contains(result.Informational, i => i.Code == "X99" && i.Message.Contains("X18"));
    }

    /// <summary>
    /// Adversarial: two tint transforms that each name themselves. The comparison follows the
    /// references, and without its depth cap it recursed until the node budget ran out, 100 000
    /// frames deep. Capped, the pair is undecided, which is an X99, not a verdict.
    /// </summary>
    [Fact]
    public void SpotColour_SelfReferencingTintTransforms_StopAtTheDepthCap()
    {
        var f = Spots("[/Separation /Spot /DeviceCMYK 10 0 R]", "[/Separation /Spot /DeviceCMYK 11 0 R]");
        f.Objects.Add((10, "<< /FunctionType 2 /Domain [0 1] /N 1 /Next 10 0 R >>"));
        f.Objects.Add((11, "<< /FunctionType 2 /Domain [0 1] /N 1 /Next 11 0 R >>"));

        PdfXValidationResult result = null!;
        var worker = new System.Threading.Thread(() => result = Validate(f), 256 * 1024);
        worker.Start();
        Assert.True(worker.Join(System.TimeSpan.FromMinutes(2)));

        Assert.DoesNotContain(result.Violations, v => v.Code == "X18");
        Assert.Contains(result.Informational, i => i.Code == "X99" && i.Message.Contains("X18"));
    }

    private static Fixture Spots(string first, string second)
        => new Fixture { PageResources = $"/ColorSpace << /CS0 {first} /CS1 {second} >>" };

    // ------------------------------------------------------------------
    //  X19 — no audio, video or 3-D annotations
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("3D", true)]
    [InlineData("Screen", true)]
    [InlineData("RichMedia", true)]
    [InlineData("Movie", true)]
    [InlineData("Sound", true)]
    [InlineData("Link", false)]
    public void MediaAnnotation_ReportsX19(string subtype, bool reported)
    {
        var fixture = new Fixture
        {
            PageExtra = $"/TrimBox [0 0 200 200] /Annots [<< /Type /Annot /Subtype /{subtype} /Rect [0 0 10 10] >>]",
        };
        Assert.Equal(reported, Codes(fixture).Contains("X19"));
        if (reported) Assert.Contains($"/{subtype} on page 1", Single("X19", fixture).Message);
    }

    // ------------------------------------------------------------------
    //  X24 — no annotations in the print area (Application Notes for PDF/X v4, §2.28)
    // ------------------------------------------------------------------

    /// <summary>
    /// The TrimBox is the print area when a page has no BleedBox: an annotation inside it is
    /// reported at every level, one beyond it is not, and one whose edge lies on the box's edge is
    /// outside it (touching is not overlapping).
    /// </summary>
    [Theory]
    [InlineData(PdfXConformanceLevel.X1a)]
    [InlineData(PdfXConformanceLevel.X3)]
    [InlineData(PdfXConformanceLevel.X4)]
    public void AnnotationInsideTheTrimBox_ReportsX24_OutsideOrTouchingDoesNot(PdfXConformanceLevel level)
    {
        var inside = Annots("<< /Type /Annot /Subtype /Text /Rect [50 50 70 70] >>");
        var x24 = Single("X24", inside, level);
        Assert.Contains("/Text at [50 50 70 70] on page 1 (overlaps the TrimBox)", x24.Message);

        Assert.DoesNotContain(Codes(Annots("<< /Type /Annot /Subtype /Text /Rect [-30 50 -10 70] >>"), level), c => c == "X24");
        Assert.DoesNotContain(Codes(Annots("<< /Type /Annot /Subtype /Text /Rect [200 50 220 70] >>"), level), c => c == "X24");
    }

    /// <summary>
    /// With a BleedBox the print area is the BleedBox: an annotation in the bleed margin is
    /// reported — except a PrinterMark, which may lie there but not over the TrimBox.
    /// </summary>
    [Fact]
    public void WithABleedBox_TheMarginIsPrintArea_ExceptForAPrinterMark()
    {
        const string boxes = "/TrimBox [0 0 200 200] /BleedBox [-10 -10 210 210]";
        var text = Annots("<< /Type /Annot /Subtype /Text /Rect [-9 50 -2 60] >>", boxes);
        Assert.Contains("overlaps the BleedBox", Single("X24", text).Message);

        Assert.DoesNotContain(Codes(Annots("<< /Type /Annot /Subtype /PrinterMark /Rect [-9 50 -2 60] >>", boxes)), c => c == "X24");
        var mark = Annots("<< /Type /Annot /Subtype /PrinterMark /Rect [-9 50 5 60] >>", boxes);
        Assert.Contains("/PrinterMark at [-9 50 5 60] on page 1 (overlaps the TrimBox)", Single("X24", mark).Message);

        // Outside the bleed as well: nothing.
        Assert.DoesNotContain(Codes(Annots("<< /Type /Annot /Subtype /Text /Rect [-30 50 -12 60] >>", boxes)), c => c == "X24");
    }

    /// <summary>A trap network lies over the page by definition and a popup draws nothing of its own.</summary>
    [Theory]
    [InlineData("TrapNet")]
    [InlineData("Popup")]
    public void TrapNetAndPopup_AreNotJudged(string subtype)
        => Assert.DoesNotContain(Codes(Annots($"<< /Type /Annot /Subtype /{subtype} /Rect [0 0 200 200] >>")), c => c == "X24");

    /// <summary>A widget is an annotation; a field in the print area is reported by its name, and
    /// an ArtBox stands in for the TrimBox.</summary>
    [Fact]
    public void AWidgetInThePrintArea_IsReportedByItsField_AgainstTheArtBox()
    {
        var f = new Fixture
        {
            PageExtra = "/ArtBox [0 0 200 200] /Annots [8 0 R]",
            CatalogExtra = "/AcroForm << /Fields [8 0 R] >>",
        };
        f.Objects.Add((8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [20 20 120 40] /F 4 >>"));
        var x24 = Single("X24", f);
        Assert.Contains("/Widget (field 'Name') at [20 20 120 40] on page 1 (overlaps the ArtBox)", x24.Message);
    }

    /// <summary>The list is capped; the count is not.</summary>
    [Fact]
    public void ManyAnnotationsInThePrintArea_AreCountedInFull_AndNamedUpToTheCap()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 12; i++) sb.Append($"<< /Type /Annot /Subtype /Square /Rect [{10 + i} 10 {15 + i} 15] >> ");
        var x24 = Single("X24", Annots(sb.ToString()));
        Assert.Contains("Found 12:", x24.Message);
        Assert.EndsWith(", ….", x24.Message);
        Assert.Equal(8, x24.Message.Split(new[] { "/Square at" }, System.StringSplitOptions.None).Length - 1);
    }

    private static Fixture Annots(string annots, string boxes = "/TrimBox [0 0 200 200]")
        => new Fixture { PageExtra = $"{boxes} /Annots [{annots}]" };

    // ------------------------------------------------------------------
    //  X02 and X25 — the output intent is the document's
    // ------------------------------------------------------------------

    private const string PageIntent = "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (FOGRA39) >>]";

    /// <summary>
    /// "PDF/X-4 does not allow for page-level output Intents, and only uses a document level output
    /// intent" (the PDF/X-6 Application Notes, §3.2): a page's /OutputIntents is an X25 at X-4, beside
    /// the document's or instead of it; X-1a and X-3, which predate the key, are not told X25.
    /// </summary>
    [Theory]
    [InlineData(PdfXConformanceLevel.X4, true)]
    [InlineData(PdfXConformanceLevel.X3, false)]
    [InlineData(PdfXConformanceLevel.X1a, false)]
    public void APageLevelOutputIntentIsAnX25AtX4(PdfXConformanceLevel level, bool reported)
    {
        var withPageIntent = new Fixture { PageExtra = "/TrimBox [0 0 200 200] " + PageIntent };
        Assert.Equal(reported, Codes(withPageIntent, level).Contains("X25"));
        Assert.DoesNotContain("X25", Codes(new Fixture(), level));
        if (reported) Assert.Contains("1 page(s)", Single("X25", withPageIntent, level).Message);
    }

    /// <summary>
    /// X02 asks the catalog: an intent on a page does not stand in for the document's, at any level —
    /// page /OutputIntents are PDF 2.0's (ISO 32000-2 Table 31), which these levels predate.
    /// </summary>
    [Theory]
    [InlineData(PdfXConformanceLevel.X4)]
    [InlineData(PdfXConformanceLevel.X3)]
    [InlineData(PdfXConformanceLevel.X1a)]
    public void X02_APageIntentDoesNotStandInForTheDocuments(PdfXConformanceLevel level)
    {
        var pageOnly = new Fixture { CatalogIntent = false, PageExtra = "/TrimBox [0 0 200 200] " + PageIntent };
        Assert.Contains("X02", Codes(pageOnly, level));
        Assert.DoesNotContain("X02", Codes(new Fixture(), level));
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>A one-component CMYK tint transform to <paramref name="c1"/>, written inline.</summary>
    private static string Tint4(string c1)
        => $"<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [{c1}] /N 1 >>";

    private static PdfXViolation Single(string code, Fixture f,
        PdfXConformanceLevel level = PdfXConformanceLevel.X4)
        => Assert.Single(Validate(f, level).Violations, v => v.Code == code);

    /// <summary>
    /// The codes found, without the ones the fixture does not set out to satisfy (X03 XMP, X04
    /// fonts, X10 Trapped, X11 identifier) — every other code is the test's business.
    /// </summary>
    private static List<string> Codes(Fixture f, PdfXConformanceLevel level = PdfXConformanceLevel.X4)
        => Validate(f, level).Violations.Select(v => v.Code)
            .Where(c => c != "X03" && c != "X10").ToList();

    private static PdfXValidationResult Validate(Fixture f, PdfXConformanceLevel level = PdfXConformanceLevel.X4)
        => PdfXValidator.Validate(new MemoryStream(f.Build()), level);

    private static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] Concat(string head, byte[] body, string tail)
        => Encoding.ASCII.GetBytes(head).Concat(body).Concat(Encoding.ASCII.GetBytes(tail)).ToArray();

    /// <summary>
    /// Objects 1-5: catalog, page tree, page, content, and the output intent's profile, whose
    /// <c>/N</c> is <see cref="IntentComponents"/> (null embeds none and names FOGRA39). Extra
    /// objects are numbered from 10 by the test.
    /// </summary>
    private sealed class Fixture
    {
        public string Content = "q Q";
        public string PageResources = "";
        public string PageExtra = "/TrimBox [0 0 200 200]";
        public string CatalogExtra = "";
        public int? IntentComponents = 4;
        public bool CatalogIntent = true;
        public List<(int Num, string Body)> Objects { get; } = new();
        public List<(int Num, byte[] Body)> RawObjects { get; } = new();

        public byte[] Build()
        {
            var bytes = new List<byte>();
            var offsets = new Dictionary<int, int>();
            void Emit(byte[] b) => bytes.AddRange(b);
            void EmitS(string s) => Emit(Encoding.Latin1.GetBytes(s));
            void Obj(int n, byte[] body)
            {
                offsets[n] = bytes.Count;
                EmitS($"{n} 0 obj\n");
                Emit(body);
                EmitS("\nendobj\n");
            }

            string intent = IntentComponents.HasValue
                ? "/OutputConditionIdentifier (Custom) /DestOutputProfile 5 0 R"
                : "/OutputConditionIdentifier (FOGRA39)";

            EmitS("%PDF-1.6\n");
            string catalogIntent = CatalogIntent ? $"/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX {intent} >>]" : "";
            Obj(1, Encoding.Latin1.GetBytes($"<< /Type /Catalog /Pages 2 0 R {catalogIntent} {CatalogExtra} >>"));
            Obj(2, Encoding.Latin1.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
            Obj(3, Encoding.Latin1.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] {PageExtra} " +
                                            $"/Contents 4 0 R /Resources << {PageResources} >> >>"));
            Obj(4, Encoding.Latin1.GetBytes($"<< /Length {Content.Length} >>\nstream\n{Content}\nendstream"));
            if (IntentComponents.HasValue)
                Obj(5, Encoding.Latin1.GetBytes($"<< /N {IntentComponents} /Length 1 >>\nstream\n \nendstream"));

            foreach (var (num, body) in Objects) Obj(num, Encoding.Latin1.GetBytes(body));
            foreach (var (num, body) in RawObjects) Obj(num, body);

            int highest = offsets.Keys.Max();
            int xref = bytes.Count;
            EmitS($"xref\n0 {highest + 1}\n0000000000 65535 f \n");
            for (int i = 1; i <= highest; i++)
                EmitS(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
            EmitS($"trailer\n<< /Size {highest + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return bytes.ToArray();
        }
    }
}
