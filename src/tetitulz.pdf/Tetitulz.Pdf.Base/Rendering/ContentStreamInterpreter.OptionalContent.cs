// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    internal sealed partial class ContentStreamInterpreter
    {
        /// <summary>
        /// Returns true when the object carries an /OC entry pointing to an OCG that is
        /// currently hidden (present in <see cref="HiddenOcgs"/>).
        /// Returns false when /OC is absent, HiddenOcgs is null, or the OCG is visible.
        /// Handles direct OCG refs and OCMDs with /OCGs+/P and /VE boolean expressions
        /// (ISO 32000-1 §8.11.2.2).
        /// </summary>
        private bool IsOcHidden(PdfDictionary dict)
            => dict != null && IsOcEntryHidden(dict[PdfName.OC], HiddenOcgs, _doc);

        /// <summary>
        /// Shared /OC evaluation, also used by <see cref="PdfPageRenderer"/> for annotations
        /// (PDF §12.5.2) — an annotation on a hidden layer must be skipped just like a
        /// hidden-layer XObject or BDC block.
        /// </summary>
        internal static bool IsAnnotationOcHidden(
            PdfDictionary dict,
            System.Collections.Generic.ISet<int> hiddenOcgs,
            PdfDocument doc)
            => dict != null && IsOcEntryHidden(dict[PdfName.OC], hiddenOcgs, doc);

        /// <summary>
        /// Evaluates an optional-content entry — a reference to an OCG, or an OCMD — against the
        /// hidden set. Takes the <b>raw</b> entry, because an OCG is identified by its object
        /// number and resolving first would throw that away.
        /// </summary>
        /// <remarks>
        /// The call sites pass different shapes. XObjects and annotations carry <c>/OC</c> pointing
        /// at the OCG; <c>BDC</c> instead names a <c>/Properties</c> resource that <em>is</em> the
        /// OCG. A helper that always looked for an <c>/OC</c> key would never suppress a
        /// <c>/OC /P BDC … EMC</c> block.
        /// </remarks>
        internal static bool IsOcEntryHidden(
            PdfObject ocEntry,
            System.Collections.Generic.ISet<int> hiddenOcgs,
            PdfDocument doc)
        {
            // No shortcut on an empty hidden set: nothing hidden is every group ON, which hides
            // what an OCMD shows only when groups are off (/AnyOff, /AllOff, a /Not expression).
            if (ocEntry == null) return false;
            var ocObj = doc != null ? (doc.ResolveObject(ocEntry) ?? ocEntry) : ocEntry;
            if (ocObj is not PdfDictionary ocDict) return false;

            // /Type is optional in practice: producers routinely omit it on OCGs. Treat a
            // dictionary carrying OCMD-only keys as an OCMD and anything else as an OCG,
            // rather than requiring an explicit /Type and silently showing the content.
            var t = PdfResolve.Name(doc, ocDict, PdfName.Type)?.Value;
            bool isOcmd = t == "OCMD"
                          || (t != "OCG" && (ocDict[PdfName.OCGs] != null || ocDict[PdfName.VE] != null));

            if (isOcmd) return IsOcmdHidden(ocDict, hiddenOcgs, doc);
            return ocEntry is PdfReference r && IsGroupOff(hiddenOcgs, r.ReferenceObjectNumber);
        }

        /// <summary>
        /// Whether a group is OFF. A null set is the renderers' "nothing hidden" — every group ON
        /// (<c>PdfOptionalContentRefs.BuildHiddenSet</c> answers null rather than an empty set).
        /// </summary>
        private static bool IsGroupOff(System.Collections.Generic.ISet<int> hiddenOcgs, int objectNumber)
            => hiddenOcgs != null && hiddenOcgs.Contains(objectNumber);

        /// <summary>
        /// Evaluates an OCMD (Optional Content Membership Dictionary) against the
        /// set of hidden OCGs.  Precedence per PDF spec §8.11.2.2:
        ///   1. /VE (recursive boolean expression) — if present, takes priority.
        ///   2. /OCGs + /P (policy: AllOn / AnyOn / AnyOff / AllOff).
        /// Returns true if the OCMD resolves to "hidden" (content should be skipped).
        /// </summary>
        internal static bool IsOcmdHidden(
            PdfDictionary ocmd,
            System.Collections.Generic.ISet<int> hiddenOcgs,
            PdfDocument doc)
        {
            if (ocmd == null) return false;

            // /VE takes priority when present.
            var veEntry = ocmd[PdfName.VE];
            if (veEntry != null)
            {
                var veObj = doc != null ? (doc.ResolveObject(veEntry) ?? veEntry) : veEntry;
                if (veObj is PdfArray veArr)
                {
                    // Content is visible iff the expression evaluates true.
                    return !EvaluateVisibilityExpression(veArr, hiddenOcgs, doc);
                }
            }

            // /OCGs + /P policy.
            var ocgsObj = ocmd[PdfName.OCGs];
            if (doc != null) ocgsObj = doc.ResolveObject(ocgsObj) ?? ocgsObj;
            string policy = (PdfResolve.Name(doc, ocmd, PdfName.P))?.Value ?? "AnyOn";

            // /OCGs can be a single OCG reference OR an array of OCG references.
            var ocgRefs = new System.Collections.Generic.List<int>(4);
            if (ocgsObj is PdfArray arr)
            {
                foreach (var item in arr)
                    if (item is PdfReference pr) ocgRefs.Add(pr.ReferenceObjectNumber);
            }
            else if (ocgsObj is PdfReference singleRef)
            {
                ocgRefs.Add(singleRef.ReferenceObjectNumber);
            }

            if (ocgRefs.Count == 0) return false;

            int visibleCount = 0;
            foreach (int on in ocgRefs)
                if (!IsGroupOff(hiddenOcgs, on)) visibleCount++;
            int hiddenCount = ocgRefs.Count - visibleCount;

            // PDF spec §8.11.2.2 Table 102:
            //   AllOn  → visible iff all OCGs on   → hidden iff any hidden
            //   AnyOn  → visible iff any OCG on   → hidden iff all hidden (default)
            //   AnyOff → visible iff any OCG off  → hidden iff all on
            //   AllOff → visible iff all OCGs off → hidden iff any on
            return policy switch
            {
                "AllOn"  => hiddenCount > 0,
                "AnyOff" => hiddenCount == 0,
                "AllOff" => visibleCount > 0,
                _        => visibleCount == 0,    // AnyOn (default)
            };
        }

        /// <summary>
        /// Recursively evaluates a /VE visibility expression (ISO 32000-1 §8.11.2.2).
        ///   [/And  expr1 expr2 ...]   all sub-expressions must be visible
        ///   [/Or   expr1 expr2 ...]   any sub-expression visible
        ///   [/Not  expr]              invert
        ///   OCG-reference             visible when not in hiddenOcgs
        /// Returns true when the content governed by the expression is visible.
        /// </summary>
        // Visibility-expression (/VE) recursion is capped by
        // PdfLimits.MaxVisibilityExpressionDepth. A crafted pair of mutually-referencing
        // arrays ([/Not refB], [/Not refA]) recurses EvaluateVisibilityExpression↔
        // EvaluateVeOperand forever → StackOverflow. Bound the depth and fail open.
        //
        // Thrown when the /VE recursion exceeds that cap.  It unwinds straight to the
        // top-level EvaluateVisibilityExpression call, which fails OPEN (content visible).
        // A sentinel rather than a return value because an odd number of enclosing /Not
        // levels would otherwise invert a fail-open `true` back to `false` (content hidden),
        // making a deeply-nested expression fail *closed* — the opposite of fail-open.
        private sealed class VeDepthExceededException : System.Exception { }

        internal static bool EvaluateVisibilityExpression(
            PdfArray expr,
            System.Collections.Generic.ISet<int> hiddenOcgs,
            PdfDocument doc,
            int depth = 0)
        {
            // Top-level entry catches the depth-cap sentinel and fails open regardless of
            // the /Not parity in the unwound frames.  Recursive calls (depth > 0) come from
            // EvaluateVeOperand via EvaluateVeExpr, so they never re-enter this guard.
            if (depth == 0)
            {
                // One verdict an expression for a hidden set: every BDC, XObject and annotation naming the
                // OCMD asks again, and the expression is the same.
                VeVerdict known = VeVerdicts.GetValue(expr, _ => new VeVerdict());
                lock (known)
                {
                    if (ReferenceEquals(known.HiddenOcgs, hiddenOcgs) && known.Document == doc && known.Visible is bool v)
                        return v;
                }
                bool visible;
                try { visible = EvaluateVeExpr(expr, hiddenOcgs, doc, 0, new Dictionary<PdfArray, bool>(PdfReferenceEquality.Instance)); }
                catch (VeDepthExceededException) { visible = true; }
                lock (known)
                {
                    known.HiddenOcgs = hiddenOcgs;
                    known.Document = doc;
                    known.Visible = visible;
                }
                return visible;
            }
            return EvaluateVeExpr(expr, hiddenOcgs, doc, depth, new Dictionary<PdfArray, bool>(PdfReferenceEquality.Instance));
        }

        /// <summary>The last verdict an expression received, and the hidden set and document it was for.</summary>
        private sealed class VeVerdict
        {
            internal System.Collections.Generic.ISet<int> HiddenOcgs;
            internal PdfDocument Document;
            internal bool? Visible;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfArray, VeVerdict> VeVerdicts =
            new System.Runtime.CompilerServices.ConditionalWeakTable<PdfArray, VeVerdict>();

        /// <summary>
        /// An array's verdict within one evaluation: the expression is a graph whose arrays a file may share, and
        /// evaluated as a tree a sub-array named N times at each of D levels is visited N^D times — the depth cap
        /// alone bounds one branch, not the number of branches. Each array is evaluated once.
        /// </summary>
        private static bool EvaluateVeExpr(
            PdfArray expr,
            System.Collections.Generic.ISet<int> hiddenOcgs,
            PdfDocument doc,
            int depth,
            Dictionary<PdfArray, bool> memo)
        {
            if (memo.TryGetValue(expr, out bool done)) return done;
            bool verdict = EvaluateVeArray(expr, hiddenOcgs, doc, depth, memo);
            memo[expr] = verdict;
            return verdict;
        }

        /// <summary>
        /// How many visibility-expression arrays this thread has evaluated, all calls together: the evidence that a
        /// shared sub-array is evaluated once an evaluation, and an expression once a hidden set — a verdict cannot
        /// show which. A test reads the difference across one render.
        /// </summary>
        [ThreadStatic] internal static long VeArraysEvaluated;

        private static bool EvaluateVeArray(
            PdfArray expr,
            System.Collections.Generic.ISet<int> hiddenOcgs,
            PdfDocument doc,
            int depth,
            Dictionary<PdfArray, bool> memo)
        {
            VeArraysEvaluated++;
            if (expr == null || expr.Count == 0) return true;
            if (depth >= PdfLimits.MaxVisibilityExpressionDepth) throw new VeDepthExceededException();

            string op = (expr[0] as PdfName)?.Value;
            if (op == null) return true;

            switch (op)
            {
                case "And":
                    for (int i = 1; i < expr.Count; i++)
                        if (!EvaluateVeOperand(expr[i], hiddenOcgs, doc, depth + 1, memo)) return false;
                    return true;
                case "Or":
                    for (int i = 1; i < expr.Count; i++)
                        if (EvaluateVeOperand(expr[i], hiddenOcgs, doc, depth + 1, memo)) return true;
                    return false;
                case "Not":
                    if (expr.Count < 2) return true;
                    return !EvaluateVeOperand(expr[1], hiddenOcgs, doc, depth + 1, memo);
                default:
                    return true; // Unknown operator — fail open (show content).
            }
        }

        private static bool EvaluateVeOperand(
            PdfObject operand,
            System.Collections.Generic.ISet<int> hiddenOcgs,
            PdfDocument doc,
            int depth,
            Dictionary<PdfArray, bool> memo)
        {
            if (depth >= PdfLimits.MaxVisibilityExpressionDepth) throw new VeDepthExceededException();
            var resolved = doc != null ? (doc.ResolveObject(operand) ?? operand) : operand;

            // Sub-expression (nested array starting with /And/Or/Not).
            if (resolved is PdfArray arr && arr.Count > 0 && arr[0] is PdfName)
                return EvaluateVeExpr(arr, hiddenOcgs, doc, depth + 1, memo);

            // OCG reference — check against hidden set.
            if (operand is PdfReference pr)
                return !IsGroupOff(hiddenOcgs, pr.ReferenceObjectNumber);

            // Direct OCG dict without a reference — assume visible.
            return true;
        }
    }
}
