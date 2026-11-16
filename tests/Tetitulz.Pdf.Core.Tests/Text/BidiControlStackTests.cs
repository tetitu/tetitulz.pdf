// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text
{
    /// <summary>
    /// The embeddings, overrides and isolates open at a point of a text (UAX #9 X1–X8), as the Builder's
    /// pieces and the legacy DOCX lines read them to learn whether a piece lies inside an override.
    /// </summary>
    public class BidiControlStackTests
    {
        private const char Lre = UnicodeBidi.Controls.LeftToRightEmbedding, Rle = UnicodeBidi.Controls.RightToLeftEmbedding;
        private const char Lro = UnicodeBidi.Controls.LeftToRightOverride, Rlo = UnicodeBidi.Controls.RightToLeftOverride;
        private const char Pdf = UnicodeBidi.Controls.PopDirectionalFormatting;
        private const char Lri = UnicodeBidi.Controls.LeftToRightIsolate, Rli = UnicodeBidi.Controls.RightToLeftIsolate;
        private const char Fsi = UnicodeBidi.Controls.FirstStrongIsolate, Pdi = UnicodeBidi.Controls.PopDirectionalIsolate;

        private static BidiControlStack After(params char[] controls)
        {
            var stack = new BidiControlStack();
            foreach (char c in controls) stack.Feed(c);
            return stack;
        }

        /// <summary>An override holds its direction; an embedding holds none; nothing open holds none.</summary>
        [Fact]
        public void AnOverride_IsTheInnermostOpen_AndAnEmbeddingIsNot()
        {
            Assert.Equal(BaseDirection.LeftToRight, After(Lro).OverrideDirection);
            Assert.Equal(BaseDirection.RightToLeft, After(Rlo).OverrideDirection);
            Assert.Null(After(Lre).OverrideDirection);
            Assert.Null(After(Rle).OverrideDirection);
            Assert.Null(After().OverrideDirection);
            // An embedding inside an override is the innermost: it holds none; its PDF restores the override.
            Assert.Null(After(Rlo, Lre).OverrideDirection);
            Assert.Equal(BaseDirection.RightToLeft, After(Rlo, Lre, Pdf).OverrideDirection);
        }

        /// <summary>
        /// An isolate starts a context the override around it does not reach (X5a–X5c); a PDF inside it
        /// cannot close it, and its PDI closes it with everything opened inside (X6a).
        /// </summary>
        [Fact]
        public void AnIsolate_ShieldsItsContent_AndItsPdiClosesWhatOpenedInside()
        {
            foreach (char isolate in new[] { Lri, Rli, Fsi })
            {
                Assert.Null(After(Rlo, isolate).OverrideDirection);
                Assert.Null(After(Rlo, isolate, Pdf).OverrideDirection);              // a PDF does not close an isolate
                Assert.Equal(BaseDirection.RightToLeft, After(Rlo, isolate, Pdi).OverrideDirection);
                var nested = After(Rlo, isolate, Lro, Lre, Pdi);
                Assert.Equal(BaseDirection.RightToLeft, nested.OverrideDirection);
                Assert.Equal(Rlo.ToString(), nested.Open);
            }
        }

        /// <summary>A PDF or a PDI with nothing of its kind open changes nothing (X6a, X7); a character that is no control neither.</summary>
        [Fact]
        public void AStrayPop_AndAnOrdinaryCharacter_ChangeNothing()
        {
            Assert.Equal("", After(Pdf, Pdi).Open);
            var stack = After(Rlo, Pdi);
            stack.Feed("abc");
            Assert.Equal(Rlo.ToString(), stack.Open);
            Assert.Equal(BaseDirection.RightToLeft, stack.OverrideDirection);
        }

        /// <summary>The controls one text leaves open are carried into the next, outermost first.</summary>
        [Fact]
        public void TheOpenControls_AreCarriedIntoANewStack()
        {
            var first = After(Rle, Lro);
            Assert.Equal(new string(new[] { Rle, Lro }), first.Open);

            var next = new BidiControlStack(first.Open);
            Assert.Equal(BaseDirection.LeftToRight, next.OverrideDirection);
            next.Feed(Pdf);
            Assert.Null(next.OverrideDirection);
            Assert.Equal(Rle.ToString(), next.Open);
        }
    }
}
