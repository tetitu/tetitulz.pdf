// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// <c>CMapParser</c> has five section parsers and threads its <c>WorkBudget</c> through all five,
    /// <c>ParseCodespaceRanges</c> included.
    ///
    /// <para><b>Why that one matters more than its size suggests.</b> Its outer loop over
    /// <c>begincodespacerange</c> sections is unbounded, its inner loop over the lines of a section
    /// is unbounded, and every accepted line allocates two <c>byte[]</c> and appends to a list the
    /// method <b>returns</b> — so unlike a runaway mapping loop the cost is <i>retained</i> rather
    /// than transient, and the list is then consulted for every code the font decodes.</para>
    ///
    /// <para><b>These assert the guard's own evidence, never a wall clock</b>: elapsed time under a
    /// full-solution run measures the machine's backlog rather than the code's work.</para>
    ///
    /// <para><b>What is deliberately NOT covered, stated rather than implied.</b> The refusal
    /// branch <i>inside</i> <c>ParseCodespaceRanges</c> — where a partial range list is discarded
    /// — is not directly reachable from a test: the shared ceiling is
    /// <see cref="PdfLimits.MaxCMapMappingWrites"/> = 8 Mi ticks and a codespace line is ~14 bytes,
    /// so exhausting it through codespace lines alone would need a ~118 MB fixture. What IS proved
    /// below is the thing that matters — that codespace lines are charged to the shared budget at
    /// all — by showing they bring a *later* section's refusal forward.</para>
    /// </summary>
    public class CMapCodespaceBudgetTests
    {
        /// <summary>One full 16-bit bfrange line costs 0x10000 ticks — the per-line clamp.</summary>
        private const int TicksPerFullBfRangeLine = 0x10000;

        /// <summary>Budget in ticks, shared by every section parser for the whole parse.</summary>
        private static long Ceiling => PdfLimits.MaxCMapMappingWrites;

        private static byte[] BuildCMap(int codespaceLines, int fullBfRangeLines)
        {
            var sb = new StringBuilder();
            sb.Append("/CIDInit /ProcSet findresource begin\n");

            sb.Append("begincodespacerange\n");
            sb.Append("<0000> <FFFF>\n");                       // one real range, always
            for (int i = 0; i < codespaceLines; i++)
                sb.Append("<0000> <FFFF>\n");                   // padding: legal, and each costs a tick
            sb.Append("endcodespacerange\n");

            for (int i = 0; i < fullBfRangeLines; i++)
            {
                sb.Append("beginbfrange\n");
                sb.Append("<0000> <FFFF> <0000>\n");            // 0x10000 mappings — the clamp
                sb.Append("endbfrange\n");
            }

            sb.Append("endcmap\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        /// <summary>
        /// The differential that proves the tick exists. Two parses whose <b>only</b> difference is
        /// the number of (legal, harmless) codespace lines: one sits just under the shared ceiling
        /// and completes, the other pushes past it and is refused. Without the tick the second
        /// parse costs exactly what the first does and completes too — which is what made this
        /// test necessary and is what the mutation check confirms.
        /// </summary>
        [Fact]
        public void CodespaceLinesAreChargedToTheSharedBudgetAndCanBringARefusalForward()
        {
            // Just under: 127 full bfrange lines = 8 323 072 ticks against a 8 388 608 ceiling,
            // leaving 65 536 spare - so a handful of codespace lines is still affordable.
            int bfLines = (int)(Ceiling / TicksPerFullBfRangeLine) - 1;
            long spare = Ceiling - (long)bfLines * TicksPerFullBfRangeLine;

            CMapParser.Parse(BuildCMap(codespaceLines: 8, fullBfRangeLines: bfLines), out _, out var okRanges);
            string errorWhenAffordable = CMapParser.LastBudgetError;

            // Now spend the spare on codespace lines instead. Nothing else changes.
            CMapParser.Parse(BuildCMap(codespaceLines: (int)spare + 16, fullBfRangeLines: bfLines), out _, out _);
            string errorWhenCodespaceEatsTheSpare = CMapParser.LastBudgetError;

            Assert.Null(errorWhenAffordable);
            Assert.Equal(9, okRanges.Count);                    // 1 real + 8 padding, all kept

            Assert.NotNull(errorWhenCodespaceEatsTheSpare);     // the refusal the tick made possible
            Assert.Contains("CMap parse", errorWhenCodespaceEatsTheSpare);
        }

        /// <summary>
        /// The forbidden state, asserted directly: a reported refusal together with a non-empty
        /// range list. The codespace ranges decide how many bytes each code consumes (§9.7.6.2), so
        /// a truncated set silently mis-splits every code after it — unlike a missing <i>mapping</i>,
        /// which is merely a gap in extracted text. That is why this parser discards where the
        /// other four hand back what they produced.
        /// </summary>
        [Fact]
        public void ARefusedParseNeverReturnsAPartialCodespaceRangeList()
        {
            int bfLines = (int)(Ceiling / TicksPerFullBfRangeLine) - 1;
            long spare = Ceiling - (long)bfLines * TicksPerFullBfRangeLine;

            CMapParser.Parse(BuildCMap(codespaceLines: (int)spare + 16, fullBfRangeLines: bfLines),
                             out int maxCodeBytes, out var ranges);

            Assert.NotNull(CMapParser.LastBudgetError);
            // Refused during the bfrange section, so the codespace list is complete and valid -
            // the invariant is that a refusal is never accompanied by a TRUNCATED list, and the
            // only way this parser can produce one is the discard branch, which empties it.
            Assert.True(ranges.Count == 0 || ranges.Count == (int)spare + 17,
                $"Expected either a discarded list or the complete one, got {ranges.Count}.");
            Assert.Equal(2, maxCodeBytes);
        }

        [Fact]
        public void AWellFormedCMapStillParsesItsCodespaceRangesAndMappings()
        {
            byte[] cmap = Encoding.ASCII.GetBytes(
                "/CIDInit /ProcSet findresource begin\n" +
                "begincodespacerange\n<00> <80>\n<8140> <9FFC>\nendcodespacerange\n" +
                "beginbfchar\n<20> <0041>\nendbfchar\n" +
                "endcmap\n");

            var mapping = CMapParser.Parse(cmap, out int maxCodeBytes, out var ranges);

            Assert.Equal(2, ranges.Count);
            Assert.Equal(2, maxCodeBytes);          // the 2-byte range widens it
            Assert.Equal("A", mapping[0x20]);
            Assert.Null(CMapParser.LastBudgetError);
        }

        /// <summary>
        /// Stops the budget from being "charge so aggressively that ordinary fonts are refused". A
        /// real CMap has a handful of codespace ranges; a too-tight budget would turn this guard
        /// into a correctness bug.
        /// </summary>
        [Fact]
        public void AnOrdinaryNumberOfCodespaceRangesIsNowhereNearTheCeiling()
        {
            CMapParser.Parse(BuildCMap(codespaceLines: 512, fullBfRangeLines: 0), out _, out var ranges);

            Assert.Equal(513, ranges.Count);
            Assert.Null(CMapParser.LastBudgetError);
        }
    }
}
