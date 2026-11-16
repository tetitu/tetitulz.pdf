// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// <see cref="OptimizationOptions.MergeImageStrips"/>: a run of image strips drawn edge to edge
    /// becomes one image (GAPS §19h).
    /// </summary>
    /// <remarks>
    /// <para><b>What a strip is.</b> Exactly <c>q a 0 0 d e f cm /Name Do Q</c> — four operations, nothing
    /// between them — with <c>a</c> and <c>d</c> positive and the shears zero: an upright image at a
    /// known place. Printer drivers that rasterise a page emit one such group per band, dozens or
    /// hundreds of them, each with its own image object and dictionary; the bands are the picture.</para>
    /// <para><b>What a run is.</b> Four or more consecutive strips that share the
    /// left edge <c>e</c> and the width <c>a</c>, each stacked directly on the one before — the next
    /// one's top edge is this one's bottom, or the reverse — whose images share the pixel width, 8 bits
    /// a component and a <c>/DeviceGray</c> or <c>/DeviceRGB</c> colour space, carry no mask,
    /// <c>/Decode</c>, <c>/Alternates</c> or optional content, come under lossless filters (a JPEG strip
    /// re-encoded losslessly would grow, and the merge promises to lose nothing), and are each drawn at
    /// the run's scale — the pixel rows per point are the same for every strip, so the merged image is
    /// not stretched anywhere. The order in the file is not the stacking order: the strips are sorted
    /// by their top edge, since image space's first row is the top.</para>
    /// <para><b>What is written.</b> One Flate-encoded image of the strips' samples in stacking order,
    /// under a resource name the page's <c>/XObject</c> dictionary does not have, and one group in the
    /// place of the run: <c>q a 0 0 D e F cm /Name Do Q</c> with <c>D</c> the summed height and <c>F</c>
    /// the lowest bottom. The page's content comes out as one stream (an array is concatenated, as
    /// <see cref="ConcatenateContentStreams"/> does, with the same refusal of a fragment another page
    /// draws). The strips' images stay in the object map; the ones nothing else draws fall to the
    /// unreachable-object pass. A page holding an inline image is left alone, like every other content
    /// rewrite here: the tokenizer refuses the stream.</para>
    /// </remarks>
    public static partial class PdfOptimizer
    {
        /// <summary>The fewest strips a run merges; fewer saves little and risks a seam.</summary>
        private const int MinStripsPerRun = 4;

        /// <summary>How far two strips' edges may differ, in points, and still be one edge.</summary>
        private const double StripEdgeTolerancePt = 0.001;

        /// <summary>How far a strip's rows-per-point may differ from the run's, as a fraction, and still be the run's scale.</summary>
        private const double StripScaleTolerance = 0.001;

        private readonly struct Strip
        {
            internal Strip(int firstItem, int lastItem, double a, double d, double e, double f, PdfName name)
            {
                FirstItem = firstItem; LastItem = lastItem; A = a; D = d; E = e; F = f; Name = name;
            }

            /// <summary>Index of the <c>q</c> token in the item list.</summary>
            internal readonly int FirstItem;
            /// <summary>Index of the <c>Q</c> token in the item list.</summary>
            internal readonly int LastItem;
            internal readonly double A, D, E, F;
            internal readonly PdfName Name;
            internal double Top => F + D;
        }

        private sealed class StripImage
        {
            internal int Width, Height, Components;
            internal PdfName ColorSpace;
            internal PdfStream Stream;
        }

        /// <summary>Merges every run on every page; returns how many runs became one image.</summary>
        private static int MergeImageStrips(Dictionary<int, PdfObject> objects, PdfDictionary trailer,
            OptimizationOptions options, OptimizationStats stats)
        {
            var uses = CountContentStreamUses(objects, trailer);
            int nextObjectNumber = 1;
            foreach (int num in objects.Keys)
                if (num >= nextObjectNumber) nextObjectNumber = num + 1;

            int merged = 0;
            var nameCursors = new Dictionary<object, int>(PdfReferenceEquality.Instance);
            foreach (var entry in CollectPages(objects, trailer))
            {
                PdfDictionary page = entry.Page;
                PdfDictionary resources = Deref(page[PdfName.Resources], objects) as PdfDictionary ?? entry.InheritedResources;
                if (!(Deref(resources?[PdfName.XObject], objects) is PdfDictionary xobjects)) continue;

                byte[] content = PageContentForRewrite(page, objects, options, uses, entry.ObjectNumber, stats);
                if (content == null) continue;

                List<ContentStreamItem> items = TokenizeContentStream(content);
                if (items == null) continue;   // an inline image: the tokenizer has no state for its data

                List<Strip> strips = FindStrips(items);
                if (strips.Count < MinStripsPerRun) continue;

                var replacements = new List<(int first, int last, byte[] bytes)>();
                int runStart = 0;
                while (runStart < strips.Count)
                {
                    int runEnd = ExtendRun(strips, runStart, items);
                    if (runEnd - runStart >= MinStripsPerRun)
                    {
                        byte[] group = TryMergeRun(strips, runStart, runEnd, xobjects, objects, options, stats,
                            entry.ObjectNumber, ref nextObjectNumber, nameCursors);
                        if (group != null)
                        {
                            replacements.Add((strips[runStart].FirstItem, strips[runEnd - 1].LastItem, group));
                            merged++;
                        }
                    }
                    runStart = runEnd;
                }
                if (replacements.Count == 0) continue;

                // Splice the groups in, back to front so the earlier indices stay valid.
                for (int i = replacements.Count - 1; i >= 0; i--)
                {
                    (int first, int last, byte[] bytes) = replacements[i];
                    items.RemoveRange(first, last - first + 1);
                    items.Insert(first, ContentStreamItem.Token(bytes));
                }

                byte[] rewritten = SerializeItems(items);
                byte[] compressed = FlateEncodeFilter.Encode(rewritten, options.FlateCompressionLevel);
                var stream = new PdfStream();
                if (compressed != null && compressed.Length < rewritten.Length)
                {
                    stream.RawData = compressed;
                    stream.Set(PdfName.Filter, PdfName.FlateDecode);
                }
                else
                {
                    stream.RawData = rewritten;
                }
                stream.Set(PdfName.Length, new PdfNumber(stream.RawData.Length));
                int newNum = nextObjectNumber++;
                objects[newNum] = stream;
                page.Set(PdfName.Contents, new PdfReference(newNum, 0));
            }
            return merged;
        }

        /// <summary>
        /// The page's content as one decoded byte array, or null when it cannot be rewritten: a fragment
        /// another page draws too, or one that does not decode within the budget.
        /// </summary>
        private static byte[] PageContentForRewrite(PdfDictionary page, Dictionary<int, PdfObject> objects,
            OptimizationOptions options, Dictionary<int, int> uses, int pageNumber, OptimizationStats stats)
        {
            PdfObject contents = Deref(page[PdfName.Contents], objects);
            var fragments = new List<PdfReference>();
            if (contents is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                    if (array[i] is PdfReference r) fragments.Add(r);
            }
            else if (page[PdfName.Contents] is PdfReference single)
            {
                fragments.Add(single);
            }
            if (fragments.Count == 0) return null;

            var parts = new List<(int Number, PdfStream Stream)>(fragments.Count);
            foreach (PdfReference r in fragments)
            {
                if (uses.TryGetValue(r.ReferenceObjectNumber, out int n) && n > 1)
                {
                    Report("Optimize.MergeImageStrips", "Page " + PageName(pageNumber) + " shares a /Contents stream " +
                        "with another page, so its image strips were left as they are; rewriting it would have " +
                        "changed what the other page draws.", stats);
                    return null;
                }
                if (!objects.TryGetValue(r.ReferenceObjectNumber, out PdfObject obj) || !(obj is PdfStream fragment))
                    return null;
                parts.Add((r.ReferenceObjectNumber, fragment));
            }

            byte[] joined = JoinContentParts(parts, options, out int failed, out string reason);
            if (reason != null)
                Report("Optimize.MergeImageStrips", failed >= 0
                    ? "Object " + failed + " could not be decoded (" + reason + "); page " + PageName(pageNumber) +
                      "'s image strips were left as they are."
                    : "Page " + PageName(pageNumber) + "'s image strips were left as they are: " + reason + ".", stats);
            return joined;
        }

        /// <summary>Every <c>q a 0 0 d e f cm /Name Do Q</c> group in the item list, in content order.</summary>
        private static List<Strip> FindStrips(List<ContentStreamItem> items)
        {
            var strips = new List<Strip>();
            var operands = new List<ContentStreamItem>(8);
            int qIndex = -1;          // the item index of the `q` opening a candidate
            int stage = 0;            // 0: want q; 1: want cm; 2: want Do; 3: want Q
            double a = 0, d = 0, e = 0, f = 0;
            PdfName name = null;

            for (int i = 0; i < items.Count; i++)
            {
                ContentStreamItem item = items[i];
                if (item.IsWhitespace) continue;
                bool isOperator = IsOperatorToken(item);
                if (!isOperator)
                {
                    operands.Add(item);
                    continue;
                }

                bool matched = false;
                switch (stage)
                {
                    case 1 when item.Is("cm") && operands.Count == 6
                                && TryNumber(operands[0], out a) && TryNumber(operands[1], out double b)
                                && TryNumber(operands[2], out double c) && TryNumber(operands[3], out d)
                                && TryNumber(operands[4], out e) && TryNumber(operands[5], out f)
                                && b == 0 && c == 0 && a > 0 && d > 0:
                        stage = 2; matched = true; break;
                    case 2 when item.Is("Do") && operands.Count == 1 && operands[0].Length > 1 && operands[0][0] == (byte)'/':
                        name = new PdfName(operands[0].Ascii(1));
                        stage = 3; matched = true; break;
                    case 3 when item.Is("Q") && operands.Count == 0:
                        strips.Add(new Strip(qIndex, i, a, d, e, f, name));
                        stage = 0; matched = true; break;
                }
                if (!matched)
                {
                    // Any other operator ends the candidate; a `q` with no operands starts a new one.
                    stage = item.Is("q") && operands.Count == 0 ? 1 : 0;
                    qIndex = stage == 1 ? i : -1;
                }
                operands.Clear();
            }
            return strips;
        }

        /// <summary>
        /// The index one past the last strip of the run starting at <paramref name="start"/>: consecutive
        /// in the content (nothing but white space between one group's <c>Q</c> and the next's <c>q</c>),
        /// one left edge and width, each stacked directly on the one before, in either direction.
        /// </summary>
        private static int ExtendRun(List<Strip> strips, int start, List<ContentStreamItem> items)
        {
            int end = start + 1;
            int direction = 0;   // +1: each next strip is above the last; -1: below
            while (end < strips.Count)
            {
                Strip prev = strips[end - 1], next = strips[end];
                if (!OnlyWhitespaceBetween(items, prev.LastItem, next.FirstItem)) break;
                if (Math.Abs(prev.E - next.E) > StripEdgeTolerancePt || Math.Abs(prev.A - next.A) > StripEdgeTolerancePt) break;
                int step = Math.Abs(next.F - prev.Top) <= StripEdgeTolerancePt ? 1
                         : Math.Abs(prev.F - next.Top) <= StripEdgeTolerancePt ? -1 : 0;
                if (step == 0 || (direction != 0 && step != direction)) break;
                direction = step;
                end++;
            }
            return end;
        }

        private static bool OnlyWhitespaceBetween(List<ContentStreamItem> items, int after, int before)
        {
            for (int i = after + 1; i < before; i++)
                if (!items[i].IsWhitespace) return false;
            return true;
        }

        /// <summary>
        /// The merged group's bytes for the run, after writing its image into the object map and the
        /// resource dictionary — or null, with the reason reported, when the strips' images are not one
        /// picture's bands.
        /// </summary>
        private static byte[] TryMergeRun(List<Strip> strips, int start, int end, PdfDictionary xobjects,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, OptimizationStats stats,
            int pageNumber, ref int nextObjectNumber, Dictionary<object, int> nameCursors)
        {
            var ordered = new List<Strip>(end - start);
            for (int i = start; i < end; i++) ordered.Add(strips[i]);
            ordered.Sort((x, y) => y.Top.CompareTo(x.Top));   // top strip first: image space's first row is the top

            var images = new List<StripImage>(ordered.Count);
            double rowsPerPoint = 0;
            long totalRows = 0;
            foreach (Strip strip in ordered)
            {
                StripImage image = ReadStripImage(strip.Name, xobjects, objects, options, out string why);
                if (image == null)
                {
                    Report("Optimize.MergeImageStrips", "Page " + PageName(pageNumber) + ": a run of " + ordered.Count +
                        " image strips was left as it is because /" + strip.Name.Value + " " + why + ".", stats);
                    return null;
                }
                if (images.Count > 0)
                {
                    StripImage first = images[0];
                    if (image.Width != first.Width || image.Components != first.Components || image.ColorSpace != first.ColorSpace)
                    {
                        Report("Optimize.MergeImageStrips", "Page " + PageName(pageNumber) + ": a run of " + ordered.Count +
                            " image strips was left as it is because /" + strip.Name.Value + " differs from the first in " +
                            "pixel width or colour space.", stats);
                        return null;
                    }
                }
                // Every strip at the run's scale: its rows per point is the first's, else the merged
                // image would stretch some bands and squeeze others.
                double rows = image.Height / strip.D;
                if (images.Count == 0) rowsPerPoint = rows;
                else if (Math.Abs(rows - rowsPerPoint) > StripScaleTolerance * rowsPerPoint)
                {
                    Report("Optimize.MergeImageStrips", "Page " + PageName(pageNumber) + ": a run of " + ordered.Count +
                        " image strips was left as it is because /" + strip.Name.Value + " is drawn at another scale " +
                        "than the first.", stats);
                    return null;
                }
                images.Add(image);
                totalRows += image.Height;
            }

            StripImage head = images[0];
            if (totalRows > int.MaxValue / Math.Max(1, head.Width * head.Components)
                || (long)head.Width * totalRows > MaxReencodePixels)
            {
                Report("Optimize.MergeImageStrips", "Page " + PageName(pageNumber) + ": a run of " + ordered.Count +
                    " image strips was left as it is because the merged image would exceed the re-encode ceiling.", stats);
                return null;
            }

            // Decoded only now, each straight into the merged buffer and let go: the ceiling above is
            // judged from the dictionaries, so a run of strips each under it is refused before any
            // is decoded, and the merge holds one strip beside the result, not every strip.
            var samples = new byte[(long)head.Width * totalRows * head.Components];
            int at = 0;
            for (int i = 0; i < images.Count; i++)
            {
                byte[] strip = ReadStripSamples(images[i], objects, options, out string why);
                if (strip == null)
                {
                    Report("Optimize.MergeImageStrips", "Page " + PageName(pageNumber) + ": a run of " + ordered.Count +
                        " image strips was left as it is because /" + ordered[i].Name.Value + " " + why + ".", stats);
                    return null;
                }
                Buffer.BlockCopy(strip, 0, samples, at, strip.Length);
                at += strip.Length;
            }

            var mergedImage = new PdfStream();
            mergedImage.Set(PdfName.Type, PdfName.XObject);
            mergedImage.Set(PdfName.Subtype, PdfName.Image);
            mergedImage.Set(PdfName.Width, new PdfNumber(head.Width));
            mergedImage.Set(PdfName.Height, new PdfNumber(totalRows));
            mergedImage.Set(PdfName.ColorSpace, head.ColorSpace);
            mergedImage.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            byte[] encoded = FlateEncodeFilter.Encode(samples, options.FlateCompressionLevel) ?? samples;
            if (encoded.Length < samples.Length)
            {
                mergedImage.RawData = encoded;
                mergedImage.Set(PdfName.Filter, PdfName.FlateDecode);
            }
            else
            {
                mergedImage.RawData = samples;
            }
            mergedImage.Set(PdfName.Length, new PdfNumber(mergedImage.RawData.Length));

            int imageNumber = nextObjectNumber++;
            objects[imageNumber] = mergedImage;
            PdfName resourceName = UnusedResourceName(xobjects, nameCursors);
            xobjects.Set(resourceName, new PdfReference(imageNumber, 0));

            Strip top = ordered[0], bottom = ordered[ordered.Count - 1];
            double height = top.Top - bottom.F;
            string group = "q " + Pt(top.A) + " 0 0 " + Pt(height) + " " + Pt(top.E) + " " + Pt(bottom.F) +
                           " cm /" + resourceName.Value + " Do Q";
            return Encoding.ASCII.GetBytes(group);
        }

        /// <summary>
        /// The strip's image as its dictionary describes it, or null with <paramref name="why"/> when it
        /// is not one a merge may take: not an image, another depth or colour space, masked, decoded,
        /// under a lossy filter, or optional. Its samples are not read here
        /// (<see cref="ReadStripSamples"/>).
        /// </summary>
        private static StripImage ReadStripImage(PdfName name, PdfDictionary xobjects, Dictionary<int, PdfObject> objects,
            OptimizationOptions options, out string why)
        {
            why = null;
            if (!(Deref(xobjects[name], objects) is PdfStream image) || DerefName(image, PdfName.Subtype, objects)?.Value != "Image")
            {
                why = "is not an image XObject";
                return null;
            }
            if (image[PdfName.SMask] != null || image[PdfName.Mask] != null || image[PdfName.Decode] != null
                || image[PdfName.ImageMask] != null || image[PdfName.OC] != null || image[PdfName.Alternates] != null
                || image[PdfName.SMaskInData] != null)
            {
                why = "carries a mask, /Decode, /Alternates, /SMaskInData or optional content";
                return null;
            }
            if ((DerefInteger(image, PdfName.BitsPerComponent, objects) ?? 0) != 8)
            {
                why = "is not 8 bits a component";
                return null;
            }
            PdfName colorSpace = DerefName(image, PdfName.ColorSpace, objects);
            int components = colorSpace == PdfName.DeviceGray ? 1 : colorSpace == PdfName.DeviceRGB ? 3 : 0;
            if (components == 0)
            {
                why = "is not under /DeviceGray or /DeviceRGB";
                return null;
            }
            // A /Filter that is neither a name nor an array of names makes GetFilterChain return null,
            // as the other callers guard for; the foreach would otherwise throw NullReferenceException
            // and abandon the whole optimization (pages after this one never examined).
            System.Collections.Generic.IEnumerable<string> chain = GetFilterChain(image, objects);
            if (chain == null)
            {
                why = "has a /Filter that is neither a name nor an array of names";
                return null;
            }
            foreach (string filter in chain)
            {
                if (PdfFilterNames.IsImageCodec(filter))
                {
                    why = "is under /" + filter + ", which a lossless merge does not re-encode";
                    return null;
                }
            }
            int width = DerefInteger(image, PdfName.Width, objects) ?? 0;
            int height = DerefInteger(image, PdfName.Height, objects) ?? 0;
            if (width <= 0 || height <= 0 || (long)width * height * components > MaxReencodePixels)
            {
                why = "has no usable size";
                return null;
            }
            return new StripImage { Width = width, Height = height, Components = components, ColorSpace = colorSpace, Stream = image };
        }

        /// <summary>
        /// The strip's 8-bit samples, or null with <paramref name="why"/> when they do not decode, or not
        /// to exactly the size its dictionary declares.
        /// </summary>
        private static byte[] ReadStripSamples(StripImage image, Dictionary<int, PdfObject> objects,
            OptimizationOptions options, out string why)
        {
            why = null;
            long declared = (long)image.Width * image.Height * image.Components;
            if (!TryDecodeBounded(image.Stream, objects, options, out byte[] samples, out string reason))
            {
                why = "could not be decoded (" + reason + ")";
                return null;
            }
            if (samples == null || samples.Length != declared)
            {
                why = "decodes to " + (samples?.Length ?? 0) + " bytes, not its declared " + declared;
                return null;
            }
            return samples;
        }

        /// <summary>A name the resource dictionary does not have yet — a name added never replaces the page's.</summary>
        /// <remarks>
        /// The search resumes where this dictionary's last one stopped (<paramref name="cursors"/>, one
        /// per dictionary by identity): pages sharing one <c>/XObject</c> dictionary each add their runs to
        /// it, and starting every search at <c>MergedStrips1</c> made the pass quadratic in the runs the
        /// dictionary receives. Names below the cursor are taken by the pass itself or by the file.
        /// </remarks>
        internal static PdfName UnusedResourceName(PdfDictionary xobjects, Dictionary<object, int> cursors)
        {
            cursors.TryGetValue(xobjects, out int i);
            while (true)
            {
                i++;
                var candidate = new PdfName("MergedStrips" + i.ToString(CultureInfo.InvariantCulture));
                if (xobjects[candidate] == null)
                {
                    cursors[xobjects] = i;
                    return candidate;
                }
            }
        }

        private static bool IsOperatorToken(ContentStreamItem item)
        {
            if (item.Length == 0) return false;
            byte b = item[0];
            // Numbers, names, strings, arrays and dictionaries are operands; anything else that is not a
            // delimiter is an operator.
            if (b == (byte)'/' || b == (byte)'(' || b == (byte)'<' || b == (byte)'[' || b == (byte)']'
                || b == (byte)'+' || b == (byte)'-' || b == (byte)'.' || (b >= (byte)'0' && b <= (byte)'9'))
                return false;
            return true;
        }

        private static bool TryNumber(ContentStreamItem item, out double value)
            => double.TryParse(item.Ascii(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>A coordinate for the content stream: as the file wrote it when integral, else to four decimals (§7.3.3's real).</summary>
        private static string Pt(double v)
            => v == Math.Floor(v) && Math.Abs(v) < 1e9
                ? (SaturatingCast.ToLong(v)).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
