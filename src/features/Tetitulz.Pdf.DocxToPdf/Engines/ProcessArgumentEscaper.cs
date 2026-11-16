// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.DocxToPdf.Engines
{
    /// <summary>
    /// Joins arguments into a single Windows command line that <c>CommandLineToArgvW</c> parses back
    /// into exactly the arguments given.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> <c>ProcessStartInfo.ArgumentList</c> passes each argument
    /// separately, so a path containing spaces or quotes cannot break out and inject further
    /// arguments. It arrived in .NET Core 2.1 and exists on neither netstandard2.0 nor .NET
    /// Framework, where the only channel is the single <c>Arguments</c> string — which means the
    /// escaping has to be done here.</para>
    /// <para><b>This is security-sensitive code on a caller-supplied path</b>, so it is a faithful
    /// transcription of the algorithm .NET itself uses for <c>ArgumentList</c>
    /// (<c>PasteArguments.AppendArgument</c>), not an approximation of the rules. The three that are
    /// easy to get wrong, and that a naive "wrap it in quotes" gets wrong:</para>
    /// <list type="bullet">
    ///   <item>a run of <i>n</i> backslashes before a <c>"</c> becomes 2<i>n</i>+1 backslashes, so
    ///   the quote is escaped and the backslashes are literal;</item>
    ///   <item>a run of <i>n</i> backslashes at the <b>end</b> becomes 2<i>n</i>, so they do not
    ///   escape the closing quote — <c>C:\dir\</c> is the everyday case, and getting it wrong
    ///   swallows the quote and merges the next argument into this one;</item>
    ///   <item>an <b>empty</b> argument must be written <c>""</c> or it disappears entirely,
    ///   silently shifting every argument after it.</item>
    /// </list>
    /// <para><b>net10.0 does not use this.</b> <c>LibreOfficeConverter</c> keeps <c>ArgumentList</c>
    /// there; trading a framework guarantee for our own code where the framework provides one would
    /// be a downgrade. It is compiled into both legs only so the tests — which run on net10.0 — can
    /// reach it.</para>
    /// <para><b>The test is the evidence, not this comment.</b>
    /// <c>ProcessArgumentEscaperTests</c> feeds the output to the real
    /// <c>CommandLineToArgvW</c> and asserts the round trip returns the original array, so the
    /// claim is checked against the operating system's own parser rather than against a reading of
    /// the documentation.</para>
    /// </remarks>
    internal static class ProcessArgumentEscaper
    {
        /// <summary>Joins <paramref name="arguments"/> into one space-separated command line.</summary>
        public static string Join(IEnumerable<string> arguments)
        {
            var sb = new StringBuilder();

            foreach (string argument in arguments)
            {
                if (sb.Length > 0) sb.Append(' ');
                AppendArgument(sb, argument ?? string.Empty);
            }

            return sb.ToString();
        }

        private static void AppendArgument(StringBuilder sb, string argument)
        {
            // A token with nothing the parser treats specially needs no quoting at all. The length
            // test matters as much as the character test: an empty argument has no special
            // characters either, and passing it through unquoted would delete it.
            if (argument.Length != 0 && !ContainsSpecialCharacter(argument))
            {
                sb.Append(argument);
                return;
            }

            sb.Append('"');

            int i = 0;
            while (i < argument.Length)
            {
                char c = argument[i++];

                if (c == '\\')
                {
                    int backslashes = 1;
                    while (i < argument.Length && argument[i] == '\\')
                    {
                        i++;
                        backslashes++;
                    }

                    if (i == argument.Length)
                    {
                        // Before the closing quote: double them so they stay literal and the quote
                        // still closes the argument.
                        sb.Append('\\', backslashes * 2);
                    }
                    else if (argument[i] == '"')
                    {
                        // Before a literal quote: double them, plus one more to escape the quote.
                        sb.Append('\\', (backslashes * 2) + 1);
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        // Anywhere else a backslash is just a backslash.
                        sb.Append('\\', backslashes);
                    }
                }
                else if (c == '"')
                {
                    sb.Append('\\').Append('"');
                }
                else
                {
                    sb.Append(c);
                }
            }

            sb.Append('"');
        }

        private static bool ContainsSpecialCharacter(string argument)
        {
            foreach (char c in argument)
            {
                if (c == ' ' || c == '\t' || c == '"') return true;
            }

            return false;
        }
    }
}
