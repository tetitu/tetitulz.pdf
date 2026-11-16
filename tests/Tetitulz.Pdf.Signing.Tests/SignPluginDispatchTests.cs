// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Plugins;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Which <see cref="ISignPlugin"/> members a registered plugin actually sees called, pinned
/// through the public <see cref="PdfSigningOperations.Sign(Stream, Stream, SignatureParameters)"/>
/// entry point rather than by calling the plugin directly.
/// </summary>
/// <remarks>
/// <para>
/// <c>RunWithSignPlugins</c> — the single orchestrator behind all six <c>Sign</c> overloads —
/// invokes <see cref="ISignPlugin.OnBeforeSign"/> and <see cref="ISignPlugin.OnAfterSign"/>;
/// <c>PdfSigner</c> invokes <see cref="ISignPlugin.WrapSignature"/> on the CMS blob.
/// <see cref="ISignPlugin.ComputeDigest"/> has no call site anywhere in <c>src/</c>, yet the
/// interface summary once advertised it as the way to do HSM signing. A bank implementing it to
/// route the digest to a PKCS#11 device got a document signed with whatever software key happened
/// to be configured, and nothing said so.
/// </para>
/// <para>
/// The interface now states the dispatch reality at the type and at each member, and points at
/// <see cref="IExternalSigner"/> — the hook <c>PdfSigner</c> really calls — for HSM signing. These
/// tests hold that documentation to the code in both directions: the live hooks must keep running
/// <em>and honour what they return</em>, and <see cref="ISignPlugin.ComputeDigest"/> must not
/// quietly stay wired to nothing while the docs claim otherwise. <see cref="ISignPlugin.WrapSignature"/>
/// was in that second group until it was wired up; the tests below moved with the code rather than
/// being relaxed, which is the point of asserting dispatch rather than assuming it.
/// </para>
/// </remarks>
public sealed class SignPluginDispatchTests : IDisposable
{
    private readonly RecordingSignPlugin _plugin = new();

    public SignPluginDispatchTests() => PdfPluginHost.Instance.Register(_plugin);

    public void Dispose() => PdfPluginHost.Instance.Unregister(_plugin.Name);

    [Fact]
    public void SigningInvokesTheTwoDispatchedHooks()
    {
        Sign();

        Assert.Equal(1, _plugin.BeforeSignCalls);
        Assert.Equal(1, _plugin.AfterSignCalls);
    }

    /// <summary>
    /// <see cref="ISignPlugin.OnAfterSign"/> is chained: its return value must reach the caller's
    /// stream, or "post-processing" would be a no-op too.
    /// </summary>
    [Fact]
    public void OnAfterSignReplacementBytes_ReachTheCallersStream()
    {
        _plugin.AfterSignReplacement = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x39, 0x39 };

        byte[] output = Sign();

        Assert.Equal(_plugin.AfterSignReplacement, output);
    }

    /// <summary>
    /// The documented gap, asserted rather than assumed: <see cref="ISignPlugin.ComputeDigest"/> is
    /// not reached by a full signing operation, and cannot be — it returns raw signature octets
    /// where <c>/Contents</c> needs a complete CMS SignedData, which is why
    /// <see cref="IExternalSigner"/> is the supported HSM seam.
    /// </summary>
    [Fact]
    public void ComputeDigest_IsNotDispatched()
    {
        Sign();

        Assert.Equal(0, _plugin.ComputeDigestCalls);
    }

    // WrapSignature's dispatch is NOT asserted here, deliberately. This class registers its
    // recorder for the whole test, and other classes sign concurrently through `new PdfSigner()`
    // — which reaches WrapSignature but not RunWithSignPlugins — so a call COUNT on this hook
    // measures the process, not this test, and goes red whenever a neighbour signs. (The
    // OnBeforeSign/OnAfterSign counts above are stable for the mirror-image reason: only the
    // PdfSigningOperations entry point dispatches them.) WrapSignatureHookTests covers the hook
    // properly, with a plugin that stays a pass-through unless it sees its own private marker in
    // SignatureParameters.Reason, and asserts the replacement blob reaches /Contents.

    /// <summary>
    /// A post-processing hook that returns nothing leaves the signed document as it was. Null is
    /// "no change", not an empty file.
    /// </summary>
    [Fact]
    public void OnAfterSignReturningNothing_LeavesTheSignedDocumentIntact()
    {
        _plugin.AfterSignReplacement = null;

        byte[] output = Sign();

        Assert.True(output.Length > 0, "the signed document was replaced by nothing");
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(output, 0, 5), System.StringComparison.Ordinal);
    }

    private byte[] Sign()
    {
        var root = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(root);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        PdfSigningOperations.Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = "Signature1",
        });
        return output.ToArray();
    }

    private sealed class RecordingSignPlugin : ISignPlugin
    {
        public string Name => "Tests.RecordingSignPlugin";
        public string Description => "Counts which ISignPlugin members the pipeline reaches.";
        public Version Version => new(1, 0, 0);

        public int BeforeSignCalls { get; private set; }
        public int AfterSignCalls { get; private set; }
        public int ComputeDigestCalls { get; private set; }
        public int WrapSignatureCalls { get; private set; }
        public byte[]? AfterSignReplacement { get; set; }

        public void Initialize() { }
        public void Shutdown() { }

        public void OnBeforeSign(SignatureParameters parameters) => BeforeSignCalls++;

        public byte[]? ComputeDigest(string algorithm, byte[] bytesToSign)
        {
            ComputeDigestCalls++;
            return null;
        }

        public byte[]? WrapSignature(byte[] cmsBlob, SignatureParameters parameters)
        {
            WrapSignatureCalls++;
            return null;
        }

        public byte[]? OnAfterSign(byte[] signedPdfBytes, SignatureParameters parameters)
        {
            AfterSignCalls++;
            return AfterSignReplacement;
        }
    }
}
