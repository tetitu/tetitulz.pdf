// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Locates an ICC profile the operating system ships, for the one kind of assertion a synthetic
/// profile cannot make.
/// </summary>
/// <remarks>
/// <para><b>Why a real profile is worth the host dependency.</b> A synthetic fixture proves the
/// reader agrees with the rule the fixture was written to — which is real evidence about the
/// plumbing and none at all about whether the rule is ICC's. A profile written by someone else,
/// whose <c>A2B0</c> and <c>B2A0</c> tables were built to be mutually inverse, is the only
/// independent check available in a tree with no reference CMM: read either table wrongly and the
/// round trip does not close.</para>
///
/// <para><b>Windows only, and not guaranteed even there.</b> The colour directory is a Windows
/// path, and which profiles are installed depends on what drivers the host has seen. So this can
/// only ever gate a test, never carry one — the synthetic fixtures remain the ones that must
/// always run.</para>
/// </remarks>
internal static class RealIccProfile
{
    // `Directory` and `Path` both shadow System.IO types inside this class, so every use of
    // either is fully qualified. Renaming the members instead would read worse at the call site,
    // which is the only place they appear.
    private static readonly string ColorDirectory =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                               "spool", "drivers", "color");

    internal static string Path(string fileName)
        => System.IO.Path.Combine(ColorDirectory, fileName);

    internal static bool Exists(string fileName)
    {
        try { return File.Exists(Path(fileName)); }
        catch (Exception) { return false; }   // no such special folder, or no access
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips — visibly, with a reason — when the operating system
/// does not ship the ICC profile the test needs.
/// </summary>
/// <remarks>
/// Same contract as <c>CorpusFactAttribute</c> and <c>SystemFontFactAttribute</c>: a skipped test
/// reports as <b>skipped</b>. Returning early from the body instead would report <b>Passed</b>
/// while executing nothing, which is CLAUDE.md's banned pattern 2 — and here it would be
/// particularly misleading, because this is the test that provides the independent evidence.
/// </remarks>
public sealed class RealIccProfileFactAttribute : FactAttribute
{
    public RealIccProfileFactAttribute(string fileName)
    {
        if (!RealIccProfile.Exists(fileName))
            Skip = $"requires the OS-supplied ICC profile '{fileName}', which is not on this host";
    }
}

/// <summary>The <c>[Theory]</c> counterpart of <see cref="RealIccProfileFactAttribute"/>: every row skips, visibly, when the profile is absent.</summary>
public sealed class RealIccProfileTheoryAttribute : TheoryAttribute
{
    public RealIccProfileTheoryAttribute(string fileName)
    {
        if (!RealIccProfile.Exists(fileName))
            Skip = $"requires the OS-supplied ICC profile '{fileName}', which is not on this host";
    }
}