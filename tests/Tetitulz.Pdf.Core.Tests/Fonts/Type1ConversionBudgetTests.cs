// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Converting a Type 1 program to CFF interprets every glyph and holds every glyph's outline until
/// the CFF is written, so the per-glyph step cap bounds one glyph and the glyph count multiplies it:
/// a glyph of a few bytes can call a tree of shared subroutines that stays just under the cap. The
/// whole conversion spends one font account (<see cref="PdfLimits.MaxCharstringStepsPerFont"/>), as a
/// CFF font's scans do. <see cref="CharstringWork.StepsRun"/> is the evidence, never a clock.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class Type1ConversionBudgetTests
{
    /// <summary>Subroutine tree depth: 2^14 leaves of "1 1 rlineto", about 147 000 steps a glyph.</summary>
    private const int Depth = 14;

    [Fact]
    public void Adversarial_glyphs_each_near_the_per_glyph_cap_are_stopped_by_the_fonts_budget()
    {
        Type1Font font = Parse(Program(glyphs: 800));   // about 118 million steps, unbounded
        CharstringWork.StepsRun = 0;

        byte[] cff = Type1ToCffConverter.Convert(font, "P");

        Assert.Null(cff);
        Assert.Contains("PdfLimits.MaxCharstringStepsPerFont", Type1ToCffConverter.LastError);
        Assert.InRange(CharstringWork.StepsRun, PdfLimits.MaxCharstringStepsPerFont,
            PdfLimits.MaxCharstringStepsPerFont + Type1CharstringInterpreter.MaxStepsPerGlyph + 1);
    }

    /// <summary>The control: the same glyphs, few enough to fit the font's budget, convert — every glyph's outline written.</summary>
    [Fact]
    public void Glyphs_within_the_fonts_budget_convert()
    {
        Type1Font font = Parse(Program(glyphs: 20));
        CharstringWork.StepsRun = 0;

        byte[] cff = Type1ToCffConverter.Convert(font, "P");

        Assert.True(cff != null, Type1ToCffConverter.LastError);
        Assert.Null(Type1ToCffConverter.LastError);
        Assert.InRange(CharstringWork.StepsRun, 20L * 100_000, 21L * Type1CharstringInterpreter.MaxStepsPerGlyph);
        // Twenty glyphs of 16 384 segments each, a few bytes a segment once written.
        Assert.InRange(cff.Length, 20 * 16_384, 20 * 16_384 * 8);
    }

    /// <summary>A lowered ceiling refuses what the default admits: the forwarding of the setting, not a constant.</summary>
    [Fact]
    public void Lowered_font_budget_is_the_one_asked()
    {
        Type1Font font = Parse(Program(glyphs: 20));
        try
        {
            PdfLimits.MaxCharstringStepsPerFont = 1_000_000;
            CharstringWork.StepsRun = 0;

            Assert.Null(Type1ToCffConverter.Convert(font, "P"));
            Assert.Contains("PdfLimits.MaxCharstringStepsPerFont", Type1ToCffConverter.LastError);
            Assert.InRange(CharstringWork.StepsRun, 1_000_000, 1_000_000 + Type1CharstringInterpreter.MaxStepsPerGlyph + 1);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A declared <c>/Subrs</c> count is clamped to the entries the section could hold (ten bytes each at
    /// the least) before the array is allocated: two billion declared over a few kilobytes allocates a
    /// few kilobytes of references, and the entries present are read.
    /// </summary>
    [Fact]
    public void Adversarial_subrs_count_allocates_by_the_section_not_the_declaration()
    {
        byte[] program = Program(glyphs: 2, declaredSubrs: 2_000_000_000, padding: 200_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Type1Font font = Parse(program);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The decrypted section is a copy of the program; a reference a byte of it would add eight more.
        Assert.InRange(allocated, program.Length / 2, 4L * program.Length);
        Assert.Equal(Depth + 1, CountNonEmpty(font.Subrs));
    }

    private static int CountNonEmpty(IReadOnlyList<byte[]> subrs)
    {
        int count = 0;
        foreach (byte[] s in subrs) if (s != null && s.Length > 0) count++;
        return count;
    }

    private static Type1Font Parse(byte[] program)
    {
        Type1Font font = Type1Parser.Parse(program, Length1, program.Length - Length1);
        Assert.NotNull(font);
        return font;
    }

    private static int Length1;

    private static byte[] Num(int v) => new[] { (byte)(v + 139) };   // -107..107

    private static byte[] Encrypt(byte[] plain, ushort key)
    {
        var o = new byte[plain.Length];
        ushort r = key;
        for (int i = 0; i < plain.Length; i++) { byte c = (byte)(plain[i] ^ (r >> 8)); o[i] = c; r = (ushort)((c + r) * 52845 + 22719); }
        return o;
    }

    /// <summary>
    /// A Type 1 program of <paramref name="glyphs"/> glyphs (and .notdef), each "0 100 hsbw 0 callsubr
    /// endchar", whose subroutines 0 to <see cref="Depth"/> − 1 each call the next twice and whose last
    /// draws "1 1 rlineto": lenIV 4, binary eexec.
    /// </summary>
    private static byte[] Program(int glyphs, long? declaredSubrs = null, int padding = 0)
    {
        byte[] clear = Encoding.ASCII.GetBytes("%!PS-AdobeFont-1.0: P 001\n/FontName /P def\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n" +
                                               "/Encoding StandardEncoding def\ncurrentdict end\ncurrentfile eexec\n");
        var priv = new MemoryStream();
        void A(string s) { byte[] b = Encoding.ASCII.GetBytes(s); priv.Write(b, 0, b.Length); }
        void Entry(string head, List<byte> cs, string tail)
        {
            var seeded = new byte[cs.Count + 4];
            cs.CopyTo(seeded, 4);
            byte[] enc = Encrypt(seeded, 4330);
            A($"{head} {enc.Length} RD "); priv.Write(enc, 0, enc.Length); A(tail);
        }
        A("dup /Private 8 dict dup begin\n/lenIV 4 def\n");
        A($"/Subrs {declaredSubrs ?? Depth + 1} array\n");
        for (int i = 0; i < Depth; i++)
        {
            var cs = new List<byte>(); cs.AddRange(Num(i + 1)); cs.Add(10); cs.AddRange(Num(i + 1)); cs.Add(10); cs.Add(11);
            Entry($"dup {i}", cs, " NP\n");
        }
        { var cs = new List<byte>(); cs.AddRange(Num(1)); cs.AddRange(Num(1)); cs.Add(5); cs.Add(11); Entry($"dup {Depth}", cs, " NP\n"); }
        A("ND\n2 index /CharStrings " + (glyphs + 1) + " dict dup begin\n");
        for (int g = 0; g <= glyphs; g++)
        {
            var cs = new List<byte>(); cs.AddRange(Num(0)); cs.AddRange(Num(100)); cs.Add(13);
            if (g > 0) { cs.AddRange(Num(0)); cs.Add(10); }
            cs.Add(14);
            Entry(g == 0 ? "/.notdef" : "/g" + g, cs, " ND\n");
        }
        A("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        A(new string(' ', padding));

        var plain = new byte[priv.Length + 4];
        Array.Copy(priv.ToArray(), 0, plain, 4, (int)priv.Length);
        byte[] encrypted = Encrypt(plain, 55665);
        Length1 = clear.Length;
        var all = new byte[clear.Length + encrypted.Length];
        clear.CopyTo(all, 0);
        encrypted.CopyTo(all, clear.Length);
        return all;
    }
}
