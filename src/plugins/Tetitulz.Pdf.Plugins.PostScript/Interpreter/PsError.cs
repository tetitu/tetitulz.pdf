// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// The PostScript error names this interpreter raises (PLRM 3rd ed. §A.1). The set is
    /// deliberately the spec's, not an invented one: a program can catch any of them with
    /// <c>stopped</c> and branch on <c>$error /errorname</c>, so the names are observable.
    /// </summary>
    internal enum PsErrorKind
    {
        DictFull,
        DictStackOverflow,
        DictStackUnderflow,
        ExecStackOverflow,
        InvalidAccess,
        InvalidExit,
        InvalidRestore,
        IoError,
        LimitCheck,
        RangeCheck,
        StackOverflow,
        StackUnderflow,
        SyntaxError,
        TypeCheck,
        Undefined,
        UndefinedResult,
        UnmatchedMark,
        VmError,

        /// <summary><c>findresource</c> of an instance not defined (PLRM §3.9.4).</summary>
        UndefinedResource
    }

    /// <summary>
    /// A raised PostScript error.
    ///
    /// <para><b>Why this is an exception at all, given the step loop is iterative.</b> The loop is
    /// iterative so that <c>exit</c> and <c>stop</c> unwind by searching a heap stack rather than by
    /// throwing through CLR frames. An <i>error</i> is different: it can be raised from arbitrary
    /// depth inside an operator implementation — halfway through <c>roll</c>, from inside the
    /// arithmetic helpers — where returning a status code would mean threading one through every
    /// operator. So errors throw, and the step loop catches at exactly one place and converts the
    /// throw into the PostScript-level unwind.</para>
    ///
    /// <para><b>It is never allowed to escape the interpreter.</b> A raised error with no enclosing
    /// <c>stopped</c> is reported, the current top-level object is abandoned, and execution
    /// continues with the next one — because one bad line in a prologue must not cost the whole
    /// illustration.</para>
    /// </summary>
    internal sealed class PsErrorException : Exception
    {
        public PsErrorKind Kind { get; }

        /// <summary>The operator that raised it, for the diagnostic. May be null.</summary>
        public string Operator { get; }

        /// <summary>
        /// True only for the executable-name lookup in <c>PsInterpreter</c> that found nothing —
        /// i.e. <see cref="Operator"/> is a <b>name the interpreter does not have</b>, not an
        /// operator it has that objected to its operands.
        ///
        /// <para>The distinction exists because <c>undefined</c> is overloaded here: this
        /// interpreter also raises it for "no current point" and "the path is empty", on
        /// <c>rmoveto</c>, <c>currentpoint</c>, <c>pathbbox</c>, <c>charpath</c> and the show
        /// operators — every one of which <i>is</i> implemented. Reporting those as missing
        /// operators would fill <c>UnimplementedOperators</c> with names that are not gaps and make
        /// the list worthless for the thing it is read for.</para>
        /// </summary>
        public bool IsUndefinedName { get; private init; }

        public PsErrorException(PsErrorKind kind, string op = null, string detail = null)
            : base(Compose(kind, op, detail))
        {
            Kind     = kind;
            Operator = op;
        }

        /// <summary>An executable name that resolved to nothing anywhere on the dictionary stack.</summary>
        public static PsErrorException UndefinedName(string name) =>
            new(PsErrorKind.Undefined, name) { IsUndefinedName = true };

        /// <summary>The name a program sees in <c>$error /errorname</c>.</summary>
        public string ErrorName => Kind switch
        {
            PsErrorKind.DictFull            => "dictfull",
            PsErrorKind.DictStackOverflow   => "dictstackoverflow",
            PsErrorKind.DictStackUnderflow  => "dictstackunderflow",
            PsErrorKind.ExecStackOverflow   => "execstackoverflow",
            PsErrorKind.InvalidAccess       => "invalidaccess",
            PsErrorKind.InvalidExit         => "invalidexit",
            PsErrorKind.InvalidRestore      => "invalidrestore",
            PsErrorKind.IoError             => "ioerror",
            PsErrorKind.LimitCheck          => "limitcheck",
            PsErrorKind.RangeCheck          => "rangecheck",
            PsErrorKind.StackOverflow       => "stackoverflow",
            PsErrorKind.StackUnderflow      => "stackunderflow",
            PsErrorKind.SyntaxError         => "syntaxerror",
            PsErrorKind.TypeCheck           => "typecheck",
            PsErrorKind.Undefined           => "undefined",
            PsErrorKind.UndefinedResult     => "undefinedresult",
            PsErrorKind.UndefinedResource   => "undefinedresource",
            PsErrorKind.UnmatchedMark       => "unmatchedmark",
            PsErrorKind.VmError             => "VMerror",
            _                               => "unknownerror"
        };

        private static string Compose(PsErrorKind kind, string op, string detail)
        {
            string name = kind.ToString();
            if (!string.IsNullOrEmpty(op)) name += " in '" + op + "'";
            if (!string.IsNullOrEmpty(detail)) name += ": " + detail;
            return name;
        }
    }
}
