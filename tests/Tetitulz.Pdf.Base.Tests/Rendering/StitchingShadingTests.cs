// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering
{
    /// <summary>
    /// Regression test for axial/radial shadings driven by a multi-segment
    /// <b>stitching</b> function (FunctionType 3).  The colour resolver previously only
    /// understood FunctionType 2 and fell back to a flat black→white ramp for anything
    /// else, AND only sampled the two endpoints — so a rainbow gradient rendered as
    /// grayscale.  Observed on a Cal credit-card statement
    /// (raw/דף פירוט דיגיטלי כאל 07-25 (1).pdf) whose card "gradient strip" (a
    /// DeviceRGB Type-3 stitching gradient filling a thin rectangle) rendered as a plain
    /// black→white band.
    /// </summary>
    public sealed class StitchingShadingTests
    {
        // Builds a 100×100 page that paints, clipped to the whole page, an axial shading
        // whose colour function is a Type-3 stitch of two Type-2 segments:
        //   t∈[0,0.5]  red → green     t∈[0.5,1]  green → blue
        // so the gradient reads red (left) · green (centre) · blue (right).
        private static byte[] BuildStitchingShadingPdf(bool indirectSubFunctions = false)
        {
            var pdf = new StringBuilder();
            pdf.Append("%PDF-1.4\n");
            var offsets = new List<int>();
            void Obj(string body)
            {
                offsets.Add(pdf.Length);
                pdf.Append(body);
            }

            Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
                "/Contents 4 0 R /Resources << /Shading << /Sh 5 0 R >> >> >>\nendobj\n");

            string content = "q 0 0 100 100 re W n /Sh sh Q";
            Obj("4 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");

            Obj("5 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 50 100 50] " +
                "/Domain [0 1] /Function 6 0 R /Extend [true true] >>\nendobj\n");
            // FunctionType 3 stitch of two Type-2 segments. The sub-functions are either
            // INLINE (as most producers emit) or INDIRECT references (which the evaluator
            // could not resolve before the fix — they returned 0 → flat black ramp).
            string funcsArr = indirectSubFunctions
                ? "[7 0 R 8 0 R]"
                : "[<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >> " +
                  "<< /FunctionType 2 /Domain [0 1] /C0 [0 1 0] /C1 [0 0 1] /N 1 >>]";
            Obj("6 0 obj\n<< /FunctionType 3 /Domain [0 1] /Bounds [0.5] /Encode [0 1 0 1] " +
                "/Functions " + funcsArr + " >>\nendobj\n");
            if (indirectSubFunctions)
            {
                Obj("7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >>\nendobj\n");
                Obj("8 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0 1 0] /C1 [0 0 1] /N 1 >>\nendobj\n");
            }

            int xref = pdf.Length;
            pdf.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
            pdf.Append("0000000000 65535 f \n");
            foreach (int off in offsets)
                pdf.Append(off.ToString("D10")).Append(" 00000 n \n");
            pdf.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\n");
            pdf.Append("startxref\n").Append(xref).Append("\n%%EOF\n");

            return Encoding.ASCII.GetBytes(pdf.ToString());
        }

        private static bool IsReddish(SKColor c) => c.Red > 150 && c.Green < 110 && c.Blue < 110;
        private static bool IsGreenish(SKColor c) => c.Green > 140 && c.Red < 130 && c.Blue < 130;
        private static bool IsBluish(SKColor c) => c.Blue > 150 && c.Red < 110 && c.Green < 130;
        private static bool IsGray(SKColor c)
        {
            int max = System.Math.Max(c.Red, System.Math.Max(c.Green, c.Blue));
            int min = System.Math.Min(c.Red, System.Math.Min(c.Green, c.Blue));
            return max - min < 24;
        }

        private static void AssertRedGreenBlueGradient(byte[] pdf)
        {
            using var ms = new MemoryStream(pdf);
            byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
            using var bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);

            int y = bmp.Height / 2;
            SKColor left   = bmp.GetPixel(bmp.Width / 10, y);
            SKColor middle = bmp.GetPixel(bmp.Width / 2, y);
            SKColor right  = bmp.GetPixel(bmp.Width * 9 / 10, y);

            // The pre-fix bug rendered the whole strip as a black→white ramp, so the
            // centre was mid-gray. The interior stitch colour (green) is the key guard.
            Assert.False(IsGray(middle), $"gradient centre was grayscale: {middle}");
            Assert.True(IsGreenish(middle), $"gradient centre should be green, was {middle}");
            Assert.True(IsReddish(left),    $"gradient start should be red, was {left}");
            Assert.True(IsBluish(right),    $"gradient end should be blue, was {right}");
        }

        [Fact]
        public void StitchingFunctionShading_RendersInteriorColours_NotGrayscale()
            => AssertRedGreenBlueGradient(BuildStitchingShadingPdf(indirectSubFunctions: false));

        [Fact]
        public void StitchingFunctionShading_IndirectSubFunctions_StillRenderColours()
            => AssertRedGreenBlueGradient(BuildStitchingShadingPdf(indirectSubFunctions: true));
    }
}
