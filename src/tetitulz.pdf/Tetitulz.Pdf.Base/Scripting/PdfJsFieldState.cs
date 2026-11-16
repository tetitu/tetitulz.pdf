// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Scripting
{
    /// <summary>
    /// What a document's scripts asked of a field's state — <c>readonly</c>, <c>required</c>,
    /// <c>hidden</c> or <c>display</c> written on a Field object — gathered per field in
    /// <see cref="PdfJsExecutionContext.FieldStateWrites"/>: a viewer applies it to its own
    /// handling of the field; nothing is written into the document, whose <c>/Ff</c> flags and
    /// annotation flags stay as they are. A property the scripts did not write is null.
    /// </summary>
    public sealed class PdfJsFieldState
    {
        /// <summary>The field may not be edited (<c>readonly = true</c>), or may again (<c>false</c>); null when not written.</summary>
        public bool? ReadOnly { get; set; }

        /// <summary>The field must hold a value before submission (<c>required</c>); null when not written.</summary>
        public bool? Required { get; set; }

        /// <summary>
        /// The field is not shown and takes no press (<c>hidden = true</c>, or <c>display</c> set to
        /// hidden or noView), or is shown again; null when not written.
        /// </summary>
        public bool? Hidden { get; set; }

        /// <summary>
        /// Takes one script write, as an engine hands it over: <paramref name="key"/> is the Field
        /// property's name (<c>readonly</c>, <c>required</c>, <c>hidden</c>, <c>display</c>),
        /// <paramref name="value"/> the written value as text (<c>display</c>'s is its number).
        /// Any other key is ignored.
        /// </summary>
        public void Take(string key, string value)
        {
            switch (key)
            {
                case "readonly": ReadOnly = IsTrue(value); break;
                case "required": Required = IsTrue(value); break;
                case "hidden": Hidden = IsTrue(value); break;
                // display.visible 0, hidden 1, noPrint 2, noView 3: on screen the odd two hide.
                case "display": Hidden = value == "1" || value == "3"; break;
            }
        }

        /// <summary>Copies every property <paramref name="from"/> wrote over this one's.</summary>
        internal void Merge(PdfJsFieldState from)
        {
            if (from.ReadOnly.HasValue) ReadOnly = from.ReadOnly;
            if (from.Required.HasValue) Required = from.Required;
            if (from.Hidden.HasValue) Hidden = from.Hidden;
        }

        private static bool IsTrue(string value)
            => value == "true" || (value != "false" && value != "0" && value != "" && value != "null" && value != "undefined" && value != "NaN");
    }
}
