// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.CMap.Tests
{
    /// <summary>
    /// End-to-end (public-surface) adversarial tests for the CMap code-review fixes,
    /// exercised through <see cref="PredefinedCMapRegistry"/> + the public
    /// <see cref="PdfTextExtractor"/> pipeline (this test project only sees public API):
    ///   4. A cyclic / self-referential usecmap registration must not StackOverflow when a
    ///      Type0 font triggers the predefined-CMap lookup during text extraction.
    ///   7. A ToUnicode bfchar with an odd trailing hex unit is not truncated.
    /// </summary>
    public sealed class ReviewFixCMap_RecursionAndHexTests
    {
        // ============================================================
        //  Finding 4 — usecmap cycle guard (end-to-end via PdfTextExtractor)
        // ============================================================

        [Fact]
        public void CyclicUsecmap_DuringTextExtraction_DoesNotStackOverflow()
        {
            // "Adobe-Japan1-UCS2" is the first alias PdfType0Font probes for a Japan1
            // ordering.  It references a helper CMap via usecmap, which references it
            // back (A → B → A).  Pre-fix, the lazy parse triggered by decoding would
            // recurse forever → StackOverflow.  A unique helper name is used so we do not
            // clobber any embedded plugin CMap.  "Adobe-Japan1-UCS2" also maps CID 0x42
            // → U+4E2D so we can assert the mapping still resolves once the cycle breaks.
            const string helperName = "ReviewFix-Cycle-Helper";
            byte[] a = Ascii(
                "begincmap\n" +
                "/" + helperName + " usecmap\n" +
                "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
                "1 beginbfchar\n<0042> <4E2D>\nendbfchar\n" +
                "endcmap\n");
            byte[] b = Ascii(
                "begincmap\n" +
                "/Adobe-Japan1-UCS2 usecmap\n" +
                "endcmap\n");

            PredefinedCMapRegistry.Register("Adobe-Japan1-UCS2", a);
            PredefinedCMapRegistry.Register(helperName, b);
            try
            {
                byte[] pdf = BuildJapan1Type0Pdf(showHex: "0042");
                using var doc = PdfDocument.Open(new MemoryStream(pdf));

                // Must complete without StackOverflow; the cyclic CMaps resolve and the
                // CID 0x42 → U+4E2D mapping is honoured.
                string text = PdfTextExtractor.ExtractText(doc.Pages[0]);
                Assert.Contains("中", text); // U+4E2D
            }
            finally
            {
                PredefinedCMapRegistry.Register("Adobe-Japan1-UCS2", Array.Empty<byte>());
                PredefinedCMapRegistry.Register(helperName, Array.Empty<byte>());
            }
        }

        // ============================================================
        //  Finding 7 — odd trailing hex unit not dropped (via ToUnicode)
        // ============================================================

        [Fact]
        public void ToUnicodeBfChar_OddTrailingUnit_DuringExtraction_NotDropped()
        {
            // A ToUnicode CMap whose bfchar destination has a dangling 2-nibble tail:
            // "0041004141" → "A" "A" "A" (the trailing "41" must round-trip, not drop).
            byte[] toUnicode = Ascii(
                "begincmap\n" +
                "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
                "1 beginbfchar\n<0001> <0041004141>\nendbfchar\n" +
                "endcmap\n");

            byte[] pdf = BuildType0PdfWithToUnicode(showHex: "0001", toUnicode: toUnicode);
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            string text = PdfTextExtractor.ExtractText(doc.Pages[0]);
            Assert.Contains("AAA", text);
        }

        // ============================================================
        //  Helpers — build a minimal Type0 PDF in memory.
        // ============================================================

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        // Type0 font with Ordering /Japan1 and no /ToUnicode, so decoding consults the
        // registered predefined CMaps (Adobe-Japan1-UCS2 / UniJIS-UTF16-H).
        private static byte[] BuildJapan1Type0Pdf(string showHex)
        {
            var objs = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                    "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
                ContentStreamObject(showHex),
                // Type0 font (obj 5) -> descendant CIDFontType0 (obj 6)
                "<< /Type /Font /Subtype /Type0 /BaseFont /HiraMinPro-W3 " +
                    "/Encoding /Identity-H /DescendantFonts [6 0 R] >>",
                "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /HiraMinPro-W3 " +
                    "/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 5 >> " +
                    "/DW 1000 >>",
            };
            return AssemblePdf(objs);
        }

        // Type0 font with an explicit /ToUnicode stream (obj 7).
        private static byte[] BuildType0PdfWithToUnicode(string showHex, byte[] toUnicode)
        {
            string toUniStr = Encoding.ASCII.GetString(toUnicode);
            var objs = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                    "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
                ContentStreamObject(showHex),
                "<< /Type /Font /Subtype /Type0 /BaseFont /TestFont " +
                    "/Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
                "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont " +
                    "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                    "/DW 1000 >>",
                "<< /Length " + toUniStr.Length.ToString() + " >>\nstream\n" + toUniStr + "\nendstream",
            };
            return AssemblePdf(objs);
        }

        private static string ContentStreamObject(string showHex)
        {
            string stream = "BT /F1 12 Tf 10 100 Td <" + showHex + "> Tj ET";
            return "<< /Length " + stream.Length.ToString() + " >>\nstream\n" + stream + "\nendstream";
        }

        // Assembles a list of object bodies (1-indexed) into a valid PDF with an xref table.
        private static byte[] AssemblePdf(List<string> objectBodies)
        {
            var sb = new StringBuilder();
            sb.Append("%PDF-1.5\n");

            int count = objectBodies.Count;
            var offsets = new int[count + 1];

            for (int i = 0; i < count; i++)
            {
                offsets[i + 1] = sb.Length;
                sb.Append((i + 1).ToString()).Append(" 0 obj\n")
                  .Append(objectBodies[i]).Append("\nendobj\n");
            }

            int xrefOffset = sb.Length;
            sb.Append("xref\n0 ").Append((count + 1).ToString()).Append('\n');
            sb.Append("0000000000 65535 f \n");
            for (int i = 1; i <= count; i++)
                sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

            sb.Append("trailer\n<< /Size ").Append((count + 1).ToString())
              .Append(" /Root 1 0 R >>\nstartxref\n")
              .Append(xrefOffset.ToString()).Append("\n%%EOF\n");

            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }
}
