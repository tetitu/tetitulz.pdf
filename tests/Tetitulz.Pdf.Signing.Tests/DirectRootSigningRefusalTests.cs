// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Timestamping;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A trailer whose <c>/Root</c> is a <b>direct</b> dictionary has no object number to supersede, and
/// signing it must be refused rather than produce a file that cannot be read.
/// </summary>
/// <remarks>
/// <para><b>It wrote <c>0 0 obj</c> and reported success.</b> <c>GetObjectNumber</c> answers
/// <c>0</c> for a non-reference, and §7.5.4 reserves object 0 as the head of the free list — so the
/// signed catalog was emitted at a number no reader will follow and an xref entry was added for it.
/// The signature then verified over a document whose catalog cannot be found: the worst shape a
/// signing API has, because the caller is told it worked.</para>
///
/// <para><b>The guard already existed one assembly over.</b>
/// <c>Plugins.EthereumSign.PdfBlockchainSigner</c> carries the identical <c>GetObjectNumber</c>
/// helper <i>and</i> a <c>catalogObjNum &lt;= 0</c> refusal, under a comment naming this exact
/// outcome — "the proof went into a malformed file that reported success". The two writers in this
/// assembly shared the helper and never got the guard: the repository's documented "landed one
/// level short" shape.</para>
///
/// <para><b>The assertions are on the emitted bytes as well as the throw.</b> A refusal test that
/// only catches an exception cannot tell a guard from a crash, and the damage being prevented is
/// specifically <c>0 0 obj</c> reaching a file.</para>
/// </remarks>
public class DirectRootSigningRefusalTests
{
    [Fact]
    public void SigningADocumentWithADirectRootIsRefused()
    {
        using var input = new MemoryStream(DirectRootPdf(), writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(() => Sign(input, output));

        Assert.Contains("not an indirect reference", ex.Message);
        Assert.Contains("free list", ex.Message);
    }

    /// <summary>Nothing that could be mistaken for a signed file reaches the caller's stream.</summary>
    /// <remarks>
    /// The point of the guard is the <b>bytes</b>: without it the output held a valid-looking
    /// incremental revision whose catalog sat at object 0. A test that asserted only the throw
    /// would pass against a version that threw <i>after</i> writing.
    /// </remarks>
    [Fact]
    public void NoObjectZeroHeaderReachesTheOutput()
    {
        using var input = new MemoryStream(DirectRootPdf(), writable: false);
        using var output = new MemoryStream();

        try { Sign(input, output); } catch (PdfSigningException) { }

        Assert.DoesNotContain("0 0 obj", Encoding.Latin1.GetString(output.ToArray()));
    }

    /// <summary>
    /// The control: the same document with an <b>indirect</b> <c>/Root</c> is not refused for this
    /// reason.
    /// </summary>
    /// <remarks>
    /// Without it, "always throw" satisfies both cases above. The fixture has no certificate, so
    /// signing still fails — but on the signing material, not on the catalog, and that is the
    /// distinction being pinned.
    /// </remarks>
    [Fact]
    public void AnIndirectRootIsNotRefusedForThisReason()
    {
        using var input = new MemoryStream(IndirectRootPdf(), writable: false);
        using var output = new MemoryStream();

        var ex = Record.Exception(() => Sign(input, output));

        Assert.Null(ex);
        Assert.True(output.Length > 0, "an indirect /Root signs normally");
    }

    // ================================================================ the document timestamper

    /// <summary>
    /// The same refusal from <c>PdfDocumentTimestamper</c>, which would otherwise write the updated
    /// catalog as object 0 and point the new trailer's <c>/Root</c> at it, so the output opened to
    /// nothing. Refused before a byte is copied, so the output stays empty — and
    /// before the TSA is contacted, so the URL here is never dialled.
    /// </summary>
    [Fact]
    public void TimestampingADocumentWithADirectRootIsRefusedBeforeAnythingIsWritten()
    {
        using var input = new MemoryStream(DirectRootPdf(), writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfDocumentTimestamper.AddTimestamp(input, output, UnreachableTsa));

        Assert.Contains("not an indirect reference", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The control: with an indirect <c>/Root</c> the timestamper gets as far as the TSA — the
    /// failure is the unreachable responder, after the document has been copied out, not the catalog.
    /// </summary>
    [Fact]
    public void AnIndirectRootReachesTheTsaInstead()
    {
        using var input = new MemoryStream(IndirectRootPdf(), writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfDocumentTimestamper.AddTimestamp(input, output, UnreachableTsa));

        Assert.DoesNotContain("not an indirect reference", ex.Message);
        Assert.Contains("TSA", ex.Message);
        Assert.True(output.Length > 0, "the document was copied out before the TSA was dialled");
    }

    /// <summary>A loopback port nothing listens on: the connection is refused at once, with no network.</summary>
    private const string UnreachableTsa = "http://127.0.0.1:9/";

    // ------------------------------------------------------------------

    /// <summary>
    /// Real signing material, because the catalog guard sits <b>after</b> the certificate check.
    /// </summary>
    /// <remarks>
    /// The first draft passed <c>new SignatureParameters()</c> and got
    /// <c>"No certificate provided"</c> — so the fixture never reached the guard, and the two cases
    /// that did not assert on the message passed <b>vacuously</b>: nothing was written because
    /// signing had already refused for an unrelated reason. That is the "the fixture never reached
    /// the feature" failure this repository's guide names, and the only thing that caught it was
    /// asserting the message text.
    /// </remarks>
    private static void Sign(Stream input, Stream output)
    {
        var root = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(root);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = "Signature1",
        });
    }

    /// <summary>Object 1 is missing; the trailer carries the catalog inline instead.</summary>
    private static byte[] DirectRootPdf() => Build(
        trailerRoot: "<< /Type /Catalog /Pages 2 0 R >>",
        bodies: new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        });

    private static byte[] IndirectRootPdf() => Build(
        trailerRoot: "1 0 R",
        bodies: new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        });

    private static byte[] Build(string trailerRoot, IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root ").Append(trailerRoot).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
