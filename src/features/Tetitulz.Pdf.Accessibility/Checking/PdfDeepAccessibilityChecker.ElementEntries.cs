// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA33 / UA34 — entries an element of a type must carry
        // ================================================================

        /// <summary>
        /// What the report says when UA33's emptiness half could not be judged, for the reason
        /// <see cref="Ua22EncryptionLimit"/> gives for a link's <c>/Contents</c>.
        /// </summary>
        private const string Ua33EncryptionLimit =
            "UA33's emptiness rule was weakened: a Formula's /Alt is an encrypted string, so an /Alt " +
            "that is present was accepted without checking whether it says anything";

        /// <summary>
        /// What the report says when UA34 judged only that a Note's <c>/ID</c> is present: strings are
        /// encrypted under a key of the object holding them (ISO 32000-1 §7.6.2), and AES adds a random
        /// vector, so one identifier held by two elements reads as two, and an empty one as bytes. Which
        /// duplicates and which empty identifiers showed would depend on the cipher, not the document.
        /// </summary>
        private const string Ua34EncryptionLimit =
            "UA34 was weakened: a Note's /ID is an encrypted string, so an /ID that is present was " +
            "accepted without checking that it is non-empty and that no other element carries it";

        /// <summary>
        /// UA33 — ISO 14289-1 7.7: "All mathematical expressions shall be enclosed within a Formula tag
        /// … and shall have an Alt attribute." An element that means <c>Formula</c> (its role through
        /// <c>/RoleMap</c>) without an <c>/Alt</c> that says something. 7.7 names <c>/Alt</c> alone,
        /// where 7.3 accepts a figure's replacement text, so an <c>/ActualText</c> does not stand in for
        /// it. Whether some content is a mathematical expression that no Formula encloses is a reading of
        /// the content, which this check does not make.
        /// </summary>
        private static void CheckFormulaAltText(CheckContext ctx)
        {
            var lacking = new List<Occurrence>();
            foreach (StructNode node in ctx.Elements)
            {
                if (node.Dict == null || RoleOf(ctx, node) != StructureTypes.Formula) continue;
                PdfObject alt = node.Dict[PdfName.Alt];
                if (alt is PdfReference) alt = ResolveObject(ctx.Doc, alt);

                string detail;
                if (alt is not PdfString text) detail = "has no /Alt";
                else if (ctx.Encrypted)
                {
                    NoteEncryptionLimit(ctx, Ua33EncryptionLimit);
                    continue;
                }
                else if (string.IsNullOrWhiteSpace(text.GetText())) detail = "has an empty /Alt";
                else continue;

                var occurrence = Occurrence.For(ctx, node);
                occurrence.Detail = detail;
                lacking.Add(occurrence);
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA33", IssueCategory.Structure, WcagLevel.A,
                null, "1.1.1", lacking,
                o => "Formula element " + o.Detail + " (ISO 14289-1 7.7: a Formula shall have an Alt " +
                     "attribute). A screen reader cannot speak a formula's glyphs as mathematics.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " Formula element(s) have no /Alt " +
                    "that says anything (e.g. " + objects + ").",
                "Give each Formula element an /Alt that reads the expression aloud in words, for example " +
                "\"x equals the square root of y\". An /ActualText does not satisfy ISO 14289-1 7.7.");
        }

        /// <summary>
        /// UA34 — ISO 14289-1 7.9: "Footnotes and endnotes shall be tagged with a Note tag. Each note
        /// tag shall have a unique entry in the ID key" — the element identifier of ISO 32000-1 Table 323,
        /// a byte string unique among the elements of the structure tree. An element that means
        /// <c>Note</c> whose <c>/ID</c> is missing, not a string, empty, or carried by another element too.
        /// Identifiers are compared as bytes. Whether some content is a footnote that no Note encloses is a
        /// reading of the content, which this check does not make.
        /// </summary>
        private static void CheckNoteIdentifiers(CheckContext ctx)
        {
            var holders = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (StructNode node in ctx.Elements)
            {
                string key = IdentifierKey(ctx, node);
                if (key != null) holders[key] = holders.TryGetValue(key, out int n) ? n + 1 : 1;
            }

            var lacking = new List<Occurrence>();
            foreach (StructNode node in ctx.Elements)
            {
                if (node.Dict == null || RoleOf(ctx, node) != StructureTypes.Note) continue;
                PdfObject id = node.Dict[PdfName.ID];
                if (id is PdfReference) id = ResolveObject(ctx.Doc, id);

                string detail;
                string key = IdentifierKey(ctx, node);
                if (id == null) detail = "has no /ID";
                else if (id is not PdfString) detail = "has an /ID that is not a byte string";
                else if (ctx.Encrypted)
                {
                    NoteEncryptionLimit(ctx, Ua34EncryptionLimit);
                    continue;
                }
                else if (key == null) detail = "has an empty /ID";
                else if (holders[key] > 1) detail = "has an /ID another structure element also carries";
                else continue;

                var occurrence = Occurrence.For(ctx, node);
                occurrence.Detail = detail;
                lacking.Add(occurrence);
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA34", IssueCategory.Structure, WcagLevel.A,
                null, "1.3.1", lacking,
                o => "Note element " + o.Detail + " (ISO 14289-1 7.9: each Note shall have a unique entry " +
                     "in the ID key). Assistive technology locates a footnote from its citation by that identifier.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " Note element(s) have no unique /ID " +
                    "(e.g. " + objects + ").",
                "Give each Note element an /ID byte string no other structure element carries, and list it " +
                "in the structure tree root's /IDTree (ISO 32000-1 Table 323 and §14.7.2).");
        }

        /// <summary>
        /// An element's <c>/ID</c> bytes as a comparison key, or <see langword="null"/> when it has no
        /// non-empty byte string there. Latin-1 maps each byte to one character, so two keys are equal
        /// exactly when the bytes are.
        /// </summary>
        private static string IdentifierKey(CheckContext ctx, StructNode node)
        {
            PdfObject id = node.Dict?[PdfName.ID];
            if (id is PdfReference) id = ResolveObject(ctx.Doc, id);
            byte[] bytes = (id as PdfString)?.RawBytes;
            return bytes == null || bytes.Length == 0 ? null : Core.Text.Latin1Text.Decode(bytes);
        }
    }
}
