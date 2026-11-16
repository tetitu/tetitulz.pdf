// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// The form scripts a viewer runs when a user commits a field's value: the field's Keystroke
    /// script (<c>event.willCommit</c> true), then its Validate script, and — when both accept the
    /// value — every Calculate script in the AcroForm's <c>/CO</c> order (ISO 32000-1 §12.6.3,
    /// Table 196; §12.7.3 <c>/CO</c>). The scripts run through <see cref="PdfActionRunner"/>, so
    /// through the registered <see cref="IJavaScriptPlugin"/>; with none registered nothing runs, the
    /// value is accepted as typed, and <see cref="FieldCommitResult.Warnings"/> says why.
    /// </summary>
    /// <remarks>
    /// <para><b>Each event's answer is read by what the event means.</b> The engine writes
    /// <c>event.value</c> into <see cref="PdfJsExecutionContext.ModifiedFieldValues"/> under the
    /// event's own field for every event, so that dictionary cannot tell a Calculate result (the
    /// field's new value) from a Format result (display text only). The event's field is therefore
    /// read from <see cref="PdfJsExecutionContext.EventValue"/>, and the dictionary only for the
    /// <i>other</i> fields a script wrote through <c>getField(…).value</c>.</para>
    /// <para><b>A rejection keeps nothing</b>: a Keystroke or Validate script that sets
    /// <c>event.rc = false</c>, throws or runs out of time (the engine fails closed) leaves every
    /// value as it was, its side effects included. So does one an engine is registered for and that
    /// did not run, the commit's budget spent: a gate that did not run has approved nothing.</para>
    /// <para><b>Format runs last</b>, for the committed field and every field the commit changed; its
    /// text is returned in <see cref="FieldCommitResult.DisplayValues"/> for
    /// <see cref="FormFillOptions.DisplayValues"/> to draw, and never becomes a value.</para>
    /// <para><b>While a field is typed into</b>, <see cref="Keystroke"/> runs its Keystroke script
    /// once a keystroke, with the change and the selection it replaces.</para>
    /// <para><b>Each operation is one script environment</b> (<see cref="PdfJsDocumentScope"/>): the
    /// document-level scripts run first, once, and every event's script sees what they define; each
    /// event carries the API's <c>event.type</c> and <c>event.name</c>, and the document's fields
    /// described.</para>
    /// </remarks>
    public static class PdfFieldScripts
    {
        /// <summary>
        /// Commits <paramref name="value"/> to <paramref name="fieldName"/> through the field's
        /// scripts and the document's calculations, under
        /// <see cref="PluginJavaScriptRunner.DefaultDocumentBudget"/>.
        /// </summary>
        /// <param name="document">The document the scripts are read from.</param>
        /// <param name="fieldName">The field's fully qualified name.</param>
        /// <param name="value">The value the user entered.</param>
        /// <param name="fieldValues">Every field's current value, by fully qualified name — what
        /// <c>getField(…).value</c> answers and what the calculations start from.</param>
        public static FieldCommitResult Commit(PdfDocument document, string fieldName, string value,
            IReadOnlyDictionary<string, string> fieldValues)
            => Commit(document, fieldName, value, fieldValues, PluginJavaScriptRunner.DefaultDocumentBudget);

        /// <summary>
        /// Commits <paramref name="value"/> to <paramref name="fieldName"/> through the field's
        /// scripts and the document's calculations, every script of the commit sharing
        /// <paramref name="budget"/>.
        /// </summary>
        /// <param name="document">The document the scripts are read from.</param>
        /// <param name="fieldName">The field's fully qualified name.</param>
        /// <param name="value">The value the user entered.</param>
        /// <param name="fieldValues">Every field's current value, by fully qualified name.</param>
        /// <param name="budget">The execution time the commit's scripts share; the engine's own
        /// limits are per script, and a form chooses how many calculations it has. Zero or negative
        /// runs no script.</param>
        /// <exception cref="Tetitulz.Pdf.Core.PdfEncryptedDocumentException"><paramref name="document"/>
        /// is encrypted and was opened without its password.</exception>
        public static FieldCommitResult Commit(PdfDocument document, string fieldName, string value,
            IReadOnlyDictionary<string, string> fieldValues, TimeSpan budget)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrEmpty(fieldName)) throw new ArgumentException("A field name is required.", nameof(fieldName));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (fieldValues == null) throw new ArgumentNullException(nameof(fieldValues));
            // An encrypted document opened without its password has no catalog: no field, no
            // script, and the value would be "accepted" by gates that were never read.
            EncryptedRead.Refuse(document, "the form's scripts",
                "PdfOperations.CommitFieldValue(Stream, string, string, string, IReadOnlyDictionary<string, string>)");

            var result = new FieldCommitResult { Value = value };
            var given = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in fieldValues)
                if (kv.Key != null) given[kv.Key] = kv.Value ?? "";
            given[fieldName] = value;

            var runner = new PdfActionRunner(document);
            using (PluginJavaScriptRunner.BeginDocumentBudget(budget))
            {
                var scripts = new DocumentScripts(document, given);
                // The values every event sees, which the operation updates as the events change them.
                Dictionary<string, string> values = scripts.Values;
                // Keystroke at commit, then Validate: either may refuse, or replace, the value.
                foreach (string trigger in new[] { "Keystroke", "Validate" })
                {
                    PdfJsExecutionContext ctx = Run(runner, scripts, result, fieldName, result.Value,
                        trigger, willCommit: true, out bool skipped);
                    // A gate an engine could have run and did not (the budget spent) has not
                    // approved the value; with no engine at all, nothing is a gate.
                    if (skipped && PluginJavaScriptRunner.IsAvailable)
                        return Refuse(result, trigger + " script did not run: " + result.WarningList[result.WarningList.Count - 1]);
                    if (ctx == null) continue;
                    if (!ctx.ValidationPassed) return Refuse(result, ctx.ValidationMessage);
                    result.Value = ctx.EventValue ?? result.Value;
                    values[fieldName] = result.Value;
                    TakeOtherChanges(ctx, fieldName, values, result);
                }

                result.Accepted = true;

                // Every calculation, once, in /CO order; each sees the values the ones before it set.
                foreach (string calculated in PdfActionDispatcher.CollectCalculationOrder(document))
                {
                    values.TryGetValue(calculated, out string current);
                    PdfJsExecutionContext ctx = Run(runner, scripts, result, calculated, current ?? "",
                        "Calculate", willCommit: false, out _);
                    if (ctx == null || !ctx.ValidationPassed) continue;   // a failed calculation changes nothing

                    string next = ctx.EventValue ?? "";
                    if (next != (current ?? ""))
                    {
                        values[calculated] = next;
                        if (calculated == fieldName) result.Value = next;
                        else result.OtherChangesMap[calculated] = next;
                    }
                    TakeOtherChanges(ctx, calculated, values, result);
                }

                // Format, last, for every field whose value this commit set: its text is what the
                // field shows, never its value (Table 196). A Format script that does not run or
                // fails leaves the value to be drawn as it is.
                var changed = new List<string> { fieldName };
                changed.AddRange(result.OtherChangesMap.Keys);
                foreach (string formatted in changed)
                {
                    values.TryGetValue(formatted, out string current);
                    PdfJsExecutionContext ctx = Run(runner, scripts, result, formatted, current ?? "",
                        "Format", willCommit: false, out _);
                    if (ctx == null || !ctx.ValidationPassed || ctx.EventValue == null) continue;
                    result.DisplayValuesMap[formatted] = ctx.EventValue;
                }
            }
            return result;
        }

        /// <summary>
        /// One keystroke into <paramref name="fieldName"/> while it is being typed into: its Keystroke
        /// script with <c>event.willCommit</c> false, <c>event.change</c> the text typed or pasted and
        /// <c>event.selStart</c>/<c>selEnd</c> the selection it replaces in <paramref name="value"/>.
        /// The script may refuse the keystroke or rewrite the change (Table 196, <c>/K</c>).
        /// </summary>
        /// <param name="document">The document the script is read from.</param>
        /// <param name="fieldName">The field's fully qualified name.</param>
        /// <param name="value">The field's text before the keystroke.</param>
        /// <param name="selectionStart">Where the replaced selection begins in <paramref name="value"/>.</param>
        /// <param name="selectionEnd">Where it ends (exclusive); equal to the start for an insertion.</param>
        /// <param name="change">The text typed or pasted; empty for a deletion.</param>
        /// <param name="fieldValues">Every field's current value, by fully qualified name.</param>
        /// <param name="budget">The execution time the keystroke's script may take.</param>
        /// <exception cref="ArgumentOutOfRangeException">The selection is not within <paramref name="value"/>.</exception>
        /// <exception cref="Tetitulz.Pdf.Core.PdfEncryptedDocumentException"><paramref name="document"/>
        /// is encrypted and was opened without its password.</exception>
        public static KeystrokeResult Keystroke(PdfDocument document, string fieldName, string value,
            int selectionStart, int selectionEnd, string change,
            IReadOnlyDictionary<string, string> fieldValues, TimeSpan budget)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrEmpty(fieldName)) throw new ArgumentException("A field name is required.", nameof(fieldName));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (change == null) throw new ArgumentNullException(nameof(change));
            if (fieldValues == null) throw new ArgumentNullException(nameof(fieldValues));
            if (selectionStart < 0 || selectionStart > value.Length)
                throw new ArgumentOutOfRangeException(nameof(selectionStart), selectionStart, "The selection must lie within the value.");
            if (selectionEnd < selectionStart || selectionEnd > value.Length)
                throw new ArgumentOutOfRangeException(nameof(selectionEnd), selectionEnd, "The selection must lie within the value.");
            EncryptedRead.Refuse(document, "the form's scripts",
                "PdfOperations.CommitFieldValue(Stream, string, string, string, IReadOnlyDictionary<string, string>)");

            var given = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in fieldValues)
                if (kv.Key != null) given[kv.Key] = kv.Value ?? "";

            var collected = new FieldCommitResult();
            var runner = new PdfActionRunner(document);
            using (PluginJavaScriptRunner.BeginDocumentBudget(budget))
            {
                var scripts = new DocumentScripts(document, given);
                PdfJsExecutionContext ctx = Run(runner, scripts, collected, fieldName, value,
                    "Keystroke", willCommit: false, out bool skipped, c =>
                    {
                        c.EventChange = change;
                        c.EventSelStart = selectionStart;
                        c.EventSelEnd = selectionEnd;
                    });
                // As at commit: a gate an engine could have run and did not has approved nothing.
                if (skipped && PluginJavaScriptRunner.IsAvailable) return new KeystrokeResult(false, change, false);
                if (ctx == null) return new KeystrokeResult(true, change, false);
                return ctx.ValidationPassed
                    ? new KeystrokeResult(true, ctx.EventChange ?? "", true)
                    : new KeystrokeResult(false, change, true);
            }
        }

        private static FieldCommitResult Refuse(FieldCommitResult result, string message)
        {
            result.Accepted = false;
            result.RejectionMessage = message;
            result.OtherChangesMap.Clear();
            result.FieldStateMap.Clear();
            return result;
        }

        /// <summary>
        /// Runs one field event's action; <c>null</c> when the field has no script for it or no
        /// script ran — <paramref name="skipped"/> telling the second, its reason added to the
        /// result's warnings.
        /// </summary>
        private static PdfJsExecutionContext Run(PdfActionRunner runner, DocumentScripts scripts,
            FieldCommitResult result, string field, string value,
            string trigger, bool willCommit, out bool skipped, Action<PdfJsExecutionContext> prepare = null)
        {
            skipped = false;
            var ctx = scripts.Next();
            ctx.EventFieldName = field;
            ctx.EventValue = value;
            ctx.EventWillCommit = willCommit;
            (ctx.EventType, ctx.EventName) = PdfJsEventNames.For(PdfActionSource.Field, trigger);
            prepare?.Invoke(ctx);

            PdfActionResult ran = runner.RunFieldAction(field, trigger, ctx);
            if (PdfActionRunner.LastStreamExtractError != null)
                result.WarningList.Add(field + " (" + trigger + "): " + PdfActionRunner.LastStreamExtractError);
            if (ran == null) return null;
            foreach (string warning in ran.Warnings) result.WarningList.Add(field + " (" + trigger + "): " + warning);
            if (!ran.JavaScriptExecuted)
            {
                // A script the runner skipped says so in a warning; an action of another type does not.
                skipped = ran.Warnings.Count > 0;
                return null;
            }

            result.ScriptsRan = true;
            result.AlertList.AddRange(ctx.AlertMessages);
            foreach (var kv in ctx.FieldStateWrites)
            {
                if (!result.FieldStateMap.TryGetValue(kv.Key, out PdfJsFieldState state))
                    result.FieldStateMap[kv.Key] = state = new PdfJsFieldState();
                state.Merge(kv.Value);
            }
            return ctx;
        }

        /// <summary>
        /// One operation's script environment: a <see cref="PdfJsDocumentScope"/> in which the
        /// document-level scripts have run, once, before the first field event — so a field script
        /// calls the functions they define, as in a viewer — and one context the document is
        /// described to once (<see cref="PdfJsDocumentFacts"/>), every event run in it afresh.
        /// </summary>
        /// <remarks>
        /// <b>One context, not one an event</b>: an operation runs an event for each calculation the
        /// document lists, and a context made for each would copy every field, annotation and
        /// attachment of the document — a form of many calculated fields their count squared,
        /// outside the scripts' budget. <see cref="Values"/> is the context's own field values: the
        /// caller's, the document's where the caller gave none, updated by the operation as the
        /// events change them.
        /// </remarks>
        private sealed class DocumentScripts
        {
            private readonly PdfJsExecutionContext _ctx = new PdfJsExecutionContext { DocumentScope = new PdfJsDocumentScope() };

            internal DocumentScripts(PdfDocument document, Dictionary<string, string> given)
            {
                foreach (var kv in given) _ctx.FieldValues[kv.Key] = kv.Value;
                PdfJsDocumentFacts.Describe(document, _ctx);
                _ctx.PageWords = PdfJsDocumentFacts.PageWordsReader(() => document, ownsEachOpen: false);
                if (!PluginJavaScriptRunner.IsAvailable) return;
                PdfJsExecutionContext open = Next();
                (open.EventType, open.EventName) = ("Doc", "Open");
                foreach (PdfAction action in PdfActionDispatcher.DocumentLevelScripts(document))
                    if (action.ActionType == PdfActionType.JavaScript && !string.IsNullOrEmpty(action.Target))
                        PluginJavaScriptRunner.TryExecute(action.Target, open);
            }

            /// <summary>The field values every event sees and the operation updates.</summary>
            internal Dictionary<string, string> Values => _ctx.FieldValues;

            /// <summary>
            /// The context for the next event: what the last one set — its event, the values it
            /// wrote, its field states, its messages, its verdict and its error — cleared; the document
            /// and the field values kept.
            /// </summary>
            internal PdfJsExecutionContext Next()
            {
                PdfJsExecutionContext c = _ctx;
                c.EventType = c.EventName = c.EventFieldName = c.EventValue = c.EventChange = string.Empty;
                c.EventSelStart = c.EventSelEnd = -1;
                c.EventWillCommit = false;
                c.ModifiedFieldValues.Clear();
                c.FieldStateWrites.Clear();
                c.AlertMessages.Clear();
                c.ConsoleMessages.Clear();
                c.ValidationPassed = true;
                c.ValidationMessage = null;
                c.Error = null;
                return c;
            }
        }

        /// <summary>The values a script wrote to fields other than its event's own.</summary>
        private static void TakeOtherChanges(PdfJsExecutionContext ctx, string eventField,
            Dictionary<string, string> values, FieldCommitResult result)
        {
            foreach (var kv in ctx.ModifiedFieldValues)
            {
                if (kv.Key == eventField || kv.Key == null) continue;
                string next = kv.Value ?? "";
                values[kv.Key] = next;
                result.OtherChangesMap[kv.Key] = next;
            }
        }
    }

    /// <summary>What a field's Keystroke script made of one keystroke (<see cref="PdfFieldScripts.Keystroke"/>).</summary>
    public sealed class KeystrokeResult
    {
        internal KeystrokeResult(bool accepted, string change, bool scriptRan)
        {
            Accepted = accepted;
            Change = change;
            ScriptRan = scriptRan;
        }

        /// <summary>False when the script refused the keystroke, failed, or could not run under the budget.</summary>
        public bool Accepted { get; }

        /// <summary>The text to put in place of the selection — the script's rewrite, or the change as typed.</summary>
        public string Change { get; }

        /// <summary>True when the field's Keystroke script ran.</summary>
        public bool ScriptRan { get; }
    }

    /// <summary>What <see cref="PdfFieldScripts.Commit(PdfDocument, string, string, IReadOnlyDictionary{string, string})"/> decided.</summary>
    public sealed class FieldCommitResult
    {
        internal readonly Dictionary<string, string> OtherChangesMap = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> DisplayValuesMap = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, PdfJsFieldState> FieldStateMap = new Dictionary<string, PdfJsFieldState>(StringComparer.Ordinal);

        /// <summary>
        /// The field states the scripts wrote — <c>readonly</c>, <c>required</c>, <c>hidden</c> or
        /// <c>display</c> — by fully qualified name, the last write of each property kept, for a
        /// viewer to apply to its own handling of the field (its editor, its hit testing); the
        /// document's flags are not changed. Empty when the value was refused.
        /// </summary>
        public IReadOnlyDictionary<string, PdfJsFieldState> FieldStates => FieldStateMap;

        /// <summary>
        /// What each changed field's Format script made of its value, by fully qualified name — the
        /// text to draw (<see cref="FormFillOptions.DisplayValues"/>), not to store. A field whose
        /// Format script did not run, or that has none, is absent. Empty when the value was refused.
        /// </summary>
        public IReadOnlyDictionary<string, string> DisplayValues => DisplayValuesMap;
        internal readonly List<string> AlertList = new List<string>();
        internal readonly List<string> WarningList = new List<string>();

        /// <summary>
        /// False when the field's Keystroke or Validate script refused the value — or failed, which
        /// refuses it too; nothing is then to be written.
        /// </summary>
        public bool Accepted { get; internal set; }

        /// <summary>The value to write to the field: the one entered, as the scripts left it.</summary>
        public string Value { get; internal set; }

        /// <summary>
        /// Values the calculations and the scripts set on other fields, by fully qualified name —
        /// written with <see cref="Value"/>. Empty when the value was refused.
        /// </summary>
        public IReadOnlyDictionary<string, string> OtherChanges => OtherChangesMap;

        /// <summary>Why the value was refused (the script's own message where it gave one), or null.</summary>
        public string RejectionMessage { get; internal set; }

        /// <summary>What the scripts asked <c>app.alert</c> to show, in order.</summary>
        public IReadOnlyList<string> Alerts => AlertList;

        /// <summary>
        /// Scripts that did not run — no engine registered, the commit's budget spent, a script
        /// stream that could not be read — one entry each.
        /// </summary>
        public IReadOnlyList<string> Warnings => WarningList;

        /// <summary>True when at least one script ran.</summary>
        public bool ScriptsRan { get; internal set; }
    }
}
