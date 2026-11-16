// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text
{
    /// <summary>
    /// UAX #9 X6 — the directional <b>override</b> status, which is what makes RLO/LRO
    /// (and therefore HTML's <c>&lt;bdo&gt;</c>) mean anything.
    ///
    /// <para>Pushing an odd embedding level is only half of an override. I1 lifts a class-L
    /// character sitting at an odd level to the next <i>even</i> level, so without X6 the Latin
    /// text inside an RLO resolves back to left-to-right and the override is inert — it changes
    /// levels and changes nothing observable.</para>
    /// </summary>
    public class BidiOverrideTests
    {
        private const char RLO = '‮';
        private const char LRO = '‭';
        private const char PDF_ = '‬';
        private const char RLI = '⁧';
        private const char PDI = '⁩';

        private static string Visual(string s, BaseDirection dir)
        {
            var p = UnicodeBidi.ResolveParagraph(s, dir);
            var order = p.VisualOrder(0, s.Length);
            return new string(order.Select(i => s[i]).ToArray());
        }

        // ------------------------------------------------------------------ the override itself

        [Fact]
        public void RightToLeftOverride_ReversesLatin()
        {
            // Latin left to itself never reverses, at either base direction — which is exactly
            // why it is the fixture that can detect the override.
            Assert.Equal("cba", Visual(RLO + "abc" + PDF_, BaseDirection.LeftToRight).Replace(RLO.ToString(), "").Replace(PDF_.ToString(), ""));
        }

        [Fact]
        public void WithoutTheOverride_LatinKeepsItsOrder()
        {
            // The control: the same characters with no RLO must stay in logical order. If this
            // ever fails, the test above is measuring something other than the override.
            Assert.Equal("abc", Visual("abc", BaseDirection.LeftToRight));
            Assert.Equal("abc", Visual("abc", BaseDirection.RightToLeft));
        }

        [Fact]
        public void RightToLeftEmbedding_DoesNotReverseLatin()
        {
            // RLE pushes the same odd level as RLO but has no override status, so I1 lifts the
            // Latin to the next even level and it reads left-to-right. This is the distinction
            // X6 encodes — and the reason an embedding is not a substitute for an override.
            string v = Visual('‫' + "abc" + PDF_, BaseDirection.LeftToRight);
            Assert.Equal("abc", v.Replace('‫'.ToString(), "").Replace(PDF_.ToString(), ""));
        }

        [Fact]
        public void RightToLeftEmbedding_OrdersNumbersRightToLeft()
        {
            // Inside an RLE the sos is R, so the spaces between numbers resolve to R (N1, EN as R) at
            // level 1 while each number rises to 2 (I2): L2 reverses the sequence — "3 2 1" — where
            // without the embedding, the control, they read as written.
            string v = Visual('‫' + "1 2 3" + PDF_, BaseDirection.LeftToRight);
            Assert.Equal("3 2 1", v.Replace('‫'.ToString(), "").Replace(PDF_.ToString(), ""));
            Assert.Equal("1 2 3", Visual("1 2 3", BaseDirection.LeftToRight));
        }

        [Fact]
        public void LeftToRightOverride_KeepsHebrewInLogicalOrder()
        {
            // The mirror image: LRO forces class L onto Hebrew, so it does NOT reverse — which is
            // observable precisely because Hebrew otherwise would.
            const string heb = "אבג";                 // א ב ג
            string plain = Visual(heb, BaseDirection.LeftToRight);
            string forced = Visual(LRO + heb + PDF_, BaseDirection.LeftToRight)
                .Replace(LRO.ToString(), "").Replace(PDF_.ToString(), "");

            Assert.Equal("גבא", plain);               // reversed without the override
            Assert.Equal(heb, forced);                               // logical order with it
        }

        // ------------------------------------------------------------------ scope and nesting

        [Fact]
        public void TheOverrideEndsAtItsPopCharacter()
        {
            string v = Visual(RLO + "ab" + PDF_ + "cd", BaseDirection.LeftToRight);
            string plain = new string(v.Where(c => !UnicodeBidi.IsBidiControl(c)).ToArray());

            Assert.Equal("bacd", plain);   // only the first pair reversed
        }

        [Fact]
        public void AnUnterminatedOverrideDoesNotLeakPastTheParagraphSeparator()
        {
            // A missing PDF_ is ordinary in real markup. It must not turn the rest of the document
            // right-to-left; the paragraph separator resets the stack.
            string v = Visual(RLO + "ab\ncd", BaseDirection.LeftToRight);
            string plain = new string(v.Where(c => !UnicodeBidi.IsBidiControl(c) && c != '\n').ToArray());

            Assert.StartsWith("ba", plain, StringComparison.Ordinal);
            Assert.EndsWith("cd", plain, StringComparison.Ordinal);
        }

        [Fact]
        public void AnIsolateNestedInAnOverride_DoesNotEscapeIt()
        {
            // The isolate's own content is isolated, but the text after the matching PDI is still
            // inside the override — X6a applies the override to the PDI and everything following.
            string v = Visual(RLO + "ab" + RLI + "cd" + PDI + "ef" + PDF_, BaseDirection.LeftToRight);
            string plain = new string(v.Where(c => !UnicodeBidi.IsBidiControl(c)).ToArray());

            Assert.Contains("fe", plain, StringComparison.Ordinal);   // the trailing pair still reversed
            Assert.Contains("ba", plain, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the strip

        [Theory]
        [InlineData('‪')]
        [InlineData('‫')]
        [InlineData('‬')]
        [InlineData('‭')]
        [InlineData('‮')]
        [InlineData('⁦')]
        [InlineData('⁧')]
        [InlineData('⁨')]
        [InlineData('⁩')]
        public void EveryBidiControlIsRecognised(char c) => Assert.True(UnicodeBidi.IsBidiControl(c));

        [Theory]
        [InlineData('a')]
        [InlineData('א')]
        [InlineData('‏')]   // RLM is a strong character, not a formatting control — it IS drawn
        [InlineData(' ')]   // narrow no-break space, just past the block
        [InlineData('⁥')]   // just before the isolate block
        [InlineData('⁪')]   // just after it
        public void OrdinaryCharactersAreNotTreatedAsControls(char c) => Assert.False(UnicodeBidi.IsBidiControl(c));

        [Fact]
        public void StripRemovesEveryControlAndNothingElse()
        {
            Assert.Equal("abc", UnicodeBidi.StripBidiControls(RLO + "a" + RLI + "b" + PDI + "c" + PDF_));
            Assert.Equal("", UnicodeBidi.StripBidiControls(RLO.ToString() + PDF_));
        }

        // ------------------------------------------------------------------ a run under an override

        /// <summary>
        /// <see cref="UnicodeBidi.ReorderOverriddenWithMap"/>: right to left, reversed and mirrored
        /// ("a(b" draws "b)a"), the map naming each character's logical index; left to right, the
        /// logical order, Hebrew included, where the algorithm alone would reverse it. The override's
        /// own controls are in neither result.
        /// </summary>
        [Fact]
        public void ARunUnderAnOverride_IsReversedOrKept_WithoutItsControls()
        {
            Assert.Equal("b)a", UnicodeBidi.ReorderOverriddenWithMap("a(b", rightToLeft: true, out int[] rtl));
            Assert.Equal(new[] { 2, 1, 0 }, rtl);

            string hebrew = new string(new[] { (char)0x05E9, (char)0x05DC, (char)0x05D5, (char)0x05DD }) + " ab";
            Assert.Equal(hebrew, UnicodeBidi.ReorderOverriddenWithMap(hebrew, rightToLeft: false, out int[] ltr));
            Assert.Equal(Enumerable.Range(0, hebrew.Length), ltr);
            Assert.NotEqual(hebrew, UnicodeBidi.Reorder(hebrew, BaseDirection.LeftToRight));   // the algorithm's own order differs
        }

        /// <summary>
        /// The composer draws a run under <see cref="TextRunOptions.DirectionalOverride"/> in that
        /// order — "ab" reversed right to left, where the algorithm keeps Latin forwards — and ignores
        /// it under an automatic direction.
        /// </summary>
        [Fact]
        public void TheComposer_DrawsARunUnderAnOverride()
        {
            var font = Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram.Parse(Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build("ab"));
            ushort[] Gids(BaseDirection dir, bool overridden) => TextRunComposer.Compose("ab", font,
                new TextRunOptions { BaseDirection = dir, DirectionalOverride = overridden }).GlyphIds.ToArray();

            Assert.Equal(new ushort[] { 2, 1 }, Gids(BaseDirection.RightToLeft, true));
            Assert.Equal(new ushort[] { 1, 2 }, Gids(BaseDirection.RightToLeft, false));
            Assert.Equal(new ushort[] { 1, 2 }, Gids(BaseDirection.Auto, true));

            // Under Auto the algorithm decides: Hebrew is reversed, override or not.
            string alefBet = new string(new[] { (char)0x05D0, (char)0x05D1 });
            var hebrew = Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram.Parse(Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build(alefBet));
            Assert.Equal(new ushort[] { 2, 1 }, TextRunComposer.Compose(alefBet, hebrew,
                new TextRunOptions { BaseDirection = BaseDirection.Auto, DirectionalOverride = true }).GlyphIds.ToArray());
        }

        [Fact]
        public void StripReturnsTheSameInstanceWhenThereIsNothingToStrip()
        {
            // The common case must not allocate — every token of every paragraph goes through here.
            string s = "plain text";
            Assert.Same(s, UnicodeBidi.StripBidiControls(s));
        }
    }
}
