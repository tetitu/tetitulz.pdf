// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <c>PdfSignatureDetection.HasSignature</c> is the "is this document signed?" guard behind
/// <c>PdfOptimizer</c>, <c>PdfEncryptedRoundTrip</c> and <c>ConverterInputInspection</c> — the
/// refusal that stops sanitize, optimize, linearize, PDF/X conversion and re-encryption from
/// rewriting a file whose signature covers its bytes.
/// </summary>
/// <remarks>
/// <b><c>/FT</c> and <c>/V</c> are inheritable, so they need not be on the same dictionary.</b>
/// Table 220 makes them field attributes an ancestor may supply, and the common shape puts
/// <c>/T</c> and <c>/V</c> on the field with <c>/FT</c> on its widget kid. Requiring both on one
/// dictionary would report a genuinely signed document of that shape <b>unsigned</b>.
///
/// <para><b>The consequence would not be a mis-report.</b> The callers above would then rewrite the
/// file, leaving a signature that covers bytes which no longer exist — the exact outcome the guard
/// exists to prevent.</para>
///
/// <para><b>Two readers, one answer.</b> <c>Core.Forms</c>'s <c>InheritedFieldTypeTests</c> pins
/// this very fixture as signed for <c>PdfAcroForm</c>; these cases assert the GUARD's answer, since a
/// rule held by one reader can be missing from the other.</para>
/// </remarks>
public class SignatureDetectionInheritanceTests
{
    [Fact]
    public void ASignatureTypedOnItsWidgetWithTheValueOnTheGroupIsDetected()
    {
        Assert.True(HasSignature(Build(
            AcroFormCatalog("6 0 R"), PageTree(), Page("7 0 R"), EmptyStream(), Resources(),
            "<< /T (sig) /V 8 0 R /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Sig /Rect [100 150 180 170] >>",
            SignatureDictionary())));
    }

    /// <summary>
    /// The mirror shape: the group carries <c>/FT /Sig</c> and the widget kid carries the
    /// <c>/V</c>. Inheritance runs in this direction too.
    /// </summary>
    /// <remarks>
    /// Both directions are needed, and only one of them was covered at first: with <c>/FT</c> on
    /// the WIDGET, dropping type inheritance changes nothing, because the widget supplies its own.
    /// This is the case that kills that mutant.
    /// </remarks>
    [Fact]
    public void ASignatureTypedOnTheGroupWithTheValueOnItsWidgetIsDetected()
    {
        Assert.True(HasSignature(Build(
            AcroFormCatalog("6 0 R"), PageTree(), Page("7 0 R"), EmptyStream(), Resources(),
            "<< /T (sig) /FT /Sig /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /V 8 0 R /Rect [100 150 180 170] >>",
            SignatureDictionary())));
    }

    /// <summary>
    /// The ordinary merged shape — <c>/FT</c> and <c>/V</c> on one dictionary. The control: it
    /// always worked, so the case above is about inheritance and not about detection at all.
    /// </summary>
    [Fact]
    public void AMergedSignatureFieldIsStillDetected()
    {
        Assert.True(HasSignature(Build(
            AcroFormCatalog("6 0 R"), PageTree(), Page("6 0 R"), EmptyStream(), Resources(),
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (sig) /V 7 0 R /Rect [100 150 180 170] >>",
            SignatureDictionary())));
    }

    /// <summary>
    /// An <b>unsigned</b> signature field — <c>/FT /Sig</c> with no <c>/V</c> anywhere — is a
    /// placeholder, and rewriting it invalidates nothing.
    /// </summary>
    /// <remarks>
    /// This is the floor the inheritance fix must not break. Carrying values down the descent must
    /// not manufacture a <c>/V</c> that no ancestor set: without this case, "always return true"
    /// satisfies both cases above.
    /// </remarks>
    [Fact]
    public void AnEmptySignaturePlaceholderIsNotADetectedSignature()
    {
        Assert.False(HasSignature(Build(
            AcroFormCatalog("6 0 R"), PageTree(), Page("7 0 R"), EmptyStream(), Resources(),
            "<< /T (sig) /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Sig /Rect [100 150 180 170] >>")));
    }

    /// <summary>
    /// A value inherited from an ancestor whose type is NOT <c>/Sig</c> is not a signature: a text
    /// field's value must not turn a sibling into one.
    /// </summary>
    [Fact]
    public void AnInheritedValueUnderANonSignatureTypeIsNotASignature()
    {
        Assert.False(HasSignature(Build(
            AcroFormCatalog("6 0 R"), PageTree(), Page("7 0 R"), EmptyStream(), Resources(),
            "<< /T (name) /FT /Tx /V (typed text) /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [100 150 180 170] >>")));
    }

    /// <summary>
    /// A <c>/Kids</c> cycle must not hang the walk. Legal to write, illegal to follow.
    /// </summary>
    [Fact]
    public void ACyclicFieldTreeTerminates()
    {
        Assert.False(HasSignature(Build(
            AcroFormCatalog("6 0 R"), PageTree(), Page("7 0 R"), EmptyStream(), Resources(),
            "<< /T (root) /Kids [7 0 R] >>",
            "<< /T (loop) /Parent 6 0 R /Kids [7 0 R] >>")));
    }

    // ------------------------------------------------------------------

    private static bool HasSignature(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        return PdfSignatureDetection.HasSignature(
            doc.Trailer, n => doc.ResolveObject(new PdfReference(n, 0)));
    }

    private static string AcroFormCatalog(string firstField) =>
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + firstField + "] >> >>";

    private static string PageTree() => "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";

    private static string Page(string annot) =>
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots ["
        + annot + "] /Resources 5 0 R >>";

    private static string EmptyStream() => "<< /Length 0 >>\nstream\n\nendstream";

    private static string Resources() => "<< >>";

    private static string SignatureDictionary() =>
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
        + "/ByteRange [0 0 0 0] /Contents <00> >>";

    private static byte[] Build(params string[] bodies)
    {
        var acc = new List<byte>();
        var off = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        for (int i = 0; i < bodies.Length; i++)
        {
            off.Add(acc.Count);
            Emit($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int xref = acc.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in off) sb.Append($"{o:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        Emit(sb.ToString());
        return acc.ToArray();
    }
}
