// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing
{
    /// <summary>
    /// <see cref="PdfContentStreamBuilder.BeginOptionalContent"/> — content that a reader shows
    /// only while its layer is visible.
    /// </summary>
    /// <remarks>
    /// <para><b>The one BDC emitter that cannot inline its property list.</b>
    /// <c>BeginMarkedContent</c> and <c>BeginArtifact</c> write theirs directly into the stream,
    /// which §14.6.2 permits because neither holds an indirect reference. An optional-content group
    /// is an indirect object by definition (§8.11.2) — the catalog's <c>/OCProperties /OCGs</c> and
    /// every page using it name the same object — so it has to be reached through the page's
    /// <c>/Properties</c>, a resource category the builder did not model until now.</para>
    ///
    /// <para><b>A missing <c>/Properties</c> entry is invisible in the stream.</b>
    /// <c>/OC /OC0 BDC</c> is perfectly well-formed on its own; it simply names nothing, and a
    /// reader resolves it to no group and shows the content unconditionally. So the assertions here
    /// are on the resources dictionary as much as on the bytes — the stream alone cannot tell a
    /// working layer from a broken one.</para>
    /// </remarks>
    public class ContentStreamBuilderOptionalContentTests
    {
        [Fact]
        public void ItEmitsAnOcBdcNamingAPropertiesEntry()
        {
            var builder = new PdfContentStreamBuilder();

            builder.BeginOptionalContent(new PdfReference(7, 0));
            builder.EndMarkedContent();

            Assert.Contains("/OC /OC0 BDC", Content(builder), StringComparison.Ordinal);
            Assert.Contains("EMC", Content(builder), StringComparison.Ordinal);
        }

        [Fact]
        public void TheResourcesNameTheGroupItWasGiven()
        {
            var builder = new PdfContentStreamBuilder();
            builder.BeginOptionalContent(new PdfReference(7, 0));
            builder.EndMarkedContent();

            PdfDictionary properties = PropertiesOf(builder);

            Assert.NotNull(properties);
            // ReferenceObjectNumber, not ObjectNumber: PdfObject defines the latter as the number
            // of the object this one is STORED IN, which is 0 here. Asserting it would pass against
            // a builder that stored the wrong reference entirely.
            var reference = Assert.IsType<PdfReference>(properties.Get(new PdfName("OC0")));
            Assert.Equal(7, reference.ReferenceObjectNumber);
            Assert.Equal(0, reference.ReferenceGenerationNumber);
        }

        /// <summary>One group used three times is one resource entry and three sequences.</summary>
        /// <remarks>
        /// A layer is normally applied to several disjoint runs — three paragraphs of a
        /// watermark, every rule on a grid. Allocating a name per <em>call</em> would put three
        /// entries in <c>/Properties</c> pointing at one object, which is legal, wasteful, and
        /// makes the resource dictionary grow with the content rather than with the design.
        /// </remarks>
        [Fact]
        public void OneGroupUsedRepeatedlyGetsOneResourceEntry()
        {
            var builder = new PdfContentStreamBuilder();
            var layer = new PdfReference(7, 0);

            for (int i = 0; i < 3; i++)
            {
                builder.BeginOptionalContent(layer);
                builder.EndMarkedContent();
            }

            PdfDictionary properties = PropertiesOf(builder);
            Assert.Single(properties.Keys);

            string content = Content(builder);
            Assert.Equal(3, CountOccurrences(content, "/OC /OC0 BDC"));
        }

        /// <summary>Two different groups get two names.</summary>
        [Fact]
        public void TwoGroupsGetDistinctNames()
        {
            var builder = new PdfContentStreamBuilder();

            builder.BeginOptionalContent(new PdfReference(7, 0));
            builder.EndMarkedContent();
            builder.BeginOptionalContent(new PdfReference(8, 0));
            builder.EndMarkedContent();

            PdfDictionary properties = PropertiesOf(builder);
            Assert.Equal(2, properties.Keys.Count);

            string content = Content(builder);
            Assert.Contains("/OC /OC0 BDC", content, StringComparison.Ordinal);
            Assert.Contains("/OC /OC1 BDC", content, StringComparison.Ordinal);
        }

        /// <summary>
        /// Two references to the same object at different generations are two groups.
        /// </summary>
        /// <remarks>
        /// The dedupe compares the whole reference. Comparing object numbers alone would collapse
        /// <c>7 0 R</c> and <c>7 1 R</c> into one entry, and in a file with an incremental update
        /// those are different objects — the second is what supersedes the first.
        /// </remarks>
        [Fact]
        public void GenerationIsPartOfTheGroupIdentity()
        {
            var builder = new PdfContentStreamBuilder();

            builder.BeginOptionalContent(new PdfReference(7, 0));
            builder.EndMarkedContent();
            builder.BeginOptionalContent(new PdfReference(7, 1));
            builder.EndMarkedContent();

            Assert.Equal(2, PropertiesOf(builder).Keys.Count);
        }

        /// <summary>A page with no optional content has no /Properties category at all.</summary>
        /// <remarks>
        /// <b>The acceptance control for every existing caller.</b> The builder produces the
        /// resources dictionary every page in this library is written with, so an empty category
        /// appearing on pages that never asked for one would change output everywhere.
        /// </remarks>
        [Fact]
        public void APageWithNoOptionalContentHasNoPropertiesCategory()
        {
            var builder = new PdfContentStreamBuilder();
            builder.BeginArtifact("Layout");
            builder.EndMarkedContent();

            Assert.Null(PropertiesOf(builder));
        }

        /// <summary>The sequence is closed by the shared EMC, and unbalanced closes still throw.</summary>
        /// <remarks>
        /// <c>EMC</c> does not distinguish what it closes, so one depth counter serves all three
        /// emitters — which means an optional-content sequence has to participate in the same
        /// balance check rather than having its own.
        /// </remarks>
        [Fact]
        public void AnUnbalancedCloseStillThrows()
        {
            var builder = new PdfContentStreamBuilder();

            builder.BeginOptionalContent(new PdfReference(7, 0));
            builder.EndMarkedContent();

            Assert.Throws<InvalidOperationException>(() => builder.EndMarkedContent());
        }

        [Fact]
        public void ANullGroupIsRejected()
        {
            var builder = new PdfContentStreamBuilder();
            Assert.Throws<ArgumentNullException>(() => builder.BeginOptionalContent(null));
        }

        // ---- helpers ----------------------------------------------------------------

        private static string Content(PdfContentStreamBuilder builder)
            => Encoding.ASCII.GetString(builder.Build(compress: false));

        /// <summary>The <c>/Properties</c> resource category, or null when there is none.</summary>
        private static PdfDictionary PropertiesOf(PdfContentStreamBuilder builder)
        {
            int next = 1;
            (PdfReference _, PdfDictionary resources, _) =
                builder.BuildObjects(ref next, compress: false, embeddedFontObjNums: null);
            return resources.Get(PdfName.Properties) as PdfDictionary;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            for (int at = 0; (at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0; at += needle.Length)
                count++;
            return count;
        }
    }
}
