// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A usage-rights signature is in no field (§12.8.1) — it is <c>/Perms /UR3</c> (or the older
/// <c>/UR</c>) — and a whole-file rewrite breaks its <c>/ByteRange</c> as it breaks a field
/// signature's, so the guard the rewriters ask counts it.
/// </summary>
public sealed class SignatureDetectionUsageRightsTests
{
    private static bool Signed(string permsKey)
    {
        var sig = new PdfDictionary();
        sig.Set(PdfName.Type, new PdfName("Sig"));
        sig.Set(new PdfName("ByteRange"), PdfArray.OfNumbers(new double[] { 0, 10, 20, 30 }));
        var perms = new PdfDictionary();
        perms.Set(new PdfName(permsKey), sig);
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, new PdfName("Catalog"));
        catalog.Set(PdfName.Perms, perms);
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, catalog);
        return PdfSignatureDetection.HasSignature(trailer, _ => null);
    }

    [Theory]
    [InlineData("UR3")]
    [InlineData("UR")]
    [InlineData("DocMDP")]
    public void AUsageRightsOrCertificationSignatureInPerms_IsASignature(string key)
        => Assert.True(Signed(key));

    /// <summary>The control: a <c>/Perms</c> entry that names no signature is not one.</summary>
    [Fact]
    public void AnUnrelatedPermsEntry_IsNot()
        => Assert.False(Signed("Other"));
}
