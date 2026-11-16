// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Plugins.Templating
{
    /// <summary>
    /// Fills a PDF template: named AcroForm fields, and literal <c>{{token}}</c> placeholders drawn
    /// on the page.
    ///
    /// <para><b>Hebrew and other right-to-left scripts are supported</b>, through
    /// <c>Core.Text.TextRunComposer</c> — the same shape-then-bidi-reorder pipeline that generates
    /// form-field appearances. Two things a caller has to supply, because neither can be inferred:
    /// a <b>font</b> (this library ships none — see <see cref="TemplateOptions.FontProvider"/>), and
    /// for an RTL template the <b>direction</b> (see
    /// <see cref="TemplateOptions.BaseDirection"/>, and read why it cannot be auto-detected).</para>
    ///
    /// <para><b>What this does to signatures.</b> Filling form fields alone appends an incremental
    /// update, so an existing approval signature over the original bytes still verifies. Replacing a
    /// page token does <b>not</b>: erasing the placeholder means rewriting the content stream, which
    /// is a whole-file rewrite, and every existing signature is invalidated. That is the trade the
    /// erasure requirement forces — an incremental update cannot destroy the old glyphs, because
    /// they remain in the revision the first signature's digest covers. If you need the signature
    /// preserved, use form fields.</para>
    /// </summary>
    public static class PdfTemplateFiller
    {
        /// <summary>
        /// Fills <paramref name="values"/> into <paramref name="input"/> and writes the result to
        /// <paramref name="output"/>.
        /// </summary>
        /// <param name="input">The template. Must be readable; seekability is arranged internally.</param>
        /// <param name="output">Receives the filled document.</param>
        /// <param name="values">Placeholder name → replacement text.</param>
        /// <param name="options">Null uses the defaults.</param>
        /// <returns>What was filled, what was not, and why — see <see cref="TemplateFillReport"/>.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public static TemplateFillReport Fill(Stream input, Stream output,
                                              IDictionary<string, string> values,
                                              TemplateOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (values == null) throw new ArgumentNullException(nameof(values));

            options = options ?? new TemplateOptions();
            var report = new TemplateFillReport();

            bool wantFields = (options.Sources & TemplateSources.FormFields) != 0;
            bool wantTokens = (options.Sources & TemplateSources.PageTokens) != 0;

            var remaining = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in values) remaining[kvp.Key] = kvp.Value;

            // Form fields first, and page tokens over that result. The order matters: filling fields
            // appends an incremental update, while replacing a token rewrites the whole file — doing
            // the rewrite second means the field values it carries forward are already in place.
            byte[] current = ReadAll(input);

            // Encrypted input, first: PdfDocument loads no catalog for one, so HasAcroForm below
            // answers false for a reason that has nothing to do with the template's shape. The
            // comment on HasAcroForm justifies that by saying the token pass reports its own
            // reason — which does not hold when the caller disabled the token pass. With
            // Sources = FormFields alone the run then wrote the input back out unchanged and put
            // EVERY supplied key into UnmatchedKeys, whose own documentation tells the caller to
            // go and check their field names for a typo. That is the wrong problem, for a document
            // that simply could not be opened.
            //
            // This assembly cannot decrypt (the ring model keeps Tetitulz.Pdf.Encryption out of
            // this package graph), so it says so and keeps the keys out of UnmatchedKeys.
            bool encrypted = IsEncrypted(current);
            if (encrypted)
            {
                report.WarningList.Add(
                    "The template is encrypted, so neither its form fields nor its page content " +
                    "could be read; no value was filled. Decrypt it first with " +
                    "Tetitulz.Pdf.Encryption (PdfEncryptedRoundTrip) and fill the result.");
                wantFields = false;
                wantTokens = false;
            }

            // A template with no AcroForm is an ordinary token-only template, not an error —
            // PdfFormFiller throws "PDF has no AcroForm" rather than returning an empty result, so
            // the absence has to be established here instead of being caught after the fact.
            if (wantFields && !HasAcroForm(current)) wantFields = false;

            if (wantFields)
            {
                using var fieldInput = new MemoryStream(current, writable: false);
                using var fieldOutput = new MemoryStream();

                var formResult = PdfFormFiller.Fill(fieldInput, fieldOutput, remaining, new FormFillOptions
                {
                    FontProvider = options.FontProvider,
                    AllowSystemFontSubstitution = options.AllowSystemFontSubstitution,
                    GenerateAppearances = options.GenerateAppearances,
                    BaseDirection = options.BaseDirection,
                });

                foreach (string name in formResult.FilledFields)
                {
                    report.FilledFieldList.Add(name);
                    remaining.Remove(name);          // filled here; not a token candidate
                }
                foreach (string name in formResult.FieldsWithoutAppearance) report.NotDrawnList.Add(name);
                foreach (string w in formResult.Warnings) report.WarningList.Add(w);
                report.NeedAppearances = formResult.NeedAppearances;

                if (fieldOutput.Length > 0) current = fieldOutput.ToArray();
            }

            if (wantTokens && remaining.Count > 0)
            {
                using var tokenInput = new MemoryStream(current, writable: false);
                using var tokenOutput = new MemoryStream();

                var tokenResult = PdfTemplateEngine.Replace(tokenInput, tokenOutput, remaining,
                    new TemplateReplaceOptions
                    {
                        TokenOpen = options.TokenOpen,
                        TokenClose = options.TokenClose,
                        BaseDirection = options.BaseDirection,
                        FontProvider = options.FontProvider,
                        AllowSystemFontSubstitution = options.AllowSystemFontSubstitution,
                        KeepSlot = options.Fit == TemplateFit.KeepSlot,
                        VerifyErasure = options.VerifyErasure,
                    });

                // De-duplicated: the engine reports one entry per *match*, and the same token may
                // legitimately appear several times on a page. The report is keyed by placeholder
                // name, so listing a key twice would make FilledTokens.Count mean "occurrences"
                // in some documents and "keys" in others — and the invariant this type documents
                // is that every supplied key lands in exactly one bucket.
                foreach (string key in tokenResult.ReplacedKeys)
                {
                    if (!report.FilledTokenList.Contains(key)) report.FilledTokenList.Add(key);
                    remaining.Remove(key);
                }

                // A key whose placeholder was located but sits in a shared Form XObject was NOT
                // matched — but it is emphatically not "unmatched": the token is there, and telling
                // the caller to check their spelling would send them to the wrong problem.
                foreach (string key in tokenResult.NotEditableKeys)
                {
                    if (!report.NotEditableList.Contains(key)) report.NotEditableList.Add(key);
                    remaining.Remove(key);
                }

                // Carried through rather than folded into NotEditableKeys: the two answer different
                // questions. "Not editable" is decided before writing, from the template's shape;
                // this is decided after writing, by reading the output back. A key can be on both
                // (a token in a shared form) or on this one alone, which is the case worth seeing.
                foreach (string key in tokenResult.UnverifiedKeys)
                    if (!report.UnverifiedList.Contains(key)) report.UnverifiedList.Add(key);

                foreach (string key in tokenResult.NotDrawnKeys)
                    if (!report.NotDrawnList.Contains(key)) report.NotDrawnList.Add(key);
                foreach (string w in tokenResult.Warnings) report.WarningList.Add(w);

                if (tokenOutput.Length > 0) current = tokenOutput.ToArray();
            }

            // Whatever is still here matched neither a field nor a token — unless nothing was ever
            // looked at, in which case "unmatched" would be a claim about the template that was
            // never tested.
            if (!encrypted)
                foreach (string key in remaining.Keys) report.UnmatchedList.Add(key);

            output.Write(current, 0, current.Length);
            return report;
        }

        // One implementation, in Core.IO.StreamBytes.
        private static byte[] ReadAll(Stream input) => StreamBytes.ReadAll(input);

        /// <summary>
        /// Whether the document declares a trailer <c>/Encrypt</c>. A file this cannot open is not
        /// reported as encrypted — that failure belongs to whichever pass tries to read it.
        /// </summary>
        private static bool IsEncrypted(byte[] pdf)
        {
            try
            {
                using var ms = new MemoryStream(pdf, writable: false);
                using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
                return doc.IsEncrypted;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether the document declares an AcroForm <b>with at least one field</b>.
        ///
        /// <para>The field count is part of the question, not decoration. <c>doc.AcroForm != null</c>
        /// alone is true of <c>/AcroForm &lt;&lt; /Fields [] &gt;&gt;</c> — what XFA-stripped and
        /// form-capable-but-form-less generators routinely emit — and such a template would take the
        /// form-fill branch, match nothing, and still append an incremental update that changes not
        /// one byte of content.</para>
        ///
        /// <para>An unreadable document answers <c>false</c> and is left to the token pass, which
        /// reports its own reason — swallowing the failure here would turn "this file will not open"
        /// into the wrong diagnosis, "it has no form fields".</para>
        /// </summary>
        private static bool HasAcroForm(byte[] pdf)
        {
            try
            {
                using var ms = new MemoryStream(pdf, writable: false);
                using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
                if (doc.AcroForm == null) return false;

                // Core's own /Fields walk, so "has a field" means here what it means to
                // PdfFormFiller one call later.
                var fields = doc.AcroForm.Fields;
                return fields != null && fields.Count > 0;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}

