// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// A <c>CIEBasedA</c>, <c>CIEBasedABC</c>, <c>CIEBasedDEF</c> or <c>CIEBasedDEFG</c> space (PLRM
    /// §4.8.3) evaluated to CIE XYZ: the components held to their range, a <c>DEF</c>/<c>DEFG</c> colour
    /// decoded and looked up in its <c>/Table</c> to <c>ABC</c>, the <c>ABC</c> (or <c>A</c>) decoded and
    /// carried through <c>/MatrixABC</c> (<c>/MatrixA</c>) to <c>LMN</c>, those held to <c>/RangeLMN</c>,
    /// decoded and carried through <c>/MatrixLMN</c> to XYZ. PDF has no such space, so a colour and an
    /// image in one are carried as CIE L*a*b* against the space's own white point — the same colour,
    /// since Lab and XYZ are one another's exact transforms — in <c>[/Lab &lt;&lt;…&gt;&gt;]</c>.
    ///
    /// <para><b>Each decode procedure is a function of one component</b>, so it is run once per entry
    /// of a <see cref="Samples"/>-entry table over its range when the space is set, through the
    /// program's own interpreter (<see cref="PsSpaceColor.Transform"/>), and read back by linear
    /// interpolation; a colour or a pixel then costs no procedure run. A procedure that gives no answer
    /// refuses the space.</para>
    /// </summary>
    internal sealed class PsCieSpace
    {
        /// <summary>The entries each decode procedure is sampled at.</summary>
        internal const int Samples = 256;

        private readonly int _inputs;                    // 1 (A), 3 (ABC, DEF), 4 (DEFG)
        private readonly double[] _range;                // the components' ranges
        private readonly Table1[] _decodeInput;          // DecodeA / DecodeABC, or DecodeDEF / DecodeDEFG
        private readonly double[] _rangeHij;             // DEF/DEFG: the table's input range
        private readonly int[] _tableSize;               // DEF/DEFG: m1 m2 m3 (m4)
        private readonly byte[] _table;                  // DEF/DEFG: the lattice of ABC triples
        private readonly double[] _rangeAbc;             // DEF/DEFG: the ABC the table answers spans this
        private readonly Table1[] _decodeAbc;            // DEF/DEFG: DecodeABC after the table
        private readonly double[] _matrixAbc;            // 9 entries (A's 3 widened to the first row)
        private readonly double[] _rangeLmn;
        private readonly Table1[] _decodeLmn;
        private readonly double[] _matrixLmn;

        /// <summary>The space's white point, which the Lab it is carried as is relative to.</summary>
        public double[] WhitePoint { get; }

        /// <summary>The space's black point (zero when it names none).</summary>
        public double[] BlackPoint { get; }

        /// <summary>The components a colour in the space has.</summary>
        public int Components => _inputs;

        /// <summary>The components' ranges, the default <c>/Decode</c> of an image in the space.</summary>
        public double[] Range => _range;

        /// <summary>The <c>[/Lab &lt;&lt;/WhitePoint … /BlackPoint … /Range [-128 127 -128 127]&gt;&gt;]</c> the space is carried as.</summary>
        public PdfArray LabSpace { get; }

        private PsCieSpace(string family, int inputs, double[] range, Table1[] decodeInput,
            double[] rangeHij, int[] tableSize, byte[] table, double[] rangeAbc, Table1[] decodeAbc,
            double[] matrixAbc, double[] rangeLmn, Table1[] decodeLmn, double[] matrixLmn,
            double[] white, double[] black)
        {
            _inputs = inputs; _range = range; _decodeInput = decodeInput;
            _rangeHij = rangeHij; _tableSize = tableSize; _table = table; _rangeAbc = rangeAbc; _decodeAbc = decodeAbc;
            _matrixAbc = matrixAbc; _rangeLmn = rangeLmn; _decodeLmn = decodeLmn; _matrixLmn = matrixLmn;
            WhitePoint = white; BlackPoint = black;

            var dict = new PdfDictionary();
            dict[PdfName.WhitePoint] = Numbers(white);
            if (black[0] != 0 || black[1] != 0 || black[2] != 0) dict[new PdfName("BlackPoint")] = Numbers(black);
            dict[PdfName.Range] = Numbers(new double[] { -128, 127, -128, 127 });
            LabSpace = new PdfArray();
            LabSpace.Add(new PdfName(PdfColorSpaceNames.Lab));
            LabSpace.Add(dict);
        }

        /// <summary>
        /// The space <paramref name="dict"/> describes, or null with <paramref name="refusal"/>:
        /// no usable white point, an entry of the wrong shape, a procedure that gives no answer, a
        /// table too small for its sizes.
        /// </summary>
        public static PsCieSpace Build(PsInterpreter i, string family, PsDict dict, out string refusal)
        {
            refusal = null;
            double[] white = Numbers(dict, "WhitePoint", 3);
            if (white == null || !(white[0] > 0) || white[1] != 1 || !(white[2] > 0))
                return Refuse(out refusal, "its /WhitePoint is missing or not a white point (X and Z positive, Y 1)");
            double[] black = Numbers(dict, "BlackPoint", 3) ?? new double[3];

            double[] rangeLmn = Numbers(dict, "RangeLMN", 6) ?? Unit(3);
            Table1[] decodeLmn = Decodes(i, dict, "DecodeLMN", rangeLmn, 3, out refusal);
            if (decodeLmn == null) return null;
            double[] matrixLmn = Numbers(dict, "MatrixLMN", 9) ?? Identity3;

            switch (family)
            {
                case "CIEBasedA":
                {
                    double[] range = Numbers(dict, "RangeA", 2) ?? Unit(1);
                    Table1[] decode = Decodes(i, dict, "DecodeA", range, 1, out refusal);
                    if (decode == null) return null;
                    double[] m = Numbers(dict, "MatrixA", 3) ?? new double[] { 1, 1, 1 };
                    var matrixAbc = new double[] { m[0], m[1], m[2], 0, 0, 0, 0, 0, 0 };
                    return new PsCieSpace(family, 1, range, decode, null, null, null, null, null,
                        matrixAbc, rangeLmn, decodeLmn, matrixLmn, white, black);
                }
                case "CIEBasedABC":
                {
                    double[] range = Numbers(dict, "RangeABC", 6) ?? Unit(3);
                    Table1[] decode = Decodes(i, dict, "DecodeABC", range, 3, out refusal);
                    if (decode == null) return null;
                    return new PsCieSpace(family, 3, range, decode, null, null, null, null, null,
                        Numbers(dict, "MatrixABC", 9) ?? Identity3, rangeLmn, decodeLmn, matrixLmn, white, black);
                }
                case "CIEBasedDEF":
                case "CIEBasedDEFG":
                {
                    int n = family == "CIEBasedDEF" ? 3 : 4;
                    string suffix = n == 3 ? "DEF" : "DEFG";
                    double[] range = Numbers(dict, "Range" + suffix, 2 * n) ?? Unit(n);
                    Table1[] decode = Decodes(i, dict, "Decode" + suffix, range, n, out refusal);
                    if (decode == null) return null;
                    double[] rangeHij = Numbers(dict, n == 3 ? "RangeHIJ" : "RangeHIJK", 2 * n) ?? Unit(n);
                    if (!TryTable(i, family, dict, n, out int[] sizes, out byte[] table, out refusal)) return null;
                    double[] rangeAbc = Numbers(dict, "RangeABC", 6) ?? Unit(3);
                    Table1[] decodeAbc = Decodes(i, dict, "DecodeABC", rangeAbc, 3, out refusal);
                    if (decodeAbc == null) return null;
                    return new PsCieSpace(family, n, range, decode, rangeHij, sizes, table, rangeAbc, decodeAbc,
                        Numbers(dict, "MatrixABC", 9) ?? Identity3, rangeLmn, decodeLmn, matrixLmn, white, black);
                }
                default:
                    return Refuse(out refusal, $"/{family} is not a CIE-based space");
            }
        }

        /// <summary>A colour's components as XYZ.</summary>
        public (double X, double Y, double Z) ToXyz(double[] c)
        {
            double a, b, cc;
            if (_table == null)
            {
                a = Decode(_decodeInput, 0, Hold(c, 0, _range));
                b = _inputs > 1 ? Decode(_decodeInput, 1, Hold(c, 1, _range)) : 0;
                cc = _inputs > 1 ? Decode(_decodeInput, 2, Hold(c, 2, _range)) : 0;
            }
            else
            {
                var hij = new double[_inputs];
                for (int k = 0; k < _inputs; k++)
                {
                    double decoded = Decode(_decodeInput, k, Hold(c, k, _range));
                    hij[k] = Math.Max(_rangeHij[2 * k], Math.Min(_rangeHij[2 * k + 1], decoded));
                }
                var (ta, tb, tc) = Lookup(hij);
                // The table's bytes span RangeABC (PLRM §4.8.3: 0 to 255 over each range), then DecodeABC.
                a = Decode(_decodeAbc, 0, _rangeAbc[0] + ta * (_rangeAbc[1] - _rangeAbc[0]));
                b = Decode(_decodeAbc, 1, _rangeAbc[2] + tb * (_rangeAbc[3] - _rangeAbc[2]));
                cc = Decode(_decodeAbc, 2, _rangeAbc[4] + tc * (_rangeAbc[5] - _rangeAbc[4]));
            }

            double[] m = _matrixAbc;
            double l = a * m[0] + b * m[3] + cc * m[6];
            double mm = a * m[1] + b * m[4] + cc * m[7];
            double n = a * m[2] + b * m[5] + cc * m[8];
            l = Decode(_decodeLmn, 0, Math.Max(_rangeLmn[0], Math.Min(_rangeLmn[1], l)));
            mm = Decode(_decodeLmn, 1, Math.Max(_rangeLmn[2], Math.Min(_rangeLmn[3], mm)));
            n = Decode(_decodeLmn, 2, Math.Max(_rangeLmn[4], Math.Min(_rangeLmn[5], n)));
            double[] x = _matrixLmn;
            return (l * x[0] + mm * x[3] + n * x[6], l * x[1] + mm * x[4] + n * x[7], l * x[2] + mm * x[5] + n * x[8]);
        }

        /// <summary>A colour's components as the Lab it is carried as, held to Lab's ranges (L 0 to 100, a and b −128 to 127).</summary>
        public double[] ToLab(double[] c)
        {
            var (x, y, z) = ToXyz(c);
            var (l, a, b) = PsCie.XyzToLab(Math.Max(0, x), Math.Max(0, y), Math.Max(0, z), WhitePoint);
            return new[] { Math.Max(0, Math.Min(100, l)), Math.Max(-128, Math.Min(127, a)), Math.Max(-128, Math.Min(127, b)) };
        }

        /// <summary>A colour's Lab as the three bytes a Lab image's default <c>/Decode</c> spans (§8.9.5.2).</summary>
        public void ToLabBytes(double[] c, byte[] target, long at)
        {
            double[] lab = ToLab(c);
            target[at]     = SaturatingCast.ToByte(Math.Round(lab[0] / 100 * 255));
            target[at + 1] = SaturatingCast.ToByte(Math.Round(lab[1] + 128));
            target[at + 2] = SaturatingCast.ToByte(Math.Round(lab[2] + 128));
        }

        private static double Hold(double[] c, int k, double[] range) =>
            Math.Max(range[2 * k], Math.Min(range[2 * k + 1], k < c.Length ? c[k] : 0));

        private static double Decode(Table1[] tables, int k, double v) => tables?[k] == null ? v : tables[k].At(v);

        /// <summary>The table entry at <paramref name="hij"/>, multilinearly interpolated, as fractions of RangeABC.</summary>
        private (double A, double B, double C) Lookup(double[] hij)
        {
            int n = _inputs;
            var lo = new int[n];
            var t = new double[n];
            for (int k = 0; k < n; k++)
            {
                double span = _rangeHij[2 * k + 1] - _rangeHij[2 * k];
                double pos = span > 0 ? (hij[k] - _rangeHij[2 * k]) / span * (_tableSize[k] - 1) : 0;
                lo[k] = Math.Min(_tableSize[k] - 2 < 0 ? 0 : _tableSize[k] - 2, SaturatingCast.ToInt(Math.Floor(pos)));
                if (lo[k] < 0) lo[k] = 0;
                t[k] = _tableSize[k] > 1 ? pos - lo[k] : 0;
            }
            double ra = 0, rb = 0, rc = 0;
            for (int corner = 0; corner < 1 << n; corner++)
            {
                double weight = 1;
                long index = 0;
                for (int k = 0; k < n; k++)
                {
                    bool up = (corner & (1 << k)) != 0 && _tableSize[k] > 1;
                    weight *= up ? t[k] : 1 - t[k];
                    index = index * _tableSize[k] + lo[k] + (up ? 1 : 0);
                }
                if (weight == 0) continue;
                long at = index * 3;
                ra += weight * _table[at] / 255.0;
                rb += weight * _table[at + 1] / 255.0;
                rc += weight * _table[at + 2] / 255.0;
            }
            return (ra, rb, rc);
        }

        /// <summary>
        /// <c>/Table</c> (PLRM §4.8.3): <c>[m1 m2 m3 [strings]]</c> for DEF — m1 strings of
        /// m2 × m3 × 3 bytes — or <c>[m1 m2 m3 m4 [arrays]]</c> for DEFG — m1 arrays of m2 strings of
        /// m3 × m4 × 3 bytes; each size at least 2, the lattice at most 64 ×
        /// <see cref="PsLimits.MaxTintTransformSamples"/> points.
        /// </summary>
        /// <remarks>
        /// The lattice is charged to the allocation budget before it is made: it is up to 786 KB, a
        /// space is built at every <c>setcolorspace</c>, and its strings are checked only after — so a
        /// loop setting a space whose strings are the wrong length made and dropped the whole lattice
        /// each time, for a few operations' charge.
        /// </remarks>
        private static bool TryTable(PsInterpreter i, string family, PsDict dict, int n, out int[] sizes, out byte[] table,
            out string refusal)
        {
            sizes = null; table = null; refusal = null;
            if (!dict.TryGetName("Table", out PsObject value) || value.Type != PsType.Array || value.AsArray().Length != n + 1)
                { refusal = "its /Table is missing or not an array of sizes and strings"; return false; }
            PsArray array = value.AsArray();
            sizes = new int[n];
            long points = 1;
            for (int k = 0; k < n; k++)
            {
                if (!array[k].IsNumber || array[k].AsInt() < 2) { refusal = "a /Table size is not an integer of 2 or more"; return false; }
                sizes[k] = array[k].AsInt();
                points *= sizes[k];
                if (points > 64L * PsLimits.MaxTintTransformSamples) { refusal = "its /Table has more points than this converter carries"; return false; }
            }
            if (array[n].Type != PsType.Array || array[n].AsArray().Length != sizes[0])
                { refusal = "its /Table does not hold one entry per first index"; return false; }
            PsArray outer = array[n].AsArray();
            i.ChargeAllocation(points * 3, family);
            table = new byte[points * 3];

            // DEF: m1 strings of m2 × m3 × 3 bytes. DEFG: m1 arrays of m2 strings of m3 × m4 × 3 bytes.
            // Either way the bytes in order are the lattice with the last index varying fastest.
            long per = n == 3 ? (long)sizes[1] * sizes[2] * 3 : (long)sizes[2] * sizes[3] * 3;
            long at = 0;
            for (int k = 0; k < sizes[0]; k++)
            {
                if (n == 3)
                {
                    if (!TakeString(outer[k], per, table, ref at)) { refusal = $"its /Table's string {k} is not {per} bytes"; return false; }
                    continue;
                }
                if (outer[k].Type != PsType.Array || outer[k].AsArray().Length != sizes[1])
                    { refusal = $"its /Table's entry {k} is not an array of {sizes[1]} strings"; return false; }
                for (int j = 0; j < sizes[1]; j++)
                    if (!TakeString(outer[k].AsArray()[j], per, table, ref at))
                        { refusal = $"its /Table's string {k} {j} is not {per} bytes"; return false; }
            }
            return true;
        }

        private static bool TakeString(PsObject value, long length, byte[] table, ref long at)
        {
            if (value.Type != PsType.String || value.AsString().Length != length) return false;
            Array.Copy(value.AsString().ToArray(), 0, table, at, length);
            at += length;
            return true;
        }

        private static Table1[] Decodes(PsInterpreter i, PsDict dict, string key, double[] range, int n, out string refusal)
        {
            refusal = null;
            var tables = new Table1[n];
            if (!dict.TryGetName(key, out PsObject value)) return tables;     // identity
            if (n == 1 && value.Type == PsType.Array && value.IsExecutable)
            {
                tables[0] = Table1.Sample(i, value, range[0], range[1]);
                if (tables[0] == null) { refusal = $"its /{key} procedure gave no answer"; return null; }
                return tables;
            }
            if (value.Type != PsType.Array || value.IsExecutable || value.AsArray().Length != n)
            {
                refusal = $"its /{key} is not {n} procedures";
                return null;
            }
            for (int k = 0; k < n; k++)
            {
                PsObject procedure = value.AsArray()[k];
                if (procedure.Type != PsType.Array || !procedure.IsExecutable) { refusal = $"its /{key} holds something that is not a procedure"; return null; }
                tables[k] = Table1.Sample(i, procedure, range[2 * k], range[2 * k + 1]);
                if (tables[k] == null) { refusal = $"its /{key} procedure {k} gave no answer"; return null; }
            }
            return tables;
        }

        /// <summary>A procedure of one component, sampled at <see cref="Samples"/> points over its range and read back by linear interpolation.</summary>
        private sealed class Table1
        {
            private readonly double _lo, _hi;
            private readonly double[] _values;

            private Table1(double lo, double hi, double[] values) { _lo = lo; _hi = hi; _values = values; }

            public static Table1 Sample(PsInterpreter i, PsObject procedure, double lo, double hi)
            {
                var values = new double[Samples];
                for (int k = 0; k < Samples; k++)
                {
                    double v = lo + (hi - lo) * k / (Samples - 1);
                    double[] answer = PsSpaceColor.Transform(i, procedure, new[] { v }, 1);
                    if (answer == null || double.IsNaN(answer[0]) || double.IsInfinity(answer[0])) return null;
                    values[k] = answer[0];
                }
                return new Table1(lo, hi, values);
            }

            public double At(double v)
            {
                if (!(_hi > _lo)) return _values[0];
                double pos = (v - _lo) / (_hi - _lo) * (Samples - 1);
                // NaN too — a matrix overflowing to infinity times a zero entry makes one without any
                // setcolor — or (int)NaN indexes int.MinValue on .NET Framework (.NET 9 on saturates).
                if (!(pos > 0)) return _values[0];
                if (pos >= Samples - 1) return _values[Samples - 1];
                int k = SaturatingCast.ToInt(pos);
                double t = pos - k;
                return _values[k] + t * (_values[k + 1] - _values[k]);
            }
        }

        private static readonly double[] Identity3 = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        private static double[] Unit(int n) => Emit.PdfValues.UnitRanges(n);

        private static double[] Numbers(PsDict dict, string key, int count)
            => PsNumeric.FiniteArray(dict, key, count, out _);

        private static PdfArray Numbers(double[] values) => Emit.PdfValues.Numbers(values);

        private static PsCieSpace Refuse(out string refusal, string why)
        {
            refusal = why;
            return null;
        }
    }
}
