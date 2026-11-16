// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A field created by <see cref="PdfFormFieldBuilder"/> defines the font its appearance names —
/// on the appearance's own <c>/Resources</c> and on the form's <c>/DR</c>.
/// </summary>
/// <remarks>
/// <para><b>What this closes.</b> The generated content named a font (<c>/Helv</c> for text,
/// <c>/ZaDb</c> for a check box) that <b>nothing in the file defined</b>: the appearance carried no
/// <c>/Resources</c> and a newly created form carried no <c>/DR</c>. It was legal only because the
/// builder also writes <c>/NeedAppearances true</c>, asking the processor to regenerate — so a
/// consumer that honours the supplied appearance, or a caller that clears the flag, got an
/// unresolvable font. GAPS.md §6c carried it, and named the fix: write a <c>/DR</c> with the
/// standard-14 font the default appearance string names.</para>
///
/// <para><b>The same dependency hid a missing <c>/AP /N</c> level for as long as it existed</b>
///, which is why this is asserted
/// on the written bytes rather than trusted to the flag.</para>
/// </remarks>
public sealed class GeneratedFieldFontResourceTests
{
    // ── the form's /DR ───────────────────────────────────────────────────────────────

    [Fact]
    public void ATextFieldDefinesItsAppearanceFontOnTheFormsDefaultResources()
    {
        using PdfDocument doc = AddTextField();

        PdfDictionary fonts = DefaultResourceFonts(doc);

        PdfObject helv = fonts[new PdfName("Helv")];
        Assert.NotNull(helv);
        Assert.Equal("Helvetica", BaseFontOf(doc, helv));
    }

    /// <summary>
    /// A check box draws its tick through <c>/ZaDb</c>, so that is the name its form has to define —
    /// a different name from the text case, which is why both are asserted.
    /// </summary>
    [Fact]
    public void ACheckboxDefinesZapfDingbatsOnTheFormsDefaultResources()
    {
        using PdfDocument doc = AddCheckbox();

        PdfDictionary fonts = DefaultResourceFonts(doc);

        PdfObject zadb = fonts[new PdfName("ZaDb")];
        Assert.NotNull(zadb);
        Assert.Equal("ZapfDingbats", BaseFontOf(doc, zadb));
    }

    /// <summary>
    /// <b>ZapfDingbats keeps its built-in encoding.</b> §9.6.6.2 makes a named <c>/Encoding</c>
    /// override the font's own, so writing <c>/WinAnsiEncoding</c> here would replace the check-mark
    /// glyph with whatever Latin character shares its code — a tick that silently becomes a letter.
    /// </summary>
    [Fact]
    public void TheSymbolicFontIsNotGivenAnEncoding()
    {
        using PdfDocument doc = AddCheckbox();

        var zadb = (PdfDictionary)doc.ResolveObject(DefaultResourceFonts(doc)[new PdfName("ZaDb")]);

        Assert.Null(zadb[PdfName.Encoding]);
    }

    /// <summary>The text font does get one, so the test above is pinning the symbolic case.</summary>
    [Fact]
    public void TheTextFontIsGivenWinAnsiEncoding()
    {
        using PdfDocument doc = AddTextField();

        var helv = (PdfDictionary)doc.ResolveObject(DefaultResourceFonts(doc)[new PdfName("Helv")]);

        Assert.Equal("WinAnsiEncoding", ((PdfName)helv[PdfName.Encoding]).Value);
    }

    // ── the appearance's own /Resources ──────────────────────────────────────────────

    /// <summary>
    /// The appearance stream stands on its own: the font it names is defined on the stream, so a
    /// consumer that draws the supplied appearance needs neither the form's <c>/DR</c> nor
    /// <c>/NeedAppearances</c>.
    /// </summary>
    [Fact]
    public void TheAppearanceStreamDefinesTheFontItNames()
    {
        using PdfDocument doc = AddTextField();

        PdfStream appearance = NormalAppearanceOfFirstField(doc);

        // The name the content actually draws with, and the font that name resolves to — not
        // merely that a /Resources exists. An empty one would satisfy "non-null" and leave the
        // appearance exactly as unusable as it was.
        string drawn = System.Text.Encoding.GetEncoding("iso-8859-1")
            .GetString(appearance.GetDecodedData());
        Assert.Contains("/Helv", drawn, System.StringComparison.Ordinal);

        var resources = (PdfDictionary)doc.ResolveObject(appearance[PdfName.Resources]);
        var fonts = (PdfDictionary)doc.ResolveObject(resources[PdfName.Font]);

        Assert.Equal("Helvetica", BaseFontOf(doc, fonts[new PdfName("Helv")]));
    }

    /// <summary>
    /// <b>One object, named twice.</b> The <c>/DR</c> entry and the appearance's <c>/Resources</c>
    /// entry are the <i>same</i> indirect object — two font dictionaries that merely agreed would
    /// be a disagreement waiting to happen, and would also write the font twice into every file.
    /// </summary>
    [Fact]
    public void TheFormAndTheAppearanceNameTheSameFontObject()
    {
        using PdfDocument doc = AddTextField();

        var fromForm = (PdfReference)DefaultResourceFonts(doc)[new PdfName("Helv")];

        PdfStream appearance = NormalAppearanceOfFirstField(doc);
        var apResources = (PdfDictionary)doc.ResolveObject(appearance[PdfName.Resources]);
        var apFonts = (PdfDictionary)doc.ResolveObject(apResources[PdfName.Font]);
        var fromAppearance = (PdfReference)apFonts[new PdfName("Helv")];

        Assert.Equal(fromForm.ReferenceObjectNumber, fromAppearance.ReferenceObjectNumber);
    }

    // ---------------------------------------------------------------- helpers

    private static PdfDictionary DefaultResourceFonts(PdfDocument doc)
    {
        var acroForm = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);
        var dr = doc.ResolveObject(acroForm[PdfName.DR]) as PdfDictionary;
        Assert.NotNull(dr);

        var fonts = doc.ResolveObject(dr[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);
        return fonts;
    }

    private static string BaseFontOf(PdfDocument doc, PdfObject fontRef)
    {
        var font = (PdfDictionary)doc.ResolveObject(fontRef);
        return ((PdfName)font[PdfName.BaseFont]).Value;
    }

    private static PdfStream NormalAppearanceOfFirstField(PdfDocument doc)
    {
        var acroForm = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);
        var fields = (PdfArray)doc.ResolveObject(acroForm[PdfName.Fields]);
        var widget = (PdfDictionary)doc.ResolveObject(fields[0]);

        var ap = (PdfDictionary)doc.ResolveObject(widget[PdfName.AP]);
        var normal = doc.ResolveObject(ap[PdfName.N]);
        return Assert.IsAssignableFrom<PdfStream>(normal);
    }

    private static PdfDocument AddTextField()
    {
        using var output = new MemoryStream();
        using (var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            PdfFormFieldBuilder.AddTextField(input, output, new FormTextField
            {
                FieldName = "Name", PageIndex = 0, X = 50, Y = 700, Width = 200, Height = 20,
                // A value, so the appearance actually draws text and therefore names its font.
                DefaultValue = "Alice",
            });
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static PdfDocument AddCheckbox()
    {
        using var output = new MemoryStream();
        using (var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            PdfFormFieldBuilder.AddCheckbox(input, output, new FormCheckboxField
            {
                FieldName = "Agree", PageIndex = 0, X = 50, Y = 650, Width = 14, Height = 14,
            });
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }
}
