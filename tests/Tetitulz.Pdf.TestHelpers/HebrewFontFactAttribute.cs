// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A <see cref="FactAttribute"/> that reports <b>Skipped</b> when this host has no font covering
/// Hebrew, rather than failing or — worse — quietly weakening into something that cannot fail.
///
/// <para>Shared by every test project that needs a Hebrew face (Core, DocxToPdf, Templating), where
/// it was copied three times while this library had no xunit reference. An early
/// <c>return</c> is banned here (CLAUDE.md banned pattern 2): it reports <i>Passed</i> while
/// executing nothing, so the test can never go red.</para>
///
/// <para>The probe deliberately runs through the production <see cref="SystemFontResolver"/> rather
/// than a private copy — it is the type under test in the sibling file, and a test-only font search
/// that disagreed with the shipping one would gate the tests on the wrong question. Note it asks
/// for real <b>cmap coverage</b>, not for a family by name: "is a font called David installed" and
/// "can anything here draw א" are different questions, and only the second one decides whether the
/// behaviour is observable.</para>
/// </summary>
public sealed class HebrewFontFactAttribute : FactAttribute
{
    public HebrewFontFactAttribute()
    {
        if (!HebrewFontProbe.Available)
            Skip = "No font on this host covers Hebrew (U+05D0–U+05EA).";
    }
}

/// <summary>Theory counterpart of <see cref="HebrewFontFactAttribute"/>.</summary>
public sealed class HebrewFontTheoryAttribute : TheoryAttribute
{
    public HebrewFontTheoryAttribute()
    {
        if (!HebrewFontProbe.Available)
            Skip = "No font on this host covers Hebrew (U+05D0–U+05EA).";
    }
}

/// <summary>The host font covering Hebrew, found once per process through <see cref="SystemFontResolver"/>.</summary>
public static class HebrewFontProbe
{
    /// <summary>A short Hebrew word — "shalom". Every letter is an ordinary consonant, so a font
    /// covering the block at all covers this.</summary>
    public const string Shalom = "שלום";

    private static readonly object Gate = new();
    private static FontResolution? _resolution;
    private static bool _probed;

    public static bool Available => Resolve() != null;

    /// <summary>Bytes of a font covering <see cref="Shalom"/>; throws if the probe said none exists,
    /// which means the test should have been skipped.</summary>
    public static byte[] Bytes =>
        Resolve()?.FontData
        ?? throw new InvalidOperationException(
            "No Hebrew font on this host; the test should have been skipped by [HebrewFontFact].");

    public static TrueTypeFontProgram Program =>
        Resolve()?.Program
        ?? throw new InvalidOperationException(
            "No Hebrew font on this host; the test should have been skipped by [HebrewFontFact].");

    private static FontResolution? Resolve()
    {
        lock (Gate)
        {
            if (_probed) return _resolution;
            _probed = true;

            var required = new List<int>();
            foreach (char c in Shalom) required.Add(c);

            var result = SystemFontResolver.Resolve(
                new FontResolutionRequest
                {
                    FamilyHint = "Hebrew",
                    RequiredCodePoints = required,
                    AllowSystemFontSubstitution = true,
                },
                fontProvider: null);

            _resolution = result.Success ? result : null;
            return _resolution;
        }
    }
}
