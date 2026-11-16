// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The visible-signature builder signs under the caller's <see cref="SignatureParameters"/> rather
/// than a five-field object it builds itself.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>VisualSignatureBuilder.SignInternal</c> constructed a fresh
/// <c>SignatureParameters</c> carrying <c>CertificatePath</c>, <c>CertificatePassword</c>,
/// <c>SignatureFieldName</c>, <c>Reason</c> and <c>Location</c> — and dropped every other setting
/// on the floor. A caller asking for a visible signature with LTV, a timestamp, a PAdES baseline, a
/// non-default digest or a DocMDP permission got a visible <b>basic</b> signature and no error,
/// because the appearance is drawn either way. Nothing looks wrong until a verifier reports no
/// revocation data.</para>
///
/// <para><b>These assert on the produced bytes, not on the parameters object.</b> Checking that the
/// builder copied a property would be a restatement of the copy; what the caller cares about is
/// whether the setting reached the signature dictionary. So the output is scanned for the keys the
/// dropped settings produce — <c>/SubFilter</c> for the PAdES sub-filter and the <c>/Contents</c>
/// hole for the reserve — which is a second opinion on the writer.</para>
///
/// <para><b>Not asserted here: LTV and timestamping.</b> Both reach for a TSA over HTTP, so a test
/// that exercised them would be a network test wearing a unit test's clothes. The sub-filter and
/// reserve are the two settings that are observable offline, and they travel through the same
/// single hand-off — if they arrive, the object was passed rather than rebuilt.</para>
/// </remarks>
public sealed class VisualSignatureParameterPassthroughTests
{
    /// <summary>
    /// <c>UseEtsiPadesSubFilter</c> reaches the signature dictionary through the visible path.
    /// </summary>
    /// <remarks>
    /// The old overloads could not express this at all: the sub-filter is decided by
    /// <c>SignatureParameters</c>, and the visible path built its own. The control below signs the
    /// same document without the flag, so this cannot pass by the writer always emitting
    /// <c>ETSI.CAdES.detached</c>.
    /// </remarks>
    [Fact]
    public void ThePadesSubFilterSurvivesTheVisibleSignaturePath()
    {
        string signed = SignVisible(p => p.UseEtsiPadesSubFilter = true);

        Assert.Contains("/ETSI.CAdES.detached", signed);
    }

    /// <summary>
    /// The control: without the flag the same path writes the ordinary sub-filter.
    /// </summary>
    [Fact]
    public void WithoutThePadesFlagTheVisiblePathWritesTheOrdinarySubFilter()
    {
        string signed = SignVisible(_ => { });

        Assert.DoesNotContain("/ETSI.CAdES.detached", signed);
        Assert.Contains("/adbe.pkcs7.detached", signed);
    }

    /// <summary>
    /// <c>EstimatedSignatureSize</c> reaches the writer, so the <c>/Contents</c> hole is the size
    /// the caller asked for.
    /// </summary>
    /// <remarks>
    /// A second, independently-observable setting matters here: the sub-filter alone would also be
    /// satisfied by a builder that special-cased that one flag. The reserve is a number the caller
    /// chooses freely, and it is visible as the literal width of the hex hole — measured through
    /// the <c>/ByteRange</c> gap, the same way <c>SignatureByteRangeTests</c> locates it.
    /// </remarks>
    [Fact]
    public void TheEstimatedSignatureSizeSurvivesTheVisibleSignaturePath()
    {
        const int reserve = 9001; // Deliberately not a default and not a round number.

        string signed = SignVisible(p => p.EstimatedSignatureSize = reserve);

        Match m = Regex.Match(signed, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(m.Success, "the visible path produced no /ByteRange");

        int gapStart = int.Parse(m.Groups[2].Value);
        int gapEnd = int.Parse(m.Groups[3].Value);

        // The gap is `<` + 2 hex digits per reserved byte + `>`.
        Assert.Equal(reserve * 2 + 2, gapEnd - gapStart);
    }

    /// <summary>
    /// The three fields the builder owns are overwritten, and the field name in particular is the
    /// minted one rather than whatever the caller set.
    /// </summary>
    /// <remarks>
    /// This is the documented wart, pinned so it cannot drift into the opposite behaviour by
    /// accident: the name has to match the widget the builder just wrote, so honouring a
    /// caller-chosen one would leave the signature and its appearance naming different fields.
    /// </remarks>
    [Fact]
    public void TheFieldNameIsMintedByTheBuilderAndNotTakenFromTheCaller()
    {
        string signed = SignVisible(p => p.SignatureFieldName = "CallerChosenName");

        Assert.DoesNotContain("CallerChosenName", signed);
        Assert.Contains("/T (VisualSig", signed);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Signs the minimal fixture through the visible path with the caller's parameters, returning
    /// the output as Latin-1 text so structural keys can be located without a parser.
    /// </summary>
    private static string SignVisible(System.Action<SignatureParameters> configure)
    {
        var root = SigningTestCrypto.Ca("CN=Visible Passthrough Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Visible Passthrough Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        string pfxPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pfx");
        File.WriteAllBytes(pfxPath, pfx);
        try
        {
            var parameters = new SignatureParameters();
            configure(parameters);

            var appearance = new SignatureAppearanceOptions
            {
                Width = 180,
                Height = 60,
                SignerName = "Visible Passthrough",
            };

            using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
            using var output = new MemoryStream();

            VisualSignatureBuilder.Sign(input, output, pfxPath, pw, appearance, parameters);

            return Encoding.Latin1.GetString(output.ToArray());
        }
        finally
        {
            try { File.Delete(pfxPath); } catch (IOException) { }
        }
    }
}
