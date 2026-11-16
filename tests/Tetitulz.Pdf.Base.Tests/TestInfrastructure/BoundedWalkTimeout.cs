// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// How long a budget-bounded walk running on its own thread may take before the test calls it a
/// hang.
/// </summary>
/// <remarks>
/// <para><b>This bound is not the assertion, and it must not be tight enough to become one.</b> The
/// tests that use it assert the guard's own evidence — a refusal message naming the budget, a typed
/// exception — exactly as CLAUDE.md requires. The join exists only so that a genuine regression,
/// where the walk becomes unbounded, fails in finite time instead of hanging the suite.</para>
///
/// <para><b>Sixty seconds was not enough, measured rather than guessed.</b> These fixtures exhaust
/// a hundred-thousand-invocation budget, so they do real work — several seconds on an idle machine.
/// Under a full-solution run, twenty-seven test assemblies compete for the same cores and
/// <c>GraphicsExtractorFormRecursionTests.AnAcyclicFanOutOfFormsIsBoundedByTheWalkBudget</c> hit the
/// sixty-second ceiling twice, reporting "graphics extraction did not return" while passing in
/// isolation every time. That is the failure mode CLAUDE.md names: under a full-solution run,
/// elapsed time measures the machine's backlog, not the code's work.</para>
///
/// <para><b>One constant because the two callers must agree.</b> They had the same literal and the
/// same reasoning, which is the shape the DRY rule exists for — and a ceiling raised at one of them
/// would leave the other flaking for a reason nobody would connect to this one.</para>
/// </summary>
internal static class BoundedWalkTimeout
{
    /// <summary>
    /// Five minutes. Generous against machine backlog, and still finite against a walk that has
    /// genuinely stopped being bounded — which would not return at all.
    /// </summary>
    internal const int Milliseconds = 300_000;
}
