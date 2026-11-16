// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Base</c>'s exported surface against <c>tests/ApiSnapshots</c>.
///
/// <para><b>What this guard is for.</b> It is the instrument that makes a large refactor
/// reviewable. Splitting a 6 000-line type into partials, promoting a nested class, or moving a file
/// between folders are all supposed to leave the public API untouched — and without this test,
/// "supposed to" is the whole of the evidence. A mechanical change shows an empty diff; a
/// substantive one shows exactly which members moved. A failure is therefore not automatically a
/// bug: an intended API change fails here once, and the fix is to accept the regenerated
/// <c>.actual</c> file <i>in the same commit</i>, which turns the diff into the migration record.
/// What the guard prevents is a change nobody noticed.</para>
///
/// <para>The renderer lives in <see cref="PublicApiSnapshot"/> (TestHelpers) rather than here,
/// because <c>Tetitulz.Pdf.Accessibility</c> is not referenced by this project and needs the same
/// formatting. Two formatters would drift, and a baseline diff is only trustworthy if the thing
/// producing it is identical on both sides.</para>
///
/// <para><b>There used to be a second copy of this class</b>, in this project's
/// <c>TestInfrastructure/</c> folder, pinning Core, Base, Signing and Encryption together from one
/// place. All four now have a pin in their <i>own</i> test project — the shape
/// <c>ApiBaselineCoverageTests</c> enforces and the other 24 packages follow — so the grouped copy
/// had become a fifth wheel four times over, and its only remaining effect was that a one-member
/// change to Base failed twice and had to be read twice. Deleted 2026-08-28; its floor test was the
/// weaker of the two (500 lines against 1 500, one type name against four), so the floor below is
/// strictly stronger than what went away.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly TargetAssembly => typeof(Tetitulz.Pdf.Base.PdfOperations).Assembly;

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

        Assert.Contains("Tetitulz.Pdf.Base.PdfOperations", rendered);
        Assert.Contains("Tetitulz.Pdf.Base.Rendering.PdfPageRenderer", rendered);
        Assert.Contains("Tetitulz.Pdf.Base.Rendering.PdfSvgRenderer", rendered);
        Assert.Contains("Tetitulz.Pdf.Base.Operations.VerifiedWrite", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 1500,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Base; the renderer is filtering too much.");
    }
}
