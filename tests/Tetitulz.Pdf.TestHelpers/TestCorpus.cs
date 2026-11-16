// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Locates the optional developer-local PDF corpus used by the diagnostic and
/// smoke tests.
///
/// The corpus is deliberately NOT committed: <c>tests/pdf/</c> is gitignored and
/// <c>raw/</c> holds ad-hoc customer samples. Tests that need it must therefore be
/// able to *skip honestly* on a clean checkout rather than either hard-failing
/// (as several used to) or silently early-returning and reporting green while
/// executing nothing.
///
/// Resolution order for each root:
///   1. the <c>TETITULZ_PDF_CORPUS</c> / <c>TETITULZ_PDF_RAW</c> environment variable, if set;
///   2. <c>&lt;repo&gt;/tests/pdf</c> and <c>&lt;repo&gt;/raw</c>, where the repo root is found
///      by walking up from the test assembly until <c>tetitulz.pdf.sln</c> is seen — or
///      <c>tetitulz.pdf-apps.sln</c>, the apps' repository, which runs these helpers from its
///      submodule against its own <c>tests/ApiSnapshots</c>.
///
/// There are no hardcoded absolute paths anywhere in this type, by design: the
/// NoHardcodedPathsTests guard in Tetitulz.Pdf.Base.Tests fails the build if an
/// absolute drive-letter literal reappears anywhere under tests/.
/// </summary>
public static class TestCorpus
{
    /// <summary>Human-readable reason surfaced as the xUnit skip message.</summary>
    public const string UnavailableReason =
        "PDF corpus not present. This test needs developer-local sample PDFs, which are not " +
        "committed to the repository. Set TETITULZ_PDF_CORPUS (and/or TETITULZ_PDF_RAW) to a " +
        "directory of .pdf files to run it.";

    private static readonly Lazy<string?> _repoRoot = new(FindRepoRoot);
    private static readonly Lazy<string?> _root = new(() => Resolve("TETITULZ_PDF_CORPUS", Path.Combine("tests", "pdf")));
    private static readonly Lazy<string?> _rawRoot = new(() => Resolve("TETITULZ_PDF_RAW", "raw"));

    /// <summary>The <c>tests/pdf</c> corpus directory, or <c>null</c> when unavailable.</summary>
    public static string? Root => _root.Value;

    /// <summary>The <c>raw</c> sample directory, or <c>null</c> when unavailable.</summary>
    public static string? RawRoot => _rawRoot.Value;

    /// <summary>True when <see cref="Root"/> exists and holds at least one PDF.</summary>
    public static bool HasCorpus => Root != null && Pdfs().Count > 0;

    /// <summary>True when <see cref="RawRoot"/> exists.</summary>
    public static bool HasRaw => RawRoot != null;

    /// <summary>Skip message for a test that needs the JPEG 2000 corpus.</summary>
    public const string JpxUnavailableReason =
        "JPEG 2000 corpus not present. Sparse-clone github.com/uclouvain/openjpeg-data " +
        "(input/conformance, input/nonregression, baseline/conformance) into tests/jpx/openjpeg-data, " +
        "or set TETITULZ_JPX_CORPUS to such a clone. It is not committed: that repository states no licence.";

    private static readonly Lazy<string?> _jpxRoot =
        new(() => Resolve("TETITULZ_JPX_CORPUS", Path.Combine("tests", "jpx", "openjpeg-data")));

    /// <summary>
    /// The root of the JPEG 2000 corpus — a clone of the reference decoder's test data, holding <c>input/</c> and
    /// <c>baseline/</c> — or <c>null</c> when unavailable.
    /// </summary>
    public static string? JpxRoot => _jpxRoot.Value;

    /// <summary>Skip message for a test that needs the HTJ2K corpus.</summary>
    public const string HtJpxUnavailableReason =
        "HTJ2K corpus not present. Clone github.com/aous72/jp2k_test_codestreams (BSD-2) into " +
        "tests/jpx/jp2k_test_codestreams, or set TETITULZ_HTJ2K_CORPUS to its openjph folder.";

