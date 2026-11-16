// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.EthereumSign</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.EthereumSign.txt</c>, so a breaking change to a
/// shipping package's public API fails a test instead of reaching a consumer silently.
///
/// <para>A failure is not automatically a bug: an <i>intended</i> API change is expected to fail
/// here once, and the fix is to accept the regenerated <c>.actual</c> baseline in the same commit,
/// which turns the diff into the migration record.</para>
///
/// <para>The renderer lives in <see cref="PublicApiSnapshot"/> (TestHelpers) and is shared with
/// every other package's baseline, so the files are directly comparable and cannot drift in
/// format.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    [ApiBaselineFact]
    public void EthereumSign_exported_surface_matches_its_baseline()
    {
        Assembly assembly = typeof(EthereumSignPlugin).Assembly;
        // `var` rather than `string?`: this project does not enable the nullable annotation
        // context, and spelling the annotation out warns (CS8632).
        var report = PublicApiSnapshot.Compare(assembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to an
    /// empty file and silently pass forever.
    ///
    /// <para>The minimum is derived from what this package actually exports, with headroom: the
    /// three data-carrying types alone (<c>BlockchainParameters</c>, <c>BlockchainProof</c>,
    /// <c>BlockchainVerificationResult</c>) render 29 member lines plus their constructors and
    /// type lines, and the four operational types add their own on top — 53 lines in total today.
    /// The floor sits below that rather than at it on purpose: pinning it to the exact count would
    /// make a <i>deliberate</i> removal of two members fail here instead of in the baseline diff,
    /// which is the assertion that is supposed to report an API change.</para>
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(typeof(EthereumSignPlugin).Assembly);

        Assert.Contains("Tetitulz.Pdf.Plugins.EthereumSign.EthereumSignPlugin", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.EthereumSign.PdfBlockchainSigner", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.EthereumSign.BlockchainVerificationResult", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 40,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.EthereumSign; the renderer is " +
            "probably filtering everything out.");
    }
}
