// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// PDF "shadow attack" coverage: a document whose visible content changed after signing, where every
/// CMS still verifies against its own byte range.
/// </summary>
/// <remarks>
/// <para><b>The defence being tested.</b> <c>PdfSignatureVerifier.VerifyAll</c> computes the furthest
/// byte any signature covers and, when that stops short of EOF, records a validation error on
/// <b>every</b> signature — not just the furthest. Its own comment explains why the "every" matters:
/// otherwise an attacker adds a decoy signature reaching deeper than a genuine one, the flag lands on
/// the decoy, and the genuine signature still reports <c>IsValid</c>.</para>
///
/// <para><b>The gap this file was written for, and what closed it.</b> Only signatures whose CMS
/// verifies may vouch for their coverage, which closes the decoy whose CMS is garbage. It does not
/// close the decoy whose CMS verifies — a <b>self-signed</b> certificate verifies against itself,
/// needs no trust anchor, and costs an attacker one key generation — and <c>IsValid</c> is
/// <c>IsIntegrityValid &amp;&amp; ValidationErrors.Count == 0</c>, so with the document-level flag
/// suppressed there was nothing left to fail. What closed it is a second, per-signature question:
/// <c>VerifyAll</c> asks <c>DocMdpAnalyzer.AnalyzeChangesSince</c> what the revisions after each
/// approval signature changed, at Table 257's level 3, and a rewritten page is a validation error on
/// the signature that signed the old one — whoever signed afterwards.</para>
///
/// <para><b>Read the control test first.</b>
/// <see cref="SignedThenTampered_WithoutADecoy_IsFlaggedAsAShadowAttack"/> is the same tampering with
/// no decoy appended. It must be red-free for the decoy test to mean anything: if the plain tamper
/// were not caught either, the decoy would not be laundering a defence, there would simply be no
/// defence, and both tests would be describing something else.</para>
/// </remarks>
public class SignatureShadowAttackTests
{
    /// <summary>The genuine signer's field.</summary>
    private const string GenuineField = "Signature1";

    /// <summary>What the attacker rewrites the page to say.</summary>
    private const string AttackerContent = "BT /F1 12 Tf 72 700 Td (PAY THE ATTACKER 1000000) Tj ET\n";

