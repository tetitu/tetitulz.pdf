// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// Reads <c>/RoleMap</c> (§14.7.3) and resolves an element's own structure type to the type it
    /// <b>means</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the editing side needs this.</b> The deep checker resolves roles — its
    /// <c>RoleOf</c> says "every structural rule must test this, never <c>StructNode.Role</c>",
    /// because comparing the raw name does not merely miss a defect, it <i>manufactures</i> one. The
    /// fixers — <c>SetFigureAltText</c>, <c>FixHeadingHierarchy</c> and the remediation pipeline's
    /// own gate — resolve through this type too.</para>
    ///
    /// <para>Comparing <c>node.Role</c> raw, for a document with
    /// <c>/RoleMap &lt;&lt; /Illustration /Figure &gt;&gt;</c> the audit would report the missing alt
    /// text and <b>the step that fixes it would walk straight past the element</b> — leaving
    /// <c>IsConforming</c> unreachable however many times <c>Remediate</c> is re-run. A checker and
    /// the fixer for that check must agree on what an element is.</para>
    ///
    /// <para><b>A leaf by construction</b> — no static fields, so no type initializer and no
    /// initialization edge onto anything that uses it.</para>
    /// </remarks>
    internal static class RoleMapResolver
    {
        /// <summary>
        /// Hops allowed while following a chain of custom types, from the shared implementation.
        /// </summary>
        internal const int MaxHops = PdfRoleMap.MaxHops;

        /// <summary>
        /// The document's <c>/RoleMap</c> as a plain name→name dictionary, empty when absent.
        /// </summary>
        /// <remarks>
        /// <b>This overload is for the editing side, which always holds an unencrypted document.</b>
        /// It resolves the <c>/StructTreeRoot</c> from <c>doc.Catalog</c>, which
        /// <c>PdfDocument.Load</c> leaves null for an encrypted file — the reason the shared
        /// <see cref="PdfRoleMap.Build"/> takes the root dictionary instead. A caller that may hold
        /// an encrypted document must resolve the root itself and call that directly.
        /// </remarks>
        internal static Dictionary<string, string> Build(PdfDocument doc)
        {
            if (doc?.Catalog == null)
                return new Dictionary<string, string>(System.StringComparer.Ordinal);

            var structTree = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot])
                             as PdfDictionary;
            return PdfRoleMap.Build(structTree, doc.ResolveObject);
        }

        /// <summary>
        /// Follows <paramref name="role"/> through the map to the type it means, or returns it
        /// unchanged when the map says nothing about it. One implementation, in
        /// <see cref="PdfRoleMap.Resolve"/>.
        /// </summary>
        internal static string Resolve(string role, Dictionary<string, string> map)
            => PdfRoleMap.Resolve(role, map);

        /// <summary>
        /// Whether an element with structure type <paramref name="role"/> <b>means</b>
        /// <paramref name="target"/>. One implementation, in <see cref="PdfRoleMap.Means"/> —
        /// see there for why both the raw and the resolved name are tested.
        /// </summary>
        internal static bool Means(string role, Dictionary<string, string> map, string target)
            => PdfRoleMap.Means(role, map, target);
    }
}
