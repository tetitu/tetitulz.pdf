// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// The ISO 32000-1 §9.6.4 tag rule itself, exercised directly rather than through a writer.
    ///
    /// <para>
    /// <see cref="SubsetTag"/> exists because both halves of that rule were duplicated across this
    /// tree and the copies had drifted — one of them tagged its subsets and the other did not, and
    /// two of the strip implementations disagreed about whether a bare "ABCDEF+" is a tag. So the
    /// edges are what these tests are about: a name that only <i>looks</i> tagged, a name that is
    /// tagged twice, and a hash that has to be stable across the unspecified enumeration order of
    /// whatever collection the glyph ids arrived in.
    /// </para>
    /// </summary>
    public class SubsetTagTests
    {
        [Fact]
        public void Make_ProducesSixUppercaseLetters()
        {
            string tag = SubsetTag.Make("LiberationSans", new ushort[] { 3, 36, 68 });

            Assert.Equal(6, tag.Length);
            foreach (char c in tag)
                Assert.InRange(c, 'A', 'Z');
        }

        [Fact]
        public void Make_IsStableAcrossTheOrderOfTheGlyphIds()
        {
            // Glyph ids reach this from a dictionary's key collection, whose enumeration order is
            // unspecified. A tag that varied with it would make the same document write different
            // bytes on different runs.
            Assert.Equal(SubsetTag.Make("Arial", new ushort[] { 5, 1, 9 }),
                         SubsetTag.Make("Arial", new ushort[] { 9, 5, 1 }));
        }

        [Fact]
        public void Make_IgnoresRepeatedGlyphIds()
        {
            // The subset contains one copy of each glyph however many times the caller listed it,
            // so the tag naming that subset must not depend on the duplication.
            Assert.Equal(SubsetTag.Make("Arial", new ushort[] { 1, 2 }),
                         SubsetTag.Make("Arial", new ushort[] { 1, 2, 2, 1, 2 }));
        }

        [Fact]
        public void Make_DistinguishesGlyphSetsAndFonts()
        {
            // Two subsets sharing a tag is the failure this whole mechanism exists to prevent: a
            // consumer keying on "ABCDEF+Arial" would treat one glyph set as the other.
            string oneTwo = SubsetTag.Make("Arial", new ushort[] { 1, 2 });

            Assert.NotEqual(oneTwo, SubsetTag.Make("Arial", new ushort[] { 1, 3 }));
            Assert.NotEqual(oneTwo, SubsetTag.Make("Arial", new ushort[] { 1, 2, 3 }));
            Assert.NotEqual(oneTwo, SubsetTag.Make("Times", new ushort[] { 1, 2 }));
        }

        [Fact]
        public void Make_FromTextDistinguishesTheTextAndIsStable()
        {
            // The string overload serves the simple-font path, where the caller has the characters
            // the page draws rather than glyph ids.
            Assert.Equal(SubsetTag.Make("Arial", "Hello"), SubsetTag.Make("Arial", "Hello"));
            Assert.NotEqual(SubsetTag.Make("Arial", "Hello"), SubsetTag.Make("Arial", "Hell"));
        }

        [Fact]
        public void Make_FromEmptyAndNullInputsStillProducesAWellFormedTag()
        {
            // An unused font is a real case (the remediator embeds one whole), and a name that
            // sanitised away to nothing is a hostile one. Neither may yield a name a reader cannot
            // parse, so the shape holds even when there is nothing to hash.
            string empty = SubsetTag.Make(null, (IEnumerable<ushort>)null);

            Assert.Equal(6, empty.Length);
            foreach (char c in empty)
                Assert.InRange(c, 'A', 'Z');
            Assert.Equal(empty, SubsetTag.Make(null, new ushort[0]));
        }

        [Fact]
        public void Strip_RemovesATagAndItsSeparator()
        {
            Assert.Equal("LiberationSans", SubsetTag.Strip("ABCDEF+LiberationSans"));
        }

        [Theory]
        // Lower case: §9.6.4 says uppercase, and a family whose name happens to fit the shape is
        // not a tag.
        [InlineData("abcdef+Arial")]
        // Five letters, then a plus in the wrong place.
        [InlineData("ABCDE+Arial")]
        // Seven letters — the plus is not at index 6.
        [InlineData("ABCDEFG+Arial")]
        // No separator at all.
        [InlineData("ABCDEFArial")]
        // A digit inside the tag.
        [InlineData("ABC1EF+Arial")]
        // A tag with nothing after it names no font. Stripping would return the empty string, and
        // this is exactly the case two of the copies this class replaced disagreed about.
        [InlineData("ABCDEF+")]
        public void Strip_LeavesANameThatOnlyLooksTagged(string name)
        {
            Assert.Equal(name, SubsetTag.Strip(name));
        }

        [Fact]
        public void Strip_LeavesAnEmptyOrNullNameAlone()
        {
            Assert.Null(SubsetTag.Strip(null));
            Assert.Equal("", SubsetTag.Strip(""));
            Assert.Equal("A", SubsetTag.Strip("A"));
        }

        [Fact]
        public void Apply_ReplacesAnExistingTagRatherThanStackingOne()
        {
            // A program's PostScript name can already be tagged — it may be a subset an earlier
            // tool produced — and PDF name sanitisation keeps '+', so prefixing blindly yields
            // "GHIJKL+ABCDEF+Arial", which no reader matches back to Arial.
            Assert.Equal("GHIJKL+Arial", SubsetTag.Apply("ABCDEF+Arial", "GHIJKL"));
            Assert.Equal("GHIJKL+Arial", SubsetTag.Apply("Arial", "GHIJKL"));
        }

        [Fact]
        public void Apply_WithNoTagLeavesTheNameExactlyAsItWas()
        {
            // This is the "we did not subset" answer, and it must be a true no-op: a caller that
            // embedded the whole program passes the result of its decision straight through, and a
            // strip here would quietly rename a font that legitimately arrived tagged.
            Assert.Equal("ABCDEF+Arial", SubsetTag.Apply("ABCDEF+Arial", null));
            Assert.Equal("Arial", SubsetTag.Apply("Arial", ""));
        }
    }
}
