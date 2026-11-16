// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="SignatureFieldRegistration"/> — what the interactive form says after a signature field
/// is added, for the signer, the document timestamper and the visible-signature builder alike.
/// </summary>
/// <remarks>
/// <para>The timestamper tests reach the writer the way <c>SupersededObjectGenerationTests</c> does:
/// the revision is written before the TSA is asked, so an unreachable TSA leaves exactly the bytes
/// under test in the output.</para>
/// </remarks>
public sealed class SignatureFieldRegistrationTests
{
    // ------------------------------------------------------------------ names

    [Fact]
    public void AFreeNameIsKept()
    {
        Assert.Equal("Approval", SignatureFieldRegistration.UniqueName("Approval", Taken("Other")));
    }

    [Theory]
    [InlineData("Signature1", new[] { "Signature1" }, "Signature2")]
    [InlineData("Signature1", new[] { "Signature1", "Signature2" }, "Signature3")]
    [InlineData("DocTimeStamp1", new[] { "DocTimeStamp1" }, "DocTimeStamp2")]
    [InlineData("123", new[] { "123" }, "Signature2")]
    public void ATakenNameGetsTheFirstFreeNumber(string wanted, string[] taken, string expected)
    {
        Assert.Equal(expected, SignatureFieldRegistration.UniqueName(wanted, Taken(taken)));
    }

