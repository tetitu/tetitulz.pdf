// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>The PostScript type of a <see cref="PsObject"/> (PLRM 3rd ed. §3.3).</summary>
    internal enum PsType : byte
    {
        Null,
        Integer,
        Real,
        Boolean,
        Name,
        String,
        Array,
        Dictionary,
        Operator,
        Mark,
        Save,

        /// <summary>What <c>currentfile</c> and <c>filter</c> push. PLRM calls it filetype.</summary>
        File,

        /// <summary>What <c>gstate</c> pushes: a graphics state as an object. PLRM calls it gstatetype.</summary>
        GState
    }

    /// <summary>
    /// A PostScript object.
    ///
    /// <para><b>A readonly struct, not a class hierarchy.</b> The interpreter pushes millions of
    /// integers under <see cref="PsLimits.MaxTotalOps"/>, and a class per value would make GC
    /// pressure the dominant cost of running a program that draws a few hundred paths. Composite
    /// types keep reference semantics through <see cref="_ref"/> — which PostScript
    /// <i>requires</i>: two names bound to one array must see each other's <c>put</c>.</para>
    ///
    /// <para><b>Integer vs real is a real distinction, not cosmetics.</b> <c>5 2 idiv</c> is 2 while
    /// <c>5.0 2 idiv</c> is a <c>typecheck</c>; <c>array</c> and <c>string</c> demand integers;
    /// <c>type</c> is observable and real prologues switch on it. This is the single reason
    /// <c>Core.Functions.PdfFunctionEvaluator</c>'s float-only model cannot be reused here — that
    /// model is correct for the closed Type 4 subset and wrong for the language.</para>
    /// </summary>
    internal readonly struct PsObject : IEquatable<PsObject>
    {
        /// <summary>The object's PostScript type.</summary>
        public readonly PsType Type;

        /// <summary>
        /// The executable attribute (PLRM §3.3.1): <c>{proc}</c> vs <c>[array]</c>, and a bare
        /// <c>foo</c> vs a literal <c>/foo</c>. It belongs to the <b>object</b>, not to the
        /// composite it points at, which is why <c>cvx</c>/<c>cvlit</c> return a new object rather
        /// than mutating one.
        /// </summary>
        public readonly bool IsExecutable;

        private readonly double _num;   // Integer / Real / Boolean / Save payload
        private readonly object _ref;   // string (Name) | PsString | PsArray | PsDict | PsOperator | PsFile | PsGStateObject

        private PsObject(PsType type, double num, object reference, bool executable)
        {
            Type         = type;
            _num         = num;
            _ref         = reference;
            IsExecutable = executable;
        }

        public static readonly PsObject Null = new(PsType.Null, 0, null, false);
        public static readonly PsObject Mark = new(PsType.Mark, 0, null, false);
        public static readonly PsObject True  = new(PsType.Boolean, 1, null, false);
        public static readonly PsObject False = new(PsType.Boolean, 0, null, false);

        public static PsObject Int(long v)          => new(PsType.Integer, v, null, false);
        public static PsObject Real(double v)       => new(PsType.Real, v, null, false);
        public static PsObject Bool(bool v)         => v ? True : False;
        public static PsObject LitName(string s)    => new(PsType.Name, 0, s, false);
        public static PsObject ExecName(string s)   => new(PsType.Name, 0, s, true);
        public static PsObject Str(PsString s)      => new(PsType.String, 0, s, false);
        public static PsObject Arr(PsArray a)       => new(PsType.Array, 0, a, false);
        public static PsObject Proc(PsArray a)      => new(PsType.Array, 0, a, true);
        public static PsObject Dict(PsDict d)       => new(PsType.Dictionary, 0, d, false);

        /// <summary>A file object — what currentfile and filter push. PLRM type: filetype.</summary>
        public static PsObject File(PsFile f)       => new(PsType.File, 0, f, false);
        public static PsObject Op(PsOperator o)     => new(PsType.Operator, 0, o, true);
        public static PsObject SaveMark(int id)     => new(PsType.Save, id, null, false);

        /// <summary>A graphics state object — what <c>gstate</c> pushes. PLRM type: gstatetype.</summary>
        public static PsObject GState(Graphics.PsGStateObject g) => new(PsType.GState, 0, g, false);

        public bool IsNumber => Type == PsType.Integer || Type == PsType.Real;

        /// <summary>Numeric value of an Integer or Real. The caller has already type-checked.</summary>
        public double Num => _num;

        /// <summary>
        /// Integer value, <b>saturating</b> at <see cref="int.MinValue"/>/<see cref="int.MaxValue"/>
        /// rather than wrapping.
        ///
        /// <para>PostScript Level 2 integers are 32-bit and arithmetic that overflows converts to
        /// real, so a hostile program can put 1e300 on the stack legitimately. Saturating on the way
        /// <i>out</i> — at every array index, string length and loop count — is what stops an
        /// unchecked cast turning that into a small positive number.</para>
        /// </summary>
        public int AsInt() => SaturatingCast.ToInt(_num);

        public long AsLong() => SaturatingCast.ToLong(_num);

        public bool       AsBool()     => _num != 0;
        public string     AsName()     => (string)_ref;
        public PsString   AsString()   => (PsString)_ref;
        public PsArray    AsArray()    => (PsArray)_ref;
        public PsDict     AsDict()     => (PsDict)_ref;
        public PsFile     AsFile()     => (PsFile)_ref;
        public PsOperator AsOperator() => (PsOperator)_ref;
        public Graphics.PsGStateObject AsGState() => (Graphics.PsGStateObject)_ref;
        public int        SaveId       => SaturatingCast.ToInt(_num);

        /// <summary><c>cvx</c> / <c>cvlit</c>: same value, flipped executable attribute.</summary>
        public PsObject WithExecutable(bool executable) => new(Type, _num, _ref, executable);

        /// <summary>
        /// The name <c>type</c> pushes (PLRM §3.3). Distinct from <see cref="Type"/> only in that
        /// procedures and literal arrays share <c>arraytype</c>, as the spec requires.
        /// </summary>
        public string TypeName => Type switch
        {
            PsType.Null       => "nulltype",
            PsType.Integer    => "integertype",
            PsType.Real       => "realtype",
            PsType.Boolean    => "booleantype",
            PsType.Name       => "nametype",
            PsType.String     => "stringtype",
            PsType.Array      => "arraytype",
            PsType.Dictionary => "dicttype",
            PsType.Operator   => "operatortype",
            PsType.Mark       => "marktype",
            PsType.Save       => "savetype",
            PsType.File       => "filetype",
            PsType.GState     => "gstatetype",
            _                 => "nulltype"
        };

        /// <summary>
        /// Equality as <c>eq</c> defines it (PLRM §3.3, "Object equality"): numbers compare by
        /// value across the integer/real divide, names and strings compare by <i>characters</i>,
        /// and every other composite compares by reference identity.
        /// </summary>
        public bool Equals(PsObject other)
        {
            if (IsNumber && other.IsNumber) return _num == other._num;
            // PLRM eq: (abc) /abc eq is true — a name and a string compare by characters.
            if (Type == PsType.Name && other.Type == PsType.String)
                return ((PsString)other._ref).ContentEquals((string)_ref);
            if (Type == PsType.String && other.Type == PsType.Name)
                return ((PsString)_ref).ContentEquals((string)other._ref);
            if (Type != other.Type) return false;

            return Type switch
            {
                PsType.Null     => true,
                PsType.Mark     => true,
                PsType.Boolean  => _num == other._num,
                PsType.Save     => _num == other._num,
                PsType.Name     => string.Equals((string)_ref, (string)other._ref, StringComparison.Ordinal),
                PsType.String   => ((PsString)_ref).ContentEquals((PsString)other._ref),
                _               => ReferenceEquals(_ref, other._ref)
            };
        }

        public override bool Equals(object obj) => obj is PsObject o && Equals(o);

        /// <summary>
        /// This object as a dictionary key: a string is converted to the literal name of its
        /// characters (PLRM §3.10.1), so <c>(abc) 1 def</c> defines what <c>abc</c> looks up, and no
        /// key is a string a program may later change.
        /// </summary>
        internal PsObject AsKey() => Type == PsType.String ? LitName(((PsString)_ref).AsLatin1()) : this;

        /// <summary>
        /// This object's hash as a dictionary key. A number, a name and a string are keys a program
        /// chooses, so they hash under this process's key (<see cref="KeyedHash"/>): under
        /// <c>double.GetHashCode</c>, which XORs the value's two halves, a program can choose reals
        /// that all share one bucket and make every <c>def</c> walk them. A string hashes as the name of
        /// its characters, which it equals.
        /// </summary>
        public override int GetHashCode() => Type switch
        {
            PsType.Integer or PsType.Real => KeyedHash.Of(_num),
            PsType.Boolean or PsType.Save => _num.GetHashCode(),
            PsType.Name                   => KeyedHash.Of((string)_ref),
            PsType.String                 => KeyedHash.Of(((PsString)_ref).AsLatin1()),
            PsType.Null or PsType.Mark    => (int)Type,
            _                             => _ref == null ? 0 : System.Runtime.CompilerServices
                                                 .RuntimeHelpers.GetHashCode(_ref)
        };
    }
}
