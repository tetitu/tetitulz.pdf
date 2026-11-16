// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Answers one question for the whole-file rewriters: <b>does this document carry a signature
    /// that rewriting would invalidate?</b>
    ///
    /// <para><b>Why this is a shared type rather than a check inside each writer.</b> A whole-file
    /// rewrite renumbers every object, so a signature's <c>/ByteRange</c> — a direct array of
    /// absolute file offsets — ends up naming positions that no longer exist. The document still
    /// looks signed to a viewer while verifying as <i>corrupt</i> rather than <i>modified</i>, which
    /// is the one outcome a reader must not be handed silently. <c>PdfXConverter</c>,
    /// <c>PdfAConverter</c>, <c>PdfEncryptedRoundTrip</c>, <c>PdfOptimizer</c>,
    /// <see cref="PdfSanitizer"/> and <c>PdfLinearizer</c> all refuse on this basis unless the caller
    /// allows it.</para>
    ///
    /// <para><b>Written for the sanitizer and the linearizer too, which never asked.</b> Only the
    /// optimizer adopted it among the three, so a signed document came out of the other two looking
    /// signed and verifying as corrupt, without a word. A rewriter that answers this question
    /// differently, or not at all, is the defect this type exists to prevent.</para>
    ///
    /// <para>The traversal is deliberately parameterised by a resolver rather than taking a
    /// <c>PdfDocument</c>: the optimizer works on a mutated object map part-way through its
    /// pipeline and has no document to hand, and a check that could not be called from there would
    /// not have been called at all.</para>
    /// </summary>
    public static class PdfSignatureDetection
    {
        /// <summary>
        /// True when the document has at least one signature field carrying a value, or a DocMDP
        /// or usage-rights (<c>/UR3</c>, <c>/UR</c>) signature in <c>/Perms</c>.
        /// </summary>
        /// <param name="trailer">The document trailer; <c>/Root</c> is followed to the catalog.</param>
        /// <param name="resolve">
        /// Resolves an object number to its object. Returning <c>null</c> for an unknown number is
        /// fine — an unresolvable reference simply cannot be a signature.
        /// </param>
        /// <remarks>
        /// Errs toward <c>true</c> only on evidence, never on absence: a document whose AcroForm
        /// cannot be resolved reports <c>false</c>, because refusing to process every document with
        /// an unusual catalog would be worse than the risk it avoids. A field tree larger than
        /// <see cref="PdfLimits.MaxXrefEntries"/> nodes is the exception: the walk did not finish,
        /// so it has no evidence either way, and it reports <c>true</c>.
        /// </remarks>
        public static bool HasSignature(PdfDictionary trailer, Func<int, PdfObject> resolve)
        {
            if (trailer == null || resolve == null) return false;

            var catalog = Deref(trailer[PdfName.Root], resolve) as PdfDictionary;
            if (catalog == null) return false;

            // /Perms /DocMDP is a certification signature even when the field walk below misses it,
            // and /UR3 (/UR) a usage-rights signature, which is in no field at all (§12.8.1) — its
            // /ByteRange breaks under a rewrite exactly as a field signature's does.
            if (Deref(catalog[PdfName.Perms], resolve) is PdfDictionary perms
                && (perms[PdfName.DocMDP] != null || perms[PdfName.UR3] != null || perms[PdfName.UR] != null))
            {
                return true;
            }

            var acroForm = Deref(catalog[PdfName.AcroForm], resolve) as PdfDictionary;
            if (acroForm == null) return false;

            var fields = Deref(acroForm[PdfName.Fields], resolve) as PdfArray;
            if (fields == null) return false;
            // Field trees nest, so this is a walk rather than a scan. The visited set is what keeps
            // a /Kids cycle -- legal to write, illegal to follow -- from hanging the caller.
            //
            // /FT AND /V ARE INHERITABLE (Table 220), AND REQUIRING THEM ON ONE DICTIONARY MADE A
            // SIGNED DOCUMENT REPORT AS UNSIGNED. The common shape puts /T and /V on the field and
            // /FT on its widget kid -- which is exactly Core's InheritedFieldTypeTests fixture, a
            // document that test asserts IS signed. This walk said false for it. Because the answer
            // guards PdfOptimizer, PdfEncryptedRoundTrip and ConverterInputInspection, the cost was
            // not a mis-report: those callers would go on to REWRITE the file and leave the
            // signature covering bytes that no longer exist.
            //
            // The inherited values ride down the /Kids descent rather than being climbed back up
            // the /Parent chain, which needs no second traversal and cannot loop. Core's
            // PdfFormField does the climb instead; it is not reachable from here, since this takes
            // a raw trailer and a resolver rather than an opened document.
            var visited = new HashSet<int>();
            var stack = new Stack<Node>();
            for (int i = 0; i < fields.Count; i++) stack.Push(new Node(fields[i], null, null));

            // The walk is the size of the tree: each indirect node is visited once, and a direct one
            // is bytes of the file, so the ceiling is the document's own object ceiling: a fixed node
            // cap answering false would report unsigned a form of nine thousand fields, each with its
            // widget, whose signature field comes last, and the callers this guards would rewrite it
            // and break the signature.
            long budget = PdfLimits.MaxXrefEntries;

            while (stack.Count > 0 && budget-- > 0)
            {
                Node node = stack.Pop();
                PdfObject current = node.Object;
                if (current is PdfReference reference)
                {
                    if (!visited.Add(reference.ReferenceObjectNumber)) continue;
                    current = resolve(reference.ReferenceObjectNumber);
                }

                if (!(current is PdfDictionary field)) continue;

                PdfName fieldType = Deref(field[PdfName.FT], resolve) as PdfName ?? node.InheritedType;
                PdfObject value = field[PdfName.V] ?? node.InheritedValue;

                // A signature field WITH A VALUE. An unsigned, empty signature field is a
                // placeholder and rewriting it invalidates nothing.
                if (fieldType != null && fieldType.Value == "Sig" && value != null) return true;

                if (Deref(field[PdfName.Kids], resolve) is PdfArray kids)
                    for (int i = 0; i < kids.Count; i++)
                        stack.Push(new Node(kids[i], fieldType, value));
            }

            // A walk that stopped with the tree unread has no answer. Stopping is not evidence of
            // absence, and a rewrite that breaks a signature this never reached is the outcome the
            // type exists to prevent, so the unread tree counts as signed. The caller's own
            // allow-signed option is the way through.
            return stack.Count > 0;
        }

        /// <summary>
        /// One node of the field-tree descent, carrying the inheritable entries an ancestor
        /// supplied (Table 220). Null means "no ancestor set it".
        /// </summary>
        private readonly struct Node
        {
            internal readonly PdfObject Object;
            internal readonly PdfName InheritedType;
            internal readonly PdfObject InheritedValue;

            internal Node(PdfObject obj, PdfName inheritedType, PdfObject inheritedValue)
            {
                Object = obj;
                InheritedType = inheritedType;
                InheritedValue = inheritedValue;
            }
        }

        private static PdfObject Deref(PdfObject obj, Func<int, PdfObject> resolve)
        {
            int guard = 0;
            while (obj is PdfReference reference && guard++ < 32)
                obj = resolve(reference.ReferenceObjectNumber);
            return obj;
        }
    }
}
