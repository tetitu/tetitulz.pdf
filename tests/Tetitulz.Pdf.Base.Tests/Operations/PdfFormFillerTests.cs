// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Behaviour of <see cref="PdfFormFiller"/>: the value that is stored, and the appearance that
/// makes it visible.
///
/// <para>Every test here reads the <b>output</b> document back — the object that was written, or
/// the decoded content of the appearance stream. A fill that returns a valid PDF while changing
/// nothing observable is exactly the failure these replace: check boxes used to receive a
/// <c>/V</c> string and no <c>/AS</c> at all, which no viewer draws.</para>
/// </summary>
public class PdfFormFillerTests
{
    private const string Hebrew = "שלום";   // שלום
    // "house" — all three letters join, so every glyph differs from its isolated form.
    private const string Arabic = "بيت";

    // ================================================================
    //  Check boxes
    // ================================================================

    [Fact]
    public void Checkbox_TruthyValue_SetsWidgetAppearanceStateToTheOnState()
    {
        var doc = Fill(FormPdfFixtures.Checkbox(), ("agree", "true"), out var result);

        var widget = Dict(doc, 6);
        Assert.Equal("Yes", widget.GetName(PdfName.AS)?.Value);
        Assert.Equal("Yes", widget.GetName(PdfName.V)?.Value);
        Assert.Contains("agree", result.FilledFields);
    }

    /// <summary>
    /// A check box's value is a <b>name</b> (ISO 32000-1 §12.7.4.2.3), not a string. Storing
    /// <c>(Yes)</c> leaves the field un-selected however the appearance is drawn, so the type is
    /// as load-bearing as the spelling.
    /// </summary>
    [Fact]
    public void Checkbox_Value_IsWrittenAsAName_NotAString()
    {
        var doc = Fill(FormPdfFixtures.Checkbox(), ("agree", "Yes"), out _);

        var value = doc.ResolveObject(Dict(doc, 6)[PdfName.V]);
        Assert.IsType<PdfName>(value);
        Assert.IsNotType<PdfString>(value);
    }

    [Fact]
    public void Checkbox_FalsyValue_SelectsTheOffState()
    {
        var doc = Fill(FormPdfFixtures.Checkbox(startsChecked: true), ("agree", "false"), out _);

        var widget = Dict(doc, 6);
        Assert.Equal("Off", widget.GetName(PdfName.AS)?.Value);
        Assert.Equal("Off", widget.GetName(PdfName.V)?.Value);
    }

    /// <summary>
    /// The on-state is whatever the widget's <c>/AP /N</c> calls it — a common viewer writes <c>/Yes</c>,
    /// but a generated form may name it anything. Assuming <c>/Yes</c> would leave those unchecked.
    /// </summary>
    [Fact]
    public void Checkbox_UsesTheWidgetsOwnStateName_NotAssumedYes()
    {
        var doc = Fill(FormPdfFixtures.Checkbox(onState: "1"), ("agree", "on"), out _);

        var widget = Dict(doc, 6);
        Assert.Equal("1", widget.GetName(PdfName.AS)?.Value);
        Assert.Equal("1", widget.GetName(PdfName.V)?.Value);
    }

    [Fact]
    public void Checkbox_ValueMatchingNoState_IsRefusedAndLeavesTheFieldUntouched()
    {
        var doc = Fill(FormPdfFixtures.Checkbox(), ("agree", "banana"), out var result);

        var widget = Dict(doc, 6);
        Assert.Equal("Off", widget.GetName(PdfName.AS)?.Value);
        Assert.Equal("Off", widget.GetName(PdfName.V)?.Value);
        Assert.Empty(result.FilledFields);
        Assert.Contains(result.Warnings, w => w.Contains("banana") && w.Contains("Yes"));
    }

    // ================================================================
    //  Radio groups
    // ================================================================

    [Fact]
    public void RadioGroup_SelectsTheNamedKid_AndTurnsTheOtherOff()
    {
        var doc = Fill(FormPdfFixtures.RadioGroup(), ("colour", "Green"), out var result);

        Assert.Equal("Green", Dict(doc, 6).GetName(PdfName.V)?.Value);
        Assert.Equal("Off", Dict(doc, 7).GetName(PdfName.AS)?.Value);    // the /Red widget
        Assert.Equal("Green", Dict(doc, 8).GetName(PdfName.AS)?.Value);  // the /Green widget
        Assert.Contains("colour", result.FilledFields);
    }

