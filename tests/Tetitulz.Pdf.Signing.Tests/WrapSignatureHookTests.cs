// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Plugins;
using Tetitulz.Pdf.Signing.Tests.Helpers;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="ISignPlugin.WrapSignature"/> is offered the CMS blob before it is written.
///
/// <para><b>The defect.</b> The hook had no call site anywhere in <c>src/</c>. Its own contract
/// advertises LTV embedding — "return a replacement CMS blob (e.g. with embedded OCSP/CRL)" — and
/// the blob a plugin returned was discarded, with nothing reporting that it had been ignored. A
/// host that implemented it got the un-augmented signature and no signal.</para>
///
/// <para><b>On the process-wide plugin host.</b> Registration is global, so this plugin is a
/// pass-through (<c>return null</c>, the contract's documented "leave unchanged") for every
/// signing call except one carrying a private marker in
/// <see cref="SignatureParameters.Reason"/>. A test signing concurrently in another class is
/// therefore unaffected by the registration window.</para>
/// </summary>
public sealed class WrapSignatureHookTests
{
    private const string Marker = "wrap-signature-hook-test";

    [Fact]
    public void ReplacementBlobFromThePlugin_IsWhatGetsWrittenToContents()
    {
        var plugin = new MarkerWrapPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            byte[] signed = Sign(Marker);

            Assert.True(plugin.Called, "WrapSignature was never invoked, so the fixture never " +
                                       "reached the code under test.");

            byte[] written = ReadContents(signed);
            // /Contents is zero-padded out to EstimatedSignatureSize, so compare the prefix.
            Assert.True(written.Length >= plugin.Replacement.Length);
            Assert.Equal(plugin.Replacement, written.Take(plugin.Replacement.Length).ToArray());

            // And the blob handed to the plugin was the real CMS, not an empty placeholder.
            Assert.True(plugin.Received!.Length > 500,
                $"The plugin was handed {plugin.Received.Length} bytes; that is not a CMS blob.");
            Assert.Equal(0x30, plugin.Received[0]); // DER SEQUENCE
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(plugin.Name);
        }
    }

    [Fact]
    public void PluginReturningNull_LeavesTheSignatureVerifiable()
    {
        var plugin = new MarkerWrapPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            // No marker, so the plugin defers — the documented pass-through escape.
            byte[] signed = Sign(reason: "ordinary signing");
            Assert.False(plugin.Called);

            using var ms = new MemoryStream(signed);
            using var doc = PdfDocument.Open(ms);
            var result = new PdfSignatureVerifier().Verify(doc, "Signature1");
            Assert.True(result.IsIntegrityValid,
                "A deferring plugin must not disturb the signature: " +
                string.Join("; ", result.ValidationErrors));
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(plugin.Name);
        }
    }

    // ---------------------------------------------------------------- plugin

    private sealed class MarkerWrapPlugin : ISignPlugin
    {
        public string Name => "wrap-signature-hook-test-plugin";
        public Version Version => new(1, 0);
        public string Description => "Test plugin: replaces the CMS blob for one marked call.";

        public bool Called { get; private set; }
        public byte[]? Received { get; private set; }

        /// <summary>A DER SEQUENCE header over filler, so it is distinguishable at a glance.</summary>
        public byte[] Replacement { get; } =
            new byte[] { 0x30, 0x82, 0x01, 0x00 }
                .Concat(Enumerable.Repeat((byte)0xA7, 256)).ToArray();

        public void Initialize() { }
        public void Shutdown() { }

        public void OnBeforeSign(SignatureParameters parameters) { }
        public byte[]? ComputeDigest(string algorithm, byte[] bytesToSign) => null;
        public byte[]? OnAfterSign(byte[] signedPdfBytes, SignatureParameters parameters) => null;

        public byte[]? WrapSignature(byte[] cmsBlob, SignatureParameters parameters)
        {
            if (parameters.Reason != Marker) return null; // defer for everyone else
            Called = true;
            Received = cmsBlob;
            return Replacement;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Sign(string reason)
    {
        var root = SigningTestCrypto.Ca("CN=Wrap Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Wrap Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);

        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("Wrap hook fixture"));
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            SignatureFieldName = "Signature1",
            Reason = reason,
        });
        return output.ToArray();
    }

    private static byte[] ReadContents(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var field = doc.AcroForm!.FindSignatureField("Signature1");
        Assert.True(field?.SignatureValue != null, "No signed field in the output.");
        return field!.SignatureValue.GetString(PdfName.Contents)!.RawBytes;
    }
}
