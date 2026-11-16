// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Scripting
{
    /// <summary>
    /// Holds the mutable state shared between the host application and a
    /// PDF JavaScript execution: form field values, event properties, and
    /// any side-effects (alerts, validation result) produced by the script.
    /// </summary>
    public sealed class PdfJsExecutionContext
    {
        // ---- Input ----------------------------------------------------------------

        /// <summary>Current form field values (name → value string).</summary>
        public Dictionary<string, string> FieldValues { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Total page count exposed to the script as <c>this.numPages</c>.</summary>
        public int NumPages { get; set; } = 1;

        /// <summary>
        /// The document's fields as <see cref="Operations.Forms.PdfFormReader"/> reads them (fully
        /// qualified name → field): what a script's field object answers for <c>type</c>, <c>rect</c>,
        /// <c>page</c>, <c>readonly</c>, a choice field's items and the rest. Empty when the host
        /// supplies values alone; a script then sees every field it names.
        /// </summary>
        public Dictionary<string, Operations.Forms.PdfFormFieldInfo> FieldDescriptions { get; } =
            new Dictionary<string, Operations.Forms.PdfFormFieldInfo>(StringComparer.Ordinal);

        /// <summary>
        /// The document's <c>/Info</c> entries that are text (key → text, a date in its PDF form):
        /// what <c>this.author</c>, <c>this.title</c>, <c>this.creationDate</c> and <c>this.info</c> answer.
        /// </summary>
        public Dictionary<string, string> DocumentInfo { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// The document's authors as its XMP packet lists them (<c>dc:creator</c>, in order), else
        /// its <c>/Info /Author</c> alone — what <c>this.info.Authors</c> answers; empty when neither
        /// names one, and the key is then absent.
        /// </summary>
        public List<string> Authors { get; } = new List<string>();

        /// <summary>
        /// The catalog's XMP packet as text — what <c>this.metadata</c> answers — read within the
        /// packet reader's ceiling; null when the document has none, and a script then reads an
        /// empty text.
        /// </summary>
        public string Metadata { get; set; }

        /// <summary>
        /// Each page's rotation and boxes, by page index — what <c>this.getPageRotation</c> and
        /// <c>getPageBox</c> answer. Empty when the host hands none over; a script then reads 0 and
        /// an empty box.
        /// </summary>
        public List<PdfJsPageInfo> Pages { get; } = new List<PdfJsPageInfo>();

        /// <summary>
        /// The document's annotations (<see cref="Operations.Extraction.PdfAnnotationExtractor"/>'s
        /// inventory) — what <c>this.getAnnots</c> and <c>getAnnot</c> answer, a widget, link or
        /// popup left out. Empty when the host hands none over; a script then finds no annotation.
        /// </summary>
        public List<Operations.Extraction.PdfAnnotationInfo> Annotations { get; } =
            new List<Operations.Extraction.PdfAnnotationInfo>();

        /// <summary>
        /// The document's optional content groups (<see cref="Operations.Structure.PdfOcgManager"/>'s
        /// listing) — what <c>this.getOCGs</c> answers for any page, since which page uses a group is
        /// not read. Empty when the host hands none over.
        /// </summary>
        public List<Operations.Structure.OcgInfo> OptionalContentGroups { get; } =
            new List<Operations.Structure.OcgInfo>();

        /// <summary>
        /// A page's words in reading order, by zero-based page index — what
        /// <c>this.getPageNumWords</c> and <c>getPageNthWord</c> answer; an index outside the document
        /// answers none. <c>null</c> when the host hands no page text over: a script then counts no
        /// words. Set by the document operations; a host supplying values alone may set its own.
        /// </summary>
        public Func<int, IReadOnlyList<string>> PageWords { get; set; }

        /// <summary>
        /// Whether the document's attachments are handed to its scripts — <c>this.dataObjects</c>,
        /// <c>getDataObject</c>, <c>getDataObjectContents</c>. Off by default: a script then sees no
        /// attachment, and none is decoded for it.
        /// </summary>
        public bool ExposeAttachments { get; set; }

        /// <summary>The document's attachments: what <c>this.dataObjects</c> lists (empty unless <see cref="ExposeAttachments"/>).</summary>
        public List<Operations.PdfAttachmentInfo> Attachments { get; } = new List<Operations.PdfAttachmentInfo>();

        /// <summary>
        /// The attachments' decoded contents a script may read (<c>this.getDataObjectContents</c>), by
        /// name: an attachment larger than the engine hands a script is absent.
        /// </summary>
        public Dictionary<string, byte[]> AttachmentContents { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>
        /// The scope this execution shares with the document's other scripts, or <c>null</c> for an
        /// execution of its own. Contexts holding the same scope run in one script environment, as a
        /// viewer runs a document's scripts: a document-level function is visible to every later script.
        /// </summary>
        public PdfJsDocumentScope DocumentScope { get; set; }

        /// <summary>
        /// <c>event.type</c> — <c>"Field"</c>, <c>"Doc"</c> or <c>"Page"</c>, the API's event types;
        /// empty when the host does not say.
        /// </summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>
        /// <c>event.name</c> — the API's name for the trigger (<c>"Keystroke"</c>, <c>"Calculate"</c>,
        /// <c>"Open"</c>, <c>"Mouse Up"</c>); empty when the host does not say.
        /// </summary>
        public string EventName { get; set; } = string.Empty;

        // ---- Event state (writable from JS via the <c>event</c> object) ----------

        /// <summary>The triggering field name (empty for document-level scripts).</summary>
        public string EventFieldName { get; set; } = string.Empty;

        /// <summary>
        /// <c>event.value</c> — the field's current (display) value.
        /// Scripts may modify this to change the committed value.
        /// </summary>
        public string EventValue { get; set; } = string.Empty;

        /// <summary>
        /// <c>event.change</c> — the key change string for Keystroke events. A script may rewrite it
        /// (<c>event.change = event.change.toUpperCase()</c>); the engine writes the result back.
        /// </summary>
        public string EventChange { get; set; } = string.Empty;

        /// <summary>
        /// <c>event.selStart</c> — where in <see cref="EventValue"/> the selection the change replaces
        /// begins, for a Keystroke event; -1 (the default) means the change is appended.
        /// </summary>
        public int EventSelStart { get; set; } = -1;

        /// <summary><c>event.selEnd</c> — where that selection ends (exclusive); -1 with <see cref="EventSelStart"/>.</summary>
        public int EventSelEnd { get; set; } = -1;

        /// <summary><c>event.willCommit</c> — true during field-level validation.</summary>
        public bool EventWillCommit { get; set; }

        // ---- Output ---------------------------------------------------------------

        /// <summary>
        /// After execution, contains every value that was modified by the script
        /// (field name → new value). Initially empty; scripts populate it by
        /// writing <c>event.value</c> or calling <c>this.getField(name).value = …</c>.
        /// </summary>
        public Dictionary<string, string> ModifiedFieldValues { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// The field states the scripts wrote (<c>readonly</c>, <c>required</c>, <c>hidden</c>,
        /// <c>display</c>), by fully qualified field name, the last write of each property kept —
        /// for a viewer to apply to its handling of the field; the document is not changed.
        /// </summary>
        public Dictionary<string, PdfJsFieldState> FieldStateWrites { get; } = new Dictionary<string, PdfJsFieldState>(StringComparer.Ordinal);

        /// <summary>Messages produced by <c>app.alert()</c> or <c>app.response()</c>.</summary>
        public List<string> AlertMessages { get; } = new List<string>();

        /// <summary>Lines a script wrote with <c>console.println()</c>.</summary>
        public List<string> ConsoleMessages { get; } = new List<string>();

        /// <summary>
        /// Set to <c>false</c> by a script to indicate validation failure
        /// (<c>event.rc = false</c>).  Defaults to <c>true</c>.
        /// </summary>
        public bool ValidationPassed { get; set; } = true;

        /// <summary>Validation failure message set by the script, if any.</summary>
        public string ValidationMessage { get; set; }

        /// <summary>
        /// Exception thrown during script execution, if any. A script that failed leaves this set
        /// and is still considered <i>executed</i>; "no engine was registered at all" is reported
        /// separately, on <see cref="PluginJavaScriptRunner.LastError"/>.
        /// </summary>
        /// <remarks>
        /// The setter is public because the engine lives in <c>Tetitulz.Pdf.Plugins.JavaScript</c> —
        /// an <see cref="IJavaScriptPlugin"/> implementation lives outside Base by design and must
        /// be able to report a script failure here.
        /// </remarks>
        public Exception Error { get; set; }
    }
}
