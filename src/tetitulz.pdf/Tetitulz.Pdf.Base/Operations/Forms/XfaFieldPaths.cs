// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Maps an AcroForm fully-qualified field name onto a path in the XFA <c>datasets</c> tree.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the two names differ at all.</b> A static XFA form carries the same form twice:
    /// as XFA XML, and as an AcroForm whose widgets are what a non-XFA viewer draws. The authoring tool names
    /// the AcroForm fields after the XFA SOM expression that produced them —
    /// <c>topmostSubform[0].Page1[0].total[0]</c> — while the datasets tree that holds the values is
    /// plain nested elements, <c>topmostSubform/Page1/total</c>. The difference is the occurrence
    /// index, which SOM writes and XML does not need because position carries it.</para>
    ///
    /// <para><b>So the mapping is: drop the indices, then look the path up.</b> Not "guess a
    /// similar name" — the path either exists in the packet or it does not, and
    /// <see cref="Resolve"/> answers <see langword="null"/> rather than picking something close.
    /// Writing a value into the wrong subform produces a form that says something the caller never
    /// asked it to say, which is worse than one field left unfilled.</para>
    ///
    /// <para><b>One fallback, and only when it is unambiguous.</b> Producers other than the authoring tool
    /// write AcroForm names that are the leaf name alone, or that prefix a subform the datasets
    /// tree does not repeat. When the full path misses, a leaf whose own name matches and is the
    /// <i>only</i> such leaf in the packet is taken. A second leaf with that name makes the answer a
    /// coin toss, so there is no answer.</para>
    ///
    /// <para><b>Only non-index brackets survive.</b> <c>total[0]</c> loses its index;
    /// <c>total[x]</c> keeps its brackets, because that is not an occurrence index and stripping it
    /// would silently rename the field.</para>
    /// </remarks>
    internal static class XfaFieldPaths
    {
        /// <summary>
        /// The datasets path for <paramref name="acroFormName"/>, or <see langword="null"/> when the
        /// packet has no element for it.
        /// </summary>
        /// <param name="acroFormName">The AcroForm fully-qualified field name.</param>
        /// <param name="leafPaths">
        /// Every leaf path in the datasets packet — <c>XfaDatasets.ReadValues</c>' keys.
        /// </param>
        internal static string Resolve(string acroFormName, IEnumerable<string> leafPaths)
        {
            if (string.IsNullOrEmpty(acroFormName) || leafPaths == null) return null;

            // The paths model holds the first of repeated siblings only (XfaDatasets: the first
            // wins), so a later occurrence has no element here; stripping its index would name
            // the first instance and write the value into the wrong box.
            if (HasLaterOccurrence(acroFormName)) return null;

            string stripped = StripOccurrenceIndices(acroFormName);

            string onlyLeafMatch = null;
            int leafMatches = 0;
            string leafName = Core.Forms.FieldNames.Leaf(stripped);

            foreach (string path in leafPaths)
            {
                if (string.Equals(path, stripped, StringComparison.Ordinal))
                    return path;                       // the exact path wins outright

                if (string.Equals(Core.Forms.FieldNames.Leaf(path), leafName, StringComparison.Ordinal))
                {
                    leafMatches++;
                    onlyLeafMatch = path;
                }
            }

            return leafMatches == 1 ? onlyLeafMatch : null;
        }

        /// <summary>
        /// Whether <paramref name="name"/> carries an occurrence index other than zero on a named
        /// segment — an instance after the first of its siblings.
        /// </summary>
        internal static bool HasLaterOccurrence(string name)
        {
            for (int i = name.IndexOf('['); i >= 0; i = name.IndexOf('[', i + 1))
            {
                int close = name.IndexOf(']', i + 1);
                if (close <= i + 1) continue;
                // An unnamed subform (#subform[1], a designer's page) has no element in the data.
                int segment = name.LastIndexOf('.', i) + 1;
                if (segment < name.Length && name[segment] == '#') continue;
                bool allDigits = true, nonZero = false;
                for (int d = i + 1; d < close; d++)
                {
                    char c = name[d];
                    if (c < '0' || c > '9') { allDigits = false; break; }
                    if (c != '0') nonZero = true;
                }
                if (allDigits && nonZero) return true;
            }
            return false;
        }

        /// <summary>
        /// <paramref name="name"/> with every <c>[n]</c> occurrence index removed.
        /// </summary>
        /// <remarks>
        /// Only a bracketed run of decimal digits is an occurrence index. Anything else inside
        /// brackets is part of the name as the producer wrote it, and removing it would address a
        /// different field.
        /// </remarks>
        internal static string StripOccurrenceIndices(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('[') < 0) return name;

            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] != '[') { sb.Append(name[i]); continue; }

                int close = name.IndexOf(']', i + 1);
                bool allDigits = close > i + 1;
                for (int d = i + 1; d < close && allDigits; d++)
                    if (name[d] < '0' || name[d] > '9') allDigits = false;

                if (allDigits) i = close;              // skip "[123]" entirely
                else sb.Append(name[i]);
            }

            return sb.ToString();
        }
    }
}
