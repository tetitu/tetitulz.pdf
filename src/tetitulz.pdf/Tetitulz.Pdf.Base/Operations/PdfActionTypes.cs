// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Which kind of dictionary an <c>/AA</c> (additional-actions) dictionary belongs to. Each defines
    /// its own trigger keys (§12.6.3, Tables 197–200).
    /// </summary>
    internal enum AdditionalActionsOwner
    {
        /// <summary>The document catalog (Table 200).</summary>
        Catalog,

        /// <summary>A page object (Table 198).</summary>
        Page,

        /// <summary>An annotation (Table 197).</summary>
        Annotation,

        /// <summary>
        /// A form field (Table 199). A field merged with its widget holds the annotation triggers too,
        /// so this owner answers both tables.
        /// </summary>
        Field,
    }

    /// <summary>
    /// What an action dictionary says, read once for <see cref="PdfActionDispatcher"/> (which
    /// describes actions to a caller) and <see cref="PdfActionRunner"/> (which raises them to a host).
    /// </summary>
    /// <remarks>
    /// <para><b>The two are properly separate, and "what does this action say" is not one of the
    /// ways they differ.</b> The subtype switch was shared first. The target and the trigger names
    /// were not, and the runner's parse carried a comment saying it mirrored the dispatcher's. It did
    /// not. A <c>Named</c> action had no target in one and its name in the other, a <c>/URI</c>
    /// written as a name was read by one only, a file specification dictionary was read for its
    /// <c>/F</c> by one and printed whole by the other, and each side's trigger list lacked keys the
    /// spec defines.</para>
    /// <para>A static class with no instance state. The one table is read-only after the type
    /// initializer runs.</para>
    /// </remarks>
    internal static class PdfActionTypes
    {
        /// <summary>The §12.6.4 <c>/S</c> subtype name → <see cref="PdfActionType"/>.</summary>
        /// <remarks>
        /// Note the two spellings the enum deliberately does not mirror: PDF writes <c>/GoToR</c> and
        /// <c>/SetOCGState</c>, which map to <c>GoToRemote</c> and <c>SetOcgState</c>. Anything
        /// unrecognised is <see cref="PdfActionType.Unknown"/> rather than an exception: an unknown
        /// action type is a legal thing for a newer PDF to contain, and §12.6.4 says a reader ignores
        /// what it does not implement.
        /// </remarks>
        internal static PdfActionType FromSubtypeName(string subtype) => (subtype ?? "") switch
            {
                "GoTo"        => PdfActionType.GoTo,
                "GoToR"       => PdfActionType.GoToRemote,
                "Launch"      => PdfActionType.Launch,
                "URI"         => PdfActionType.Uri,
                "JavaScript"  => PdfActionType.JavaScript,
                "SubmitForm"  => PdfActionType.SubmitForm,
                "ResetForm"   => PdfActionType.ResetForm,
                "ImportData"  => PdfActionType.ImportData,
                "Named"       => PdfActionType.Named,
                "SetOCGState" => PdfActionType.SetOcgState,
                "Rendition"   => PdfActionType.Rendition,
                _             => PdfActionType.Unknown
            };

        /// <summary>
        /// The action's primary target, as <see cref="PdfAction.Target"/> and each runner event
        /// carry it; <c>null</c> for a type that has none.
        /// </summary>
        /// <param name="doc">Resolves indirect entries. May be null for a graph of direct objects.</param>
        /// <param name="action">The action dictionary.</param>
        /// <param name="type">The action's type, from <see cref="FromSubtypeName"/>.</param>
        /// <param name="error">
        /// Why a script stream could not be read, or <c>null</c>. Set only when the answer is
        /// <c>null</c> because decoding threw, so a caller can tell "no script" from "a script it
        /// could not read".
        /// </param>
        /// <remarks>
        /// <list type="bullet">
        ///   <item><description><c>URI</c>: <c>/URI</c>, a string or (tolerated) a name.</description></item>
        ///   <item><description><c>JavaScript</c>: <c>/JS</c>, a text string or a text stream. A text
        ///   stream's bytes follow the text-string rules (§7.9.3): PDFDocEncoding, or UTF-16BE or
        ///   UTF-8 behind a byte-order mark. Both sides decoded them as UTF-8.</description></item>
        ///   <item><description><c>Named</c>: <c>/N</c>.</description></item>
        ///   <item><description><c>GoTo</c>: <c>/D</c>, a destination name or string as its text, an
        ///   explicit destination as <c>[array:<i>n</i>]</c>.</description></item>
        ///   <item><description><c>GoToR</c>: the file, then <c>:</c> and the destination when there is
        ///   one.</description></item>
        ///   <item><description><c>Launch</c>: <c>/F</c>, else the Windows launch dictionary's
        ///   <c>/F</c> (Table 208). A launch written with only <c>/Win</c> is the classic shape of a
        ///   document that starts a program, and it had no target at all.</description></item>
        ///   <item><description><c>SubmitForm</c>, <c>ImportData</c>: <c>/F</c>, the URL or file the
        ///   form data goes to or comes from.</description></item>
        /// </list>
        /// A file specification dictionary is read through
        /// <see cref="PdfAttachmentManager.FileSpecName"/>, so <c>/UF</c> comes before <c>/F</c>
        /// (Table 43). Every entry follows an indirect reference.
        /// </remarks>
        internal static string DescribeTarget(PdfDocument doc, PdfDictionary action, PdfActionType type,
            out string error)
        {
            error = null;
            if (action == null) return null;

            switch (type)
            {
                case PdfActionType.Uri:
                    return Text(Entry(doc, action, PdfName.URI));

                case PdfActionType.JavaScript:
                    return ScriptText(Entry(doc, action, PdfName.JS), out error);

                case PdfActionType.Named:
                    return (Entry(doc, action, PdfName.N) as PdfName)?.Value;

                case PdfActionType.GoTo:
                    return Destination(Entry(doc, action, PdfName.D));

                case PdfActionType.GoToRemote:
                {
                    string file = FileOf(doc, action, type);
                    string dest = Destination(Entry(doc, action, PdfName.D));
                    return dest == null ? file : file + ":" + dest;
                }

                case PdfActionType.Launch:
                case PdfActionType.SubmitForm:
                case PdfActionType.ImportData:
                    return FileOf(doc, action, type);

                default:
                    return null;
            }
        }

        /// <summary>
        /// The file (or URL) an action names, alone: <c>GoToR</c>'s, <c>SubmitForm</c>'s and
        /// <c>ImportData</c>'s <c>/F</c>; <c>Launch</c>'s <c>/F</c>, else its Windows launch
        /// dictionary's. <c>null</c> for a type that names none. <see cref="DescribeTarget"/> reads
        /// the file through here, so a caller that wants the file without <c>GoToR</c>'s
        /// <c>:destination</c> suffix — a path may hold a colon — reads the same answer.
        /// </summary>
        internal static string FileOf(PdfDocument doc, PdfDictionary action, PdfActionType type)
        {
            if (action == null) return null;
            switch (type)
            {
                case PdfActionType.GoToRemote:
                case PdfActionType.SubmitForm:
                case PdfActionType.ImportData:
                    return FileName(doc, Entry(doc, action, PdfName.F));

                case PdfActionType.Launch:
                    return FileName(doc, Entry(doc, action, PdfName.F))
                           ?? (Entry(doc, action, PdfName.Win) is PdfDictionary win
                               ? Text(Entry(doc, win, PdfName.F))
                               : null);

                default:
                    return null;
            }
        }

        private static PdfObject Entry(PdfDocument doc, PdfDictionary dict, PdfName key)
        {
            PdfObject raw = dict[key];
            return doc?.ResolveObject(raw) ?? raw;
        }

        private static string Text(PdfObject obj) => obj switch
        {
            PdfString s => s.GetText(),
            PdfName n   => n.Value,
            _           => null
        };

        private static string Destination(PdfObject obj) => obj switch
        {
            PdfArray a => "[array:" + a.Count + "]",
            _          => Text(obj)
        };

        private static string FileName(PdfDocument doc, PdfObject obj) => obj switch
        {
            PdfString s     => s.GetText(),
            PdfDictionary d => PdfAttachmentManager.FileSpecName(doc, d),
            _               => null
        };

        private static string ScriptText(PdfObject js, out string error)
        {
            error = null;
            if (js is PdfString s) return s.GetText();
            if (!(js is PdfStream stream)) return null;

            try
            {
                return new PdfString(stream.GetDecodedData() ?? Array.Empty<byte>()).GetText();
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        // ------------------------------------------------------------------
        //  Trigger events (§12.6.3)
        // ------------------------------------------------------------------

        /// <summary>Table 200: the catalog's triggers.</summary>
        private static readonly (string Key, string Trigger)[] CatalogTriggers =
        {
            ("WC", "WillClose"), ("WS", "WillSave"), ("DS", "DidSave"), ("WP", "WillPrint"), ("DP", "DidPrint"),
        };

        /// <summary>Table 198: a page's triggers.</summary>
        private static readonly (string Key, string Trigger)[] PageTriggers =
        {
            ("O", "PageOpen"), ("C", "PageClose"),
        };

        /// <summary>Table 197: an annotation's triggers.</summary>
        private static readonly (string Key, string Trigger)[] AnnotationTriggers =
        {
            ("E", "MouseEnter"), ("X", "MouseExit"), ("D", "MouseDown"), ("U", "MouseUp"),
            ("Fo", "Focus"), ("Bl", "Blur"),
            ("PO", "PageOpen"), ("PC", "PageClose"), ("PV", "PageVisible"), ("PI", "PageInvisible"),
        };

        /// <summary>Table 199 and then Table 197, for a field merged with its widget.</summary>
        private static readonly (string Key, string Trigger)[] FieldTriggers =
        {
            ("K", "Keystroke"), ("F", "Format"), ("V", "Validate"), ("C", "Calculate"),
            ("E", "MouseEnter"), ("X", "MouseExit"), ("D", "MouseDown"), ("U", "MouseUp"),
            ("Fo", "Focus"), ("Bl", "Blur"),
            ("PO", "PageOpen"), ("PC", "PageClose"), ("PV", "PageVisible"), ("PI", "PageInvisible"),
        };

        /// <summary>
        /// The trigger keys <paramref name="owner"/>'s <c>/AA</c> defines, with the name each is
        /// reported and run by, in the order the spec's table lists them.
        /// </summary>
        /// <remarks>
        /// <b>One table for both directions.</b> The dispatcher mapped key to name and the runner name
        /// to key, in two lists that disagreed: neither had the catalog's <c>/WC</c> or the annotation
        /// triggers <c>/PO</c>, <c>/PC</c>, <c>/PV</c> and <c>/PI</c>, and the dispatcher read the
        /// catalog's non-existent <c>/O</c> and <c>/C</c>.
        /// </remarks>
        internal static (string Key, string Trigger)[] Triggers(AdditionalActionsOwner owner) => owner switch
        {
            AdditionalActionsOwner.Catalog    => CatalogTriggers,
            AdditionalActionsOwner.Page       => PageTriggers,
            AdditionalActionsOwner.Annotation => AnnotationTriggers,
            _                                 => FieldTriggers,
        };

        /// <summary>
        /// The <c>/AA</c> key <paramref name="owner"/> uses for <paramref name="trigger"/>, or
        /// <c>null</c> when it defines no such trigger.
        /// </summary>
        internal static string AdditionalActionKey(string trigger, AdditionalActionsOwner owner)
        {
            foreach (var (key, name) in Triggers(owner))
                if (name == trigger) return key;
            return null;
        }
    }
}
