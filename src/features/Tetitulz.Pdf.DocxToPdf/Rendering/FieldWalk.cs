// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// A paragraph's inline children with its fields resolved — the one walk for the body renderer,
    /// the cell renderer and the Builder mapper, so a field reads the same in all three.
    ///
    /// <para>A complex field (ECMA-376 Part 1 §17.16.18: a run holding <c>w:fldChar begin</c>, runs of
    /// <c>w:instrText</c>, <c>separate</c>, the cached result's runs, <c>end</c>) is replaced by one
    /// run of the text <c>compute</c> answers for its instruction, in the first result run's
    /// formatting, when it answers; else its result runs are kept as they are, since the cached
    /// result is the best this converter has for a field it does not evaluate. A <c>w:fldSimple</c>
    /// is resolved the same way, and kept whole, for the caller to open, when it is not computed. A
    /// field nested inside another's instruction or result is the outer field's, and the instruction
    /// is read only to <see cref="MaxInstructionChars"/>, since only the field's name is wanted.</para>
    /// </summary>
    internal static class FieldWalk
    {
        /// <summary>Where a complex field's instruction stops gathering runs: its name, the only part read, is its first word.</summary>
        internal const int MaxInstructionChars = 1024;

        internal static IEnumerable<OpenXmlElement> ResolveFields(IEnumerable<OpenXmlElement> children, Func<string, string> compute)
        {
            int depth = 0;
            var instruction = new StringBuilder();
            bool inResult = false, computed = false;
            string value = null;
            Run firstResult = null;

            foreach (var child in children)
            {
                if (child is Run run && run.GetFirstChild<FieldChar>() is FieldChar fieldChar)
                {
                    var type = fieldChar.FieldCharType?.Value;
                    if (type == FieldCharValues.Begin)
                    {
                        depth++;
                        if (depth == 1) { instruction.Clear(); inResult = false; computed = false; firstResult = null; }
                        else if (inResult && !computed) yield return child;
                        continue;
                    }
                    if (depth == 0) continue;                   // a stray separate or end
                    if (depth == 1)
                    {
                        if (type == FieldCharValues.Separate)
                        {
                            inResult = true;
                            value = compute(instruction.ToString());
                            computed = value != null;
                            continue;
                        }
                        if (type == FieldCharValues.End)
                        {
                            if (computed) yield return Synthesized(value, firstResult);
                            depth = 0;
                            inResult = false;
                            computed = false;
                            continue;
                        }
                    }
                    else if (type == FieldCharValues.End) depth--;
                    if (inResult && !computed) yield return child;
                    continue;
                }

                if (depth > 0)
                {
                    if (!inResult)
                    {
                        if (child is Run instr && instr.GetFirstChild<FieldCode>() is FieldCode code && instruction.Length < MaxInstructionChars)
                            instruction.Append(code.Text);
                        continue;                                // the instruction is never drawn
                    }
                    if (computed)
                    {
                        if (firstResult == null && child is Run result) firstResult = result;
                        continue;                                // the cached result gives way to the value
                    }
                    yield return child;
                    continue;
                }

                if (child is SimpleField simple)
                {
                    string simpleValue = compute(simple.Instruction?.Value);
                    if (simpleValue != null)
                    {
                        yield return Synthesized(simpleValue, simple.Descendants<Run>().FirstOrDefault());
                        continue;
                    }
                }
                yield return child;
            }
        }

        /// <summary>A run of <paramref name="text"/> in <paramref name="like"/>'s formatting; detached, as a field's value is the walk's, not the file's.</summary>
        private static Run Synthesized(string text, Run like)
        {
            var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            if (like?.RunProperties != null) run.RunProperties = (RunProperties)like.RunProperties.CloneNode(true);
            return run;
        }
    }
}
