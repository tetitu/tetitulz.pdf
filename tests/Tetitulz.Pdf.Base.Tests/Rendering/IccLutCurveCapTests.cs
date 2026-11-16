// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>lut16Type</c>'s curve-entry counts are capped by <c>PdfLimits.MaxIccCurvePoints</c>. The default
/// (65536) is one past what the two-byte field can say, so the cap binds only when a host lowers it;
/// this lowers it and asserts the refusal names the cap, with a table under it as the control.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class IccLutCurveCapTests
{
    [Fact]
    public void ALut16CurveLongerThanTheCapIsRefused()
    {
        try
        {
            PdfLimits.MaxIccCurvePoints = 100;

            byte[] over = IccB2AConversionTests.BuildB2AProfile(
                "CMYK", inputs: 3, outputs: 4, grid: 2, clut: new int[2 * 2 * 2 * 4], type: "mft2", curveEntries: 101);
            byte[] under = IccB2AConversionTests.BuildB2AProfile(
                "CMYK", inputs: 3, outputs: 4, grid: 2, clut: new int[2 * 2 * 2 * 4], type: "mft2", curveEntries: 100);

            Assert.Null(IccProfile.ParseB2A(over, out _, out string reason));
            Assert.Contains("curve-point cap", reason);
            Assert.NotNull(IccProfile.ParseB2A(under, out _, out _));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
