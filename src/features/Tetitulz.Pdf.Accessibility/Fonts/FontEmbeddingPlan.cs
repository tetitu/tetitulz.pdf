// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    /// <summary>
    /// A font-embedding revision that has been computed but <b>not written</b> — the output of
    /// <see cref="PdfFontEmbeddingRemediator.Plan"/>, and the sibling of <c>Tagging.AutoTagPlan</c>,
    /// <c>Artifacts.ArtifactMarkPlan</c> and <c>ReadingOrder.ReadingOrderPlan</c>.
    /// </summary>
    /// <remarks>
    /// <para>Every writer in this assembly appends a <i>clear-text</i> incremental update, which is
    /// why they refuse an encrypted document. This assembly cannot decrypt either — it does not
    /// reference <c>Tetitulz.Pdf.Encryption</c>, and must not — so the operation travels to the
    /// decryptor instead of the decryptor travelling here.</para>
    /// <para><b><see cref="Describe"/> matters more here than for the other three.</b> Embedding is
    /// what closes UA21/UA25, so a plan that was staged and then lost must come back as
    /// <i>skips with a stated reason</i> rather than as embedded fonts — otherwise the result
    /// declares a conformance claim about a document that still violates it. That is the same rule
    /// the clear-text path enforces on itself through <c>VerifiedWrite</c>, moved to the caller
    /// because here the caller owns the write.</para>
    /// </remarks>
    public sealed class FontEmbeddingPlan
    {
        private readonly int _fontsExamined;
        private readonly int _alreadyEmbedded;
        private readonly List<string> _embedded;
        private readonly int _subsetted;
        private readonly List<FontSiteRef> _embeddedSites;
        private readonly List<FontEmbeddingSkip> _skips;
        private readonly List<string> _warnings;

        /// <summary>Identifies a staged font for the lost-revision accounting.</summary>
        internal readonly struct FontSiteRef
        {
            internal FontSiteRef(string baseFont, string resourceName, int pageIndex)
            {
                BaseFont = baseFont;
                ResourceName = resourceName;
                PageIndex = pageIndex;
            }

            internal string BaseFont { get; }
            internal string ResourceName { get; }
            internal int PageIndex { get; }
        }

        internal FontEmbeddingPlan(
            IReadOnlyList<IndirectObject> objects,
            int fontsExamined, int alreadyEmbedded, List<string> embedded, int subsetted,
            IEnumerable<FontSiteRef> embeddedSites,
            List<FontEmbeddingSkip> skips, List<string> warnings)
        {
            Objects = objects ?? Array.Empty<IndirectObject>();
            _fontsExamined = fontsExamined;
            _alreadyEmbedded = alreadyEmbedded;
            _embedded = embedded ?? new List<string>();
            _subsetted = subsetted;
            _embeddedSites = new List<FontSiteRef>(embeddedSites ?? Array.Empty<FontSiteRef>());
            _skips = skips ?? new List<FontEmbeddingSkip>();
            _warnings = warnings ?? new List<string>();
        }

        internal static FontEmbeddingPlan Nothing(List<string> warnings)
            => new FontEmbeddingPlan(
                Array.Empty<IndirectObject>(), 0, 0, null, 0, null,
                new List<FontEmbeddingSkip>(), warnings);

        /// <summary>
        /// The objects to append, ordered by object number: the font programs and any synthesised
        /// descriptors (new), plus the font dictionaries that now point at them (revisions of
        /// existing objects). Empty when no font could be embedded.
        /// </summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>What to report, given whether the revision actually reached the output.</summary>
        /// <param name="revisionWasAppended">
        /// <c>true</c> when <see cref="Objects"/> were written; <c>false</c> when the write was lost
        /// or skipped, in which case every staged font is reported as a skip and UA21/UA25 are
        /// stated to remain open.
        /// </param>
        public FontEmbeddingResult Describe(bool revisionWasAppended)
        {
            var warnings = new List<string>(_warnings);
            var skips = new List<FontEmbeddingSkip>(_skips);

            if (revisionWasAppended)
            {
                return new FontEmbeddingResult(
                    _fontsExamined, _alreadyEmbedded, _embedded.Count, _subsetted,
                    _embedded, skips, warnings);
            }

            const string Lost =
                "the font program was staged but the revision carrying it was not appended, so the "
                + "produced document is the original and this font is still not embedded";

            foreach (FontSiteRef site in _embeddedSites)
                skips.Add(new FontEmbeddingSkip(site.BaseFont, site.ResourceName, site.PageIndex, Lost));

            warnings.Add(
                "The revision carrying the embedded font programs was not appended, so the output "
                + "holds the original document and no font was embedded. UA21/UA25 remain open.");

            return new FontEmbeddingResult(_fontsExamined, _alreadyEmbedded, 0, 0, null, skips, warnings);
        }
    }
}
