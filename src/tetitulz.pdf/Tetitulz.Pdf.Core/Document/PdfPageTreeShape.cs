// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// §7.7.3's one question about a page-tree node: <b>is it a leaf page, or an intermediate
    /// node?</b>
    /// </summary>
    /// <remarks>
    /// <para><b>Four walks asked it and gave two answers.</b> <c>Core.PdfPageTree</c> and
    /// <c>Plugins.Maintenance.PageTreeShape</c> required page-shaped evidence for an untyped node;
    /// <c>Plugins.Conformance.PdfUaChecker</c> and
    /// <c>Accessibility.PdfDeepAccessibilityChecker</c> did not. The predicate is public and lives
    /// here because <c>PdfPageTree</c> is <c>internal</c>, and that accessibility — not any
    /// disagreement about the rule — is the reason the copies exist: <c>PageTreeShape</c>'s own
    /// summary says so outright.</para>
    ///
    /// <para><b>The rule has drifted twice and been reconciled twice.</b> The exclusion of a node
    /// that positively declares <c>/Type /Pages</c> was added to Core first and <c>PageTreeShape</c>
    /// did not follow "for two commits" — under a comment asserting the two agreed, which is what
    /// made it invisible. A parity test was written to hold that claim; one implementation removes
    /// the need for it.</para>
    ///
    /// <para><b>Why the test is not simply <c>/Type != /Pages</c>.</b> §7.7.3.2 requires
    /// <c>/Type</c> on an intermediate node, but real producers omit it or write it as a literal
    /// string, and this library reads those files anyway — so a leaf is also recognised
    /// <i>structurally</i>, by the absence of <c>/Kids</c>. Classifying anything not saying
    /// <c>/Type /Pages</c> as a leaf silently drops an untyped intermediate node's whole subtree:
    /// the node itself contributes nothing (it has no <c>/Contents</c>) and its <c>/Kids</c> are
    /// never walked.</para>
    ///
    /// <para><b>And the page-shaped evidence is what stops a phantom page.</b>
    /// <c>&lt;&lt; /Count 0 /MediaBox […] &gt;&gt;</c> with no <c>/Kids</c> — an emptied branch,
    /// what a page-removal tool leaves behind when it hoists the box — has to be excluded by the
    /// <c>/Type /Pages</c> clause; a node with neither <c>/Contents</c> nor a <c>/MediaBox</c> is
    /// not page-shaped at all and is not a page. The two conformance checkers omitted that second
    /// clause and so counted such a node as a page, then ran their own checks against it — which
    /// each of their comments independently describes as the thing to avoid.</para>
    ///
    /// <para><b>No static fields</b> — a leaf, like the other shared decisions in this library.</para>
    /// </remarks>
    public static class PdfPageTreeShape
    {
        /// <summary>
        /// True when the node is a page rather than an intermediate node.
        /// </summary>
        /// <param name="type">The node's resolved <c>/Type</c>, or null when it has none.</param>
        /// <param name="kids">The node's resolved <c>/Kids</c>, or null when it has none.</param>
        /// <param name="hasContents">Whether the node carries a <c>/Contents</c> entry.</param>
        /// <param name="hasMediaBox">
        /// Whether the node carries its <b>own</b> <c>/MediaBox</c> — never an inherited one, which
        /// would make every childless node under a <c>/Pages</c> node that declares a box look like
        /// a page.
        /// </param>
        public static bool IsLeafPage(PdfName type, PdfArray kids, bool hasContents, bool hasMediaBox)
            => (type != null && type.Value == "Page")
               || (!(type != null && type.Value == "Pages")
                   && kids == null && (hasContents || hasMediaBox));

        /// <summary>
        /// The page attributes §7.7.3.4 makes <b>inheritable</b>, in Table 31's order.
        /// </summary>
        /// <remarks>
        /// <para><b><c>/UserUnit</c> is deliberately not among them, and that is the whole reason
        /// this is stated once.</b> It sits beside the four in Table 30 and is routinely assumed to
        /// join them — §7.7.3.4 does not list it, so it is read from the page's own dictionary by
        /// <c>PdfPage.UserUnit</c>. Core recorded that reasoning; the second copy of the list, in
        /// <c>Plugins.Maintenance.PdfLinearizer</c>, carried the four names with no such note, so
        /// anyone adding <c>/UserUnit</c> there would not have met the argument against it.</para>
        ///
        /// <para>Built per call rather than held in a static, so this type keeps the property its
        /// remarks claim: <b>no static fields, no type initializer</b>. The list has four elements
        /// and is read once per operation, so there is nothing to memoise.</para>
        /// </remarks>
        public static IReadOnlyList<PdfName> InheritableAttributes =>
            new[] { PdfName.Resources, PdfName.MediaBox, PdfName.CropBox, PdfName.Rotate };
    }
}
