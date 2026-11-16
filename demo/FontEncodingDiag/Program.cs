// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: FontEncodingDiag <pdf-path> [page-index]");
    Environment.ExitCode = 2;
    return;
}

var pdfPath = args[0];
int pageIdx  = args.Length > 1 ? int.Parse(args[1]) : 0;

using var stream = File.OpenRead(pdfPath);
using var doc = PdfDocument.Open(stream);

var page = doc.Pages[pageIdx];
var resources = doc.ResolveObject(page.Dictionary[PdfName.Resources]) as PdfDictionary
    ?? page.Dictionary.GetDictionary(PdfName.Resources);
var fonts = (doc.ResolveObject(resources?[PdfName.Font]) as PdfDictionary)
    ?? resources?.GetDictionary(PdfName.Font);

if (fonts == null) { Console.WriteLine("No fonts"); return; }

foreach (var kv in fonts)
{
    var fontDict = doc.ResolveObject(kv.Value) as PdfDictionary;
    if (fontDict == null) continue;
    var subtype  = fontDict.GetName(PdfName.Subtype)?.Value ?? "?";
    var baseFont = fontDict.GetName(PdfName.BaseFont)?.Value ?? "?";
    Console.WriteLine($"/{kv.Key} Subtype={subtype} BaseFont={baseFont}");

    var descArr = fontDict[PdfName.DescendantFonts];
    if (descArr == null) continue;
    var arr = doc.ResolveObject(descArr) as PdfArray;
    if (arr?.Count < 1) continue;
    var cid = doc.ResolveObject(arr[0]) as PdfDictionary;
    if (cid == null) continue;

    // DW default width
    var dw = cid[PdfName.DW] as PdfNumber;
    Console.WriteLine($"  DW = {dw?.DoubleValue ?? 1000}");

    // /W array
    var wObj = cid[PdfName.W];
    if (wObj == null) { Console.WriteLine("  No /W array"); continue; }
    var wArr = doc.ResolveObject(wObj) as PdfArray;
    if (wArr == null) { Console.WriteLine("  /W not array"); continue; }

    Console.WriteLine($"  /W array ({wArr.Count} items):");
    // Print first 60 items to show the structure
    int count = Math.Min(wArr.Count, 60);
    for (int i = 0; i < count; i++)
    {
        var item = wArr[i];
        if (item is PdfNumber n) Console.Write($"  {n.DoubleValue}");
        else if (item is PdfArray sub)
        {
            Console.Write("  [");
            for (int j = 0; j < Math.Min(sub.Count, 20); j++)
                Console.Write($"{(sub[j] as PdfNumber)?.DoubleValue ?? 0} ");
            Console.Write("]");
        }
    }
    Console.WriteLine();
    Console.WriteLine($"  (total /W items: {wArr.Count})");
    Console.WriteLine();
}
