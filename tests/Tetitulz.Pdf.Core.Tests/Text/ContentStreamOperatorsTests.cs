// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text
{
    /// <summary>
    /// Guards <see cref="ContentStreamOperators"/> — the one operator set behind Core's
    /// <see cref="ContentStreamParser"/> and the accessibility feature's content scanner.
    ///
    /// <para><b>Why the reflection test is the point of this file.</b> A hand-written copy of this
    /// set at each call site can be incomplete in ways neither file shows — missing <c>ID</c> in one,
    /// or <c>PS</c>, which both renderers dispatch a handler for, in both. A list of "the
    /// operators we implemented" cannot contain the one nobody modelled, so the check has to be
    /// mechanical — against <see cref="PdfOperators"/>'s own field list, which is the closest
    /// thing in the tree to the spec's index.</para>
    /// </summary>
    public class ContentStreamOperatorsTests
    {
        private static IReadOnlyList<(string Name, string Op)> DeclaredOperators()
        {
            return typeof(PdfOperators)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .Select(f => (f.Name, (string)f.GetRawConstantValue()))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }

        [Fact]
        public void EveryOperatorConstantIsInTheSharedSet()
        {
            var missing = DeclaredOperators()
                .Where(t => !ContentStreamOperators.Contains(t.Op))
                .Select(t => $"{t.Name} (\"{t.Op}\")")
                .ToList();

            Assert.True(
                missing.Count == 0,
                "ContentStreamOperators.All is missing operators that PdfOperators declares: "
                + string.Join(", ", missing)
                + ". A splitter cannot un-glue an operator it does not know, so the handler for it "
                + "never fires on a stream that omits whitespace.");
        }

        [Fact]
        public void TheSharedSetInventsNoOperatorPdfOperatorsDoesNotDeclare()
        {
            var declared = new HashSet<string>(DeclaredOperators().Select(t => t.Op), StringComparer.Ordinal);
            var extra = ContentStreamOperators.All.Where(op => !declared.Contains(op)).ToList();

            Assert.True(
                extra.Count == 0,
                "ContentStreamOperators.All contains tokens PdfOperators does not declare: "
                + string.Join(", ", extra));
        }

        /// <summary>
        /// The count assertion is not padding. The two set-equality tests above are both satisfied
        /// by an <b>empty</b> set on both sides — the vacuity trap. Pinning the number means emptying
        /// either side reddens this file.
        /// </summary>
        [Fact]
        public void TheOperatorCountIsPinnedSoAnEmptySetCannotPassTheEqualityTests()
        {
            Assert.Equal(74, DeclaredOperators().Count);
            Assert.Equal(74, ContentStreamOperators.All.Count);
        }

        [Fact]
        public void MaxLengthIsDerivedFromTheSetRatherThanAssumedToBeThree()
        {
            int longest = ContentStreamOperators.All.Max(op => op.Length);
            Assert.Equal(longest, ContentStreamOperators.MaxLength);
            Assert.Equal(3, ContentStreamOperators.MaxLength);   // BDC / EMC / SCN today
        }

        // ================================================================
        //  The behaviour the set exists for: un-gluing run-together operators.
        //  These assert the parsed operator sequence, not that parsing "worked".
        // ================================================================

        private static List<string> ParseOperators(string content)
            => ContentStreamParser.Parse(Encoding.ASCII.GetBytes(content))
                                  .Select(op => op.Operator)
                                  .ToList();

        [Fact]
        public void AGluedPostScriptOperatorIsSplitOutInsteadOfSwallowingTheWholeToken()
        {
            // Both renderers carry a live `case PdfOperators.PostScript` handler, and before PS was
            // added to the set this token decomposed to "q" + the opaque remainder "PS", so the
            // handler could never fire on a stream that glued them.
            var ops = ParseOperators("q PS Q\nqPSQ\n");

            Assert.Equal(
                new[] { "q", "PS", "Q", "q", "PS", "Q" },
                ops);
        }

        [Fact]
        public void AGluedInlineImageDataOperatorIsRecognisedRatherThanLeftOpaque()
        {
            // ID was in the accessibility copy and not in Core's. Asserted on the un-glued token
            // sequence: before the merge the trailing run stayed as the single unknown token "IDQ".
            var ops = ParseOperators("q\nIDQ\n");

            Assert.Equal(new[] { "q", "ID", "Q" }, ops);
        }

        /// <summary>
        /// The BI branch must keep winning over the newly-recognised <c>ID</c>: §8.9.7's
        /// <c>BIID</c> is "BI, then the ID of a parameter-less image", not the two operators BI and
        /// ID. Adding ID to the set must not have turned the image's sample bytes back into
        /// operators — which is the exact damage <see cref="ContentStreamParser"/>'s own comment
        /// records for the <c>'BI</c> case.
        /// </summary>
        [Fact]
        public void AddingIdDidNotBreakInlineImageBoundaryDetection()
        {
            // A parameter-less inline image whose samples happen to spell operators.
            var ops = ParseOperators("q\nBI ID QQQQ\nEI\nQ\n");

            // The samples between ID and EI must NOT appear as operators.
            Assert.Equal(new[] { "q", "BI", "Q" }, ops);
        }
    }
}
