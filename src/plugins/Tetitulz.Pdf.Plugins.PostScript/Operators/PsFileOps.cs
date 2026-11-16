// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Reading the program's own bytes: <c>read</c>, <c>readstring</c>, <c>readhexstring</c> and
    /// <c>readline</c> (PLRM §8.2) on a file <c>currentfile</c> made — directly, or through
    /// <c>filter</c>. This is how an EPS feeds an image —
    /// <c>{ currentfile picstr readhexstring pop } image</c> — and it reads nothing but the bytes the
    /// program is made of: <c>file</c>, <c>run</c> and every write stay outside the sandbox, so any
    /// other operand is no file to read, and that is said, the read coming back empty and false so
    /// a reading loop ends.
    /// </summary>
    internal static class PsFileOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("read", Read);
            op("readstring", i => ReadString(i, "readstring", hex: false));
            op("readhexstring", i => ReadString(i, "readhexstring", hex: true));
            op("readline", ReadLine);
            op("token", Token);
            op("resetfile", ResetFile);
            op("fileposition", FilePosition);
            op("setfileposition", SetFilePosition);
        }

        // The three keep the stack effect PLRM gives them whatever they are handed, as the arity table's
        // entries did: a program calls them unconditionally, and only a reusable file has a position
        // here — the program's own bytes do not rewind, and anything else is no file. Those are said.

        /// <summary>The reusable file <paramref name="operand"/> is, or null, said under <paramref name="op"/>.</summary>
        private static PsFile Reusable(PsObject operand, string op)
        {
            if (operand.Type == PsType.File && operand.AsFile().IsReusable) return operand.AsFile();
            PostScriptDiagnostics.ReportUnimplemented(op);
            return null;
        }

        /// <summary><c>file resetfile</c>: a <c>ReusableStreamDecode</c> file back to its start, so the data an image read can be read again.</summary>
        private static void ResetFile(PsInterpreter i) => Reusable(i.Operands.Pop(), "resetfile")?.Seek(0);

        /// <summary><c>file fileposition position</c>: a reusable file's position, 0 for another.</summary>
        private static void FilePosition(PsInterpreter i)
            => i.Operands.Push(PsObject.Int(Reusable(i.Operands.Pop(), "fileposition")?.Position ?? 0));

        /// <summary>
        /// <c>file position setfileposition</c>: a reusable file to <c>position</c> — outside it a
        /// <c>rangecheck</c>, PLRM's <c>ioerror</c> having no closer kind here.
        /// </summary>
        private static void SetFilePosition(PsInterpreter i)
        {
            int position = i.Operands.PopInt("setfileposition");
            PsFile file = Reusable(i.Operands.Pop(), "setfileposition");
            if (file != null && !file.Seek(position))
                throw new PsErrorException(PsErrorKind.RangeCheck, "setfileposition", "a position outside the file");
        }

        /// <summary>
        /// <c>string token post any true</c> or <c>false</c>; <c>file token any true</c> or <c>false</c>
        /// (PLRM §8.2): one object scanned as the interpreter scans its program — a procedure whole, a
        /// <c>//name</c> looked up — through a scanner over the string, charged to the conversion's
        /// allocation; <c>post</c> the rest after the object and the one white-space character that ends
        /// it. A file is one <c>currentfile</c> made.
        /// </summary>
        private static void Token(PsInterpreter i)
        {
            PsObject source = i.Operands.Pop();
            if (source.Type == PsType.String)
            {
                PsString text = source.AsString();
                i.Tick(1 + text.Length / 64);
                var scanner = new PsScanner(text.ToArray(), i.Allocation);
                bool found = new PsParser(scanner, i.Allocation) { Dicts = i.Dicts }.TryNext(out PsObject value);
                // The scan itself, at its price: about 44 ns a byte against 92 ns a step. Charging the
                // copy alone would price a 100 000-byte token at 1 563 steps and 4.4 ms — a loop of them
                // would run a minute inside the budget's two seconds.
                i.Tick(scanner.Position / 2);
                if (!found)
                {
                    i.Operands.Push(PsObject.False);
                    return;
                }
                int consumed = scanner.Position;
                if (consumed < text.Length && PsScanner.IsWhite(text[consumed])) consumed++;
                i.Operands.Push(PsObject.Str(text.Interval(consumed, text.Length - consumed)));
                i.Operands.Push(value);
                i.Operands.Push(PsObject.True);
                return;
            }

            if (source.Type != PsType.File) throw new PsErrorException(PsErrorKind.TypeCheck, "token");
            i.Tick();
            bool got = source.AsFile().ReadToken(i, out PsObject read, out int scanned);
            i.Tick(scanned / 2);   // the scan at its price, as the string form above charges it
            if (got)
            {
                i.Operands.Push(read);
                i.Operands.Push(PsObject.True);
            }
            else
                i.Operands.Push(PsObject.False);
        }

        /// <summary><c>file read int true</c>, or <c>false</c> at the end of the data.</summary>
        private static void Read(PsInterpreter i)
        {
            PsObject file = i.Operands.Pop();
            if (!IsReadable(file, "read"))
            {
                i.Operands.Push(PsObject.False);
                return;
            }
            var one = new byte[1];
            if (file.AsFile().Read(i, one, 0, 1, hex: false) == 1)
            {
                i.Operands.Push(PsObject.Int(one[0]));
                i.Operands.Push(PsObject.True);
            }
            else
                i.Operands.Push(PsObject.False);
        }

        /// <summary>
        /// <c>file string readstring substring bool</c>: the string filled from the file — its bytes,
        /// or hexadecimal digit pairs — <c>bool</c> false when the data ended first.
        /// </summary>
        private static void ReadString(PsInterpreter i, string op, bool hex)
        {
            PsString target = i.Operands.PopString(op);
            PsObject file = i.Operands.Pop();
            if (target.Length == 0)
                throw new PsErrorException(PsErrorKind.RangeCheck, op, "a zero-length string");
            int n = IsReadable(file, op)
                ? file.AsFile().Read(i, target.Bytes, target.Offset, target.Length, hex)
                : 0;
            i.Tick(1 + n / 64);
            i.Operands.Push(PsObject.Str(target.Interval(0, n)));
            i.Operands.Push(PsObject.Bool(n == target.Length));
        }

        /// <summary>
        /// <c>file string readline substring bool</c>: up to the next end of line, which is not stored;
        /// <c>bool</c> false when the data ended without one; a line longer than the string a
        /// <c>rangecheck</c>, as PLRM has it.
        /// </summary>
        private static void ReadLine(PsInterpreter i)
        {
            PsString target = i.Operands.PopString("readline");
            PsObject file = i.Operands.Pop();
            bool ended = false, overflow = false;
            int n = IsReadable(file, "readline")
                ? file.AsFile().ReadLine(i, target.Bytes, target.Offset, target.Length, out ended, out overflow)
                : 0;
            i.Tick(1 + n / 64);
            if (overflow) throw new PsErrorException(PsErrorKind.RangeCheck, "readline", "the line is longer than the string");
            i.Operands.Push(PsObject.Str(target.Interval(0, n)));
            i.Operands.Push(PsObject.Bool(ended));
        }

        private static bool IsReadable(PsObject file, string op)
        {
            if (file.Type == PsType.File) return true;
            PostScriptDiagnostics.ReportUnimplemented($"{op} (a file other than currentfile)");
            return false;
        }
    }
}
