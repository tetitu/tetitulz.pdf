// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>Extracts all actions from a PDF file.</summary>
        public static IReadOnlyList<PdfAction> ExtractActions(string inputPath)
            => PdfActionDispatcher.ExtractActions(inputPath);

        /// <summary>Extracts all actions from a PDF stream.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<PdfAction> ExtractActions(Stream input)
            => ExtractActions(input, null);

        /// <summary>
        /// Extracts all actions from a PDF stream, decrypting first when the document is encrypted
        /// and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">
        /// The user password. Ignored for an unencrypted document. An action's target — a URI, a
        /// JavaScript body, a file name — is a PDF <i>string</i> and so is ciphertext in an
        /// encrypted file, which is why enumerating without decrypting is not enough.
        /// </param>
        public static IReadOnlyList<PdfAction> ExtractActions(Stream input, string password)
            => WithDecrypted(input, password, PdfActionDispatcher.ExtractActions);

        /// <summary>Returns the AcroForm calculation order (/CO) field names.</summary>
        public static IReadOnlyList<string> GetCalculationOrder(string inputPath)
            => PdfActionDispatcher.GetCalculationOrder(inputPath);

        /// <summary>Returns the AcroForm calculation order (/CO) field names.</summary>
        public static IReadOnlyList<string> GetCalculationOrder(Stream input)
            => GetCalculationOrder(input, null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static IReadOnlyList<string> GetCalculationOrder(Stream input, string password)
            => WithDecrypted(input, password, PdfActionDispatcher.GetCalculationOrder);

        /// <summary>
        /// Executes a JavaScript string against the given execution context.
        /// Field values, event state, and alerts are read from / written to
        /// <paramref name="ctx"/>.  Modified field values are returned in
        /// <see cref="PdfJsExecutionContext.ModifiedFieldValues"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if a registered <see cref="Scripting.IJavaScriptPlugin"/> executed the
        /// script (a script that itself threw still counts — see
        /// <see cref="PdfJsExecutionContext.Error"/>); <c>false</c> if none is registered, with the
        /// reason on <see cref="Scripting.PluginJavaScriptRunner.LastError"/>.
        /// </returns>
        /// <remarks>
        /// The engine ships in
        /// <c>Tetitulz.Pdf.Plugins.JavaScript</c> and must be registered; a <c>bool</c> return is
        /// what stops "no engine present" from being indistinguishable from "the script ran and
        /// changed nothing". See GAPS.md §15b.
        /// </remarks>
        public static bool ExecuteJavaScript(string script, PdfJsExecutionContext ctx)
            => Scripting.PluginJavaScriptRunner.TryExecute(script, ctx);

        /// <summary>
        /// Commits a value a user entered into a field through the field's Keystroke and Validate
        /// scripts and the document's calculations — what a viewer does when the user leaves the
        /// field. Writes nothing: fill the accepted value and <see cref="Operations.Forms.FieldCommitResult.OtherChanges"/>
        /// with <see cref="Operations.Forms.PdfFormFiller"/>.
        /// </summary>
        /// <param name="input">The (decrypted) document.</param>
        /// <param name="fieldName">The field's fully qualified name.</param>
        /// <param name="value">The value entered.</param>
        /// <param name="fieldValues">Every field's current value, by fully qualified name.</param>
        public static Operations.Forms.FieldCommitResult CommitFieldValue(Stream input, string fieldName,
            string value, IReadOnlyDictionary<string, string> fieldValues)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            return Operations.Forms.PdfFieldScripts.Commit(doc, fieldName, value, fieldValues);
        }

        /// <summary>
        /// <see cref="CommitFieldValue(Stream, string, string, IReadOnlyDictionary{string, string})"/>
        /// over an <b>encrypted</b> document.
        /// </summary>
        /// <param name="input">The encrypted document.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="fieldName">The field's fully qualified name.</param>
        /// <param name="value">The value entered.</param>
        /// <param name="fieldValues">Every field's current value, by fully qualified name.</param>
        public static Operations.Forms.FieldCommitResult CommitFieldValue(Stream input, string password,
            string fieldName, string value, IReadOnlyDictionary<string, string> fieldValues)
            => WithDecrypted(input, password, s => CommitFieldValue(s, fieldName, value, fieldValues));

        /// <summary>Runs document-level JavaScript from an <b>encrypted</b> file.</summary>
        /// <param name="password">User or owner password.</param>
        /// <param name="inputPath">Path to the encrypted PDF file.</param>
        /// <param name="ctx">
        /// Execution context carrying the field values and results across scripts;
        /// <see langword="null"/> mints one for this call.
        /// </param>
        public static PdfJsExecutionContext ExecuteDocumentScripts(
            string inputPath,
            string password,
            PdfJsExecutionContext ctx = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExecuteDocumentScripts(file, password, ctx);
        }

        /// <summary>
        /// Extracts and executes the JavaScript actions of a PDF file that belong to the document
        /// rather than to something a user clicks. Returns the execution context after all scripts
        /// have run.
        /// </summary>
        /// <remarks>
        /// <para>Runs, in the order <see cref="PdfActionDispatcher.ExtractActions(string)"/> lists
        /// them: the document-level scripts in <c>/Names /JavaScript</c>, the <c>/OpenAction</c>, the
        /// catalog's and each page's <c>/AA</c>, and each form field's scripts. It does not run an
        /// annotation's or an outline item's script, which runs when the user activates it.</para>
        /// <para>The scripts share one environment (<see cref="PdfJsExecutionContext.DocumentScope"/>,
        /// opened here unless the caller's context carries one), so a document-level function is
        /// visible to the scripts after it; each runs with the API's <c>event.type</c> and
        /// <c>event.name</c> for its trigger. The document's fields are handed to the engine
        /// (<see cref="PdfJsExecutionContext.FieldDescriptions"/>, and their values where the caller
        /// gave none).</para>
        /// </remarks>
        public static PdfJsExecutionContext ExecuteDocumentScripts(
            string inputPath,
            PdfJsExecutionContext ctx = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExecuteDocumentScripts(file, ctx);
        }

        /// <summary>
        /// Runs each script in <paramref name="actions"/> that <paramref name="runs"/> accepts, under
        /// one budget and in one script environment for the whole document.
        /// </summary>
        private static void RunDocumentScripts(IReadOnlyList<PdfAction> actions, PdfJsExecutionContext ctx,
            Func<PdfAction, bool> runs)
        {
            // One budget for the whole document. The engine's limits are per call, so without
            // this a form carrying two hundred field scripts is entitled to two hundred times
            // five seconds - and every one of those scripts came out of the file.
            using var budget = Scripting.PluginJavaScriptRunner.BeginDocumentBudget(
                Scripting.PluginJavaScriptRunner.DefaultDocumentBudget);

            ctx.DocumentScope ??= new Scripting.PdfJsDocumentScope();
            foreach (var action in actions)
            {
                if (!runs(action)) continue;
                (ctx.EventType, ctx.EventName) = Scripting.PdfJsEventNames.For(action.Source, action.Trigger);
                if (action.Source != PdfActionSource.Field || string.IsNullOrEmpty(action.FieldName))
                {
                    ctx.EventFieldName = string.Empty;
                    ctx.EventValue = string.Empty;
                    Scripting.PluginJavaScriptRunner.TryExecute(action.Target, ctx);
                    continue;
                }

                // A field's script runs as that field's event; only a Calculate script's event value
                // is the field's value — a Format script's is the text it shows, and the rest leave it.
                string field = action.FieldName;
                bool had = ctx.ModifiedFieldValues.TryGetValue(field, out string before);
                ctx.EventFieldName = field;
                ctx.EventValue = had ? before : ctx.FieldValues.TryGetValue(field, out string v) ? v ?? "" : "";
                Scripting.PluginJavaScriptRunner.TryExecute(action.Target, ctx);
                if (action.Trigger != "Calculate")
                {
                    if (had) ctx.ModifiedFieldValues[field] = before;
                    else ctx.ModifiedFieldValues.Remove(field);
                }
            }
            ctx.EventFieldName = string.Empty;
        }

        /// <summary>
        /// Gives <paramref name="ctx"/> what the document's scripts read of it: the page count, the
        /// fields (each value the caller did not set), the <c>/Info</c> and the attachments.
        /// </summary>
        private static void DescribeFields(byte[] document, PdfJsExecutionContext ctx)
        {
            using var doc = PdfDocument.Open(new MemoryStream(document, writable: false));
            Operations.EncryptedRead.Refuse(doc, "the form fields", "ExecuteDocumentScripts(Stream, string, PdfJsExecutionContext)");
            Scripting.PdfJsDocumentFacts.Describe(doc, ctx);
        }

        /// <summary>
        /// A JavaScript action with a script, found on the document, a page or a form field. An
        /// annotation's or an outline item's action runs when the user activates it, so it is not a
        /// document script.
        /// </summary>
        private static bool IsDocumentScript(PdfAction action)
            => action.ActionType == PdfActionType.JavaScript
               && !string.IsNullOrEmpty(action.Target)
               && (action.Source == PdfActionSource.Document
                   || action.Source == PdfActionSource.Page
                   || action.Source == PdfActionSource.Field);

        /// <summary>Runs document-level JavaScript from an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. The password sits <i>before</i> the optional tail —
        /// a required parameter cannot follow an optional one.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="ctx">
        /// Execution context carrying the field values and results across scripts;
        /// <see langword="null"/> mints one for this call.
        /// </param>
        public static PdfJsExecutionContext ExecuteDocumentScripts(
            Stream input,
            string password,
            PdfJsExecutionContext ctx = null)
            => WithDecrypted(input, password, s => ExecuteDocumentScripts(s, ctx));

        /// <summary>
        /// Extracts and executes the JavaScript actions of a PDF stream that belong to the document
        /// rather than to something a user clicks.
        /// </summary>
        /// <remarks>
        /// The same set as <see cref="ExecuteDocumentScripts(string, PdfJsExecutionContext)"/>,
        /// which gives it: document-level scripts, the <c>/OpenAction</c>, the catalog's and pages'
        /// <c>/AA</c>, and form fields' scripts; no annotation or outline script.
        /// </remarks>
        public static PdfJsExecutionContext ExecuteDocumentScripts(
            Stream input,
            PdfJsExecutionContext ctx = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return RunScriptsOf(input, ctx ?? new PdfJsExecutionContext(), IsDocumentScript);
        }

        /// <summary>
        /// Runs the scripts a viewer runs when it opens the document, in the order it runs them: the
        /// document-level scripts (<c>/Names /JavaScript</c>), the <c>/OpenAction</c>, and the first
        /// page's open action (<c>/AA /O</c>). Returns the execution context after they have run.
        /// </summary>
        /// <remarks>
        /// <para>Unlike <see cref="ExecuteDocumentScripts(Stream, PdfJsExecutionContext)"/>, no field
        /// script runs: a viewer runs a field's Keystroke, Format, Validate, Calculate and mouse scripts
        /// on those events, not at open. The scripts share one environment and see the document's
        /// fields, <c>/Info</c> and attachments, as the document scripts do.</para>
        /// </remarks>
        /// <param name="input">Stream holding the PDF.</param>
        /// <param name="ctx">
        /// Execution context carrying the field values and results across scripts;
        /// <see langword="null"/> mints one for this call.
        /// </param>
        public static PdfJsExecutionContext ExecuteOpenScripts(Stream input, PdfJsExecutionContext ctx = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return RunScriptsOf(input, ctx ?? new PdfJsExecutionContext(), IsOpenScript);
        }

        /// <summary>Runs the scripts a viewer runs when it opens the PDF file at <paramref name="inputPath"/>.</summary>
        /// <remarks>See <see cref="ExecuteOpenScripts(Stream, PdfJsExecutionContext)"/>.</remarks>
        /// <param name="inputPath">Path to the PDF file.</param>
        /// <param name="ctx">
        /// Execution context carrying the field values and results across scripts;
        /// <see langword="null"/> mints one for this call.
        /// </param>
        public static PdfJsExecutionContext ExecuteOpenScripts(string inputPath, PdfJsExecutionContext ctx = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExecuteOpenScripts(file, ctx);
        }

        /// <summary>Runs the open scripts of an <b>encrypted</b> document.</summary>
        /// <remarks>See <see cref="ExecuteOpenScripts(Stream, PdfJsExecutionContext)"/>.</remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="ctx">
        /// Execution context carrying the field values and results across scripts;
        /// <see langword="null"/> mints one for this call.
        /// </param>
        public static PdfJsExecutionContext ExecuteOpenScripts(Stream input, string password, PdfJsExecutionContext ctx = null)
            => WithDecrypted(input, password, s => ExecuteOpenScripts(s, ctx));

        /// <summary>Runs the open scripts of the <b>encrypted</b> PDF file at <paramref name="inputPath"/>.</summary>
        /// <remarks>See <see cref="ExecuteOpenScripts(Stream, PdfJsExecutionContext)"/>.</remarks>
        /// <param name="inputPath">Path to the encrypted PDF file.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="ctx">
        /// Execution context carrying the field values and results across scripts;
        /// <see langword="null"/> mints one for this call.
        /// </param>
        public static PdfJsExecutionContext ExecuteOpenScripts(string inputPath, string password, PdfJsExecutionContext ctx = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExecuteOpenScripts(file, password, ctx);
        }

        /// <summary>
        /// A JavaScript action a viewer runs when it opens the document: a document-level script or
        /// the open action, or the first page's open action.
        /// </summary>
        private static bool IsOpenScript(PdfAction action)
            => action.ActionType == PdfActionType.JavaScript
               && !string.IsNullOrEmpty(action.Target)
               && ((action.Source == PdfActionSource.Document && action.Trigger == "OpenDocument")
                   || (action.Source == PdfActionSource.Page && action.PageIndex == 0 && action.Trigger == "PageOpen"));

        /// <summary>Reads the document once, describes it to <paramref name="ctx"/>, and runs the scripts <paramref name="runs"/> accepts.</summary>
        private static PdfJsExecutionContext RunScriptsOf(Stream input, PdfJsExecutionContext ctx, Func<PdfAction, bool> runs)
        {
            byte[] document;
            using (var copy = new MemoryStream())
            {
                input.CopyTo(copy);
                document = copy.ToArray();
            }
            IReadOnlyList<PdfAction> actions = PdfActionDispatcher.ExtractActions(new MemoryStream(document, writable: false));
            DescribeFields(document, ctx);
            // A page's words are read when a script first asks for them, from the bytes read above.
            ctx.PageWords = Scripting.PdfJsDocumentFacts.PageWordsReader(
                () => PdfDocument.Open(new MemoryStream(document, writable: false)), ownsEachOpen: true);
            RunDocumentScripts(actions, ctx, runs);
            return ctx;
        }
    }
}
