// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.TestInfrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> for guards that read this repository's <c>src/</c> tree off
/// disk. It skips — <b>visibly, with a reason</b> — when the repo is not present, e.g. when the
/// test assemblies are executed from somewhere other than a checkout.
///
/// <para>An early <c>return</c> would report <b>Passed</b>, which is banned test pattern 2 from
/// CLAUDE.md: a guard reporting green when it could not run. Modelled on
/// <c>Tetitulz.Pdf.Base.Tests.TestInfrastructure.RepoSourceFactAttribute</c>; Core.Tests does not
/// reference the shared test-helper project, so it resolves the root itself.</para>
/// </summary>
public sealed class RepoSourceFactAttribute : FactAttribute
{
    public RepoSourceFactAttribute()
    {
        if (RepoSource.Root == null)
            Skip = "The repository's src/ tree is not present, so the library sources cannot be " +
                   "scanned. This guard only has teeth inside a checkout.";
    }
}

/// <summary>Locates the repository root by walking up for the solution file.</summary>
public static class RepoSource
{
    private static readonly Lazy<string?> _root = new(Find);

    /// <summary>The repository root, or <c>null</c> when the checkout is not present.</summary>
    public static string? Root => _root.Value;

    /// <summary>Reads a repo-relative source file. Only call when <see cref="Root"/> is non-null.</summary>
    public static string ReadText(string relativePath) =>
        File.ReadAllText(Path.Combine(Root!, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string? Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tetitulz.pdf.sln")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
