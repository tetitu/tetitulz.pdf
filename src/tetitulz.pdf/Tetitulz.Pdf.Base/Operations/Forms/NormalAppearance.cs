// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// The §12.5.5 discrimination every reader of an annotation's <c>/AP /N</c> has to make:
    /// the entry is <b>either</b> a single appearance stream <b>or</b> a sub-dictionary keyed by
    /// appearance state, and nothing in the object itself announces which.
    ///
    /// <para><b>Why this is a type and not four lines at each call site.</b> Getting it wrong is
    /// invisible rather than loud, because <see cref="PdfStream"/> <i>derives from</i>
    /// <see cref="PdfDictionary"/> in this object model. So <c>resolved as PdfDictionary</c>
    /// succeeds for the stream case too, and a caller that only asks that question then walks a
    /// single appearance stream's <b>own keys</b> — <c>/Length</c>, <c>/Filter</c>, <c>/BBox</c>,
    /// <c>/Resources</c>, <c>/Type</c>, <c>/Subtype</c> — believing they are appearance states.
    /// That is precisely what <c>PdfFormReader.ReadWidgets</c> did, so
    /// <c>PdfFormFieldInfo.OnStateList</c> for every text field listed its stream's dictionary
    /// keys.</para>
    ///
    /// <para><b>The warning was already written down, one file away.</b>
    /// <c>PdfFormFlattener.ResolveNormalAppearance</c> carries the comment "PdfStream derives from
    /// PdfDictionary, so the stream case must be tested first" and does it correctly. Prose in one
    /// file does not protect another; this type is that comment made reachable.</para>
    ///
    /// <para>A leaf by construction — no static fields, so no type initializer and no
    /// initialization edge to anything that has one.</para>
    /// </summary>
    internal static class NormalAppearance
    {
        /// <summary>
        /// Resolves <paramref name="widget"/>'s <c>/AP /N</c> and reports which of the two §12.5.5
        /// shapes it is.
        /// </summary>
        /// <param name="doc">Document, for following indirect references.</param>
        /// <param name="widget">The widget (or other) annotation dictionary.</param>
        /// <param name="single">
        /// Set when <c>/N</c> is a lone appearance stream — the annotation has exactly one
        /// appearance and therefore <b>no</b> appearance states.
        /// </param>
        /// <param name="states">
        /// Set when <c>/N</c> is a state sub-dictionary. Its keys are the appearance state names
        /// (<c>/Off</c> plus one or more "on" states).
        /// </param>
        /// <returns>
        /// <c>true</c> when <c>/N</c> is present and is one of the two shapes; <c>false</c> when
        /// there is no <c>/AP</c>, no <c>/N</c>, or <c>/N</c> is neither. Exactly one of
        /// <paramref name="single"/> and <paramref name="states"/> is non-null on <c>true</c>.
        /// </returns>
        internal static bool TryResolve(
            PdfDocument doc, PdfDictionary widget,
            out PdfStream single, out PdfDictionary states)
        {
            single = null;
            states = null;
            if (widget == null) return false;

            var ap = PdfResolve.Dict(doc, widget, PdfName.AP);
            if (ap == null) return false;

            var normal = PdfResolve.Value(doc, ap[PdfName.N]);

            // The stream test MUST come first: PdfStream is a PdfDictionary, so the dictionary
            // test alone matches both shapes and silently mis-reads the single-appearance one.
            if (normal is PdfStream stream) { single = stream; return true; }
            if (normal is PdfDictionary dict) { states = dict; return true; }

            return false;
        }

        /// <summary>
        /// The state sub-dictionary alone, or <c>null</c> when <c>/AP /N</c> is absent or is a
        /// single appearance stream (which has no states).
        ///
        /// <para><b>This overload exists because the shape of <see cref="TryResolve"/> is a
        /// footgun for the common caller.</b> <c>TryResolve</c> returns <c>true</c> for
        /// <i>either</i> shape, so <c>if (TryResolve(…, out _, out var states))
        /// foreach (var kv in states)</c> reads correctly and throws a
        /// <see cref="System.NullReferenceException"/> on every single-stream widget. A caller that only wants the states should not
        /// have to remember which <c>out</c> the <c>true</c> refers to.</para>
        /// </summary>
        internal static PdfDictionary GetStates(PdfDocument doc, PdfDictionary widget)
        {
            TryResolve(doc, widget, out _, out var states);
            return states;
        }
    }
}
