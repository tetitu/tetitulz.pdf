// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Once the page's stream is past its cap, a paint does not bring the clip into step: nothing more can
/// be written. A clip set after the cap is refused, so the clip the state wants runs ahead of the one
/// emitted, and each paint walked and copied every clip between them — none of it charged — so the
/// clips and the paints after the cap were their product.
/// </summary>
[Collection(PsLimitsCollection.Name)]
public sealed class TruncatedStreamClipWorkTests
{
    [Fact]
    public void AdversarialPaintsPastTheCap_DoNotWalkTheClipChain()
    {
        const int clips = 200, paints = 3000;
        string program = string.Concat(Enumerable.Repeat("0 0 10 10 rectfill ", 400)) +      // past the cap
                         string.Concat(Enumerable.Repeat("0 0 100 100 rectclip ", clips)) +
                         string.Concat(Enumerable.Repeat("0 0 10 10 rectfill ", paints));
        try
        {
            PsLimits.MaxEmittedContentBytes = 4000;
            PsClip.ChainSteps = 0;
            PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(program));

            Assert.InRange(PsClip.ChainSteps, 0, clips);   // each paint walked all 200: 600 000
        }
        finally { PsLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The control: under the cap, a clip restored by grestore and set again is brought into step —
    /// the walk the cap now skips is still made where something can be written.
    /// </summary>
    [Fact]
    public void ClipsBelowTheCap_AreStillBroughtIntoStep()
    {
        PsClip.ChainSteps = 0;
        var r = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
            "gsave 0 0 50 50 rectclip 0 0 10 10 rectfill grestore 0 0 10 10 rectfill " +
            "gsave 0 0 50 50 rectclip 0 0 20 20 rectclip grestore 0 0 10 10 rectfill"));

        string content = Encoding.ASCII.GetString(r.Content);
        Assert.Equal(3, content.Split('\n').Count(l => l.EndsWith(" re f", System.StringComparison.Ordinal) || l.Trim() == "f"));
    }
}
