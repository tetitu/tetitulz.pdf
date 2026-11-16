// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>The transcoder's work ceiling (<see cref="PdfLimits.MaxJpxDecodeWork"/>), in the collection that may write a limit.</summary>
[Collection(Tests.Constants.PdfLimitsCollection.Name)]
public sealed class JpxTranscodingLimitTests
{
    /// <summary>
    /// A transcode past the work ceiling answers false with the reason, as a Try method and as the decoder do — it
    /// threw the budget's exception — and the same codestream under the default ceiling is transcoded.
    /// </summary>
    [Fact]
    public void ATranscodePastTheWorkCeiling_AnswersFalse_WithTheReason()
    {
        byte[] codestream = JpxLosslessFixtures.Gray40x24_Blocks16_Precincts32_ThreeLayers.Codestream;
        try
        {
            PdfLimits.MaxJpxDecodeWork = 10;
            Assert.False(JpxTranscoding.TryToHighThroughput(codestream, out byte[] none, out string reason));
            Assert.Null(none);
            Assert.Contains("MaxJpxDecodeWork", reason, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
        Assert.True(JpxTranscoding.TryToHighThroughput(codestream, out _, out string accepted), accepted);
    }
}