    /// <summary>
    /// The floor. An untampered signed document must verify clean, or every "is it flagged?" test
    /// below passes for the wrong reason — a fixture that never produced a valid signature refuses
    /// everything and proves nothing.
    /// </summary>
    [Fact]
    public void UntamperedSignature_VerifiesCleanAndCoversTheWholeDocument()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField);

        SignatureVerificationResult genuine = VerifyField(signed, GenuineField, ca.Cert);

        Assert.True(genuine.IsIntegrityValid);
        Assert.True(genuine.CoversEntireDocument);
        Assert.Empty(genuine.ValidationErrors);
        Assert.True(genuine.IsValid);
    }

    /// <summary>
    /// The control: sign, then append a revision that rewrites the page. With no decoy, the existing
    /// trailing-unsigned-bytes check must catch it.
    /// </summary>
    [Fact]
    public void SignedThenTampered_WithoutADecoy_IsFlaggedAsAShadowAttack()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField);
        byte[] tampered = AppendContentRevision(signed, AttackerContent);

        SignatureVerificationResult genuine = VerifyField(tampered, GenuineField, ca.Cert);

        // The CMS itself is untouched, so integrity over its OWN range still holds. That is exactly
        // why coverage has to be a separate question.
        Assert.True(genuine.IsIntegrityValid);
        Assert.False(genuine.CoversEntireDocument);
        Assert.NotEmpty(genuine.ValidationErrors);
        Assert.False(genuine.IsValid);
    }

    /// <summary>
    /// The attack: the same tampering, plus a second signature made with a certificate the attacker
    /// generated themselves, whose <c>/ByteRange</c> honestly reaches EOF.
    /// </summary>
    /// <remarks>
    /// The decoy needs no trust anchor and is never itself trusted — it does not have to be. It only
    /// has to be <i>integrity-valid</i>, which is the sole condition for vouching that the file is
    /// covered to EOF. Once it does, the genuine signature's warning is suppressed and the genuine
    /// signature reports <c>IsValid</c> over a document whose visible content it never signed.
    /// </remarks>
    /// <summary>
    /// The genuine signature names the rewritten page itself. The decoy still suppresses the
    /// document-level trailing-bytes error — that defence is unchanged — and it no longer matters,
    /// because the error that fails the signature is about what changed after its own range.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it was not closed by trust-gating.</b> The obvious fix — let only a signature
    /// whose chain anchors vouch for coverage — was implemented, shown to refuse conforming PAdES
    /// B-LT/B-LTA and cross-CA counter-signed documents, and reverted. See
    /// <see cref="CounterSignatureUnderAnotherCa_DoesNotInvalidateTheAnchoredSignature"/> and
    /// <c>MayVouchForCoverage</c>'s remarks. Trust is the wrong axis: what separates the attack from
    /// the legitimate case is <i>what the later revision changed</i>, not who signed it.</para>
    /// <para>The per-signature <c>Warning</c> is still asserted: it is stated about this signature's
    /// own <c>/ByteRange</c>, so no other signature can suppress it either.</para>
    /// </remarks>
    [Fact]
    public void SelfSignedDecoyReachingEof_TheGenuineSignatureReportsTheRewrittenPage()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField);
        byte[] tampered = AppendContentRevision(signed, AttackerContent);

        var attacker = SigningTestCrypto.SelfSignedLeaf("CN=Attacker");
        var (attackerPfx, attackerPw) = SigningTestCrypto.Pfx(attacker);
        byte[] attacked = SignedPdfFixtures.Sign(tampered, attackerPfx, attackerPw, "Decoy");

        SignatureVerificationResult genuine = VerifyField(attacked, GenuineField, ca.Cert);

        // The page's content stream is object 4; the change is reported against it, on this signature.
        Assert.Contains(genuine.ValidationErrors,
            e => e.StartsWith("Changed after signing:", StringComparison.Ordinal)
                 && e.IndexOf("obj 4", StringComparison.Ordinal) >= 0);
        Assert.Contains(genuine.MdpViolations, v => v.IndexOf("obj 4", StringComparison.Ordinal) >= 0);

        Assert.False(genuine.CoversEntireDocument);
        Assert.Contains(genuine.Warnings,
            w => w.IndexOf("outside its digest", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// <b>A permitted change is no error, and is said.</b> An annotation at level 3 can stand over the
    /// signed text and read as part of it; the signature stays as it was and the warning names the change.
    /// </summary>
    [Fact]
    public void APermittedChange_IsNamedInAWarning()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField);

        SignatureVerificationResult annotated = VerifyField(AppendAnnotationRevision(signed), GenuineField, ca.Cert);
        Assert.Empty(annotated.MdpViolations);
        Assert.Contains(annotated.Warnings, w => w.Contains("can redraw the page", StringComparison.Ordinal)
                                                 && w.Contains("obj 60: annotation added", StringComparison.Ordinal));

        SignatureVerificationResult untouched = VerifyField(signed, GenuineField, ca.Cert);
        Assert.DoesNotContain(untouched.Warnings, w => w.Contains("can redraw the page", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The whole forgery, with real signatures.</b> The rewritten content stream names itself
    /// something a later revision may add, and a self-signed decoy reaches the end of the file. No key
    /// on a stream changes what a reader draws from its bytes, so the genuine signature still reports it.
    /// </summary>
    [Theory]
    [InlineData("/Type /DocTimeStamp")]
    [InlineData("/Subtype /Square /Rect [0 0 1 1]")]
    [InlineData("/Type /Annot")]
    public void AdversarialContentStream_NamedSomethingPermitted_BehindADecoy_IsReported(string keys)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField);
        byte[] tampered = AppendContentRevision(signed, AttackerContent, keys);

        var attacker = SigningTestCrypto.SelfSignedLeaf("CN=Attacker");
        var (attackerPfx, attackerPw) = SigningTestCrypto.Pfx(attacker);
        byte[] attacked = SignedPdfFixtures.Sign(tampered, attackerPfx, attackerPw, "Decoy");

        SignatureVerificationResult genuine = VerifyField(attacked, GenuineField, ca.Cert);

        Assert.Contains(genuine.MdpViolations, v => v.IndexOf("obj 4", StringComparison.Ordinal) >= 0);
        Assert.False(genuine.IsValid);
    }

    /// <summary>
    /// <b>A revision of a cross-reference entry alone, behind the same decoy.</b> The earlier version of
    /// the content is in the bytes the signature covers, so nothing past the signature rewrites anything.
    /// </summary>
    [Fact]
    public void AdversarialCrossReferenceEntry_PointingAtAnEarlierVersion_BehindADecoy_IsReported()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] hidden = AppendContentRevision(SignedPdfFixtures.MinimalPdf(), AttackerContent);
        long hiddenAt = Encoding.ASCII.GetString(hidden).LastIndexOf("4 0 obj", StringComparison.Ordinal);
        byte[] visible = AppendContentRevision(hidden, "BT /F1 12 Tf 72 700 Td (what the signer saw) Tj ET\n");
        byte[] signed = SignedPdfFixtures.Sign(visible, pfx, pw, GenuineField);

        long prevXref = SignedPdfFixtures.FindStartXref(signed);
        string tail = "xref\n4 1\n" + hiddenAt.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n"
                      + $"trailer\n<< /Size 64 /Root 1 0 R /Prev {prevXref} >>\nstartxref\n{signed.Length}\n%%EOF\n";
        byte[] tampered = signed.Concat(Encoding.ASCII.GetBytes(tail)).ToArray();

        var attacker = SigningTestCrypto.SelfSignedLeaf("CN=Attacker");
        var (attackerPfx, attackerPw) = SigningTestCrypto.Pfx(attacker);
        byte[] attacked = SignedPdfFixtures.Sign(tampered, attackerPfx, attackerPw, "Decoy");

        SignatureVerificationResult genuine = VerifyField(attacked, GenuineField, ca.Cert);

        Assert.Contains(genuine.MdpViolations, v => v.IndexOf("obj 4", StringComparison.Ordinal) >= 0);
        Assert.False(genuine.IsValid);
    }

    /// <summary>
    /// The control for the change analysis: a later revision that adds an annotation — a Table 257
    /// level-3 change — leaves the earlier signature valid. Without this, the decoy test could pass by
    /// failing every signature that anything follows.
    /// </summary>
    [Fact]
    public void AnAnnotationAddedAfterSigning_LeavesTheSignatureValid()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField);
        byte[] annotated = AppendAnnotationRevision(signed);

        SignatureVerificationResult genuine = VerifyField(annotated, GenuineField, ca.Cert);

        Assert.False(genuine.CoversEntireDocument);
        Assert.Empty(genuine.MdpViolations);
        // The document-level trailing-bytes error still fires — nothing signed the annotation — and
        // that is the only error: the change analysis allowed the annotation.
        Assert.All(genuine.ValidationErrors,
            e => Assert.Contains("unsigned", e, StringComparison.Ordinal));
    }

    [Fact]
    public void SelfSignedDecoyReachingEof_DoesNotLaunderTheGenuineSignaturesCoverage()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField);
        byte[] tampered = AppendContentRevision(signed, AttackerContent);

        // The attacker's own certificate. No CA, no anchor, no cooperation from anyone.
        var attacker = SigningTestCrypto.SelfSignedLeaf("CN=Attacker");
        var (attackerPfx, attackerPw) = SigningTestCrypto.Pfx(attacker);
        byte[] attacked = SignedPdfFixtures.Sign(tampered, attackerPfx, attackerPw, "Decoy");

        SignatureVerificationResult genuine = VerifyField(attacked, GenuineField, ca.Cert);

        // Unchanged facts, asserted so a failure below cannot be blamed on a broken fixture.
        Assert.True(genuine.IsIntegrityValid);
        Assert.False(genuine.CoversEntireDocument);

        Assert.False(genuine.IsValid,
            "A signature that does not cover the bytes the document now renders reported IsValid, " +
            "because a self-signed decoy reaching EOF suppressed the shadow-attack warning for " +
            "every signature including this one. IsIntegrityValid is 'this CMS verifies against " +
            "its own embedded certificate', which a self-signed certificate satisfies for free — " +
            "so it is not a basis for letting one signature vouch for another's coverage.");
    }

    /// <summary>
    /// The same attack asked the way an application actually asks it: with the genuine signer
    /// anchored, is this document fully trusted?
    /// </summary>
    /// <remarks>
    /// Separate from the test above because it fails at a different place and a caller reaches it by
    /// a different route. <c>IsFullyTrusted</c> is what the README points a caller at for "the
    /// complete verdict", so a green answer here is the one that actually ships a forged document to
    /// a user behind a tick mark.
    /// </remarks>
    [Fact]
    public void SelfSignedDecoy_DoesNotLeaveTheGenuineSignatureFullyTrusted()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField);
        byte[] tampered = AppendContentRevision(signed, AttackerContent);

        var attacker = SigningTestCrypto.SelfSignedLeaf("CN=Attacker");
        var (attackerPfx, attackerPw) = SigningTestCrypto.Pfx(attacker);
        byte[] attacked = SignedPdfFixtures.Sign(tampered, attackerPfx, attackerPw, "Decoy");

        SignatureVerificationResult genuine = VerifyField(attacked, GenuineField, ca.Cert);

        Assert.False(genuine.IsFullyTrusted,
            "IsFullyTrusted is what the documentation offers as the complete verdict, and it " +
            "returned true for a signature covering only a superseded revision of the document.");
    }

    /// <summary>
    /// A legitimately counter-signed document whose EOF-covering signature is issued by a CA the
    /// verifier did <b>not</b> anchor must still report the anchored signature as valid.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the shape that made the first attempt at the F-1 fix wrong.</b> Gating
    /// coverage-vouching on <c>ChainStatus == Trusted</c> looks obviously right and quietly refuses
    /// conforming documents: in a two-party contract each signer is under their own CA, and in every
    /// PAdES B-LT/B-LTA file the object that reaches EOF is a <c>/DocTimeStamp</c> whose TSA chains
    /// to a commercial TSA root rather than to the signer's CA. The EOF-covering object is then
    /// excluded from the coverage calculation, the furthest *trusted* coverage stops at the first
    /// signature, and every signature in the file is told the document has unsigned trailing
    /// bytes.</para>
    /// <para>The asymmetry is what makes it especially bad: a caller who supplies no anchors at all
    /// is unaffected, so only the caller following the documented advice gets the false refusal.
    /// A guard that refuses legitimate input is a worse defect than the one it closes.</para>
    /// </remarks>
    [Fact]
    public void CounterSignatureUnderAnotherCa_DoesNotInvalidateTheAnchoredSignature()
    {
        var caA = SigningTestCrypto.Ca("CN=Root A");
        var leafA = SigningTestCrypto.Leaf(caA, "CN=Party A");
        var (pfxA, pwA) = SigningTestCrypto.Pfx(leafA, caA.Cert);

        var caB = SigningTestCrypto.Ca("CN=Root B");
        var leafB = SigningTestCrypto.Leaf(caB, "CN=Party B");
        var (pfxB, pwB) = SigningTestCrypto.Pfx(leafB, caB.Cert);

        // A signs, then B counter-signs. Nothing is tampered with: B's revision adds a signature and
        // nothing else, which is exactly what an incremental update is for.
        byte[] signedByA = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfxA, pwA, GenuineField);
        byte[] countersigned = SignedPdfFixtures.Sign(signedByA, pfxB, pwB, "Signature2");

        // The verifier anchors only its own party's CA — the ordinary case for a counter-signed
        // contract, and the one the trust-gated predicate got wrong.
        SignatureVerificationResult a = VerifyField(countersigned, GenuineField, caA.Cert);

        Assert.True(a.IsIntegrityValid);
        Assert.Empty(a.ValidationErrors);
        Assert.True(a.IsValid,
            "The anchored signature was reported invalid because the signature that covers the rest " +
            "of the file is issued by a CA this verifier does not anchor. Nothing in the document is " +
            "wrong: A signed, B counter-signed, and B's revision added only a signature. Trust in " +
            "one signature is not a precondition for another signature's bytes being intact.");
    }

    // ------------------------------------------------------------------ helpers

    private static SignatureVerificationResult VerifyField(
        byte[] pdf, string fieldName, Org.BouncyCastle.X509.X509Certificate anchor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { anchor },
        });

        Assert.NotEmpty(results);

        SignatureVerificationResult match = results.FirstOrDefault(r => r.FieldName == fieldName);
        Assert.True(match != null,
            $"No signature field named '{fieldName}' in the verified set " +
            $"[{string.Join(", ", results.Select(r => r.FieldName ?? "<null>"))}] — the fixture did " +
            "not produce the document this test describes.");
        return match;
    }

    /// <summary>
    /// Appends an incremental revision that adds a Square annotation (object 60 — well clear of the
    /// signature dictionary and field the signer allocated at 6 and 7) to the page (object 3,
    /// rewritten with an <c>/Annots</c> array and nothing else changed) — the shape a later
    /// annotator's save has, and a change Table 257 permits at level 3.
    /// </summary>
    private static byte[] AppendAnnotationRevision(byte[] pdf)
    {
        long prevXref = SignedPdfFixtures.FindStartXref(pdf);

        var body = new StringBuilder();
        long pageOffset = pdf.Length;
        body.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                    "/Resources << /Font << /F1 5 0 R >> >> /Annots [60 0 R] >>\nendobj\n");
        long annotOffset = pdf.Length + body.Length;
        body.Append("60 0 obj\n<< /Type /Annot /Subtype /Square /Rect [10 10 50 50] /F 4 /C [1 0 0] >>\nendobj\n");

        long xrefOffset = pdf.Length + body.Length;
        body.Append("xref\n3 1\n").AppendFormat("{0:D10} 00000 n \n", pageOffset);
        body.Append("60 1\n").AppendFormat("{0:D10} 00000 n \n", annotOffset);
        body.Append($"trailer\n<< /Size 64 /Root 1 0 R /Prev {prevXref} >>\n")
            .Append($"startxref\n{xrefOffset}\n%%EOF\n");

        byte[] tail = Encoding.ASCII.GetBytes(body.ToString());
        var grown = new byte[pdf.Length + tail.Length];
        Array.Copy(pdf, grown, pdf.Length);
        Array.Copy(tail, 0, grown, pdf.Length, tail.Length);
        return grown;
    }

    /// <summary>
    /// Appends an incremental revision replacing the page's content stream (object 4), leaving every
    /// previously written byte untouched.
    /// </summary>
    /// <remarks>
    /// This is the attacker's half of a shadow attack and it is deliberately legal PDF: an
    /// incremental update is how a signed document is meant to grow, and nothing about the bytes is
    /// malformed. What makes it an attack is only that the earlier signature's <c>/ByteRange</c>
    /// ends before it.
    /// </remarks>
    private static byte[] AppendContentRevision(byte[] pdf, string newContent, string keys = "")
    {
        long prevXref = SignedPdfFixtures.FindStartXref(pdf);

        var body = new StringBuilder();
        long contentOffset = pdf.Length;

        body.Append($"4 0 obj\n<< {keys} /Length {newContent.Length} >>\nstream\n")
            .Append(newContent)
            .Append("endstream\nendobj\n");

        long xrefOffset = pdf.Length + body.Length;
        body.Append("xref\n4 1\n").AppendFormat("{0:D10} 00000 n \n", contentOffset);
        body.Append($"trailer\n<< /Size 64 /Root 1 0 R /Prev {prevXref} >>\n")
            .Append($"startxref\n{xrefOffset}\n%%EOF\n");

        byte[] tail = Encoding.ASCII.GetBytes(body.ToString());
        var grown = new byte[pdf.Length + tail.Length];
        Array.Copy(pdf, grown, pdf.Length);
        Array.Copy(tail, 0, grown, pdf.Length, tail.Length);
        return grown;
    }
}
