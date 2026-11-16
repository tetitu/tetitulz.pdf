// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Two plugins hold a <c>/Kids</c>-descent cap that a doc comment says must be the same value, and
/// nothing checked it.
/// </summary>
/// <remarks>
/// <para><c>Plugins.PageEdits.AcroFormPageFilter.MaxFieldDepth</c> carries the invariant in prose —
/// "Same value as <c>Conformance.AcroFormFieldWalk.MaxFieldDepth</c>, so a field tree one of them
/// refuses is refused by both" — and <c>PdfSplitter</c> reads the PageEdits one by symbol. So the
/// splitter can never drift from PageEdits, and PageEdits can drift from Conformance at any time
/// with nothing to say so. The desync is <b>one-sided and silent</b>: raising Conformance's alone
/// makes the checker accept field trees the splitter still refuses, and no test, build step or
/// symbol reference notices.</para>
///
/// <para><b>This repository's own guide names the shape.</b> A comment asserting a safety property
/// is not the property; a documented hazard is a missing test. The layering guard beside this one
/// exists for exactly the same reason — the ring model was "doc-and-review enforced only" and a
/// violation shipped.</para>
///
/// <para><b>Why it is a source scan and not a reference.</b> Both constants are <c>internal</c> to
/// their own plugin, and no test project sees both: <c>InternalsVisibleTo</c> pairs each plugin
/// with its own test assembly only. Referencing both plugins from Base.Tests would also invert the
/// ring model. Reading the declarations off disk is the only way to compare them, which is the same
/// choice <c>ApiBaselineCoverageTests</c> makes and for the same reason.</para>
///
/// <para><b>It pins the DESCENT caps to each other, and nothing else.</b> Conformance's constant
/// also bounded the <c>/Parent</c> climb when this was written; the climb is a different question
/// with a different cap, and if it moves out to its own constant this test must keep naming the
/// descent one. A version of this test that silently followed the climb would keep passing while
/// asserting something its name does not claim.</para>
/// </remarks>
public class CrossPluginConstantInvariantTests
{
    private const string ConformanceWalk =
        "src/plugins/Tetitulz.Pdf.Plugins.Conformance/AcroFormFieldWalk.cs";
    private const string PageEditsFilter =
        "src/plugins/Tetitulz.Pdf.Plugins.PageEdits/AcroFormPageFilter.cs";

    [RepoSourceFact]
    public void TheTwoAcroFormDescentCapsHoldTheSameValue()
    {
        int conformance = ReadConstant(ConformanceWalk, "MaxFieldDepth");
        int pageEdits = ReadConstant(PageEditsFilter, "MaxFieldDepth");

        Assert.Equal(conformance, pageEdits);
    }

    /// <summary>
    /// The floor: the reader must actually find a number, or the case above compares two failures
    /// and passes.
    /// </summary>
    /// <remarks>
    /// Without this, renaming or deleting either constant turns the test green rather than red —
    /// <see cref="ReadConstant"/> would throw, but only if it is reached with a real file, and a
    /// typo in either path is exactly the way it would not be.
    /// </remarks>
    [RepoSourceFact]
    public void BothConstantsAreFoundAndAreARealisticDepth()
    {
        Assert.InRange(ReadConstant(ConformanceWalk, "MaxFieldDepth"), 1, 10_000);
        Assert.InRange(ReadConstant(PageEditsFilter, "MaxFieldDepth"), 1, 10_000);
    }

    // ------------------------------------------------------------------

    private static int ReadConstant(string relativePath, string name)
    {
        string root = TestCorpus.RepoRoot
                      ?? throw new InvalidOperationException("repo root not found");
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(full), $"{relativePath} is not in the tree; this guard names a path that moved.");

        Match m = Regex.Match(
            File.ReadAllText(full),
            @"\bconst\s+int\s+" + Regex.Escape(name) + @"\s*=\s*(?<v>[0-9_]+)\s*;");

        Assert.True(m.Success, $"no `const int {name}` in {relativePath}.");
        return int.Parse(m.Groups["v"].Value.Replace("_", ""), CultureInfo.InvariantCulture);
    }
}
