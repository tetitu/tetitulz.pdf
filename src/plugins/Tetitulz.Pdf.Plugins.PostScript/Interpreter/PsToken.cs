// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>What a <see cref="PsToken"/> is.</summary>
    internal enum PsTokenKind : byte
    {
        Integer,
        Real,
        LiteralName,      // /foo
        ExecutableName,   // foo
        ImmediateName,    // //foo — recognised so it is not mistaken for a name; not evaluated
        String,           // (…) or <…>
        ProcOpen,         // {
        ProcClose,        // }
        ArrayOpen,        // [
        ArrayClose,       // ]
        DictOpen,         // <<
        DictClose         // >>
    }

    /// <summary>
    /// One lexical token. A struct because the scanner produces one per whitespace-delimited run of
    /// a file that may be megabytes long.
    /// </summary>
    internal readonly struct PsToken
    {
        public readonly PsTokenKind Kind;
        public readonly double Number;
        public readonly string Text;    // name tokens
        public readonly byte[] Bytes;   // string tokens

        private PsToken(PsTokenKind kind, double number, string text, byte[] bytes)
        {
            Kind   = kind;
            Number = number;
            Text   = text;
            Bytes  = bytes;
        }

        public static readonly PsToken ProcOpen   = new(PsTokenKind.ProcOpen, 0, null, null);
        public static readonly PsToken ProcClose  = new(PsTokenKind.ProcClose, 0, null, null);
        public static readonly PsToken ArrayOpen  = new(PsTokenKind.ArrayOpen, 0, null, null);
        public static readonly PsToken ArrayClose = new(PsTokenKind.ArrayClose, 0, null, null);
        public static readonly PsToken DictOpen   = new(PsTokenKind.DictOpen, 0, null, null);
        public static readonly PsToken DictClose  = new(PsTokenKind.DictClose, 0, null, null);

        public static PsToken Integer(long v)   => new(PsTokenKind.Integer, v, null, null);
        public static PsToken Real(double v)    => new(PsTokenKind.Real, v, null, null);
        public static PsToken String(byte[] b)  => new(PsTokenKind.String, 0, null, b);

        public static PsToken Name(string text, bool literal)
            => new(literal ? PsTokenKind.LiteralName : PsTokenKind.ExecutableName, 0, text, null);

        public static PsToken ImmediateName(string text)
            => new(PsTokenKind.ImmediateName, 0, text, null);
    }
}
