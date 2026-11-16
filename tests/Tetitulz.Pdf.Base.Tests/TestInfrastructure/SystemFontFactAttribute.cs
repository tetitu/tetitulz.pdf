// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Decides whether a named font family is genuinely installed on the host running the suite.
///
/// <para><b>Why this cannot be a null check.</b> <c>SKTypeface.FromFamilyName</c> never reports
/// failure: asked for a family it does not have, DirectWrite returns Segoe UI and fontconfig
/// returns DejaVu Sans — both non-null. A test gated on <c>!= null</c> would therefore believe
/// every font in the world is installed. The family name of what came back has to be compared
/// with what was asked for, which is the same rule <c>FontCache.IsFamilyMatch</c> applies in
/// production code.</para>
/// </summary>
internal static class SystemFontProbe
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsInstalled(string family)
    {
        if (string.IsNullOrEmpty(family)) return false;

        lock (Gate)
        {
            if (Cache.TryGetValue(family, out bool known)) return known;

            bool present;
            try
            {
                using SKTypeface? tf = SKTypeface.FromFamilyName(family);
                // The production rule itself, so the gate and the code it gates cannot drift.
                present = tf != null && Tetitulz.Pdf.Core.Fonts.FontFileLocator.IsFamilyMatch(tf.FamilyName, family);
            }
            catch (Exception)
            {
                // No native SkiaSharp, or no platform font manager at all.
                present = false;
            }

            Cache[family] = present;
            return present;
        }
    }

    /// <summary>Returns the first family that is missing, or <c>null</c> when all are present.</summary>
    internal static string? FirstMissing(params string[] families)
    {
        foreach (string f in families)
            if (!IsInstalled(f)) return f;
        return null;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips — visibly, with a reason — when a font family the test
/// depends on is not installed on this host.
///
/// <para><b>Why these tests are host-dependent at all.</b> They assert that a PostScript base font
/// name resolves to the *derived* family rather than to the platform's silent substitute — the
/// defect that once made "ArialMT" render as Segoe UI. Proving that requires the target family to
/// actually exist: on a stock Ubuntu runner there is no Arial, so "ArialMT" and a nonsense name
/// both legitimately land on DejaVu Sans, and the property under test is simply not observable.
/// That is a fact about the host, not a defect and not a reason to loosen the assertion.</para>
///
/// <para>Same contract as <see cref="CorpusFactAttribute"/>: a skipped test is reported as
/// <b>skipped</b>. Returning early from the test body instead would report <b>Passed</b> while
/// executing nothing — CLAUDE.md banned pattern 2.</para>
/// </summary>
public sealed class SystemFontFactAttribute : FactAttribute
{
    public SystemFontFactAttribute(params string[] requiredFamilies)
    {
        string? missing = SystemFontProbe.FirstMissing(requiredFamilies);
        if (missing != null)
            Skip = $"requires the '{missing}' font family, which is not installed on this host";
    }
}

/// <summary><see cref="TheoryAttribute"/> counterpart of <see cref="SystemFontFactAttribute"/>.</summary>
public sealed class SystemFontTheoryAttribute : TheoryAttribute
{
    public SystemFontTheoryAttribute(params string[] requiredFamilies)
    {
        string? missing = SystemFontProbe.FirstMissing(requiredFamilies);
        if (missing != null)
            Skip = $"requires the '{missing}' font family, which is not installed on this host";
    }
}
