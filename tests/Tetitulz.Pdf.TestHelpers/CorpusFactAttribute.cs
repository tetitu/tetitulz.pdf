// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Gives every <see cref="CorpusFactAttribute"/> and <see cref="CorpusTheoryAttribute"/> test the
/// trait <c>Category=Corpus</c>, so a run can leave the developer-local corpus out with
/// <c>--filter "Category!=Corpus"</c>.
/// </summary>
/// <remarks>
/// <para><b>Why it matters: the corpus is half of a local run.</b> With <c>tests/pdf</c> present
/// the Base suite took 90 s; the same suite without its corpus tests took 43 s. CI has no corpus,
/// so those tests skip there — and a mutation killed only by them is a survivor on CI, which is
/// why <c>.claude/mutate.ps1</c> applies this filter to every sweep.</para>
///
/// <para><b>On the attribute, not on each class</b>, so a new corpus test cannot forget it; and
/// <b>here, not in one test project</b>, because every test project references this one — the
/// attribute used to be defined twice (Base.Tests and Plugins.Maintenance.Tests), and the second
/// copy would have lacked the trait.</para>
/// </remarks>
public sealed class CorpusTraitDiscoverer : ITraitDiscoverer
{
    public const string TypeName = "Tetitulz.Pdf.TestHelpers.CorpusTraitDiscoverer";
    public const string AssemblyName = "Tetitulz.Pdf.TestHelpers";

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "Corpus");
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips — visibly, with a reason — when the
/// developer-local PDF corpus is not present.
///
/// This replaces two bad patterns that both used to report green:
///   * <c>Assert.True(File.Exists(path))</c> against a hardcoded machine path,
///     which simply failed on every machine but the author's; and
///   * <c>if (!File.Exists(path)) return;</c>, which reported *Passed* while
///     executing nothing, so the test could never go red.
/// A skipped test is reported as skipped, which is the honest outcome.
/// </summary>
[TraitDiscoverer(CorpusTraitDiscoverer.TypeName, CorpusTraitDiscoverer.AssemblyName)]
public sealed class CorpusFactAttribute : FactAttribute, ITraitAttribute
{
    /// <param name="requiredFile">
    /// Optional corpus-relative file name (e.g. <c>"multicolumn.pdf"</c>). When given,
    /// the test skips unless that specific file resolves; when omitted, it skips
    /// unless the corpus directory holds at least one PDF.
    /// </param>
    public CorpusFactAttribute(string? requiredFile = null)
    {
        Skip = CorpusSkip.Reason(requiredFile);
    }
}

/// <summary>
/// <see cref="TheoryAttribute"/> counterpart of <see cref="CorpusFactAttribute"/>.
/// Also covers the case where a corpus-driven <c>[MemberData]</c> source yields no
/// rows — xUnit treats an empty theory as a failure ("No data found for..."), so
/// without this the smoke tests break a clean checkout outright.
/// </summary>
[TraitDiscoverer(CorpusTraitDiscoverer.TypeName, CorpusTraitDiscoverer.AssemblyName)]
public sealed class CorpusTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    /// <param name="requiredFile">See <see cref="CorpusFactAttribute(string)"/>.</param>
    public CorpusTheoryAttribute(string? requiredFile = null)
    {
        Skip = CorpusSkip.Reason(requiredFile);
    }
}

/// <summary>The one skip rule both corpus attributes apply.</summary>
internal static class CorpusSkip
{
    /// <summary>The skip reason, or <c>null</c> when the test can run.</summary>
    internal static string? Reason(string? requiredFile)
    {
        if (requiredFile == null)
            return TestCorpus.HasCorpus ? null : TestCorpus.UnavailableReason;
        return TestCorpus.Find(requiredFile) == null
            ? $"{TestCorpus.UnavailableReason} (needs '{requiredFile}')"
            : null;
    }
}
