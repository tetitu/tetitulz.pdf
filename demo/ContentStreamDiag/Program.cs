// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// Dumps page structure + form XObject contents for rendering diagnosis.
// Usage: ContentStreamDiag <pdf-path> [page-index]

using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

// No default path: a fallback into one checkout fails on every other machine, the defect
// CLAUDE.md bans in tests as pattern 1.
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: ContentStreamDiag <pdf-path> [page-index]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Dumps page structure and form XObject contents for rendering diagnosis.");
    Console.Error.WriteLine("Any PDF works; tests/pdf/ has a few if you need one.");
    return 2;
}

var pdfPath = args[0];
int pageIdx = args.Length > 1 ? int.Parse(args[1]) : 0;

if (!File.Exists(pdfPath))
{
    Console.Error.WriteLine($"No such file: {pdfPath}");
    return 2;
}

using var fileStream = File.OpenRead(pdfPath);
using var doc        = PdfDocument.Open(fileStream);

var page = doc.Pages[pageIdx];
Console.WriteLine($"Page {pageIdx+1}: MediaBox={DumpArr(page.Dictionary.GetArray(PdfName.MediaBox))}  CropBox={DumpArr(page.Dictionary.GetArray(PdfName.CropBox))}");

// Dump page content stream
byte[] cs = page.GetContentStreamData() ?? [];
Console.WriteLine($"\nPage content stream ({cs.Length} bytes):");
Console.WriteLine(Encoding.Latin1.GetString(cs));

// Dump page resources
var res = doc.ResolveObject(page.Dictionary[PdfName.Resources]) as PdfDictionary
       ?? page.Dictionary.GetDictionary(PdfName.Resources);
if (res != null)
{
    Console.WriteLine("\n=== Page Resources ===");
    DumpResources(res, doc, 0);
}

return 0;

