// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

/// <summary>
/// A <c>[Fact]</c> that <b>skips</b> — visibly — when no developer-local PostScript samples are
/// present.
///
/// <para>Modelled on <c>Base.Tests</c>' <c>CorpusFact</c>, and for the same reason it exists there:
/// the alternative, <c>if (!Directory.Exists(dir)) return;</c>, reports <b>Passed</b> while
/// executing nothing. That is banned test pattern 2 — roughly forty results in this repo were once
/// green that way regardless of what the code did. Skipped is the honest outcome.</para>
///
/// <para>Samples are resolved from <c>TETITULZ_PDF_RAW</c> or <c>TETITULZ_PDF_CORPUS</c>; real
/// <c>.eps</c> / <c>.ps</c> files are not committed, for the same licensing reasons the PDF corpus
/// is not.</para>
/// </summary>
public sealed class EpsCorpusFactAttribute : FactAttribute
{
    public EpsCorpusFactAttribute()
    {
        if (EpsCorpus.Files().Count == 0)
            Skip = "No PostScript corpus present. Set TETITULZ_PDF_RAW (or TETITULZ_PDF_CORPUS) " +
                   "to a directory holding .ps / .eps files to run this.";
    }
}

/// <summary>Locates developer-local <c>.ps</c> / <c>.eps</c> samples.</summary>
public static class EpsCorpus
{
    /// <summary>
    /// Every <c>*.ps</c> and <c>*.eps</c> under the raw and PDF corpus roots, sorted so a failure
    /// names the same file on every machine.
    /// </summary>
    public static IReadOnlyList<string> Files()
    {
        var found = new List<string>();

        foreach (string? root in new[] { TestCorpus.RawRoot, TestCorpus.Root })
        {
            if (root == null || !Directory.Exists(root)) continue;

            foreach (string pattern in new[] { "*.ps", "*.eps" })
                found.AddRange(Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly));
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }
}
