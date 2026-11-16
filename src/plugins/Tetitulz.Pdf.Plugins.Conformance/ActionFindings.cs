// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Action-rule violations with the same message stem, counted, so a thousand forbidden links are
    /// one entry that says how many and where the first is. Shared by <see cref="PdfAValidator"/> and
    /// <see cref="PdfXValidator"/>, with the wording of a location.
    /// </summary>
    /// <remarks>
    /// One entry per offending object was the PDF/X check's shape, and it cost nothing while the
    /// check read a handful of places. Reading every action a document holds, a form with a script
    /// on each of two thousand fields would be two thousand identical lines.
    /// </remarks>
    internal sealed class ActionFindings
    {
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, (string Code, int Count, string First)> _byStem =
            new Dictionary<string, (string, int, string)>(StringComparer.Ordinal);

        internal void Add(string code, string stem, string where)
        {
            if (_byStem.TryGetValue(stem, out var found))
            {
                _byStem[stem] = (found.Code, found.Count + 1, found.First);
                return;
            }
            _order.Add(stem);
            _byStem[stem] = (code, 1, where);
        }

        /// <summary>Each stem once, in the order first seen: <c>stem; found on X.</c> or <c>stem; N found, the first on X.</c></summary>
        internal void Emit(Action<string, string> add)
        {
            foreach (string stem in _order)
            {
                var (code, count, first) = _byStem[stem];
                add(code, count == 1
                    ? $"{stem}; found on {first}."
                    : $"{stem}; {count} found, the first on {first}.");
            }
        }

        /// <summary>Where an <see cref="ActionOwnerWalk"/> owner is.</summary>
        internal static string Where(ActionOwner owner) => owner.Kind switch
        {
            ActionOwnerKind.Catalog    => "the document catalog",
            ActionOwnerKind.Page       => $"page {owner.PageIndex + 1}",
            ActionOwnerKind.Widget     => $"a widget annotation on page {owner.PageIndex + 1}",
            ActionOwnerKind.Annotation => $"an annotation on page {owner.PageIndex + 1}",
            _                          => "a node of the AcroForm field tree",
        };

        /// <summary>Where a <see cref="PdfActionDispatcher"/> action is, and how far along its chain.</summary>
        internal static string Where(PdfAction action)
        {
            string at = action.Source switch
            {
                PdfActionSource.Document   => $"the document's {action.Trigger} trigger",
                PdfActionSource.Page       => $"page {action.PageIndex + 1}'s {action.Trigger} trigger",
                PdfActionSource.Field      => $"a form-field action of '{action.FieldName}' ({action.Trigger})",
                PdfActionSource.Annotation => $"an annotation on page {action.PageIndex + 1} ({action.Trigger})",
                _                          => "an outline item",
            };
            return action.ChainDepth == 0 ? at : $"{at}, {action.ChainDepth} along its /Next chain";
        }
    }
}