static void DumpResources(PdfDictionary res, PdfDocument doc, int depth)
{
    string indent = new string(' ', depth * 2);

    // XObjects
    var xobj = doc.ResolveObject(res[PdfName.XObject]) as PdfDictionary
            ?? res.GetDictionary(PdfName.XObject);
    if (xobj != null)
    {
        Console.WriteLine($"{indent}XObject:");
        foreach (var kv in xobj)
        {
            var xo = doc.ResolveObject(kv.Value);
            if (xo is PdfStream xs)
            {
                var subtype = (xs?.GetName(PdfName.Subtype))?.Value ?? "?";
                Console.WriteLine($"{indent}  /{kv.Key} ({subtype})");
                if (subtype == PdfName.Form.Value)
                {
                    var mbox  = xs?.GetArray(PdfName.BBox);
                    var mat   = xs?.GetArray(PdfName.Matrix);
                    var grp   = doc.ResolveObject(xs?[PdfName.Group]) as PdfDictionary ?? xs?.GetDictionary(PdfName.Group);
                    Console.WriteLine($"{indent}    BBox={DumpArr(mbox)}  Matrix={DumpArr(mat)}  Group={( grp != null ? "YES" : "none")}");
                    var formCs = xs.GetDecodedData();
                    if (formCs != null)
                    {
                        Console.WriteLine($"{indent}    Content ({formCs.Length} bytes):");
                        Console.WriteLine(Encoding.Latin1.GetString(formCs).Replace("\n", $"\n{indent}    "));
                    }
                    // Recurse into form resources
                    var formRes = doc.ResolveObject(xs?[PdfName.Resources]) as PdfDictionary
                               ?? xs?.GetDictionary(PdfName.Resources);
                    if (formRes != null) DumpResources(formRes, doc, depth + 2);
                }
                else if (subtype == PdfName.Image.Value)
                {
                    int w = xs?.GetInteger(PdfName.Width) ?? 0;
                    int h = xs?.GetInteger(PdfName.Height) ?? 0;
                    var cs2 = xs?.GetName(PdfName.ColorSpace);
                    var bpc = xs?.GetInteger(PdfName.BitsPerComponent);
                    Console.WriteLine($"{indent}    {w}×{h} ColorSpace={cs2?.Value ?? "?"} BPC={bpc}");
                }
            }
        }
    }

    // ExtGState
    var gs = doc.ResolveObject(res[PdfName.ExtGState]) as PdfDictionary
          ?? res.GetDictionary(PdfName.ExtGState);
    if (gs != null)
    {
        Console.WriteLine($"{indent}ExtGState:");
        foreach (var kv in gs)
        {
            var gsd = doc.ResolveObject(kv.Value) as PdfDictionary;
            if (gsd != null)
            {
                var ca = gsd[PdfName.ca] as PdfNumber;
                var CA = gsd[PdfName.CA] as PdfNumber;
                var bm = gsd.GetName(PdfName.BM);
                Console.WriteLine($"{indent}  /{kv.Key}  ca={ca?.DoubleValue}  CA={CA?.DoubleValue}  BM={bm?.Value}");
            }
        }
    }

    // Font
    var fonts = doc.ResolveObject(res[PdfName.Font]) as PdfDictionary
             ?? res.GetDictionary(PdfName.Font);
    if (fonts != null)
    {
        Console.WriteLine($"{indent}Font:");
        foreach (var kv in fonts)
        {
            var fd = doc.ResolveObject(kv.Value) as PdfDictionary;
            var sub = fd?.GetName(PdfName.Subtype)?.Value ?? "?";
            var bf  = fd?.GetName(PdfName.BaseFont)?.Value ?? "?";
            Console.WriteLine($"{indent}  /{kv.Key}  Subtype={sub}  BaseFont={bf}");
        }
    }

    // Pattern
    var patterns = doc.ResolveObject(res[PdfName.Pattern]) as PdfDictionary
                ?? res.GetDictionary(PdfName.Pattern);
    if (patterns != null)
    {
        Console.WriteLine($"{indent}Pattern:");
        foreach (var kv in patterns)
        {
            var pat = doc.ResolveObject(kv.Value);
            var patDict = pat as PdfDictionary;
            if (patDict == null) continue;
            var pt = (patDict[PdfName.PatternType] as PdfNumber)?.LongValue ?? 0;
            if (pt == 1) // Tiling
            {
                var pbbox = patDict.GetArray(PdfName.BBox);
                var pmat  = patDict.GetArray(PdfName.Matrix);
                var xs2   = patDict[new PdfName("XStep")] as PdfNumber;
                var ys2   = patDict[new PdfName("YStep")] as PdfNumber;
                var paint = (patDict[new PdfName("PaintType")] as PdfNumber)?.LongValue ?? 1;
                Console.WriteLine($"{indent}  /{kv.Key} TilingPattern PaintType={paint} BBox={DumpArr(pbbox)} XStep={xs2?.DoubleValue} YStep={ys2?.DoubleValue} Matrix={DumpArr(pmat)}");
                var patStream = pat as PdfStream;
                if (patStream != null)
                {
                    var patData = patStream.GetDecodedData();
                    if (patData != null)
                    {
                        Console.WriteLine($"{indent}    Content ({patData.Length} bytes):");
                        Console.WriteLine(Encoding.Latin1.GetString(patData).Replace("\n", $"\n{indent}    "));
                    }
                }
                var patRes = doc.ResolveObject(patDict[PdfName.Resources]) as PdfDictionary
                          ?? patDict.GetDictionary(PdfName.Resources);
                if (patRes != null) DumpResources(patRes, doc, depth + 2);
            }
            else if (pt == 2) // Shading
            {
                Console.WriteLine($"{indent}  /{kv.Key} ShadingPattern");
            }
        }
    }
}

static string DumpArr(PdfArray? arr)
{
    if (arr == null) return "(null)";
    var sb = new StringBuilder("[");
    foreach (var item in arr)
        sb.Append((item as PdfNumber)?.DoubleValue.ToString("G") + " ");
    sb.Append("]");
    return sb.ToString();
}
