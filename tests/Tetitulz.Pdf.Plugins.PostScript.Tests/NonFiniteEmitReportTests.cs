// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A non-finite number written through the emitter's allocation-free path — path coordinates, colours,
/// line widths, the text matrices — is said, as the formatting path says it. Both write 0; only one
/// reported it, so an overflowing coordinate drew a wrong path with nothing said.
/// </summary>
public sealed class NonFiniteEmitReportTests
{
    [Fact]
    public void AnOverflowingPathCoordinate_IsReported()
    {
        var r = PostScriptConversion.ConvertFragment(
            Encoding.Latin1.GetBytes("newpath 1e200 1e200 mul 0 moveto 10 10 lineto stroke"));

        Assert.Contains("non-finite coordinate", r.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>The control: a finite path says nothing of it.</summary>
    [Fact]
    public void AFinitePath_IsNotReported()
    {
        var r = PostScriptConversion.ConvertFragment(
            Encoding.Latin1.GetBytes("newpath 0 0 moveto 10 10 lineto stroke"));

        Assert.DoesNotContain("non-finite", r.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains(" m\n", Encoding.ASCII.GetString(r.Content), StringComparison.Ordinal);   // the path was drawn
    }
}
