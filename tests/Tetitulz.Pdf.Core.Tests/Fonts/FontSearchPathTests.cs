// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="FontSearchPath"/>: a directory a host or a package registers is searched first — before
/// the operating system's directories — so a bundled font is the one found when the host has one of
/// the same family too; removing it restores the search; the caches are cleared either way. The
/// fixture copies whatever font the machine enumerates first into a directory of its own, so the
/// test needs no particular font installed and runs the same on every platform.
/// </summary>
/// <summary>
/// Registering a directory clears the locator's caches, which the cache-cap tests elsewhere count
/// while they run, so this collection runs with nothing else alongside it.
/// </summary>
[CollectionDefinition("FontSearchPath", DisableParallelization = true)]
public class FontSearchPathCollection { }

[Collection("FontSearchPath")]   // process-global state: one test at a time, and no other test beside it
public class FontSearchPathTests
{
    [Fact]
    public void ARegisteredDirectory_IsSearchedFirst_AndRemovingItRestoresTheSearch()
    {
        var hostFonts = FontFileLocator.EnumerateFontFiles(includeOpenType: true);
        if (hostFonts.Count == 0) return;   // a host with no fonts at all has nothing to shadow
        string source = hostFonts[0];

        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-fontsearch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, Path.GetFileName(source));
        File.Copy(source, copy);
        try
        {
            FontSearchPath.Add(dir);

            Assert.Equal(Path.GetFullPath(dir), FontSearchPath.Directories[0]);
            Assert.Equal(Path.GetFullPath(dir), FontFileLocator.Directories[0], StringComparer.OrdinalIgnoreCase);
            // The copy is enumerated before the host's own file of the same name.
            var files = FontFileLocator.EnumerateFontFiles(includeOpenType: true);
            Assert.Equal(copy, files[0], StringComparer.OrdinalIgnoreCase);
            Assert.Contains(files, f => string.Equals(f, source, StringComparison.OrdinalIgnoreCase));

            Assert.True(FontSearchPath.Remove(dir));
            Assert.DoesNotContain(FontSearchPath.Directories, d => string.Equals(d, Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(FontFileLocator.EnumerateFontFiles(includeOpenType: true),
                f => f.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
            Assert.False(FontSearchPath.Remove(dir));
        }
        finally
        {
            FontSearchPath.Remove(dir);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddingTheSameDirectoryAgain_MovesItToTheFront()
    {
        string a = Path.Combine(Path.GetTempPath(), "tetitulz-fontsearch-a-" + Guid.NewGuid().ToString("N"));
        string b = Path.Combine(Path.GetTempPath(), "tetitulz-fontsearch-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            FontSearchPath.Add(a);
            FontSearchPath.Add(b);
            Assert.Equal(new[] { Path.GetFullPath(b), Path.GetFullPath(a) }, FontSearchPath.Directories.Take(2));

            FontSearchPath.Add(a);
            Assert.Equal(new[] { Path.GetFullPath(a), Path.GetFullPath(b) }, FontSearchPath.Directories.Take(2));
            Assert.Equal(1, FontSearchPath.Directories.Count(d => d == Path.GetFullPath(a)));

            // Neither exists, so neither reaches the locator's list — dropped at search time, as the
            // operating system's absent directories are.
            Assert.DoesNotContain(FontFileLocator.Directories, d => d == Path.GetFullPath(a) || d == Path.GetFullPath(b));
        }
        finally
        {
            FontSearchPath.Remove(a);
            FontSearchPath.Remove(b);
        }
    }

    [Fact]
    public void ABlankDirectory_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => FontSearchPath.Add(" "));
        Assert.False(FontSearchPath.Remove(""));
    }
}
