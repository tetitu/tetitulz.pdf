// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Where a text field's <c>/MaxLen</c>, <c>/Q</c> and <c>/DA</c> may come from other than its own
/// dictionary: an ancestor field (all three are inheritable) and, for <c>/Q</c> and <c>/DA</c>, the
/// form-wide default on <c>/AcroForm</c> (ISO 32000-2 Tables 224, 228 and 232).
/// </summary>
/// <remarks>
/// <para><b><c>/MaxLen</c> was read off the terminal field's own dictionary, unresolved</b>, by both
/// the filler and the reader, while the flattener climbed. A comb whose group held the cell count
/// was filled as ordinary text under a warning that it had no <c>/MaxLen</c>.</para>
///
/// <para><b>The form-wide <c>/Q</c> was honoured by one site in five</b>, and that one read it
/// through <c>GetInteger</c>, which does not follow a reference. The reader did not consult it, nor
/// the form-wide <c>/DA</c>, so it reported an alignment and an appearance string the filler did
/// not use.</para>
///
/// <para>Object 6 is a grouping field with <c>/FT /Tx</c>; object 7 is its terminal kid, a merged
/// field and widget named <c>group.kid</c>; object 8 is whatever a case needs to reference.</para>
/// </remarks>
public class VariableTextInheritanceTests
{
    private const string GroupDa = "/DA (/Helv 10 Tf 0 g)";

