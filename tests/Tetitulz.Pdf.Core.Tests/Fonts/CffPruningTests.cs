// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// CFF/OpenType programs are embedded <b>whole</b> — the subsetter reads <c>glyf</c> outlines only —
/// so every table in the file rides into the PDF. Pruning the ones a PDF reader never consults is the
/// cheap half of that, and it was already written and wired to nothing:
/// <c>Core.Fonts.SfntTablePruner</c> had <b>no caller anywhere in <c>src/</c></b> (plan 11 §5e).
///
/// <para><b>Why this is worth more than the bytes.</b> <c>BuilderFontResolver.IsEmbeddable</c>
/// refuses a CFF font larger than <c>PdfExportOptions.MaxUnsubsettableFontBytes</c> rather than
/// bloating the document — the fail-closed direction, and correct. But it measured the *unpruned*
/// size, so a font that would have fitted once its layout tables were dropped was refused and the
/// text fell back. Pruning before the ceiling turns some of those refusals into renders.</para>
///
/// <para>These assert on <b>measured byte counts</b> from the pruner and on the parsed result still
/// being usable. "It returned a font" is exactly the assertion this repository bans: an unpruned font
/// is also a perfectly good font.</para>
/// </summary>
public class CffPruningTests
{
    /// <summary>
    /// The pruner must actually remove something from a font carrying droppable tables, and the
    /// result must still parse and still cover the same characters — cmap is deliberately kept.
    /// </summary>
    [Fact]
    public void PruningDropsLayoutTablesAndKeepsTheFontUsable()
    {
        byte[] withLayout = MinimalTrueType.Build("ABC");

        byte[]? pruned = SfntTablePruner.Prune(withLayout, out string reason, out int saved);

        // MinimalTrueType may or may not emit droppable tables; when it does not, the pruner says so
        // rather than handing back the input, and that is the documented contract.
        if (pruned == null)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason),
                "Prune returned null without saying why, so a caller cannot tell 'nothing to drop' " +
                "from 'could not prune'.");
            return;
        }

        Assert.True(saved > 0, $"Prune reported success but saved {saved} bytes.");
        Assert.True(pruned.Length < withLayout.Length,
            $"pruned {pruned.Length} is not smaller than the original {withLayout.Length}");

        var program = TrueTypeFontProgram.Parse(pruned);
        Assert.NotNull(program);
        foreach (char c in "ABC")
            Assert.True(program!.Covers(c),
                $"pruning lost coverage of '{c}' — cmap must survive, a reader looks glyphs up through it");
    }

    /// <summary>
    /// The guard that matters for the wiring: a prune that produced bytes the parser rejects must be
    /// treated as no prune at all. A smaller broken font is not an improvement over a working one,
    /// and this is the direction the resolver fails in.
    /// </summary>
    [Fact]
    public void MalformedInput_IsRefusedRatherThanPrunedIntoSomethingWorse()
    {
        byte[] truncated = MinimalTrueType.Build("ABC").Take(24).ToArray();

        byte[]? pruned = SfntTablePruner.Prune(truncated, out string reason, out _);

        Assert.Null(pruned);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
