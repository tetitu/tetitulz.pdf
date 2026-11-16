// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

/// <summary>One tokenised content-stream operation: its operands and its operator.</summary>
internal readonly record struct ContentOp(string[] Operands, string Operator)
{
    public override string ToString() =>
        Operands.Length == 0 ? Operator : string.Join(" ", Operands) + " " + Operator;
}

/// <summary>
/// Turns emitted content-stream bytes into a list of operations so tests assert on
/// <b>structure</b>, never on whitespace or line breaks.
///
/// <para><c>Core.Text.ContentStreamParser</c> would be the natural tool and is <c>internal</c> to
/// Core, which this test project cannot see. This is a deliberately small stand-in: the emitter
/// produces a known, narrow subset — numbers, names, bracketed dash arrays and operators — so a
/// full parser is not needed to assert on it.</para>
/// </summary>
internal static class ContentStreamAssert
{
    /// <summary>Tokenises a content stream into its operations.</summary>
    public static List<ContentOp> Parse(byte[] content)
        => Parse(Encoding.ASCII.GetString(content ?? Array.Empty<byte>()));

    public static List<ContentOp> Parse(string content)
    {
        var ops = new List<ContentOp>();
        var operands = new List<string>();

        foreach (string token in Tokenize(content))
        {
            if (IsOperand(token)) { operands.Add(token); continue; }
            ops.Add(new ContentOp(operands.ToArray(), token));
            operands.Clear();
        }

        return ops;
    }

    /// <summary>Splits on whitespace, keeping a <c>[ … ]</c> array as one token.</summary>
    private static IEnumerable<string> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }

            if (s[i] == '[')
            {
                int close = s.IndexOf(']', i);
                if (close < 0) { yield return s.Substring(i); yield break; }
                yield return Normalise(s.Substring(i, close - i + 1));
                i = close + 1;
                continue;
            }

            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            yield return s.Substring(start, i - start);
        }
    }

    /// <summary>Collapses inner whitespace so <c>[1  2]</c> and <c>[1 2]</c> compare equal.</summary>
    private static string Normalise(string arrayToken)
    {
        var parts = arrayToken.Trim('[', ']')
                              .Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
        return "[" + string.Join(" ", parts) + "]";
    }

    private static bool IsOperand(string token) =>
        token.Length > 0 &&
        (token[0] == '[' || token[0] == '/' || token[0] == '(' ||
         char.IsDigit(token[0]) || token[0] == '-' || token[0] == '+' || token[0] == '.');

    // ------------------------------------------------------------------
    //  Assertions
    // ------------------------------------------------------------------

    /// <summary>The operators, in order, ignoring operands.</summary>
    public static string[] Operators(byte[] content)
        => Parse(content).Select(o => o.Operator).ToArray();

    /// <summary>How many times <paramref name="op"/> appears.</summary>
    public static int Count(byte[] content, string op)
        => Parse(content).Count(o => o.Operator == op);

    /// <summary>Asserts the stream contains <paramref name="expected"/> as consecutive operations.</summary>
    public static void ContainsSequence(byte[] content, params string[] expected)
    {
        var actual = Parse(content).Select(o => o.ToString()).ToArray();

        for (int start = 0; start + expected.Length <= actual.Length; start++)
        {
            bool match = true;
            for (int k = 0; k < expected.Length && match; k++)
                if (actual[start + k] != expected[k]) match = false;
            if (match) return;
        }

        Assert.Fail($"expected the sequence\n  {string.Join("\n  ", expected)}\n" +
                    $"in the emitted stream\n  {string.Join("\n  ", actual)}");
    }

    /// <summary>Asserts a single operation appears somewhere.</summary>
    public static void Contains(byte[] content, string expected)
        => ContainsSequence(content, expected);

    public static void DoesNotContainOperator(byte[] content, string op)
        => Assert.DoesNotContain(op, Operators(content));

    /// <summary>Every <c>q</c> has a matching <c>Q</c> and the depth never goes negative.</summary>
    public static void AssertBalancedSaveRestore(byte[] content)
    {
        int depth = 0;
        foreach (string op in Operators(content))
        {
            if (op == "q") depth++;
            else if (op == "Q")
            {
                depth--;
                Assert.True(depth >= 0, "a 'Q' appeared with no matching 'q'.");
            }
        }
        Assert.Equal(0, depth);
    }

    /// <summary>The numeric operands of the first occurrence of <paramref name="op"/>.</summary>
    public static double[] FirstOperandsOf(byte[] content, string op)
    {
        var found = Parse(content).FirstOrDefault(o => o.Operator == op);
        Assert.NotNull(found.Operands);
        return found.Operands.Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture))
                             .ToArray();
    }
}
