// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Text;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= srcset / <picture>

        /// <summary>
        /// The best image candidate for <paramref name="img"/>: the highest-resolution entry from
        /// its own <c>srcset</c>, or — when it sits inside a <c>&lt;picture&gt;</c> — from the
        /// first <c>&lt;source&gt;</c> whose <c>media</c> and <c>type</c> both apply. Returns
        /// <c>null</c> when nothing usable is declared, leaving <c>src</c> to serve.
        ///
        /// <para>Highest-resolution rather than "what a viewport would pick": the output is print,
        /// which has no viewport and no device pixel ratio, so the largest candidate is the one
        /// that will not look soft on paper.</para>
        /// </summary>
        private string? ResolveBestCandidate(IElement img)
        {
            var parent = img.ParentElement;
            if (parent != null && NameIs(parent, HtmlTags.Picture))
            {
                foreach (var source in parent.Children)
                {
                    if (!NameIs(source, HtmlTags.Source)) continue;

                    // <source media> reuses the evaluation the cascade already does, so a candidate
                    // for the other medium is skipped exactly as that medium's @media block is.
                    string? media = source.GetAttribute("media");
                    if (!string.IsNullOrEmpty(media) && !MediaApplies(media!, _mediaViewport)) continue;

                    // <source type> names a MIME type. Declining one this engine cannot decode
                    // here — rather than picking it and failing later — is what makes a
                    // <picture> with a WebP first and a PNG fallback work.
                    string? type = source.GetAttribute("type");
                    if (!string.IsNullOrEmpty(type) && !IsDecodableMediaType(type!)) continue;

                    string? candidate = PickFromSrcSet(source.GetAttribute("srcset"))
                                        ?? source.GetAttribute("src");
                    if (!string.IsNullOrEmpty(candidate)) return candidate;
                }
            }
            return PickFromSrcSet(img.GetAttribute("srcset"));
        }

        /// <summary>
        /// The highest-resolution entry of a <c>srcset</c> list, by its <c>w</c> or <c>x</c>
        /// descriptor. An entry with no descriptor counts as 1x.
        /// </summary>
        private static string? PickFromSrcSet(string? srcset)
        {
            if (string.IsNullOrWhiteSpace(srcset)) return null;

            string? best = null;
            double bestWeight = -1;
            foreach (var (url, descriptor) in SrcSetCandidates(srcset!))
            {
                double weight = 1;
                if (descriptor.EndsWith("w", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(descriptor.Substring(0, descriptor.Length - 1),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
                    weight = w;
                else if (descriptor.EndsWith("x", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(descriptor.Substring(0, descriptor.Length - 1),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                    weight = x * 1000;   // scale so an x-descriptor outranks a small w-descriptor

                if (weight > bestWeight) { best = url; bestWeight = weight; }
            }
            return best;
        }

        /// <summary>
        /// A <c>srcset</c>'s candidates as the HTML Standard parses them (§4.8.4.3.10): white space
        /// and commas skipped, the URL the run of characters up to the next white space (a trailing
        /// comma stripped, and then it has no descriptors), the descriptors up to a comma outside
        /// parentheses. A comma inside a URL — a <c>data:</c> URI's, an image service's transform —
        /// belongs to it; cutting the list at every comma first split such a URL in two.
        /// </summary>
        internal static List<(string Url, string Descriptor)> SrcSetCandidates(string srcset)
        {
            var candidates = new List<(string, string)>();
            int i = 0, n = srcset.Length;
            while (i < n)
            {
                while (i < n && (IsHtmlSpace(srcset[i]) || srcset[i] == ',')) i++;
                if (i >= n) break;

                int start = i;
                while (i < n && !IsHtmlSpace(srcset[i])) i++;
                string url = srcset.Substring(start, i - start);
                string descriptor = "";
                if (url.EndsWith(",", StringComparison.Ordinal))
                    url = url.TrimEnd(',');
                else
                {
                    int from = i, depth = 0;
                    for (; i < n; i++)
                    {
                        char c = srcset[i];
                        if (c == '(') depth++;
                        else if (c == ')' && depth > 0) depth--;
                        else if (c == ',' && depth == 0) break;
                    }
                    descriptor = srcset.Substring(from, i - from).Trim();
                    if (i < n) i++;   // the comma
                }
                if (url.Length > 0) candidates.Add((url, descriptor));
            }
            return candidates;
        }

        private static bool IsHtmlSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

        /// <summary>Whether a <c>&lt;source type&gt;</c> names a format this engine can embed.</summary>
        private static bool IsDecodableMediaType(string mediaType)
        {
            string t = mediaType.Trim().ToLowerInvariant();
            int semi = t.IndexOf(';');
            if (semi >= 0) t = t.Substring(0, semi).Trim();
            return t is "image/png" or "image/jpeg" or "image/jpg" or "image/gif" or "image/bmp"
                     or "image/x-ms-bmp" or "image/svg+xml";
        }
    }
}