    /// <summary>§12.7.4.2: "a partial name shall not contain a PERIOD character".</summary>
    [Theory]
    [InlineData("parent.approval", "parent_approval")]
    [InlineData("a.b.c", "a_b_c")]
    public void APeriodIsNotAllowedInANewFieldsName(string wanted, string expected)
    {
        Assert.Equal(expected, SignatureFieldRegistration.UniqueName(wanted, Taken()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoNameAtAllGetsADefault(string? wanted)
    {
        Assert.Equal("Signature1", SignatureFieldRegistration.UniqueName(wanted!, Taken()));
    }

    [Fact]
    public void NamesAreCollectedFromNestedFieldsBothQualifiedAndLeaf()
    {
        var kid = Field("approval");
        var parent = Field("parent");
        parent.Set(PdfName.Kids, new PdfArray { kid });
        var form = new PdfDictionary();
        form.Set(PdfName.Fields, new PdfArray { parent, Field("Top") });

        var names = SignatureFieldRegistration.FieldNames(form, o => o);

        Assert.Equal(new[] { "Top", "approval", "parent", "parent.approval" }, names.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// A chain of direct field dictionaries has no object numbers, so the visited set cannot stop it;
    /// the depth cap does. Without one, this nesting overflows the stack and takes the process with it.
    /// </summary>
    [Fact]
    public void ADeeplyNestedFormIsWalkedToTheDepthCapWithoutOverflowingTheStack()
    {
        var top = Field("level0");
        var node = top;
        for (int i = 1; i < 200_000; i++)
        {
            var kid = Field("level" + i);
            node.Set(PdfName.Kids, new PdfArray { kid });
            node = kid;
        }
        var form = new PdfDictionary();
        form.Set(PdfName.Fields, new PdfArray { top });

        HashSet<string> names = null!;
        var worker = new System.Threading.Thread(() => names = SignatureFieldRegistration.FieldNames(form, o => o),
                                                 maxStackSize: 1024 * 1024);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "the walk did not return");

        Assert.Contains("level0", names);
        Assert.DoesNotContain("level199999", names);
    }

    // ------------------------------------------------------------------ the form

    [Fact]
    public void TheFormKeepsItsEntriesAndDeclaresSignatures()
    {
        var existing = new PdfDictionary();
        existing.Set(PdfName.DA, new PdfString("/Helv 0 Tf 0 g"));
        existing.Set(PdfName.SigFlags, new PdfNumber(0));

        var form = SignatureFieldRegistration.FormWithSignature(
            existing, new PdfObject[] { new PdfReference(7, 0) }, new PdfReference(42, 0));

        Assert.NotNull(form[PdfName.DA]);
        Assert.Equal(3, ((PdfNumber)form[PdfName.SigFlags]).IntValue);
        Assert.Equal(new[] { 7, 42 }, ((PdfArray)form[PdfName.Fields]).Select(f => ((PdfReference)f).ReferenceObjectNumber));
        // The existing dictionary is not the one changed.
        Assert.Equal(0, ((PdfNumber)existing[PdfName.SigFlags]).IntValue);
    }

    [Fact]
    public void AnAdoptedFieldIsNotListedAgain()
    {
        var form = SignatureFieldRegistration.FormWithSignature(
            null, new PdfObject[] { new PdfReference(42, 0) }, newField: null);

        Assert.Single((PdfArray)form[PdfName.Fields]);
    }

    // ------------------------------------------------------------------ through the writers

    /// <summary>
    /// A second document timestamp is a second field, so it needs a second name. The timestamper
    /// took the caller's name as given and wrote another <c>DocTimeStamp1</c>.
    /// </summary>
    [Fact]
    public void ADocumentTimestampTakesANameTheFormDoesNotAlreadyUse()
    {
        string revision = TimestampRevision(PdfWithSignatureField("DocTimeStamp1", sigFlags: 3));

        Assert.Contains("/T (DocTimeStamp2)", revision, StringComparison.Ordinal);
        Assert.DoesNotContain("/T (DocTimeStamp1)", revision, StringComparison.Ordinal);
    }

    /// <summary>
    /// ISO 32000-2 Table 225: a form with a signature field declares <c>SignaturesExist</c> and
    /// <c>AppendOnly</c>. The timestamper set the flags only on a form it created.
    /// </summary>
    [Fact]
    public void ADocumentTimestampAddedToAFormDeclaringNoSignaturesSetsTheFlags()
    {
        string revision = TimestampRevision(PdfWithSignatureField("Existing", sigFlags: 0));

        Assert.Contains("/SigFlags 3", revision, StringComparison.Ordinal);
        Assert.DoesNotContain("/SigFlags 0", revision, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignatureUnderADottedNameIsWrittenWithoutThePeriod()
    {
        var ca = SigningTestCrypto.Ca();
        var (pfx, password) = SigningTestCrypto.Pfx(SigningTestCrypto.Leaf(ca), ca.Cert);
        byte[] pdf = PdfWithSignatureField("Existing", sigFlags: 3);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = password,
            SignatureFieldName = "contract.approval",
        });

        string revision = Encoding.ASCII.GetString(output.ToArray(), pdf.Length, (int)output.Length - pdf.Length);
        Assert.Contains("/T (contract_approval)", revision, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static ISet<string> Taken(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    private static PdfDictionary Field(string name)
    {
        var field = new PdfDictionary();
        field.Set(PdfName.T, new PdfString(name));
        return field;
    }

    private static string TimestampRevision(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        // Everything under test is written before the network call; the throw is expected.
        Assert.ThrowsAny<Exception>(() =>
            PdfDocumentTimestamper.AddTimestamp(input, output, "http://127.0.0.1:1/tsa"));

        byte[] written = output.ToArray();
        Assert.True(written.Length > pdf.Length, "the writer must have appended past the copied original");
        return Encoding.ASCII.GetString(written, pdf.Length, written.Length - pdf.Length);
    }

    /// <summary>A one-page document whose form holds one signed signature field of the given name.</summary>
    private static byte[] PdfWithSignatureField(string name, int sigFlags)
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        Ascii("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags {sigFlags} /DA (/Helv 0 Tf 0 g) >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>\nendobj\n");
        Obj($"4 0 obj\n<< /FT /Sig /T ({name}) /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /P 3 0 R /V 5 0 R >>\nendobj\n");
        Obj("5 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.RFC3161 /ByteRange [0 0 0 0] /Contents <00> >>\nendobj\n");

        long xref = file.Position;
        int size = offsets.Count + 1;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (long o in offsets) Ascii(o.ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
}
