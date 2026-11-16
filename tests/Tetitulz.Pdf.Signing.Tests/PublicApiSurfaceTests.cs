// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Signing</c>'s exported surface against <c>tests/ApiSnapshots</c>.
///
/// <para><b>This file exists because the baseline did, and nothing read it.</b> The repo-shape guard
/// (<c>ApiBaselineCoverageTests</c>) asserts that every packable project <i>has</i> a baseline file,
/// which is not the same as asserting that anything <i>compares</i> against it — and the difference
/// is invisible, because a baseline nothing reads is byte-identical to one that passes. Five
/// projects had the file and no comparison — Tetitulz.Pdf.Signing among them — and they were the four core-ring assemblies plus
/// one plugin: precisely the packages every other package depends on. Noticed while checking
/// whether a change to Core needed a baseline update, and finding there was nothing to update
/// against. Three of the four core-ring baselines matched on the first run — they were accurate,
/// just unasserted, which is exactly the state that decays without anyone seeing it.</para>
///
/// <para>A failure is not automatically a bug — an intended API change fails here once, and the fix
/// is to accept the regenerated <c>.actual</c> file <i>in the same commit</i>, which turns the diff
/// into the migration record. What the guard prevents is a change nobody noticed.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly TargetAssembly => typeof(Tetitulz.Pdf.Signing.PdfSignatureVerifier).Assembly;

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

        Assert.Contains("Tetitulz.Pdf.Signing.PdfSignatureVerifier", rendered);
        Assert.Contains("Tetitulz.Pdf.Signing.PdfSignatureValidator", rendered);
        Assert.Contains("Tetitulz.Pdf.Signing.Cms.CmsSignatureGenerator", rendered);
        Assert.Contains("Tetitulz.Pdf.Signing.Models.SignatureVerificationOptions", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 150,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Signing; the renderer is filtering too much.");
    }
}
