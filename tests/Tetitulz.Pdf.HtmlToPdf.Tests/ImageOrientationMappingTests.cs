// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A JPEG's EXIF orientation is applied to an <c>&lt;img&gt;</c>, as a browser applies it by
    /// default (CSS <c>image-orientation: from-image</c>); <c>image-orientation: none</c> draws the
    /// pixels as stored.
    /// </summary>
    /// <remarks>
    /// The image is stored 40 × 20px. Turned a quarter (orientation 6) it is shown 20 × 40px, 15 ×
    /// 30pt, and the image's <c>cm</c> reads <c>0 -30 15 0</c>; as stored it reads <c>30 0 0 15</c>.
    /// </remarks>
    public class ImageOrientationMappingTests
    {
        private static double[] Placement(int orientation, string style = "")
        {
            string b64 = Convert.ToBase64String(ExifJpeg.Build(40, 20, orientation));
            string content = DecodeStreams(ToPdf($"<p><img src='data:image/jpeg;base64,{b64}' style='{style}'></p>"));
            var m = Regex.Match(content,
                @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+cm\s*/\w+\s+Do\b");
            Assert.True(m.Success, "no image was drawn");
            return Enumerable.Range(1, 4).Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        }

        [Fact]
        public void APhotosExifOrientationTurnsIt_AndItsShownSize()
        {
            Assert.Equal(new[] { 0.0, -30, 15, 0 }, Placement(6));
        }

        [Fact]
        public void ImageOrientationNoneDrawsThePixelsAsStored()
        {
            Assert.Equal(new[] { 30.0, 0, 0, 15 }, Placement(6, "image-orientation:none"));
        }

        /// <summary>The control: a photo stored upright draws as it always did.</summary>
        [Fact]
        public void AnUprightPhotoIsUnchanged()
        {
            Assert.Equal(new[] { 30.0, 0, 0, 15 }, Placement(1));
        }
    }
}
