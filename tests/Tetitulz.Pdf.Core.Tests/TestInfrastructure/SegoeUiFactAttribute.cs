// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.TestInfrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that reports <b>Skipped</b> when Segoe UI (<c>segoeui.ttf</c>) is
/// not installed — for a claim about that font's own rules, which no fixture can stand in for.
/// </summary>
/// <remarks>
/// The file is found through <see cref="InstalledFontProbe"/>'s own directory list, not through
/// <c>Core.Fonts.FontFileLocator</c>: whether a test runs must not depend on the type under test.
/// </remarks>
public sealed class SegoeUiFactAttribute : FactAttribute
{
    /// <summary>The font file, or null when this host has none.</summary>
    internal static string Path =>
        InstalledFontProbe.Files.FirstOrDefault(f =>
            string.Equals(System.IO.Path.GetFileName(f), "segoeui.ttf", StringComparison.OrdinalIgnoreCase));

    public SegoeUiFactAttribute()
    {
        if (Path == null) Skip = "Segoe UI (segoeui.ttf) is not installed on this host; the claim is about that font's own rules.";
    }
}
