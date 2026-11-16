// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The glyph pruner reads its table directory through the same
/// <c>Core.Fonts.SfntTableDirectory</c> as the two Core re-emitters, so the three cannot drift on
/// what a malformed directory means.
/// </summary>
/// <remarks>
/// <para>The printable-ASCII tag rule <b>started here</b> — <c>FontSubsettingTests</c> has covered
/// it since it was written — and the two Core re-emitters did not have it, so both were measurably
/// renaming such a table on the way back out. The rules below are the ones that travelled the other
/// way, from the shared reader into this plugin.</para>
///
/// <para>Every case pins the pruner's own output, not the helper's return: a shared decision point
/// is only shared if the caller actually reaches it.</para>
/// </remarks>
public class SharedSfntDirectoryTests
{
    /// <summary>
    /// Two records naming one table are refused. This copy used to keep the first and prune on,
    /// which drops a table the input had and reports success — the same class of damage as the
    /// silent rename, and not a guess this layer is entitled to make.
    /// </summary>
    [Fact]
    public void PruneRefusesADirectoryThatNamesOneTableTwice()
    {
        byte[] font = MinimalTrueType.BuildWithDroppableTables("Hello", 512);

        // Control: unmodified, the same font prunes.
        Assert.NotNull(TrueTypeGlyphPruner.Prune(font, new HashSet<int> { 1 }, out string ok));
        Assert.Null(ok);

        byte[] duplicated = MinimalTrueType.WithTableTagBytes(
            font, "GPOS", Encoding.ASCII.GetBytes("GSUB"));

        Assert.Null(TrueTypeGlyphPruner.Prune(duplicated, new HashSet<int> { 1 }, out string reason));
        Assert.Contains("twice", reason);
    }

    /// <summary>
    /// A collection is refused with the message every other font entry point now gives, rather than
    /// three different sentences about the same file.
    /// </summary>
    [Fact]
    public void PruneRefusesACollectionWithTheSharedReason()
    {
        byte[] ttc = MinimalTrueType.BuildCollection(MinimalTrueType.Build("Hello"));

        Assert.Null(TrueTypeGlyphPruner.Prune(ttc, new HashSet<int> { 1 }, out string reason));
        Assert.Equal(Tetitulz.Pdf.Core.Fonts.SfntTableDirectory.CollectionRefusal, reason);
    }
}
