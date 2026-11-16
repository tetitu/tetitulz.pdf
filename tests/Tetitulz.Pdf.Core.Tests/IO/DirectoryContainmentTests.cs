// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// <see cref="DirectoryContainment.IsInside"/>: a path is inside a directory by its normalised
/// name, with a separator between them, under the file system's own rule for case.
/// </summary>
public class DirectoryContainmentTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "containment-root");

    [Fact]
    public void AFileInTheDirectoryOrItsSubtree_IsInside()
    {
        Assert.True(DirectoryContainment.IsInside(Root, Path.Combine(Root, "a.css")));
        Assert.True(DirectoryContainment.IsInside(Root, Path.Combine(Root, "img", "deep", "a.png")));
        Assert.True(DirectoryContainment.IsInside(Root + Path.DirectorySeparatorChar, Path.Combine(Root, "a.css")));
        Assert.True(DirectoryContainment.IsInside(Path.Combine(Root, "x", ".."), Path.Combine(Root, "a.css")));
    }

    /// <summary>
    /// The sibling whose name starts with the directory's is the case a bare prefix test admits;
    /// the others are the climb, the parent, and the directory itself, which is not a file in it.
    /// </summary>
    [Fact]
    public void ASiblingAParentAClimbAndTheDirectoryItself_AreNotInside()
    {
        Assert.False(DirectoryContainment.IsInside(Root, Root + "-evil" + Path.DirectorySeparatorChar + "a.css"));
        Assert.False(DirectoryContainment.IsInside(Root, Path.Combine(Path.GetTempPath(), "a.css")));
        Assert.False(DirectoryContainment.IsInside(Root, Path.Combine(Root, "img", "..", "..", "a.css")));
        Assert.False(DirectoryContainment.IsInside(Root, Root));
        Assert.False(DirectoryContainment.IsInside(Root, Root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void CaseIsIgnoredOnWindows_AndCountsElsewhere()
    {
        string otherCase = Path.Combine(Path.GetTempPath(), "CONTAINMENT-ROOT", "a.css");

        Assert.Equal(OperatingSystem.IsWindows(), DirectoryContainment.IsInside(Root, otherCase));
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("", "x")]
    [InlineData("x", null)]
    [InlineData("x", "")]
    public void AnEmptyPath_IsInsideNothing(string? directory, string? candidate)
        => Assert.False(DirectoryContainment.IsInside(directory!, candidate!));
}
