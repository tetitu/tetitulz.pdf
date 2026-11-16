// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Interactive-form field flags — the <c>/Ff</c> bit numbers of ISO 32000-1 tables 227
    /// (all fields), 228 (button), 229 (text) and 230 (choice).
    ///
    /// <para>The spec numbers these bits from 1, so bit <i>n</i> in the tables is
    /// <c>1 &lt;&lt; (n - 1)</c> here. They appeared as bare literals (1, 4096, 8192, 131072,
    /// 2097152) in <c>PdfFormFieldBuilder</c>, which is exactly the kind of named-concept
    /// literal the shared-constants rule exists to remove.</para>
    /// </summary>
    public static class PdfFieldFlags
    {
        // ---- Table 227: all field types ----

        /// <summary>Bit 1 — the field may not be changed by the user.</summary>
        public const int ReadOnly = 1 << 0;
        /// <summary>Bit 2 — the field must have a value when the form is submitted.</summary>
        public const int Required = 1 << 1;
        /// <summary>Bit 3 — the field is excluded from form submission.</summary>
        public const int NoExport = 1 << 2;

        // ---- Table 228: button fields ----

        /// <summary>Bit 15 — exactly one radio button in the set must be on at all times.</summary>
        public const int NoToggleToOff = 1 << 14;
        /// <summary>Bit 16 — the field is a set of radio buttons rather than a check box.</summary>
        public const int Radio = 1 << 15;
        /// <summary>Bit 17 — the field is a push button, which has no value to fill.</summary>
        public const int Pushbutton = 1 << 16;
        /// <summary>Bit 26 — radio buttons with the same on-state name toggle in unison.</summary>
        public const int RadiosInUnison = 1 << 25;

        // ---- Table 229: text fields ----

        /// <summary>Bit 13 — the text may span multiple lines.</summary>
        public const int Multiline = 1 << 12;
        /// <summary>Bit 14 — the value is a password and must not be echoed or stored.</summary>
        public const int Password = 1 << 13;
        /// <summary>Bit 21 — the value is a path to a file rather than the text itself.</summary>
        public const int FileSelect = 1 << 20;
        /// <summary>Bit 23 — the value is not spell-checked.</summary>
        public const int DoNotSpellCheck = 1 << 22;
        /// <summary>Bit 24 — the field does not scroll; input stops when the box is full.</summary>
        public const int DoNotScroll = 1 << 23;
        /// <summary>Bit 25 — the value is laid out in <c>/MaxLen</c> equally spaced comb cells.</summary>
        public const int Comb = 1 << 24;
        /// <summary>Bit 26 — the text field's value is a rich-text string (shared bit with
        /// <see cref="RadiosInUnison"/>, which applies to buttons).</summary>
        public const int RichText = 1 << 25;

        // ---- Table 230: choice fields ----

        /// <summary>Bit 18 — the field is a combo box; without it, a list box.</summary>
        public const int Combo = 1 << 17;
        /// <summary>Bit 19 — the combo box has an editable text box.</summary>
        public const int Edit = 1 << 18;
        /// <summary>Bit 20 — the <c>/Opt</c> options are sorted alphabetically.</summary>
        public const int Sort = 1 << 19;
        /// <summary>Bit 22 — more than one option may be selected at once.</summary>
        public const int MultiSelect = 1 << 21;
        /// <summary>Bit 27 — a selection is committed as soon as it is made.</summary>
        public const int CommitOnSelChange = 1 << 26;

        /// <summary>Returns true when <paramref name="flags"/> has every bit of <paramref name="mask"/> set.</summary>
        public static bool Has(int flags, int mask) => (flags & mask) == mask;
    }

    /// <summary>
    /// Field-type (<c>/FT</c>) name values. These are <c>const string</c> rather than
    /// <see cref="Tetitulz.Pdf.Core.Objects.PdfName"/> because a <c>case</c> label needs a
    /// compile-time constant (CLAUDE.md, "Constants" rule 1).
    /// </summary>
    public static class PdfFieldTypes
    {
        /// <summary><c>/Tx</c> — text field.</summary>
        public const string Text = "Tx";
        /// <summary><c>/Btn</c> — check box, radio button or push button.</summary>
        public const string Button = "Btn";
        /// <summary><c>/Ch</c> — combo box or list box.</summary>
        public const string Choice = "Ch";
        /// <summary><c>/Sig</c> — signature field.</summary>
        public const string Signature = "Sig";
    }

    /// <summary>
    /// Quadding (<c>/Q</c>) values — the justification applied to a variable-text field's
    /// generated appearance (ISO 32000-1 §12.7.3.3).
    /// </summary>
    public static class PdfQuadding
    {
        /// <summary>0 — left-justified.</summary>
        public const int Left = 0;
        /// <summary>1 — centred.</summary>
        public const int Centred = 1;
        /// <summary>2 — right-justified.</summary>
        public const int Right = 2;
    }
}
