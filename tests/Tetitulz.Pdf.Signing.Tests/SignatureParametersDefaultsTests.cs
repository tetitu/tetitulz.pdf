// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The defaults of <see cref="SignatureParameters"/> a caller gets without setting anything: no
/// PAdES baseline is requested, and a
/// certified document's signature does not invalidate its certification unless asked to.
/// </summary>
public class SignatureParametersDefaultsTests
{
    [Fact]
    public void ANewSignature_RequestsNoPadesBaseline()
    {
        Assert.Equal(PadesBaseline.None, new SignatureParameters().PadesBaselineLevel);
    }

    [Fact]
    public void ANewSignature_DoesNotAllowCertificationInvalidation()
    {
        Assert.False(new SignatureParameters().AllowCertificationInvalidation);
    }
}
