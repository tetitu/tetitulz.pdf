// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Functions;

/// <summary>
/// How a Type 4 program splits into tokens. ISO 32000-2 §7.10.5.1 admits comments into the
/// PostScript subset, and §7.10.5.2 makes the operand syntax PDF's. That brings in §7.2.3: NUL is
/// white space, and <c>{</c> / <c>}</c> are delimiters. It also brings in §7.2.4: a comment runs
/// to the end of the line and counts as one white-space character.
/// </summary>
/// <remarks>
/// Every program here computes <c>input 0.5 add</c> from an input of 0.25, so 0.75 is the right
/// answer. Each one evaluates on a worker thread with a generous join. Several of these programs
/// once sent the tokenizer into a loop that never advanced past a <c>}</c>, growing the heap by
/// gigabytes within seconds, so a regression has to fail as a test rather than take the run down
/// with it.
/// </remarks>
public class Type4ProgramSyntaxTests
{
    private const float Input = 0.25f;
    private const float Expected = 0.75f;

    [Fact]
    public void A_comment_is_white_space_and_its_text_never_executes()
    {
        // Executing "2 mul" would double the input before the add and give 1.
        var (value, unknown) = EvaluateToCompletion("{ % 2 mul\n 0.5 add }");

        Assert.Equal(Expected, value, 3);
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void A_comment_ends_at_either_end_of_line_character(string endOfLine)
    {
        // A comment that ran past this line would swallow "0.5 add" and leave the input, 0.25.
        var (value, unknown) = EvaluateToCompletion("{ % note" + endOfLine + "0.5 add }");

        Assert.Equal(Expected, value, 3);
        Assert.Empty(unknown);
    }

    [Fact]
    public void A_comment_separates_the_tokens_either_side_of_it()
    {
        // §7.2.4's own example: "abc%comment", a new line, then "123" is the two tokens abc and
        // 123. Read as one token, "0.5%half" is no number, so add would see 0.25 and nothing.
        var (value, unknown) = EvaluateToCompletion("{ 0.5%half\nadd }");

        Assert.Equal(Expected, value, 3);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Nul_is_white_space()
    {
        // Table 1 lists NUL among the white-space characters, and char.IsWhiteSpace does not.
        var (value, unknown) = EvaluateToCompletion("{ 0.5\0add }");

        Assert.Equal(Expected, value, 3);
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData("\0 \r\n{ 0.5 add }")]
    [InlineData("{ 0.5 add }\0")]
    [InlineData(" \r\n{ 0.5 add } \r\n")]
    public void White_space_around_the_braces_is_not_part_of_the_program(string program)
    {
        var (value, unknown) = EvaluateToCompletion(program);

        Assert.Equal(Expected, value, 3);
        Assert.Empty(unknown);
    }

    [Fact]
    public void A_comment_after_the_closing_brace_is_not_part_of_the_program()
    {
        var (value, unknown) = EvaluateToCompletion("{ 0.5 add } % a comment after the closing brace");

        Assert.Equal(Expected, value, 3);
        Assert.Empty(unknown);
    }

    [Fact]
    public void A_closing_brace_inside_a_comment_closes_nothing()
    {
        var (value, unknown) = EvaluateToCompletion("{ 0.5 add % }\n}");

        Assert.Equal(Expected, value, 3);
        Assert.Empty(unknown);
    }

    [Fact]
    public void An_opening_brace_inside_a_comment_opens_nothing()
    {
        // Read as code, the "{" would open a procedure that swallows "{ 1 add } if". The
        // condition would then find no procedure, the add would never run, and the answer would
        // be the pushed 1.
        var (value, unknown) = EvaluateToCompletion("{ 0.5 add true % {\n { 1 add } if }");

        Assert.Equal(Expected + 1f, value, 3);
        Assert.Empty(unknown);
    }

    /// <summary>
    /// A word that is no operator is the number 0, as another reader's lexer reads it and an independent renderer's parser pushes
    /// it, and is reported: <c>2 frobnicate mul</c> multiplies the 2 by that 0, and the 0 is added to the input, so the
    /// answer is the input itself. Skipped, the <c>mul</c> would double the input instead.
    /// </summary>
    [Fact]
    public void An_unknown_operator_is_the_number_zero()
    {
        var (value, unknown) = EvaluateToCompletion("{ 2 frobnicate mul add }");

        Assert.Equal(Input, value, 3);
        Assert.Equal(new[] { "frobnicate" }, unknown);
    }

    [Theory]
    [InlineData("{ 0.5 add } }")]
    [InlineData("{ } 0.5 add }")]
    public void A_closing_brace_that_closes_nothing_is_reported_and_skipped(string program)
    {
        var (value, unknown) = EvaluateToCompletion(program);

        Assert.Equal(Expected, value, 3);
        Assert.Equal(new[] { "}" }, unknown);
    }

    /// <summary>
    /// <b>Adversarial: two thousand distinct unknown words.</b> Each unknown operator executed
    /// searched the list of those seen before, so one cost the number of distinct unknowns — four
    /// thousand in a loop took six seconds to reach the evaluation's operation budget, which the
    /// per-page budget prices as a constant unit. A set keeps them distinct now, and is live: it holds
    /// every one, the repeated word once, and the answer is unchanged.
    /// </summary>
    [Fact]
    public void AdversarialManyDistinctUnknownWords_AreKeptDistinctBySet()
    {
        var body = new StringBuilder("{ ");
        for (int i = 0; i < 2000; i++) body.Append('u').Append(i).Append(" pop ");
        body.Append("u0 pop 0.5 add }");

        float value = 0;
        int listed = 0, seen = 0;
        string first = null;
        Exception failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                value = PdfFunctionEvaluator.Evaluate(Build(body.ToString()), new[] { Input })[0];
                listed = PdfFunctionEvaluator.LastUnknownPostScriptOps.Count;
                first = PdfFunctionEvaluator.LastUnknownPostScriptOps[0];
                seen = PdfFunctionEvaluator.UnknownPostScriptOpsSeen;
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(60)));
        Assert.Null(failure);

        Assert.Equal(Expected, value, 3);
        Assert.Equal(2000, listed);
        Assert.Equal(2000, seen);
        Assert.Equal("u0", first);
    }

    private static (float Value, IReadOnlyList<string> Unknown) EvaluateToCompletion(string program)
    {
        float[] result = null;
        IReadOnlyList<string> unknown = null;
        Exception failure = null;

        // LastUnknownPostScriptOps is thread-static, so it is read on the thread that evaluated.
        var worker = new Thread(() =>
        {
            try
            {
                result = PdfFunctionEvaluator.Evaluate(Build(program), new[] { Input });
                unknown = PdfFunctionEvaluator.LastUnknownPostScriptOps.ToArray();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(60)),
            "The Type 4 program did not finish evaluating: " + program.Replace("\0", "\\0"));
        Assert.Null(failure);
        return (result[0], unknown);
    }

    private static PdfStream Build(string program)
    {
        var fn = new PdfStream();
        fn.Set(PdfName.FunctionType, new PdfNumber(4));
        var domain = new PdfArray();
        domain.Add(new PdfNumber(-10000));
        domain.Add(new PdfNumber(10000));
        fn.Set(PdfName.Domain, domain);
        var range = new PdfArray();
        range.Add(new PdfNumber(-10000));
        range.Add(new PdfNumber(10000));
        fn.Set(PdfName.Range, range);
        fn.RawData = Encoding.ASCII.GetBytes(program);
        return fn;
    }
}
