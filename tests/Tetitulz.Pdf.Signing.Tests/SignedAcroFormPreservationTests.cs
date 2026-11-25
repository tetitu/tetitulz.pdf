// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Signing adds a field to <c>/AcroForm</c> and sets <c>/SigFlags</c>. It must change nothing else
/// about the form.
/// </summary>
/// <remarks>
/// <para><c>BuildSignedCatalog</c> built a <b>fresh</b> <c>/AcroForm</c> carrying only
/// <c>/Fields</c> and <c>/SigFlags</c> — it never read the document's existing one. Because the
/// catalog is written wholesale, that destroyed <c>/DA</c>, <c>/DR</c>, <c>/NeedAppearances</c>,
/// <c>/CO</c> and <c>/XFA</c>.</para>
///
/// <para><b>What a user sees:</b> every pre-existing text field loses the default-appearance
/// resources it names, so a form that rendered before signing renders blank afterwards — and the
/// damage is done by the operation whose whole purpose is to attest that the document has not
/// changed.</para>
///
/// <para>The same method's <c>/Perms</c> block, twenty lines below, already copied the existing
/// dictionary and replaced only <c>/DocMDP</c>, with a comment explaining that building a fresh one
/// "would silently revoke those rights". The right pattern was in the same method the whole
/// time.</para>
/// </remarks>
public class SignedAcroFormPreservationTests
{
    private const int SigDictObjNum = 41;
    private const int SigFieldObjNum = 42;

    /// <summary>
    /// <b>The defect.</b> Every AcroForm key the document declared survives signing.
    /// </summary>
    [Theory]
    [InlineData("DA")]
    [InlineData("DR")]
    [InlineData("NeedAppearances")]
    [InlineData("CO")]
    [InlineData("XFA")]
    [InlineData("Q")]
    public void AnExistingAcroFormKeySurvivesSigning(string key)
    {
        PdfDictionary acroForm = SignedAcroForm();

        Assert.True(acroForm.ContainsKey(new PdfName(key)),
            $"/{key} was destroyed by signing; a field that names it renders blank afterwards");
    }

    /// <summary>The values survive too, not merely the keys.</summary>
    [Fact]
    public void TheDefaultAppearanceStringIsUnchanged()
    {
        PdfDictionary acroForm = SignedAcroForm();

        var da = acroForm[new PdfName("DA")] as PdfString;
        Assert.NotNull(da);
        Assert.Equal("/Helv 0 Tf 0 g", System.Text.Encoding.ASCII.GetString(da.RawBytes));
    }

    /// <summary>
    /// <b>The floor.</b> The two keys signing genuinely owns are still written — otherwise
    /// "preserve everything" would have become "change nothing", and the signature field would
    /// never be registered.
    /// </summary>
    [Fact]
    public void TheSignatureFieldAndSigFlagsAreStillWritten()
    {
        PdfDictionary acroForm = SignedAcroForm();

        var fields = acroForm[PdfName.Fields] as PdfArray;
        Assert.NotNull(fields);
        Assert.Contains(fields, o => o is PdfReference r && r.ReferenceObjectNumber == SigFieldObjNum);

        var sigFlags = acroForm[new PdfName("SigFlags")] as PdfNumber;
        Assert.NotNull(sigFlags);
        Assert.Equal(3, sigFlags.LongValue);
    }

    /// <summary>
    /// A document with no <c>/AcroForm</c> at all still gets one — the ordinary path, kept so the
    /// rule cannot make an existing form a precondition.
    /// </summary>
    [Fact]
    public void ADocumentWithNoAcroFormStillGetsOne()
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, new PdfName("Catalog"));

        var signed = PdfSigner.BuildSignedCatalog(
            catalog, new List<PdfObject>(), SigFieldObjNum, fieldWasAdopted: false,
            SigDictObjNum, DocMdpPermission.None);

        var acroForm = signed[PdfName.AcroForm] as PdfDictionary;
        Assert.True(acroForm != null, "no /AcroForm was created for a document that had none");

        // Asserting the CONTENTS, not merely that a dictionary came back: a form carrying an empty
        // /Fields registers no signature, and that is what an unfalsifiable NotNull would accept.
        var fields = acroForm[PdfName.Fields] as PdfArray;
        Assert.NotNull(fields);
        Assert.Contains(fields, o => o is PdfReference r && r.ReferenceObjectNumber == SigFieldObjNum);
        Assert.Equal(3, ((PdfNumber)acroForm[new PdfName("SigFlags")]).LongValue);
    }

    // ------------------------------------------------------------------

    private static PdfDictionary SignedAcroForm()
    {
        var signed = PdfSigner.BuildSignedCatalog(
            CatalogWithForm(), new List<PdfObject>(), SigFieldObjNum, fieldWasAdopted: false,
            SigDictObjNum, DocMdpPermission.None);

        var acroForm = signed[PdfName.AcroForm] as PdfDictionary;
        Assert.True(acroForm != null, "signing produced no /AcroForm at all");
        return acroForm;
    }

    /// <summary>A catalog whose form carries the keys a real filled-in AcroForm has.</summary>
    private static PdfDictionary CatalogWithForm()
    {
        var fontRes = new PdfDictionary();
        fontRes.Set(new PdfName("Helv"), new PdfReference(5, 0));

        var dr = new PdfDictionary();
        dr.Set(PdfName.Font, fontRes);

        var acroForm = new PdfDictionary();
        acroForm.Set(PdfName.Fields, new PdfArray { new PdfReference(9, 0) });
        acroForm.Set(new PdfName("DA"), new PdfString("/Helv 0 Tf 0 g"));
        acroForm.Set(new PdfName("DR"), dr);
        acroForm.Set(new PdfName("NeedAppearances"), PdfBoolean.True);
        acroForm.Set(new PdfName("CO"), new PdfArray { new PdfReference(9, 0) });
        acroForm.Set(new PdfName("XFA"), new PdfReference(11, 0));
        acroForm.Set(new PdfName("Q"), new PdfNumber(1));

        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, new PdfName("Catalog"));
        catalog.Set(PdfName.AcroForm, acroForm);
        return catalog;
    }
}
