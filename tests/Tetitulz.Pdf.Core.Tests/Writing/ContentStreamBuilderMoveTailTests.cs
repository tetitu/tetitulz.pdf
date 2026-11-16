// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing
{
    /// <summary>
    /// <see cref="PdfContentStreamBuilder.ContentLength"/> and
    /// <see cref="PdfContentStreamBuilder.MoveTailTo"/>: content written late, painted early — a box's
    /// background drawn once its height is known and put beneath what was drawn inside it.
    /// </summary>
    public class ContentStreamBuilderMoveTailTests
    {
        private static string Text(PdfContentStreamBuilder b) => Encoding.ASCII.GetString(b.Build(compress: false));

        [Fact]
        public void TheTailMovesAheadOfWhatWasWrittenBetween()
        {
            var b = new PdfContentStreamBuilder();
            b.AppendRaw("A\n");
            int mark = b.ContentLength;
            b.AppendRaw("CONTENT\n");
            int from = b.ContentLength;
            b.AppendRaw("FILL\n");

            b.MoveTailTo(from, mark);

            Assert.Equal("A\nFILL\nCONTENT\n", Text(b));
        }

        /// <summary>
        /// Nested boxes close innermost first, and each moves its tail to a later mark than every box
        /// still open — so the outer box's mark is still right, and its fill lands beneath the inner
        /// one's.
        /// </summary>
        [Fact]
        public void MovingToTheLaterMarkFirstKeepsTheEarlierMarkValid()
        {
            var b = new PdfContentStreamBuilder();
            int outer = b.ContentLength;
            b.AppendRaw("O-TEXT\n");
            int inner = b.ContentLength;
            b.AppendRaw("I-TEXT\n");

            int innerFill = b.ContentLength;
            b.AppendRaw("I-FILL\n");
            b.MoveTailTo(innerFill, inner);

            int outerFill = b.ContentLength;
            b.AppendRaw("O-FILL\n");
            b.MoveTailTo(outerFill, outer);

            Assert.Equal("O-FILL\nO-TEXT\nI-FILL\nI-TEXT\n", Text(b));
        }

        [Fact]
        public void NothingToMoveIsNoChange()
        {
            var b = new PdfContentStreamBuilder();
            b.AppendRaw("A\n");
            int end = b.ContentLength;

            b.MoveTailTo(end, 0);
            b.MoveTailTo(0, 0);

            Assert.Equal("A\n", Text(b));
            Assert.Equal(2, b.ContentLength);
        }

        /// <summary>
        /// A position after the tail, or a tail outside the content, is refused — naming the
        /// argument that is out of range.
        /// </summary>
        [Theory]
        [InlineData(1, 2, "position")]
        [InlineData(5, 0, "from")]
        [InlineData(-1, 0, "from")]
        [InlineData(1, -1, "position")]
        public void AnImpossibleMoveIsRefused(int from, int position, string refused)
        {
            var b = new PdfContentStreamBuilder();
            b.AppendRaw("AB\n");

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => b.MoveTailTo(from, position));
            Assert.Equal(refused, ex.ParamName);
            // Potency: the content is untouched by the refused move.
            Assert.Equal("AB\n", Text(b));
        }
    }
}
