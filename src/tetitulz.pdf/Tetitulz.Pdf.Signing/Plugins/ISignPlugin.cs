// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing.Plugins
{
    /// <summary>
    /// Plugin that participates in the PDF digital-signature pipeline.
    ///
    /// <para><b>Three of the four members are dispatched; <see cref="ComputeDigest"/> is not.</b>
    /// <see cref="PdfSigningOperations"/>'s <c>RunWithSignPlugins</c> — the single orchestrator
    /// behind every <c>Sign</c> overload — calls <see cref="OnBeforeSign"/> before the signer runs
    /// and <see cref="OnAfterSign"/> on the finished PDF bytes, each registered plugin in
    /// registration order; <c>PdfSigner</c> calls <see cref="WrapSignature"/> on the freshly
    /// generated CMS blob. <see cref="ComputeDigest"/> has <b>no call site anywhere in the
    /// library</b>, and an implementation of it is silently never invoked. This is stated here
    /// because the live hooks run on every signing call, which gives a plugin author every reason
    /// to assume the last one does as well — and a signing hook that is quietly skipped is the
    /// worst kind of no-op.</para>
    ///
    /// Use-cases, with the mechanism that actually carries each:
    /// <list type="bullet">
    ///   <item>PAdES / CAdES policy enforcement — implement <see cref="OnBeforeSign"/>. Dispatched.</item>
    ///   <item>Blockchain anchoring / post-processing — implement <see cref="OnAfterSign"/>. Dispatched.</item>
    ///   <item>
    ///     HSM / PKCS#11 / cloud-KMS signing — use
    ///     <see cref="Tetitulz.Pdf.Signing.Models.IExternalSigner"/> via
    ///     <c>SignatureParameters.ExternalSigner</c>, <b>not</b> <see cref="ComputeDigest"/>.
    ///     That is the supported path and <c>PdfSigner</c> really calls it. It is also the only
    ///     one that can work: a PDF signature's <c>/Contents</c> is a complete CMS/PKCS#7 blob,
    ///     and <see cref="ComputeDigest"/> returns raw signature octets with no way to supply the
    ///     signer certificate the CMS must carry — so its return value could not be embedded even
    ///     if it were offered.
    ///   </item>
    ///   <item>
    ///     Long-term validation (LTV) embedding — either set <c>SignatureParameters.EmbedLtv</c>
    ///     (with a TSA URL), which writes a <c>/DSS</c> revision, or implement
    ///     <see cref="WrapSignature"/> to return a CMS blob carrying embedded OCSP/CRL. Dispatched.
    ///   </item>
    /// </list>
    ///
    /// <para><see cref="ComputeDigest"/> is kept rather than removed because <c>ISignPlugin</c> is
    /// a public contract in a shipped package and dropping a member breaks every implementer at
    /// compile time.</para>
    /// </summary>
    public interface ISignPlugin : IPdfPlugin
    {
        /// <summary>
        /// Called before the CMS token is generated.
        /// Implementations may modify <paramref name="parameters"/> in-place
        /// (e.g. inject a TSA URL, set PAdES subfilter, add visible signature reason).
        /// </summary>
        void OnBeforeSign(SignatureParameters parameters);

        /// <summary>
        /// <b>NOT DISPATCHED — nothing in this library calls this method.</b> Implement
        /// <see cref="Tetitulz.Pdf.Signing.Models.IExternalSigner"/> and set
        /// <c>SignatureParameters.ExternalSigner</c> for HSM / PKCS#11 / remote-key signing; that
        /// hook is called on every signing operation and returns a complete CMS plus the
        /// certificate chain, which is what a PDF <c>/Contents</c> requires.
        /// <para>
        /// Declared contract, for the day it is wired up: called with the bytes that need to be
        /// signed (the byte-range content); return the signature bytes, or <c>null</c> to pass
        /// responsibility to the next registered plugin. First non-null return value would win.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <para>
        /// This member has no call site in the library. Advertised as the HSM hook while
        /// <c>PdfSigner</c> computes the digest from the configured PFX, a plugin routing the byte
        /// range to a PKCS#11 device would be silently bypassed, and the document either signed
        /// with the wrong key or failed with a certificate error naming nothing about the
        /// plugin.
        /// </para>
        /// <para>
        /// It is not wired up because the contract cannot be honoured as declared: it returns raw
        /// signature octets, whereas the signature dictionary's <c>/Contents</c> needs a complete
        /// CMS SignedData. <see cref="Models.IExternalSigner.SignData"/> is the seam that models
        /// this correctly — it returns the whole PKCS#7 blob and <b>is</b> called by
        /// <c>PdfSigner</c>. The member is kept rather than removed so existing implementations
        /// still compile; it is documented as inert rather than left looking supported.
        /// </para>
        /// </remarks>
        byte[]? ComputeDigest(string algorithm, byte[] bytesToSign);

        /// <summary>
        /// Called with the raw CMS blob produced by the signer; return a replacement blob (e.g.
        /// with embedded OCSP/CRL), or <c>null</c> to leave it unchanged. <b>First non-null return
        /// value wins; subsequent plugins are not called.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// Dispatched by <c>PdfSigner</c> after the signature-timestamp round and <b>before</b> the
        /// size check, so a replacement is measured rather than the original — it must still fit
        /// <c>SignatureParameters.EstimatedSignatureSize</c>, and signing fails with a
        /// <c>PdfSigningException</c> if it does not.
        /// </para>
        /// <para>
        /// This hook had <b>no call site anywhere in the library</b> for its whole history, so a
        /// plugin implementing the LTV use-case this documentation recommends had its returned blob
        /// discarded and the un-augmented one written instead — silently. A plugin that throws now
        /// <b>fails the signing operation</b> rather than being skipped: the hook exists to
        /// strengthen the signature, so quietly writing the weaker blob would hand the caller a
        /// document that does not carry what they asked for. Return <c>null</c> for best-effort
        /// behaviour.
        /// </para>
        /// <para>
        /// The alternative route to LTV — <c>SignatureParameters.EmbedLtv</c> with a TSA URL —
        /// appends a <c>/DSS</c> revision instead of altering the blob, and the two are
        /// independent. To post-process the finished document rather than the CMS, use
        /// <see cref="OnAfterSign"/>.
        /// </para>
        /// </remarks>
        byte[]? WrapSignature(byte[] cmsBlob, SignatureParameters parameters);

        /// <summary>
        /// Called after the signed PDF bytes have been fully produced.
        /// Return replacement PDF bytes (e.g. with a blockchain proof appended as an
        /// incremental update), or <c>null</c> to leave this plugin's input unchanged.
        /// Unlike <see cref="ComputeDigest"/> and <see cref="WrapSignature"/>, this hook
        /// is <b>chained</b>: every registered plugin is called in registration order,
        /// each receiving the output of the previous one.
        /// Return <c>null</c> to pass through without modification.
        /// </summary>
        byte[]? OnAfterSign(byte[] signedPdfBytes, SignatureParameters parameters);
    }
}
