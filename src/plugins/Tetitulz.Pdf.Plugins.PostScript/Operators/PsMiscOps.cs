// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Operators that are neither graphics nor language — <c>eexec</c>, the hinge for embedding a
    /// document's own font, and the Level 2 <b>user object</b> trio.
    /// </summary>
    internal static class PsMiscOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("eexec", Eexec);

            op("defineuserobject",   DefineUserObject);
            op("undefineuserobject", UndefineUserObject);
            op("execuserobject",     ExecUserObject);

            // `handleerror` — the default error handler prints the error and clears newerror. There
            // is no stdout here and the error was reported when it was caught, so only the clearing
            // is left to do. It is defined at all because a program calls it right after
            // `stopped`, and an undefined name there abandoned the rest of the procedure — the
            // drawing after the handled error included.
            op("handleerror", i =>
            {
                if (i.Dicts.SystemDict.TryGetName("$error", out var slot) && slot.Type == PsType.Dictionary)
                    slot.AsDict().PutName("newerror", PsObject.False);
            });
        }

        /// <summary>
        /// User objects, by index.
        ///
        /// <para>PLRM models these as a <c>UserObjects</c> array in <c>userdict</c> that
        /// <c>defineuserobject</c> grows on demand. A dictionary keyed by index is the same thing
        /// without the growth policy, and it cannot be reached from PostScript by the array's name —
        /// which is a deviation nothing real depends on, since a program that manipulated
        /// <c>UserObjects</c> directly instead of through the three operators would be relying on
        /// the representation rather than the interface.</para>
        ///
        /// <para><c>[ThreadStatic]</c> and cleared per conversion, like every other slot in this
        /// assembly: one document's user objects must not be visible to the next.</para>
        /// </summary>
        [ThreadStatic] private static Dictionary<int, PsObject> _userObjects;

        internal static void ResetPerConversionState() => _userObjects = null;

        /// <summary><c>index any defineuserobject</c>.</summary>
        private static void DefineUserObject(PsInterpreter i)
        {
            var value = i.Operands.Pop();
            int index = i.Operands.PopInt("defineuserobject");

            if (index < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "defineuserobject");

            _userObjects ??= new Dictionary<int, PsObject>();

            // Charged like any other composite entry: `{ dup 1 add dup null defineuserobject } loop`
            // grows this dictionary once per iteration, and the operation budget charges one tick
            // for the whole operator — the same blind spot `clip` had.
            if (!_userObjects.ContainsKey(index))
                i.ChargeAllocation(PsAllocationBudget.BytesPerDictEntry, "defineuserobject");

            _userObjects[index] = value;
        }

        /// <summary><c>index undefineuserobject</c>.</summary>
        private static void UndefineUserObject(PsInterpreter i)
        {
            int index = i.Operands.PopInt("undefineuserobject");
            _userObjects?.Remove(index);
        }

        /// <summary>
        /// <c>index execuserobject</c> — pushes the stored object and executes it, which for the
        /// procedure that user objects almost always hold means running it.
        ///
        /// <para>Routed through <see cref="PsInterpreter.ExecuteResolvedValue"/>, the same entry
        /// <c>exec</c> uses, so a stored procedure pushes a frame rather than recursing and
        /// <c>exit</c> / <c>stop</c> unwind through it normally.</para>
        /// </summary>
        private static void ExecUserObject(PsInterpreter i)
        {
            int index = i.Operands.PopInt("execuserobject");

            if (_userObjects == null || !_userObjects.TryGetValue(index, out var value))
                throw new PsErrorException(PsErrorKind.Undefined, "execuserobject",
                    $"no user object is defined at index {index}");

            if (!value.IsExecutable) { i.Operands.Push(value); return; }
            if (value.Type == PsType.Name) { i.Execute(value); return; }

            i.ExecuteResolvedValue(value);
        }

        /// <summary>
        /// <c>eexec</c> — balances the operand stack. The font program itself is <b>not</b> handled
        /// here.
        ///
        /// <para><b>The capture happens in the scanner, at tokenising time</b>
        /// (<see cref="PsScanner.CapturedFontPrograms"/>), and it has to: the whole input is
        /// tokenised before the interpreter executes its first object, so by the time this operator
        /// runs the encrypted bytes are already behind the scanner. Tokenising them on the way past
        /// would be worse than useless — ciphertext scans as a storm of garbage names and,
        /// eventually, a byte that looks like <c>{</c>.</para>
        ///
        /// <para>What is left for the operator is the operand <c>currentfile</c> pushed, which this
        /// interpreter does not model. Popping it is the whole job; without it every embedding
        /// leaves a stray operand behind.</para>
        /// </summary>
        private static void Eexec(PsInterpreter i)
        {
            if (i.Operands.Count > 0) i.Operands.Pop();
        }
    }
}