    private static readonly Lazy<string?> _htJpxRoot =
        new(() => Resolve("TETITULZ_HTJ2K_CORPUS", Path.Combine("tests", "jpx", "jp2k_test_codestreams", "openjph")));

    /// <summary>
    /// The the reference HT implementation conformance codestreams: HTJ2K files with their
    /// <c>mse_pae.txt</c> expectations and <c>references/</c> images — or <c>null</c> when unavailable.
    /// </summary>
    public static string? HtJpxRoot => _htJpxRoot.Value;

    /// <summary>Skip message for a test that needs the ISO HTJ2K conformance codestreams.</summary>
    public const string IsoHtJpxUnavailableReason =
        "ISO HTJ2K conformance codestreams not present. Sparse-clone github.com/osamu620/OpenHTJ2K " +
        "(conformance_data) into tests/jpx/openhtj2k, or set TETITULZ_HTJ2K_ISO_CORPUS to its conformance_data folder.";

    private static readonly Lazy<string?> _isoHtJpxRoot =
        new(() => Resolve("TETITULZ_HTJ2K_ISO_CORPUS", Path.Combine("tests", "jpx", "openhtj2k", "conformance_data")));

    /// <summary>
    /// The ITU-T T.803 (02/2024) | ISO/IEC 15444-4:2024 test codestreams as the ISO HT conformance set carries them: the
    /// HTJ2K derived sets (<c>ds0_ht_*</c>, <c>ds0_hm_*</c>, <c>ds1_ht_*</c>), the class 1HF pair, and the
    /// class-1 reference images under <c>references/</c> — or <c>null</c> when unavailable.
    /// </summary>
    public static string? IsoHtJpxRoot => _isoHtJpxRoot.Value;

    /// <summary>
    /// Every JPEG 2000 file (<c>.j2k</c>, <c>.j2c</c>, <c>.jpc</c>, <c>.jp2</c>, <c>.jpx</c>) directly in
    /// <paramref name="subfolder"/> of <see cref="JpxRoot"/> — e.g. <c>input/conformance</c> — sorted for
    /// deterministic ordering. Empty when the corpus or the folder is absent.
    /// </summary>
    public static IReadOnlyList<string> JpxFiles(string subfolder)
    {
        if (JpxRoot == null) return Array.Empty<string>();
        string dir = Path.Combine(JpxRoot, subfolder);
        if (!Directory.Exists(dir)) return Array.Empty<string>();

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".j2k", ".j2c", ".jpc", ".jp2", ".jpx" };
        var files = Directory.GetFiles(dir).Where(f => extensions.Contains(Path.GetExtension(f))).ToArray();
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>Every <c>*.pdf</c> in <see cref="Root"/>, sorted for deterministic ordering.</summary>
    public static IReadOnlyList<string> Pdfs()
    {
        if (Root == null) return Array.Empty<string>();
        var files = Directory.GetFiles(Root, "*.pdf");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// Resolves a corpus-relative file name (e.g. <c>"multicolumn.pdf"</c>) against
    /// <see cref="RawRoot"/> then <see cref="Root"/>. Returns <c>null</c> when absent.
    /// </summary>
    public static string? Find(string fileName)
    {
        foreach (var root in new[] { RawRoot, Root })
        {
            if (root == null) continue;
            var candidate = Path.Combine(root, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The repository root (the directory holding <c>tetitulz.pdf.sln</c> or <c>tetitulz.pdf-apps.sln</c>,
    /// whichever is nearer the test assembly), or null.
    /// </summary>
    public static string? RepoRoot => _repoRoot.Value;

    private static string? Resolve(string envVar, string repoRelative)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        var repo = RepoRoot;
        if (repo == null) return null;

        var candidate = Path.Combine(repo, repoRelative);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (dir.EnumerateFiles("tetitulz.pdf.sln").Any() || dir.EnumerateFiles("tetitulz.pdf-apps.sln").Any())
                return dir.FullName;
        }
        return null;
    }
}
