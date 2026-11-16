// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.TestInfrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that reports <b>Skipped</b> when this host has too few installed
/// fonts for a claim about scanning them to be observable.
///
/// <para>Modelled on <see cref="HebrewFontFactAttribute"/> and <c>Base.Tests</c>' <c>SystemFontFact</c>.
/// The alternative shapes are both banned: an early <c>return</c> reports <i>Passed</i> while
/// executing nothing (CLAUDE.md banned pattern 2), and relaxing the assertion until a bare container
/// satisfies it produces a test that cannot fail (banned pattern 3).</para>
///
/// <para>The threshold is about <b>variety</b>, not size. A parity claim over installed fonts is only
/// worth asserting where there are enough different cmap shapes to disagree about — a container with
/// three faces can pass while telling you nothing.</para>
/// </summary>
public sealed class InstalledFontsFactAttribute : FactAttribute
{
    public InstalledFontsFactAttribute()
    {
        if (InstalledFontProbe.Count < InstalledFontProbe.Minimum)
            Skip = $"This host has {InstalledFontProbe.Count} installed font files; " +
                   $"{InstalledFontProbe.Minimum} are needed for a scan-parity claim to mean anything.";
    }
}

internal static class InstalledFontProbe
{
    internal const int Minimum = 20;

    private static readonly object Gate = new();
    private static string[] _files;

    internal static int Count => Files.Length;

    /// <summary>
    /// The OS font directories. A private copy rather than <c>Core.Fonts.FontFileLocator</c> on
    /// purpose: this decides whether a test <i>runs</i>, so gating it on the type under test would
    /// let a defect in that type hide the tests written to catch it.
    /// </summary>
    internal static string[] Files
    {
        get
        {
            lock (Gate)
            {
                if (_files != null) return _files;

                var dirs = new List<string>();
                string shared = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (Directory.Exists(shared)) dirs.Add(shared);

                string perUser = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Fonts");
                if (Directory.Exists(perUser)) dirs.Add(perUser);

                foreach (string linux in new[] { "/usr/share/fonts", "/usr/local/share/fonts" })
                    if (Directory.Exists(linux)) dirs.Add(linux);

                var extensions = new[] { ".ttf", ".ttc", ".otf" };
                var found = new List<string>();
                foreach (string dir in dirs)
                {
                    try
                    {
                        found.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
                    }
                    catch { /* an unreadable font directory is not a test failure */ }
                }

                _files = found.ToArray();
                return _files;
            }
        }
    }
}