    /// <summary>
    /// A signature field whose <c>/FT /Sig</c> sits on its widget kid is still refused — a text
    /// value must never be written onto a signature.
    /// </summary>
    /// <remarks>
    /// §12.7.3.1 distinguishes a widget kid by its lack of <c>/T</c>, so the group is the terminal
    /// field and it declares no <c>/FT</c> of its own. <c>FieldType</c> therefore answered
    /// <c>Unknown</c>, and <c>ApplyValue</c> fell past its <c>case PdfFieldType.Signature</c> guard
    /// into <c>ApplyVariableTextValue</c> — writing <c>/V</c> and an appearance onto a signature
    /// field, which is both meaningless and a tamper of exactly the object a signature covers.
    /// <para>Asserted on the WRITTEN document and on the warning, not on a return code: the failure
    /// wrote a value and reported success, so anything short of checking /V passes with the bug.</para>
    /// </remarks>
    [Fact]
    public void SignatureField_TypedOnItsWidget_IsStillRefused()
    {
        var doc = Fill(FormPdfFixtures.SignatureGroupWithTypeOnTheWidget(), ("sig", "typed in"),
                       out var result);

        Assert.Null(Dict(doc, 6)[PdfName.V]);
        Assert.DoesNotContain("sig", result.FilledFields);
        Assert.Contains(result.Warnings, w => w.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same fill against a group whose <c>/Kids</c> is an <b>indirect reference</b> — legal,
    /// routinely emitted, and it silently did nothing.
    /// </summary>
    /// <remarks>
    /// <c>PdfDictionary</c>'s typed getters do not follow a reference, so <c>/Kids 11 0 R</c> read
    /// as absent. That made the group look <b>terminal</b>, so <c>WidgetDictionaries</c> returned
    /// the group's own dictionary; it carries no <c>/AP /N</c>, so the on-state set came back empty
    /// and the fill was refused — the radio button never changed on screen.
    /// <para><c>PdfAcroForm.LoadFields</c> had already been fixed for exactly this on
    /// <c>/Fields</c>, with a comment describing the damage, 74 lines away in the same folder. The
    /// fix landed one level up and not one level down.</para>
    /// <para>Asserted as a <b>successful fill</b>, not as "no exception": the defect's signature is
    /// a refusal that returns cleanly, so anything short of checking the written state passes with
    /// the bug present.</para>
    /// </remarks>
    [Fact]
    public void RadioGroup_WithIndirectKids_StillSelectsTheNamedKid()
    {
        var doc = Fill(FormPdfFixtures.RadioGroupWithIndirectKids(), ("colour", "Green"),
                       out var result);

        Assert.Equal("Green", Dict(doc, 6).GetName(PdfName.V)?.Value);
        Assert.Equal("Off", Dict(doc, 7).GetName(PdfName.AS)?.Value);
        Assert.Equal("Green", Dict(doc, 8).GetName(PdfName.AS)?.Value);
        Assert.Contains("colour", result.FilledFields);
    }

    /// <summary>
    /// "true" cannot pick between /Red and /Green. Guessing would silently select whichever widget
    /// happened to be first in /Kids, so the fill is refused with the states named.
    /// </summary>
    [Fact]
    public void RadioGroup_AmbiguousTruthyValue_IsRefusedWithTheStatesNamed()
    {
        var doc = Fill(FormPdfFixtures.RadioGroup(), ("colour", "true"), out var result);

        Assert.Equal("Off", Dict(doc, 6).GetName(PdfName.V)?.Value);
        Assert.Equal("Off", Dict(doc, 7).GetName(PdfName.AS)?.Value);
        Assert.Equal("Off", Dict(doc, 8).GetName(PdfName.AS)?.Value);
        Assert.Contains(result.Warnings, w => w.Contains("Red") && w.Contains("Green"));
    }

    /// <summary>
    /// The widgets carry no <c>/T</c>, so they share the group's fully-qualified name. Matching
    /// them as fields in their own right would apply the value twice — once to the group and once
    /// per kid, stamping <c>/V</c> onto annotations that must not carry one.
    /// </summary>
    [Fact]
    public void RadioGroup_WidgetKids_DoNotReceiveTheirOwnValue()
    {
        var doc = Fill(FormPdfFixtures.RadioGroup(), ("colour", "Red"), out _);

        Assert.Null(Dict(doc, 7)[PdfName.V]);
        Assert.Null(Dict(doc, 8)[PdfName.V]);
    }

    // ================================================================
    //  /DA — the field's own font, size and colour
    // ================================================================

    [Fact]
    public void TextField_Appearance_UsesTheFontSizeAndColourFromDefaultAppearance()
    {
        byte[] pdf = FormPdfFixtures.TextField(fieldEntries: "/DA (/Helv 12 Tf 1 0 0 rg)");
        var doc = Fill(pdf, ("name", "Ada"), out _);

        string content = AppearanceContent(doc, 6);
        Assert.Contains("/Helv 12.00 Tf", content);
        Assert.Contains("1 0 0 rg", content);
        Assert.DoesNotContain("0 g", content);
    }

    /// <summary>
    /// The <c>/DA</c> font name is resolved through the AcroForm's <c>/DR</c>, so the appearance
    /// references the document's own font object rather than a fresh copy — which is both what
    /// makes the filled value match the rest of the form and what keeps one duplicate font
    /// dictionary per filled field out of the file.
    /// </summary>
    [Fact]
    public void TextField_Appearance_ReferencesTheDocumentsOwnFontFromDR()
    {
        var doc = Fill(FormPdfFixtures.TextField(), ("name", "Ada"), out _);

        var resources = doc.ResolveObject(AppearanceStream(doc, 6)[PdfName.Resources]) as PdfDictionary;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;

        var reference = Assert.IsType<PdfReference>(fonts[new PdfName("Helv")]);
        Assert.Equal(FormPdfFixtures.HelveticaObj, reference.ReferenceObjectNumber);
    }

    [Fact]
    public void TextField_WithHonorDefaultAppearanceOff_UsesTheCallersFontInstead()
    {
        byte[] pdf = FormPdfFixtures.TextField(fieldEntries: "/DA (/Helv 12 Tf 1 0 0 rg)");
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfFormFiller.Fill(input, output, Values(("name", "Ada")),
            new FormFillOptions { HonorDefaultAppearance = false, FontSize = 7 });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        string content = AppearanceContent(doc, 6);
        Assert.Contains("/F1 7.00 Tf", content);
        Assert.DoesNotContain("/Helv", content);
    }

    // ================================================================
    //  /Q — quadding
    // ================================================================

    [Fact]
    public void TextField_RightQuadding_PushesTheTextToTheRightEdge()
    {
        byte[] pdf = FormPdfFixtures.TextField(
            rect: "[100 700 300 720]", fieldEntries: "/DA (/Helv 10 Tf 0 g) /Q 2");
        var doc = Fill(pdf, ("name", "Hi"), out _);

        // Box is 200 wide with a 2pt margin each side; "Hi" is measured from the AFM metrics.
        double textWidth = HelveticaWidth("Hi") * 10;
        Assert.Equal(2 + (196 - textWidth), FirstTextX(AppearanceContent(doc, 6)), precision: 1);
    }

    [Fact]
    public void TextField_DefaultQuadding_LeavesTheTextAtTheLeftMargin()
    {
        byte[] pdf = FormPdfFixtures.TextField(
            rect: "[100 700 300 720]", fieldEntries: "/DA (/Helv 10 Tf 0 g)");
        var doc = Fill(pdf, ("name", "Hi"), out _);

        Assert.Equal(2.0, FirstTextX(AppearanceContent(doc, 6)), precision: 1);
    }

    // ================================================================
    //  Text outside the field font's encoding
    // ================================================================

    /// <summary>
    /// Hebrew has no representation in a Standard-14 Type 1 font. The value is still stored
    /// exactly, but nothing is drawn and the caller is told so — the previous behaviour emitted
    /// the code points as raw Latin-1 bytes, which rendered as mojibake while
    /// <c>/NeedAppearances</c> claimed the appearance was authoritative.
    /// </summary>
    [Fact]
    public void UnicodeValue_WithNoFontSource_StoresTheValueAndReportsTheMissingAppearance()
    {
        var doc = Fill(FormPdfFixtures.TextField(), ("name", Hebrew), out var result);

        var field = Dict(doc, 6);
        Assert.Equal(Hebrew, (doc.ResolveObject(field[PdfName.V]) as PdfString)?.GetText());
        Assert.Null(field[PdfName.AP]);

        Assert.Equal(new[] { "name" }, result.FieldsWithoutAppearance);
        Assert.True(result.NeedAppearances);
        Assert.Contains(result.Warnings, w => w.Contains("FontProvider"));

        var acroForm = Dict(doc, FormPdfFixtures.AcroFormObj);
        Assert.True((doc.ResolveObject(acroForm[PdfName.NeedAppearances]) as PdfBoolean)?.Value);
    }

    [Fact]
    public void LatinValue_WithAnAppearance_ClearsNeedAppearances()
    {
        var doc = Fill(FormPdfFixtures.TextField(), ("name", "Ada"), out var result);

        Assert.False(result.NeedAppearances);
        Assert.Empty(result.FieldsWithoutAppearance);
        var acroForm = Dict(doc, FormPdfFixtures.AcroFormObj);
        Assert.False((doc.ResolveObject(acroForm[PdfName.NeedAppearances]) as PdfBoolean)?.Value);
    }

    [EmbeddableFontFact(Hebrew)]
    public void UnicodeValue_WithASuppliedFont_EmbedsACompositeFontAndDrawsIt()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Hebrew);
        using var input = new MemoryStream(FormPdfFixtures.TextField());
        using var output = new MemoryStream();

        var result = PdfFormFiller.Fill(input, output, Values(("name", Hebrew)),
            new FormFillOptions { FontProvider = _ => fontBytes });

        Assert.Empty(result.FieldsWithoutAppearance);
        Assert.False(result.NeedAppearances);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        // The appearance font is a real composite font, not a Standard-14 stand-in.
        var resources = doc.ResolveObject(AppearanceStream(doc, 6)[PdfName.Resources]) as PdfDictionary;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        var appearanceFont = doc.ResolveObject(fonts[new PdfName("F1")]) as PdfDictionary;

        Assert.Equal("Type0", appearanceFont.GetName(PdfName.Subtype)?.Value);
        Assert.Equal("Identity-H", appearanceFont.GetName(PdfName.Encoding)?.Value);
        Assert.Single(Assert.IsType<PdfArray>(doc.ResolveObject(appearanceFont[PdfName.DescendantFonts])));
    }

