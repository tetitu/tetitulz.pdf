// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A read that turns an ordinary document into a <b>refusal</b>, with a reason that points the
/// caller somewhere other than the actual cause.
/// </summary>
/// <remarks>
/// <para><b>A refusal is the honest failure mode, which makes a wrong one easy to tolerate.</b>
/// Nothing wrong is written — the operation stops before writing anything. What it produces is a
/// wrong <i>explanation</i>: "Page number 1 is out of range" for a document that plainly has page 1.
/// A caller reading that investigates their own argument.</para>
///
/// <para><c>VisualSignatureBuilder</c> resolves <c>/Root</c>, <c>/Pages</c> and <c>/Kids</c>:
/// <c>GetArray</c> does not follow a reference, and the same unresolved read of <c>/Kids</c> would
/// make a <c>/Pages</c> node page 0 in <c>PdfPageTree</c>.</para>
///
/// <para>Asserted on the widget reaching the page's <c>/Annots</c> — the artefact a <i>visible</i>
/// signature exists to produce. Catching the exception would pass with the read unresolved and with
/// the feature deleted alike.</para>
/// </remarks>
public sealed class IndirectSigningRefusalTests
{
    /// <summary>
    /// A visible signature is placed on the page whichever way the page tree's <c>/Kids</c> is
    /// spelled. <c>false</c> is the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Kids [3 0 R]
    [InlineData(true)]   // /Kids 9 0 R
    public void AVisibleSignatureIsPlaced_WhicheverWayKidsIsSpelled(bool indirect)
    {
        byte[] signed = SignWithStamp(OnePagePdf(indirect));

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        Assert.NotNull(annots);

        bool widgetPresent = false;
        for (int i = 0; i < annots!.Count; i++)
        {
            if (doc.ResolveObject(annots[i]) is not PdfDictionary a) continue;
            if ((doc.ResolveObject(a[PdfName.Subtype]) as PdfName)?.Value == "Widget")
                widgetPresent = true;
        }

        Assert.True(widgetPresent, "no signature widget reached the page");
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] SignWithStamp(byte[] pdf)
    {
        var root = SigningTestCrypto.Ca("CN=Visible Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Visible Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        string pfxPath = Path.Combine(Path.GetTempPath(), $"kids-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfxPath, pfx);
        try
        {
            using var input = new MemoryStream(pdf);
            using var output = new MemoryStream();
            VisualSignatureBuilder.Sign(input, output, pfxPath, pw, Jpeg(3, 16, 16),
                pageNumber: 1, x: 50, y: 50, width: 120, height: 60);
            return output.ToArray();
        }
        finally
        {
            try { File.Delete(pfxPath); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// A JPEG carrying only what the embedder reads: SOI, a baseline SOF0 frame header, and EOI.
    /// Nothing in this path decodes the image.
    /// </summary>
    private static byte[] Jpeg(int components, int width, int height)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0 };
        int len = 8 + 3 * components;
        b.Add((byte)(len >> 8)); b.Add((byte)(len & 0xFF));
        b.Add(8);
        b.Add((byte)(height >> 8)); b.Add((byte)(height & 0xFF));
        b.Add((byte)(width >> 8)); b.Add((byte)(width & 0xFF));
        b.Add((byte)components);
        for (int i = 0; i < components; i++) { b.Add((byte)(i + 1)); b.Add(0x11); b.Add(0); }
        b.Add(0xFF); b.Add(0xD9);
        return b.ToArray();
    }

    /// <summary>
    /// A one-page document. Object 9 holds the page tree's <c>/Kids</c> array when
    /// <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] OnePagePdf(bool indirect)
    {
        string kids = indirect ? "9 0 R" : "[3 0 R]";

        return SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids " + kids + " /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "null", "null", "null", "null",
            "[3 0 R]");
    }
}
