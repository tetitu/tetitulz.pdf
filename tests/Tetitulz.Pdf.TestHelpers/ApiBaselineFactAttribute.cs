// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Skips — <b>visibly</b> — when the baseline directory is unreachable, rather than passing
/// vacuously. An early <c>return</c> or a silently-satisfied assertion would report Passed for a
/// guard that never ran, which is banned test pattern 2.
///
/// <para>One attribute for every package's <c>PublicApiSurfaceTests</c>. It was copied into each
/// test project while this library had no xunit reference; it has one since the corpus attributes
/// moved here, so the 27 copies became this one.</para>
/// </summary>
public sealed class ApiBaselineFactAttribute : FactAttribute
{
    public ApiBaselineFactAttribute()
    {
        if (PublicApiSnapshot.BaselinePath("probe") == null)
            Skip = "The repository root is not reachable from the test assembly, so the " +
                   "checked-in API baseline cannot be read.";
    }
}
