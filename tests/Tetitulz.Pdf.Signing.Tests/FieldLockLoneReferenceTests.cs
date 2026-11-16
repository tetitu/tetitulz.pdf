// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A FieldMDP lock is enforced when the signature's <c>/Reference</c> is written as a <b>lone
/// dictionary</b> rather than Table 252's array.
/// </summary>
/// <remarks>
/// <para><b>The defect this closes failed open.</b> <c>DocMdpAnalyzer</c> accepted both shapes;
/// <c>FieldMdpAnalyzer</c> required an array. A file whose <c>/Reference</c> was a single
/// dictionary therefore had its DocMDP certification <b>honoured</b> — the lenient reading
/// governing the half that <i>grants</i> permissions — while its field lock was <b>silently
/// ignored</b>, the strict reading dropping the half that <i>enforces</i>. An author's "these
/// fields may not change once this person has signed" was discarded, and nothing reported it.
/// <c>FieldMdpAnalyzer</c>'s own summary says it "fails closed like its sibling"; this was the
/// case where it did not.</para>
///
/// <para><b>How the fixture is built, and why that is honest.</b> <c>PdfSigner</c> always writes
/// the array, so the lone-dictionary shape is produced by rewriting the two brackets of a signed
/// document into spaces. That keeps the file's length and every byte offset <b>identical</b>, so
/// the cross-reference table, the <c>/ByteRange</c> and the appended revision below all stay valid.
/// It does invalidate the CMS digest — the bytes under it changed — but
/// <see cref="FieldMdpAnalyzer"/> is pure-structure analysis and never consults it, which is
/// exactly why it can be tested this way. <see cref="TheFixtureReallyCarriesALoneDictionary"/>
/// proves the patch produced the shape rather than assuming it.</para>
/// </remarks>
public sealed class FieldLockLoneReferenceTests
{
    private static readonly Lazy<(byte[] Pfx, string Password)> Keys = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Lone Reference Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Lone Reference Signer");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }));

    private const string IncludeName = "<< /Type /SigFieldLock /Action /Include /Fields [(Name)] >>";
    private const int NameField = 5;

    // ── the claim ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lock is read and the changed field is reported, exactly as it is for the array form.
    /// </summary>
    [Fact]
    public void ALockWhoseReferenceIsALoneDictionary_IsStillEnforced()
    {
        byte[] signed = AsLoneReferenceDictionary(Sign(FormPdf(IncludeName)));
        byte[] changed = ChangeValue(signed, NameField, "Mallory");

        using var doc = Open(changed);
        var analysis = Assert.Single(FieldMdpAnalyzer.Analyze(doc, changed.Length));

        Assert.Equal("Include", analysis.Action);
        Assert.Equal(new[] { "Name" }, analysis.Fields);
        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.Contains("'Name'", StringComparison.Ordinal));
    }

    /// <summary>
    /// And an unchanged document is still clean — so the test above is reporting the change rather
    /// than the unusual <c>/Reference</c> shape.
    /// </summary>
    [Fact]
    public void ALoneDictionaryReference_OnAnUnchangedDocument_ReportsNoViolation()
    {
        byte[] signed = AsLoneReferenceDictionary(Sign(FormPdf(IncludeName)));

        using var doc = Open(signed);
        var analysis = Assert.Single(FieldMdpAnalyzer.Analyze(doc, signed.Length));

        Assert.True(analysis.IsCompliant);
        Assert.Empty(analysis.Violations);
    }

    // ── the fixture is what it claims to be ──────────────────────────────────────────

    /// <summary>
    /// The patch really produced a lone dictionary: one reference, carrying the FieldMDP transform,
    /// and <c>/Reference</c> is no longer an array. A refusal test whose input does not have the
    /// property in its name proves nothing.
    /// </summary>
    [Fact]
    public void TheFixtureReallyCarriesALoneDictionary()
    {
        byte[] signed = AsLoneReferenceDictionary(Sign(FormPdf(IncludeName)));

        using var doc = Open(signed);
        PdfDictionary signature = doc.AcroForm.GetSignatureFields()
            .Single(f => f.HasSignature).SignatureValue!;

        PdfObject reference = doc.ResolveObject(signature[PdfName.Reference]);
        Assert.IsNotType<PdfArray>(reference);
        var only = Assert.IsAssignableFrom<PdfDictionary>(reference);

        // And it is the FieldMDP transform, not some other reference that happens to be there.
        Assert.Equal("FieldMDP", ((PdfName)doc.ResolveObject(only[PdfName.TransformMethod])).Value);
    }

    /// <summary>
    /// The control: the untouched array form behaves the same way. The two shapes must not be two
    /// answers, which is the whole reason the reading was consolidated.
    /// </summary>
    [Fact]
    public void TheArrayFormReportsTheSameViolation()
    {
        byte[] changed = ChangeValue(Sign(FormPdf(IncludeName)), NameField, "Mallory");

        using var doc = Open(changed);
        var analysis = Assert.Single(FieldMdpAnalyzer.Analyze(doc, changed.Length));

        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.Contains("'Name'", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Replaces the brackets of the signature's <c>/Reference</c> array with spaces, leaving a lone
    /// dictionary and a file of exactly the same length.
    /// </summary>
    private static byte[] AsLoneReferenceDictionary(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);

        int key = text.IndexOf("/Reference", StringComparison.Ordinal);
        Assert.True(key >= 0, "the signed document carries no /Reference entry to rewrite");

        int open = text.IndexOf('[', key);
        Assert.True(open > key, "/Reference is not followed by an array");

        int depth = 0, close = -1;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && --depth == 0) { close = i; break; }
        }
        Assert.True(close > open, "the /Reference array is not closed");

        byte[] patched = (byte[])pdf.Clone();
        patched[open] = (byte)' ';
        patched[close] = (byte)' ';
        return patched;
    }

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static byte[] FormPdf(string lockEntry) => SignedPdfFixtures.Assemble(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R 7 0 R] /SigFlags 3 /DA (/Helv 0 Tf 0 g) >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /FT /Tx /T (Name) /V (Alice) /Type /Annot /Subtype /Widget /Rect [50 700 250 720] /F 4 /P 3 0 R >>",
        "<< /FT /Tx /T (Date) /V (2026-09-17) /Type /Annot /Subtype /Widget /Rect [50 660 250 680] /F 4 /P 3 0 R >>",
        "<< /FT /Sig /T (Sig) /Type /Annot /Subtype /Widget /Rect [50 600 250 640] /F 4 /P 3 0 R /Lock " + lockEntry + " >>",
        "null");

    private static byte[] Sign(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(Keys.Value.Pfx),
            CertificatePassword = Keys.Value.Password,
            SignatureFieldName = "Sig",
        });
        return output.ToArray();
    }

    /// <summary>Appends a revision giving field <paramref name="objectNumber"/> a new <c>/V</c>.</summary>
    private static byte[] ChangeValue(byte[] pdf, int objectNumber, string value)
    {
        using var doc = Open(pdf);
        var field = PdfDictionary.CloneShallow((PdfDictionary)doc.GetObject(objectNumber));
        field.Set(PdfName.V, new PdfString(value));

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(pdf, output,
            new[] { new IndirectObject(objectNumber, 0, field) },
            new IncrementalUpdateOptions { Document = doc });
        return output.ToArray();
    }
}
