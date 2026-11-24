// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Encryption</c>'s exported surface against <c>tests/ApiSnapshots</c>.
///
/// <para><b>A baseline needs a comparison.</b> The repo-shape guard
/// (<c>ApiBaselineCoverageTests</c>) asserts that every packable project <i>has</i> a baseline file,
/// which is not the same as asserting that anything <i>compares</i> against it — and the difference
/// is invisible, because a baseline nothing reads is byte-identical to one that passes. An accurate
/// but unasserted baseline is exactly the state that decays without anyone seeing it.</para>
///
/// <para>A failure is not automatically a bug — an intended API change fails here once, and the fix
/// is to accept the regenerated <c>.actual</c> file <i>in the same commit</i>, which turns the diff
/// into the migration record. What the guard prevents is a change nobody noticed.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly TargetAssembly => typeof(Tetitulz.Pdf.Encryption.PdfDecryptor).Assembly;

    [ApiBaselineFact]
    public void Exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(TargetAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself. A baseline that rendered as nothing would compare equal to an
    /// empty file and pass forever, so this asserts the render actually reached the assembly's
    /// surface. The line minimum is drawn from what the package really exports — the recorded
    /// baseline is well above it — not from a round number.
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(TargetAssembly);

        Assert.Contains("Tetitulz.Pdf.Encryption.PdfDecryptor", rendered);
        Assert.Contains("Tetitulz.Pdf.Encryption.PdfEncryptor", rendered);
        Assert.Contains("Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 80,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Encryption; the renderer is filtering too much.");
    }
}
