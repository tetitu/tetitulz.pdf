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
/// Two PDF/X defects that the fixtures in <c>PdfXValidatorTests</c> could not reach.
///
/// <para><b>X08 and ICCBased.</b> <c>ClassifyColorSpace</c> condemned every <c>[/ICCBased …]</c>
/// space whose profile declares <c>/N 3</c> without consulting the level's colour policy. Since
/// X08 began running at every level, an sRGB or AdobeRGB working space — the canonical
/// device-independent colour PDF/X-3 and PDF/X-4 exist to admit, and ordinary Illustrator and
/// InDesign output — was reported as a violation on exactly those levels, in a message whose own
/// first clause names ICCBased as allowed.</para>
///
/// <para><b>The page scan's cost model.</b> <c>WalkNestedResources</c> re-iterates every resource
/// dictionary collected so far on each round. The count of distinct dictionaries was capped and
/// the decoded bytes were capped, but the <i>edges</i> between them were not — so N dictionaries
/// all naming one shared M-entry <c>/XObject</c> dictionary cost N×M resolves per round, five
/// times over per page.</para>
/// </summary>
public class PdfXValidatorColourAndScanTests
{
    // ------------------------------------------------------------------
    //  X08 — ICCBased RGB is device-independent colour
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(PdfXConformanceLevel.X3)]
    [InlineData(PdfXConformanceLevel.X4)]
    public void IccBasedThreeComponentColourSpace_AtTheLevelsThatAdmitIt_DoesNotReportX08(
        PdfXConformanceLevel level)
    {
        var result = PdfXValidator.Validate(new MemoryStream(IccBasedPdf(3)), level);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// The counterpart. X-1a admits only 1- and 4-component output, so the same file is still a
    /// violation there — without this, gating the rule off entirely would satisfy the theory
    /// above.
    /// </summary>
    [Fact]
    public void IccBasedThreeComponentColourSpace_AtX1a_StillReportsX08()
    {
        var result = PdfXValidator.Validate(
            new MemoryStream(IccBasedPdf(3)), PdfXConformanceLevel.X1a);

        var x08 = Assert.Single(result.Violations, v => v.Code == "X08");
        Assert.Contains("ICCBased", x08.Message);
    }

    /// <summary>
    /// The proof that the colour check really runs on this fixture at X-3: swap the space for an
    /// uncalibrated DeviceRGB against the fixture's non-RGB output intent and X08 fires. Without
    /// it, "does not report X08" would also pass for a fixture the check never reached.
    /// </summary>
    [Fact]
    public void DeviceRgbColourSpace_AtX3_StillReportsX08()
    {
        var result = PdfXValidator.Validate(
            new MemoryStream(ColourSpacePdf("/DeviceRGB")), PdfXConformanceLevel.X3);

        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// An uncoloured tiling pattern paints in its base space (ISO 32000-1 §8.7.3.3): the `scn`
    /// operands are DeviceRGB components when the base is <c>/DeviceRGB</c>, so
    /// <c>[/Pattern /DeviceRGB]</c> is RGB on a PDF/X-1a page and X08 reaches it through the base.
    /// A CMYK base is allowed, and a coloured pattern — bare <c>/Pattern</c> — names no space at
    /// all. Not descended, the RGB case would validate clean.
    /// </summary>
    [Theory]
    [InlineData("[/Pattern /DeviceRGB]", true)]
    [InlineData("[/Pattern /DeviceCMYK]", false)]
    [InlineData("/Pattern", false)]
    public void PatternColourSpace_IsJudgedByItsBaseSpace_AtX1a(string colourSpace, bool reportsX08)
    {
        var result = PdfXValidator.Validate(
            new MemoryStream(ColourSpacePdf(colourSpace)), PdfXConformanceLevel.X1a);

        Assert.Equal(reportsX08, result.Violations.Any(v => v.Code == "X08"));
    }

    /// <summary>A four-component ICC profile is legal everywhere, at every level.</summary>
    [Theory]
    [InlineData(PdfXConformanceLevel.X1a)]
    [InlineData(PdfXConformanceLevel.X3)]
    [InlineData(PdfXConformanceLevel.X4)]
    public void IccBasedFourComponentColourSpace_DoesNotReportX08(PdfXConformanceLevel level)
    {
        var result = PdfXValidator.Validate(new MemoryStream(IccBasedPdf(4)), level);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    // ------------------------------------------------------------------
    //  The page scan's per-entry budget
    // ------------------------------------------------------------------

    /// <summary>
    /// 1 010 form XObjects, each with its own <c>/Resources</c> naming the <b>same</b> 1 010-entry
    /// <c>/XObject</c> dictionary. Round one collects 1 010 resource dictionaries; round two walks
    /// 1 011 × 1 010 edges, and each of the five checks that scan a page pays it again. The file is
    /// around 130 KB — the same shape at 4 096 × 50 000, which is a 1.3 MB file, is 10^9
    /// operations.
    ///
    /// <para>The assertion is the guard's own evidence — the X99 that says the scan was partial —
    /// not a wall clock. A scan that quietly stops walking and reports nothing is the failure this
    /// exists to prevent: "found no forbidden colour" would then be indistinguishable from "did
    /// not look".</para>
    /// </summary>
    [Fact]
    public void SharedHugeXObjectDictionary_ExhaustsTheScanEntryBudget_AndReportsX99()
    {
        var result = PdfXValidator.Validate(
            new MemoryStream(SharedXObjectDictionaryPdf(1010)), PdfXConformanceLevel.X1a);

        Assert.Contains(result.Informational,
            i => i.Code == "X99" && i.Message.Contains("resource-dictionary entries"));
        Assert.True(result.IsPartial);
    }

    /// <summary>
    /// The control: an ordinary page must not trip the ceiling, or the guard would report every
    /// document as partially scanned and the signal would be worthless.
    /// </summary>
    [Fact]
    public void OrdinaryPage_DoesNotReportTheScanEntryBudgetX99()
    {
        var result = PdfXValidator.Validate(
            new MemoryStream(SharedXObjectDictionaryPdf(4)), PdfXConformanceLevel.X1a);

        Assert.DoesNotContain(result.Informational,
            i => i.Message.Contains("resource-dictionary entries"));
    }

    /// <summary>
    /// The soft-mask descent spends the same budget: 1 010 soft masks whose groups all
    /// name one 1 010-entry <c>/ExtGState</c> dictionary exhaust it and report X99, and four do not.
    /// Without the charge the walk would run its million entries and report nothing.
    /// </summary>
    [Theory]
    [InlineData(1010, true)]
    [InlineData(4, false)]
    public void SharedHugeExtGStateDictionary_ExhaustsTheScanEntryBudget_AndReportsX99(int count, bool expectX99)
    {
        var result = PdfXValidator.Validate(
            new MemoryStream(SharedExtGStateDictionaryPdf(count)), PdfXConformanceLevel.X1a);

        Assert.Equal(expectX99, result.Informational.Any(
            i => i.Code == "X99" && i.Message.Contains("resource-dictionary entries")));
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    /// <summary>A page whose resources name one colour space, written verbatim.</summary>
    private static byte[] ColourSpacePdf(string colourSpace) => BuildPdf(
        pageResources: $"<< /ColorSpace << /CS0 {colourSpace} >> >>");

    /// <summary>The same, for an <c>[/ICCBased …]</c> profile declaring <paramref name="n"/>.</summary>
    private static byte[] IccBasedPdf(int n) => BuildPdf(
        pageResources: "<< /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >>",
        extra: [(5, $"<< /N {n} /Length 1 >>\nstream\n \nendstream")]);

    /// <summary>
    /// <paramref name="count"/> form XObjects that all share one <c>/XObject</c> dictionary — the
    /// N-dictionaries-by-M-entries shape, in a file whose object count is only N.
    /// </summary>
    private static byte[] SharedXObjectDictionaryPdf(int count)
    {
        const int sharedDict = 5;
        const int firstForm = 6;

        var shared = new StringBuilder("<<");
        for (int i = 0; i < count; i++) shared.Append($" /X{i} {firstForm + i} 0 R");
        shared.Append(" >>");

        var extra = new List<(int, string)> { (sharedDict, shared.ToString()) };
        for (int i = 0; i < count; i++)
            extra.Add((firstForm + i,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                $"/Resources << /XObject {sharedDict} 0 R >> /Length 0 >>\nstream\n\nendstream"));

        return BuildPdf($"<< /XObject {sharedDict} 0 R >>", extra.ToArray());
    }

    /// <summary>
    /// The same shape through soft masks: <paramref name="count"/> <c>/ExtGState</c>
    /// entries in one shared dictionary, each naming a soft mask whose group's <c>/Resources</c>
    /// name that same dictionary again — so the groups' descent walks N × N <c>/ExtGState</c> entries
    /// in a file of ~2N objects.
    /// </summary>
    private static byte[] SharedExtGStateDictionaryPdf(int count)
    {
        const int sharedDict = 5;
        int firstState = 6, firstGroup = 6 + count;

        var shared = new StringBuilder("<<");
        for (int i = 0; i < count; i++) shared.Append($" /GS{i} {firstState + i} 0 R");
        shared.Append(" >>");

        var extra = new List<(int, string)> { (sharedDict, shared.ToString()) };
        for (int i = 0; i < count; i++)
        {
            extra.Add((firstState + i,
                $"<< /Type /ExtGState /SMask << /S /Luminosity /G {firstGroup + i} 0 R >> >>"));
            extra.Add((firstGroup + i,
                "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Group << /S /Transparency /CS /DeviceGray >> " +
                $"/Resources << /ExtGState {sharedDict} 0 R >> /Length 0 >>\nstream\n\nendstream"));
        }

        return BuildPdf($"<< /ExtGState {sharedDict} 0 R >>", extra.ToArray());
    }

    /// <summary>
    /// Objects 1-4 are the catalog, page tree, page and content stream; the catalog carries a
    /// GTS_PDFX output intent with no embedded profile, which is enough for the colour policy to
    /// treat the printing condition as non-RGB. Everything in <paramref name="extra"/> is emitted
    /// verbatim and must be numbered from 5 upward.
    /// </summary>
    private static byte[] BuildPdf(string pageResources, params (int Num, string Body)[] extra)
    {
        var bytes = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
        void Obj(int n, string body)
        {
            offsets[n] = bytes.Count;
            Emit($"{n} 0 obj\n{body}\nendobj\n");
        }

        const string content = "q Q";

        Emit("%PDF-1.6\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /OutputIntents [<< /Type /OutputIntent " +
               "/S /GTS_PDFX /OutputConditionIdentifier (FOGRA39) >>] >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /TrimBox [0 0 200 200] " +
               $"/Contents 4 0 R /Resources {pageResources} >>");
        Obj(4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        foreach (var (num, body) in extra) Obj(num, body);

        int highest = extra.Length == 0 ? 4 : extra.Max(e => e.Num);
        int xref = bytes.Count;
        Emit($"xref\n0 {highest + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= highest; i++)
            Emit(offsets.TryGetValue(i, out int off)
                ? $"{off:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {highest + 1} /Root 1 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return bytes.ToArray();
    }
}
