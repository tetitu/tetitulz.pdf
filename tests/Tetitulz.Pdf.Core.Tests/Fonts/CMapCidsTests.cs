// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// <see cref="CMapCids.MaximalCid"/>: the largest CID a CMap's <c>cidchar</c> and <c>cidrange</c> sections
    /// reach — PDF/A's implementation limit (the published profiles rule 6.1.12-10 / 6.1.13-10).
    /// </summary>
    public class CMapCidsTests
    {
        private static long Max(string sections) => CMapCids.MaximalCid(Encoding.ASCII.GetBytes(
            "begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange " + sections + " endcmap"));

        [Fact]
        public void ACidcharsCid_AndARangesLastCid()
        {
            Assert.Equal(70, Max("2 begincidchar <01> 70 <02> 12 endcidchar"));
            Assert.Equal(355, Max("1 begincidrange <0000> <00FF> 100 endcidrange"));
            Assert.Equal(355, Max("1 begincidchar <01> 70 endcidchar 1 begincidrange <0000> <00FF> 100 endcidrange"));
        }

        /// <summary>A range whose last CID is past 32 bits is counted, not wrapped.</summary>
        [Fact]
        public void ARangePast32Bits_IsCounted()
            => Assert.Equal(2147483647L + 255, Max("1 begincidrange <0000> <00FF> 2147483647 endcidrange"));

        [Fact]
        public void NoCidSections_AreMinusOne()
        {
            Assert.Equal(-1, Max("1 beginbfchar <01> <0041> endbfchar"));
            Assert.Equal(-1, CMapCids.MaximalCid(new byte[0]));
            Assert.Equal(-1, CMapCids.MaximalCid(null!));
        }

        /// <summary>
        /// A range whose bounds run backwards spans nothing — not even its start CID, which a reading
        /// that took start plus a negative span would still count (70 000 − 255).
        /// </summary>
        [Fact]
        public void ABackwardRange_IsNotCounted()
            => Assert.Equal(-1, Max("1 begincidrange <00FF> <0000> 70000 endcidrange"));
    }
}
