// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing
{
    /// <summary>
    /// <see cref="PdfContentStreamBuilder.AddExtGState"/> deduplicates on the alpha pair.
    ///
    /// <para>
    /// Generated documents ask for the same opacity repeatedly — every node of a translucent
    /// SmartArt diagram, every shaded cell of a table — and each distinct resource name costs an
    /// indirect object in the output. The dedupe is invisible to callers (they get a name that
    /// means what they asked for either way), so only a test on the returned names can pin it.
    /// </para>
    /// </summary>
    public class ContentStreamBuilderExtGStateTests
    {
        [Fact]
        public void SameAlphaPair_ReturnsTheSameResourceName()
        {
            var b = new PdfContentStreamBuilder();

            string first = b.AddExtGState(0.5f, null);
            string second = b.AddExtGState(0.5f, null);

            Assert.Equal(first, second);
        }

        [Fact]
        public void DifferentAlphaValues_ReturnDistinctResourceNames()
        {
            var b = new PdfContentStreamBuilder();

            string half = b.AddExtGState(0.5f, null);
            string quarter = b.AddExtGState(0.25f, null);

            Assert.NotEqual(half, quarter);
        }

        [Fact]
        public void FillAndStrokeAlpha_AreDistinguished_NotCollapsedByValue()
        {
            // 0.5 fill and 0.5 stroke are different states; a key that ignored which side the
            // value came from would hand back one resource for both.
            var b = new PdfContentStreamBuilder();

            string fillOnly = b.AddExtGState(0.5f, null);
            string strokeOnly = b.AddExtGState(null, 0.5f);
            string both = b.AddExtGState(0.5f, 0.5f);

            Assert.NotEqual(fillOnly, strokeOnly);
            Assert.NotEqual(fillOnly, both);
            Assert.NotEqual(strokeOnly, both);
        }

        [Fact]
        public void EachDistinctStateAppearsOnceInTheResourceDictionary()
        {
            var b = new PdfContentStreamBuilder();
            for (int i = 0; i < 5; i++) b.AddExtGState(0.5f, null);
            b.AddExtGState(0.25f, null);

            int next = 1;
            var (_, resources, _) = b.BuildObjects(ref next);
            var extGStates = resources.Get(PdfName.ExtGState) as PdfDictionary;

            // Six calls, two distinct states — six entries would mean six wasted objects.
            Assert.NotNull(extGStates);
            Assert.Equal(2, extGStates.Keys.Count);
        }
    }
}