    // ------------------------------------------------------------------
    //  /MaxLen — inheritable, and resolvable
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(GroupDa + " /MaxLen 4", "", "null")]        // on the group, inherited
    [InlineData(GroupDa, "/MaxLen 8 0 R", "4")]             // on the kid, written indirectly
    public void TheReaderReportsAnInheritedOrIndirectMaxLen(string group, string kid, string extra)
    {
        Assert.Equal(4, ReadKid(Pdf(group: group, kid: kid, extra: extra)).MaxLength);
    }

    /// <summary>The control: the nearest node wins, so a kid's own count is not replaced by its group's.</summary>
    [Fact]
    public void TheKidsOwnMaxLenStillWinsOverItsGroups()
    {
        Assert.Equal(3, ReadKid(Pdf(group: GroupDa + " /MaxLen 4", kid: "/MaxLen 3")).MaxLength);
    }

    [Theory]
    [InlineData(GroupDa + " /MaxLen 4", "", "null")]
    [InlineData(GroupDa, "/MaxLen 8 0 R", "4")]
    public void TheFillerWarnsAboutAValueLongerThanAnInheritedOrIndirectMaxLen(string group, string kid, string extra)
    {
        FormFillResult result = Fill(Pdf(group: group, kid: kid, extra: extra), "Hello", out _);

        Assert.Contains(result.Warnings, w => w.StartsWith("group.kid: ", System.StringComparison.Ordinal)
                                              && w.Contains("/MaxLen is 4"));
    }

    /// <summary>
    /// A comb (<c>/Ff</c> bit 25) whose cell count sits on the group is laid out one positioned run
    /// per character — two <c>Td</c>s for two characters — and draws no "no /MaxLen" warning.
    /// </summary>
    /// <remarks>
    /// The layout and the warning are separate reads in the filler, and both were raw; a fix to one
    /// would leave the other contradicting it, so both are asserted.
    /// </remarks>
    [Fact]
    public void ACombWhoseCellCountIsInheritedIsLaidOutAsAComb()
    {
        byte[] pdf = Pdf(group: GroupDa + " /MaxLen 4 /Ff 16777216", kid: "");

        using PdfDocument filled = Filled(pdf, "Ab", out FormFillResult result);

        Assert.Equal(2, Regex.Matches(AppearanceContent(filled, 7), @" Td\b").Count);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("comb flag"));
    }

    // ------------------------------------------------------------------
    //  /Q — inheritable, with a form-wide default
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/Q 2", "", "null", 2)]         // the form-wide default, nothing on the field
    [InlineData("/Q 8 0 R", "", "2", 2)]        // the same default, written indirectly
    [InlineData("/Q 2", "/Q 0", "null", 0)]     // the field's own /Q 0 is an answer, not an absence
    public void TheReaderFallsBackToTheFormWideQuadding(string acroForm, string kid, string extra, int expected)
    {
        Assert.Equal(expected, ReadKid(Pdf(acroForm: acroForm, group: GroupDa, kid: kid, extra: extra)).Quadding);
    }

    /// <summary>
    /// The filler honoured a direct form-wide <c>/Q</c> already; the indirect spelling drew
    /// left-aligned. "Hi" at 10 pt in a 200-wide box with a 2 pt margin each side.
    /// </summary>
    [Theory]
    [InlineData("/Q 2", "null")]
    [InlineData("/Q 8 0 R", "2")]
    public void TheFillerRightAlignsUnderAFormWideQuaddingHoweverItIsWritten(string acroForm, string extra)
    {
        using PdfDocument filled = Filled(Pdf(acroForm: acroForm, group: GroupDa, extra: extra), "Hi", out _);

        double textWidth = HelveticaWidth("Hi") * 10;
        Assert.Equal(2 + (196 - textWidth), FirstTextX(AppearanceContent(filled, 7)), precision: 1);
    }

    // ------------------------------------------------------------------
    //  /DA — the reader now reports the string the filler draws with
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/DA (/Helv 7 Tf 0 0 1 rg)", "null")]
    [InlineData("/DA 8 0 R", "(/Helv 7 Tf 0 0 1 rg)")]
    public void TheReaderFallsBackToTheFormWideDefaultAppearance(string acroForm, string extra)
    {
        Assert.Equal("/Helv 7 Tf 0 0 1 rg", ReadKid(Pdf(acroForm: acroForm, group: "", extra: extra)).DefaultAppearance);
    }

    /// <summary>
    /// The filler draws with the form-wide <c>/DA</c> when no node of the field's chain sets one.
    /// </summary>
    /// <remarks>
    /// It always did, and nothing asserted it: when the fallback moved into the shared
    /// <c>DefaultAppearanceOrFormDefault</c>, cutting the filler off from it left every form test
    /// green. The colour and the size are both asserted because the fallback without them draws
    /// <c>0 g</c> at an auto size, and either alone could coincide.
    /// </remarks>
    [Theory]
    [InlineData("/DA (/Helv 7 Tf 0 0 1 rg)", "null")]
    [InlineData("/DA 8 0 R", "(/Helv 7 Tf 0 0 1 rg)")]
    public void TheFillerDrawsWithTheFormWideDefaultAppearance(string acroForm, string extra)
    {
        using PdfDocument filled = Filled(Pdf(acroForm: acroForm, group: "", extra: extra), "Hi", out _);

        string content = AppearanceContent(filled, 7);
        Assert.Contains("0 0 1 rg\n", content);
        Assert.Contains(" 7.00 Tf\n", content);
    }

    /// <summary>The control: a <c>/DA</c> anywhere in the field's own chain wins over the form's.</summary>
    [Fact]
    public void TheGroupsDefaultAppearanceStillWinsOverTheForms()
    {
        Assert.Equal("/Helv 10 Tf 0 g",
            ReadKid(Pdf(acroForm: "/DA (/Helv 7 Tf 0 0 1 rg)", group: GroupDa)).DefaultAppearance);
    }

    // ------------------------------------------------------------------

    private static PdfFormFieldInfo ReadKid(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return Assert.Single(PdfFormReader.GetFields(ms), f => f.FullyQualifiedName == "group.kid");
    }

    private static FormFillResult Fill(byte[] pdf, string value, out byte[] output)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var sink = new MemoryStream();
        var result = PdfFormFiller.Fill(input, sink,
            new Dictionary<string, string> { ["group.kid"] = value }, new FormFillOptions());
        output = sink.ToArray();
        Assert.Contains("group.kid", result.FilledFields);
        return result;
    }

    private static PdfDocument Filled(byte[] pdf, string value, out FormFillResult result)
    {
        result = Fill(pdf, value, out byte[] output);
        return PdfDocument.Open(new MemoryStream(output));
    }

    private static string AppearanceContent(PdfDocument doc, int objectNumber)
    {
        var widget = Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(objectNumber));
        var ap = doc.ResolveObject(widget[PdfName.AP]) as PdfDictionary;
        Assert.NotNull(ap);
        var normal = Assert.IsType<PdfStream>(doc.ResolveObject(ap[PdfName.N]));
        return Encoding.Latin1.GetString(normal.GetDecodedData());
    }

    /// <summary>The x of the first <c>Td</c>, which is where the first line of text starts.</summary>
    private static double FirstTextX(string content)
    {
        var match = Regex.Match(content, @"(-?[\d.]+) (-?[\d.]+) Td");
        Assert.True(match.Success, "no Td operator in the appearance: " + content);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static double HelveticaWidth(string text)
        => text.Sum(c => Standard14Metrics.GetWidthByUnicode("Helvetica", c)) / 1000.0;

    private static byte[] Pdf(string acroForm = "", string group = GroupDa, string kid = "", string extra = "null")
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Fields [6 0 R] /DR << /Font << /Helv 5 0 R >> >> " + acroForm + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [7 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /T (group) /FT /Tx " + group + " /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /T (kid) /Parent 6 0 R /Rect [100 700 300 720] " + kid + " >>",
            extra,
        };
        return FormPdfFixtures.Build(bodies);
    }
}
