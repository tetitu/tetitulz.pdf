// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// The <c>FontSetInit</c> procedure set and its one documented operator, <c>StartData</c>
    /// (PLRM §5.8.1 and §8.2): <c>key int StartData</c> reads the <c>int</c> bytes of binary data after
    /// its delimiter — a CFF font set, how Adobe's applications send an OpenType-CFF face — defines a
    /// <c>/FontSet</c> instance under <c>key</c>, makes its font a Font resource <c>findfont</c>
    /// reaches by the name the set's Name INDEX gives (PLRM: the mechanism is not specified), and ends
    /// the procedure set's dictionary. Each font of the set is embedded as a CFF holding it alone
    /// (<see cref="Core.Fonts.CffFontSet.TryRead(byte[], int, out string)"/>; a set of one, the set
    /// itself), a <c>/FontFile3</c> of <c>/Type1C</c>, its <c>/Widths</c> its own. A CID-keyed font is
    /// a CIDFont resource instead, for <c>composefont</c>: embedded as a <c>/FontFile3</c> of
    /// <c>/CIDFontType0C</c> under the composite font's CMap.
    ///
    /// <para><b>The data is consumed whatever becomes of it</b>: a set this converter cannot carry — a
    /// Chameleon set (<c>key int name StartData</c>), data the CFF parser refuses, a synthetic font —
    /// is said, and its text drawn in the face its name maps to, but its bytes are never scanned as
    /// PostScript.</para>
    /// </summary>
    internal static class PsFontSetOps
    {
        /// <summary>A fresh <c>FontSetInit</c> procedure set: <c>StartData</c>, the entry PLRM documents.</summary>
        internal static PsDict FontSetInit()
        {
            var procedures = new PsDict(1);
            procedures.PutName("StartData", PsObject.Op(new PsOperator("StartData", o => StartData((PsInterpreter)o))));
            return procedures;
        }

        /// <summary>
        /// A fresh <c>CIDInit</c> procedure set: its <c>StartData</c>, which completes a Type 0 CIDFont
        /// (PLRM §5.11.3), and the CMap operators (<see cref="PsCMapOps"/>).
        /// </summary>
        internal static PsDict CidInit()
        {
            var procedures = new PsDict(32);
            procedures.PutName("StartData", PsObject.Op(new PsOperator("StartData", o => CidStartData((PsInterpreter)o))));
            PsCMapOps.AddTo(procedures);
            return procedures;
        }

        /// <summary>
        /// <c>string int StartData</c> in <c>CIDInit</c> (PLRM §8.2): the <c>int</c> bytes of the binary
        /// data section after the delimiter — <c>(Binary)</c> as they are, <c>(Hex)</c> as hexadecimal
        /// digit pairs — read whatever becomes of them, set as the CIDFont dictionary's
        /// <c>/GlyphData</c>, the dictionary defined in the CIDFont category under its
        /// <c>/CIDFontName</c>, and two dictionaries ended: the CIDFont's and the procedure set's.
        /// </summary>
        private static void CidStartData(PsInterpreter i)
        {
            int count = i.Operands.PopInt("StartData");
            PsString format = i.Operands.PopString("StartData");
            if (count < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "StartData", "a negative byte count");
            bool hex = format.AsLatin1() == "Hex";
            if (!hex && format.AsLatin1() != "Binary")
                throw new PsErrorException(PsErrorKind.RangeCheck, "StartData", "the data format is neither (Binary) nor (Hex)");

            i.ChargeAllocation(count, "StartData");
            var data = new byte[count];
            int read = i.Scanner == null ? 0 : hex ? i.Scanner.ReadHex(data, 0, count) : i.Scanner.ReadRaw(data, 0, count);

            PsDict cidFont = i.Dicts.Current;
            if (i.Dicts.Count > 3) i.Dicts.End();   // the CIDFont dictionary
            if (i.Dicts.Count > 3) i.Dicts.End();   // the procedure set

            if (read < count)
            {
                PostScriptDiagnostics.Report("PsFontSetOps.StartData",
                    $"a CIDFont's data ended {count - read} bytes short of the {count} StartData declared; it was not defined");
                return;
            }
            if (!cidFont.TryGetName("CIDFontName", out PsObject name) || name.Type != PsType.Name)
            {
                PostScriptDiagnostics.Report("PsFontSetOps.StartData", "a CIDFont completed by StartData has no /CIDFontName; it was not defined");
                return;
            }

            cidFont.PutName("GlyphData", PsObject.Str(new PsString(data)));
            PsTextOps.DefineCidFont(i, name.AsName(), cidFont);
            PsResourceOps.DefineInstance(i, "CIDFont", name, PsObject.Dict(cidFont));
        }

        private static void StartData(PsInterpreter i)
        {
            PsObject top = i.Operands.Pop();
            bool chameleon = top.Type == PsType.Name;
            int count = chameleon ? i.Operands.PopInt("StartData") : top.Type == PsType.Integer
                ? top.AsInt()
                : throw new PsErrorException(PsErrorKind.TypeCheck, "StartData", "the byte count is not an integer");
            PsObject key = i.Operands.Pop();
            if (key.Type is not (PsType.Name or PsType.String))
                throw new PsErrorException(PsErrorKind.TypeCheck, "StartData", "the font set's key is not a name or a string");
            if (count < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "StartData", "a negative byte count");

            // The data first, whatever is done with it: left in the program, it scans as names.
            i.ChargeAllocation(count, "StartData");
            var data = new byte[count];
            int read = i.Scanner != null ? i.Scanner.ReadRaw(data, 0, count) : 0;
            if (i.Dicts.Count > 3) i.Dicts.End();   // PLRM: StartData removes one dictionary, the procedure set's

            if (chameleon)
            {
                PostScriptDiagnostics.ReportUnimplemented("StartData (a Chameleon font set)");
                return;
            }
            if (read < count)
            {
                PostScriptDiagnostics.Report("PsFontSetOps.StartData",
                    $"a font set's data ended {count - read} bytes short of the {count} StartData declared; it was not carried");
                return;
            }

            var names = Core.Fonts.CffFontSet.TryReadFontNames(data, out string refusal);
            if (names == null)
            {
                PostScriptDiagnostics.Report("PsFontSetOps.StartData",
                    $"a CFF font set was not carried — {refusal} — so its text is drawn in the face its name maps to");
                return;
            }

            // Every font of the set, as other interpreters define them: each a CFF of its own,
            // since an embedded CFF holds one font (ISO 32000-2 §9.9) — each a copy of the set, charged.
            var carried = new List<PsObject>();
            for (int k = 0; k < names.Count; k++)
            {
                if (names[k] == null) continue;   // a deleted entry
                if (names.Count > 1) i.ChargeAllocation(count, "StartData");
                var set = Core.Fonts.CffFontSet.TryRead(data, k, out string refused);
                if (set == null)
                {
                    PostScriptDiagnostics.Report("PsFontSetOps.StartData",
                        $"font {k} of a CFF font set, {names[k]}, was not carried — {refused} — so its text is drawn in the face its name maps to");
                    continue;
                }

                var font = Emit.PsEmbeddedFont.FromCff(set);
                if (set.IsCidKeyed) DefineCidFont(i, font);
                else i.EmbeddedFonts[font.FontName] = font;
                carried.Add(PsObject.LitName(font.FontName));
            }

            var instance = new PsDict(1);
            instance.PutName("FontNames", PsObject.Arr(new PsArray(carried.ToArray())));
            PsResourceOps.DefineInstance(i, "FontSet", key, PsObject.Dict(instance));
        }

        /// <summary>
        /// A CID-keyed font of a set as a CIDFont resource under its name — the CFF specification puts
        /// CID-keyed fonts in font sets, PLRM §5.11 reaches a CIDFont through the CIDFont category, and
        /// other interpreters define one there — for <c>composefont</c> to find: a Type 0
        /// CIDFont's entries (Table 5.12), its <c>/CIDSystemInfo</c> the font's <c>ROS</c>, its program
        /// attached.
        /// </summary>
        private static void DefineCidFont(PsInterpreter i, Emit.PsEmbeddedFont font)
        {
            i.ChargeAllocation(6 * PsAllocationBudget.BytesPerDictEntry, "StartData");
            var info = new PsDict(3);
            info.PutName("Registry", PsObject.Str(new PsString(Core.Text.Latin1Text.Encode(font.Cff.Registry))));
            info.PutName("Ordering", PsObject.Str(new PsString(Core.Text.Latin1Text.Encode(font.Cff.Ordering))));
            info.PutName("Supplement", PsObject.Int(font.Cff.Supplement));

            var cidFont = new PsDict(4);
            cidFont.PutName("CIDFontName", PsObject.LitName(font.FontName));
            cidFont.PutName("CIDFontType", PsObject.Int(0));
            cidFont.PutName("FontType", PsObject.Int(9));
            cidFont.PutName("CIDSystemInfo", PsObject.Dict(info));
            PsTextOps.AttachCidProgram(cidFont, font.FontName, font);
            PsResourceOps.DefineInstance(i, "CIDFont", PsObject.LitName(font.FontName), PsObject.Dict(cidFont));
        }
    }
}