    /// <summary>
    /// Hebrew is stored in logical order but must be <b>drawn</b> right-to-left, so the glyph ids
    /// in the content stream are the reverse of the logical sequence. Emitting them in logical
    /// order produces a page that reads backwards — and looks entirely plausible to anyone who
    /// cannot read it.
    /// </summary>
    [EmbeddableFontFact(Hebrew)]
    public void UnicodeValue_RightToLeftText_IsDrawnInVisualOrder()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Hebrew);
        using var input = new MemoryStream(FormPdfFixtures.TextField());
        using var output = new MemoryStream();

        PdfFormFiller.Fill(input, output, Values(("name", Hebrew)),
            new FormFillOptions { FontProvider = _ => fontBytes });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        string content = AppearanceContent(doc, 6);

        var match = Regex.Match(content, @"<([0-9A-F]+)> Tj");
        Assert.True(match.Success, "the appearance shows no hex-encoded glyph run: " + content);

        var drawn = new List<ushort>();
        string hex = match.Groups[1].Value;
        for (int i = 0; i + 4 <= hex.Length; i += 4)
            drawn.Add(ushort.Parse(hex.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        var program = TrueTypeFontProgram.Parse(fontBytes);
        var logical = Hebrew.Select(c =>
        {
            Assert.True(program.TryGetGlyph(c, out ushort gid));
            return gid;
        }).ToList();

        Assert.Equal(logical.AsEnumerable().Reverse(), drawn);
    }

    /// <summary>
    /// Arabic must be <b>shaped</b> before it is drawn: each letter takes an initial / medial /
    /// final form decided by its neighbours. Hebrew needs no such thing, which is why the RTL test
    /// above could pass while Arabic came out as a row of disconnected isolated letters.
    /// </summary>
    /// <remarks>
    /// Asserted as a difference against the isolated forms rather than against a hard-coded glyph
    /// list, so it holds for whichever font the host supplies. <c>بيت</c> ("house") is chosen
    /// because all three letters join: beh is initial, yeh medial, teh final — so a correct
    /// implementation shares <b>no</b> glyph with the unshaped rendering.
    /// <para>Generating an appearance makes us the producer, and shaping is the producer's job.
    /// The <i>render</i> path must never do this: there the producer already chose the glyphs.</para>
    /// </remarks>
    [EmbeddableFontFact(Arabic)]
    public void ArabicValue_IsShapedIntoJoinedForms()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Arabic);
        using var input = new MemoryStream(FormPdfFixtures.TextField());
        using var output = new MemoryStream();

        PdfFormFiller.Fill(input, output, Values(("name", Arabic)),
            new FormFillOptions { FontProvider = _ => fontBytes });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        var match = Regex.Match(AppearanceContent(doc, 6), @"<([0-9A-F]+)> Tj");
        Assert.True(match.Success, "the appearance shows no hex-encoded glyph run");

        var drawn = new List<ushort>();
        string hex = match.Groups[1].Value;
        for (int i = 0; i + 4 <= hex.Length; i += 4)
            drawn.Add(ushort.Parse(hex.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        // The unshaped rendering: the base letters' own glyphs, reversed for RTL.
        var program = TrueTypeFontProgram.Parse(fontBytes);
        var isolated = Arabic.Select(c => program.TryGetGlyph(c, out ushort g) ? g : (ushort)0)
                             .Reverse().ToList();

        Assert.Equal(isolated.Count, drawn.Count);          // still 1:1 — no ligatures formed
        Assert.NotEqual(isolated, drawn);                   // ...but every form is a joined one
        Assert.DoesNotContain(drawn, g => g == 0);
    }

    /// <summary>
    /// ...and <c>/ToUnicode</c> must still map back to the <b>base</b> letters. Recording the
    /// presentation forms would make a search for the word visibly on the page fail, and
    /// copy-paste yield code points no keyboard produces.
    /// </summary>
    [EmbeddableFontFact(Arabic)]
    public void ArabicValue_ToUnicodeMapsBackToTheBaseLetters()
    {
        byte[] fontBytes = EmbeddableFontProbe.BytesCovering(Arabic);
        using var input = new MemoryStream(FormPdfFixtures.TextField());
        using var output = new MemoryStream();

        PdfFormFiller.Fill(input, output, Values(("name", Arabic)),
            new FormFillOptions { FontProvider = _ => fontBytes });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        var resources = doc.ResolveObject(AppearanceStream(doc, 6)[PdfName.Resources]) as PdfDictionary;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        var appearanceFont = doc.ResolveObject(fonts[new PdfName("F1")]) as PdfDictionary;
        var toUni = doc.ResolveObject(appearanceFont[PdfName.ToUnicode]) as PdfStream;
        Assert.NotNull(toUni);

        string cmap = Encoding.Latin1.GetString(toUni.GetDecodedData());
        foreach (char c in Arabic)
            Assert.Contains($"<{(int)c:X4}>", cmap);
        // ...and none of the U+FE70–FEFF forms that were actually drawn.
        Assert.DoesNotMatch(new Regex(@"<FE[89A-F][0-9A-F]>"), cmap);
    }

    // ================================================================
    //  Reporting
    // ================================================================

    [Fact]
    public void Fill_NameThatMatchesNoField_IsReportedAsUnmatched()
    {
        Fill(FormPdfFixtures.TextField(), ("nosuchfield", "x"), out var result);

        Assert.Equal(new[] { "nosuchfield" }, result.UnmatchedFields);
        Assert.Empty(result.FilledFields);
    }

    [Fact]
    public void Fill_ValueLongerThanMaxLen_IsStoredUnchangedAndReported()
    {
        byte[] pdf = FormPdfFixtures.TextField(fieldEntries: "/DA (/Helv 10 Tf 0 g) /MaxLen 3");
        var doc = Fill(pdf, ("name", "Ada Lovelace"), out var result);

        Assert.Equal("Ada Lovelace",
            (doc.ResolveObject(Dict(doc, 6)[PdfName.V]) as PdfString)?.GetText());
        Assert.Contains(result.Warnings, w => w.Contains("MaxLen"));
    }

    [Fact]
    public void ChoiceField_ValueOutsideOpt_IsStoredAndReported()
    {
        var doc = Fill(FormPdfFixtures.ChoiceField(), ("country", "FR"), out var result);

        Assert.Equal("FR", (doc.ResolveObject(Dict(doc, 6)[PdfName.V]) as PdfString)?.GetText());
        Assert.Contains(result.Warnings, w => w.Contains("/Opt"));
    }

    [Fact]
    public void ChoiceField_ExportValueFromOpt_IsAcceptedWithoutComplaint()
    {
        Fill(FormPdfFixtures.ChoiceField(), ("country", "GB"), out var result);

        Assert.Contains("country", result.FilledFields);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// A signature is produced by signing, not by writing a string into <c>/V</c>. Filling one
    /// here would leave a field that claims to be signed and verifies as corrupt.
    /// </summary>
    [Fact]
    public void SignatureField_IsNeverFilled()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Fields [6 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (sig) /Rect [100 700 300 720] >>");

        var doc = Fill(pdf, ("sig", "signed!"), out var result);

        Assert.Null(Dict(doc, 6)[PdfName.V]);
        Assert.Empty(result.FilledFields);
        Assert.Contains(result.Warnings, w => w.Contains("Signing"));
    }

    /// <summary>
    /// A merged field/widget receives both <c>/V</c> and <c>/AS</c>. Those are two edits to one
    /// object number, and writing it twice in a single incremental update would leave the xref
    /// pointing at whichever landed last — losing the other edit.
    /// </summary>
    [Fact]
    public void MergedFieldAndWidget_ReceivesBothEditsInOneObject()
    {
        byte[] output = FillToBytes(FormPdfFixtures.Checkbox(), ("agree", "Yes"));

        // The fixture ends at its one %%EOF; everything after it is the revision this wrote.
        string text = Encoding.ASCII.GetString(output);
        string appended = text.Substring(text.IndexOf("%%EOF", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(appended, @"(?m)^6 0 obj"));
    }

    // ================================================================
    //  Hostile input
    // ================================================================

    [Fact]
    public void CyclicFieldTree_TerminatesWithoutFillingAnything()
    {
        using var input = new MemoryStream(FormPdfFixtures.CyclicFieldTree());
        using var output = new MemoryStream();

        var result = PdfFormFiller.Fill(input, output, Values(("outer", "x")), new FormFillOptions());

        // Neither node is terminal (each has a named kid), so nothing matches — the point is that
        // the walk stops at all rather than following /Kids back to its own parent forever.
        Assert.Equal(new[] { "outer" }, result.UnmatchedFields);
    }

    /// <summary>
    /// Everything this writes — the value strings and the appearance streams — would go in as
    /// clear text where the reader expects per-object ciphertext, so the form would open full of
    /// noise. Refusing is the same fail-closed rule <c>PdfImageReplacer</c> and the accessibility
    /// writers apply.
    /// </summary>
    /// <summary>
    /// <b>A password field is never filled.</b> ISO 32000-2 Table 229 bit 14: "it is imperative
    /// that PDF processors never store the value of the text field in the PDF file if this flag is
    /// set". Refused with the reason, like a signature field — never written and drawn in clear text,
    /// which is what the filler did.
    /// </summary>
    [Fact]
    public void PasswordField_IsNeverFilled()
    {
        byte[] input = FormPdfFixtures.TextField(fieldEntries: "/DA (/Helv 12 Tf 0 g) /Ff 8192");
        var doc = Fill(input, ("name", "hunter2"), out var result);

        Assert.Empty(result.FilledFields);
        Assert.Contains(result.Warnings, w => w.Contains("password", StringComparison.Ordinal));
        Assert.Null(Dict(doc, 6)[PdfName.V]);
        Assert.DoesNotContain("hunter2", Encoding.Latin1.GetString(FillToBytes(input, ("name", "hunter2"))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_OnAnEncryptedDocument_IsRefused()
    {
        using var plain = new MemoryStream(FormPdfFixtures.TextField());
        using var encrypted = new MemoryStream();
        Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(plain, encrypted,
            new Tetitulz.Pdf.Base.Encryption.EncryptionParameters
            {
                UserPassword = "u",
                OwnerPassword = "o",
                Algorithm = Tetitulz.Pdf.Base.Encryption.EncryptionAlgorithm.AES128,
            });

        using var input = new MemoryStream(encrypted.ToArray());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfFormFiller.Fill(input, output, Values(("name", "Ada")), new FormFillOptions()));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fill_DocumentWithNoAcroForm_Throws()
    {
        using var input = new MemoryStream(Tetitulz.Pdf.TestHelpers.MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();

        Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfFormFiller.Fill(input, output, Values(("name", "x")), new FormFillOptions()));
    }

    // ================================================================
    //  Display values (a Format script's text)
    // ================================================================

    /// <summary>
    /// A display value is drawn and its value stored: the field holds <c>1234.5</c> and shows
    /// <c>$1,234.50</c>. A field not named draws its value.
    /// </summary>
    [Fact]
    public void ADisplayValue_IsDrawn_AndTheValueIsStored()
    {
        using var input = new MemoryStream(FormPdfFixtures.TextField());
        var output = new MemoryStream();
        PdfFormFiller.Fill(input, output, Values(("name", "1234.5")), new FormFillOptions
        {
            DisplayValues = new Dictionary<string, string> { ["name"] = "$1,234.50" },
        });
        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        Assert.Equal("1234.5", (doc.ResolveObject(Dict(doc, 6)[PdfName.V]) as PdfString)?.GetText());
        string drawn = AppearanceContent(doc, 6);
        Assert.Contains("($1,234.50)", drawn);
        Assert.DoesNotContain("(1234.5)", drawn);

        // The control: no display value draws the value.
        var plain = Fill(FormPdfFixtures.TextField(), ("name", "1234.5"), out _);
        Assert.Contains("(1234.5)", AppearanceContent(plain, 6));

        // …and so does a display value for another field.
        using var input2 = new MemoryStream(FormPdfFixtures.TextField());
        var output2 = new MemoryStream();
        PdfFormFiller.Fill(input2, output2, Values(("name", "1234.5")), new FormFillOptions
        {
            DisplayValues = new Dictionary<string, string> { ["other"] = "$1,234.50" },
        });
        output2.Position = 0;
        using var other = PdfDocument.Open(output2);
        Assert.Contains("(1234.5)", AppearanceContent(other, 6));
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static Dictionary<string, string> Values(params (string Name, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>();
        foreach (var (name, value) in pairs) map[name] = value;
        return map;
    }

    private static byte[] FillToBytes(byte[] pdf, params (string Name, string Value)[] values)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfFormFiller.Fill(input, output, Values(values), new FormFillOptions());
        return output.ToArray();
    }

    /// <summary>Fills, reopens the result, and hands back the loaded document plus the report.</summary>
    private static PdfDocument Fill(byte[] pdf, (string Name, string Value) value, out FormFillResult result)
    {
        using var input = new MemoryStream(pdf);
        var output = new MemoryStream();
        result = PdfFormFiller.Fill(input, output, Values(value), new FormFillOptions());
        output.Position = 0;
        return PdfDocument.Open(output);
    }

    private static PdfDictionary Dict(PdfDocument doc, int objectNumber) =>
        Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(objectNumber));

    private static PdfStream AppearanceStream(PdfDocument doc, int fieldObjectNumber)
    {
        var ap = doc.ResolveObject(Dict(doc, fieldObjectNumber)[PdfName.AP]) as PdfDictionary;
        Assert.NotNull(ap);
        return Assert.IsType<PdfStream>(doc.ResolveObject(ap[PdfName.N]));
    }

    private static string AppearanceContent(PdfDocument doc, int fieldObjectNumber) =>
        Encoding.GetEncoding("iso-8859-1").GetString(AppearanceStream(doc, fieldObjectNumber).GetDecodedData());

    /// <summary>The x of the first <c>Td</c>, which is where the first line of text starts.</summary>
    private static double FirstTextX(string content)
    {
        var match = Regex.Match(content, @"(-?[\d.]+) (-?[\d.]+) Td");
        Assert.True(match.Success, "no Td operator in the appearance: " + content);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static double HelveticaWidth(string text)
    {
        double total = 0;
        foreach (char c in text)
            total += Standard14Metrics.GetWidthByUnicode("Helvetica", c);
        return total / 1000.0;
    }
}
