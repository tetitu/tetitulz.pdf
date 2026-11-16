// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing
{
    /// <summary>
    /// Axial and radial shadings on <see cref="PdfContentStreamBuilder"/>.
    ///
    /// <para>
    /// The colour ramp is a function, not a list, so the interesting behaviour is the shape of what
    /// gets written: two stops become one Type 2 exponential, more become a Type 3 stitching
    /// function over one exponential per interval, and stops that do not reach the ends of the
    /// domain are padded so the gradient starts and finishes flat instead of interpolating from
    /// nowhere.
    /// </para>
    /// </summary>
    public class ContentStreamBuilderShadingTests
    {
        private static List<PdfContentStreamBuilder.ShadingStop> Stops(params (double pos, double r, double g, double b)[] s)
        {
            var list = new List<PdfContentStreamBuilder.ShadingStop>();
            foreach (var (pos, r, g, b) in s)
                list.Add(new PdfContentStreamBuilder.ShadingStop(pos, r, g, b));
            return list;
        }

        private static PdfDictionary ShadingOf(PdfContentStreamBuilder b, string name)
        {
            int next = 1;
            var (_, resources, side) = b.BuildObjects(ref next);
            var dict = resources.Get(PdfName.Shading) as PdfDictionary;
            Assert.NotNull(dict);

            var reference = dict.Get(new PdfName(name)) as PdfReference;
            Assert.NotNull(reference);

            foreach (var (objNum, obj) in side)
                if (objNum == reference.ReferenceObjectNumber) return Assert.IsType<PdfDictionary>(obj);

            Assert.Fail("the shading's indirect object was not written");
            return null;
        }

        [Fact]
        public void AxialShading_IsType2WithTheGivenAxis()
        {
            var b = new PdfContentStreamBuilder();
            string name = b.AddAxialShading(10, 20, 110, 20, Stops((0, 1, 0, 0), (1, 0, 0, 1)));

            var shading = ShadingOf(b, name);
            Assert.Equal(2, ((PdfNumber)shading.Get(PdfName.ShadingType)).IntValue);

            var coords = (PdfArray)shading.Get(PdfName.Coords);
            Assert.Equal(4, coords.Count);
            Assert.Equal(10.0, ((PdfNumber)coords[0]).DoubleValue, 3);
            Assert.Equal(110.0, ((PdfNumber)coords[2]).DoubleValue, 3);
        }

        [Fact]
        public void RadialShading_IsType3WithConcentricCircles()
        {
            var b = new PdfContentStreamBuilder();
            string name = b.AddRadialShading(50, 60, 25, Stops((0, 1, 1, 1), (1, 0, 0, 0)));

            var shading = ShadingOf(b, name);
            Assert.Equal(3, ((PdfNumber)shading.Get(PdfName.ShadingType)).IntValue);

            var coords = (PdfArray)shading.Get(PdfName.Coords);
            Assert.Equal(6, coords.Count);
            Assert.Equal(0.0, ((PdfNumber)coords[2]).DoubleValue, 3);    // inner radius
            Assert.Equal(25.0, ((PdfNumber)coords[5]).DoubleValue, 3);   // outer radius
        }

        [Fact]
        public void TwoStops_BecomeASingleExponentialFunction()
        {
            var b = new PdfContentStreamBuilder();
            string name = b.AddAxialShading(0, 0, 100, 0, Stops((0, 1, 0, 0), (1, 0, 0, 1)));

            var fn = (PdfDictionary)ShadingOf(b, name).Get(PdfName.Function);
            Assert.Equal(2, ((PdfNumber)fn.Get(PdfName.FunctionType)).IntValue);

            var c0 = (PdfArray)fn.Get(PdfName.C0);
            var c1 = (PdfArray)fn.Get(PdfName.C1);
            Assert.Equal(1.0, ((PdfNumber)c0[0]).DoubleValue, 3);   // red at the start
            Assert.Equal(1.0, ((PdfNumber)c1[2]).DoubleValue, 3);   // blue at the end
        }

        [Fact]
        public void ThreeStops_BecomeAStitchingFunctionOverTwoIntervals()
        {
            var b = new PdfContentStreamBuilder();
            string name = b.AddAxialShading(0, 0, 100, 0,
                Stops((0, 1, 0, 0), (0.25, 0, 1, 0), (1, 0, 0, 1)));

            var fn = (PdfDictionary)ShadingOf(b, name).Get(PdfName.Function);
            Assert.Equal(3, ((PdfNumber)fn.Get(PdfName.FunctionType)).IntValue);

            var subFunctions = (PdfArray)fn.Get(PdfName.Function);
            Assert.Equal(2, subFunctions.Count);

            // One interior boundary, at the middle stop's own position — an implementation that
            // spaced the stops evenly would put it at 0.5.
            var bounds = (PdfArray)fn.Get(PdfName.Bounds);
            Assert.Single(bounds);
            Assert.Equal(0.25, ((PdfNumber)bounds[0]).DoubleValue, 3);
        }

        [Fact]
        public void StopsThatDoNotReachTheEnds_ArePaddedSoTheRampStartsAndFinishesFlat()
        {
            // Stops at 30% and 70% only: before and after, the gradient must hold the end colours
            // rather than interpolate from an undefined value.
            var b = new PdfContentStreamBuilder();
            string name = b.AddAxialShading(0, 0, 100, 0, Stops((0.3, 1, 0, 0), (0.7, 0, 0, 1)));

            var fn = (PdfDictionary)ShadingOf(b, name).Get(PdfName.Function);
            Assert.Equal(3, ((PdfNumber)fn.Get(PdfName.FunctionType)).IntValue);

            var bounds = (PdfArray)fn.Get(PdfName.Bounds);
            Assert.Equal(2, bounds.Count);
            Assert.Equal(0.3, ((PdfNumber)bounds[0]).DoubleValue, 3);
            Assert.Equal(0.7, ((PdfNumber)bounds[1]).DoubleValue, 3);
        }

        [Fact]
        public void StopsGivenOutOfOrder_AreSorted()
        {
            var b = new PdfContentStreamBuilder();
            string name = b.AddAxialShading(0, 0, 100, 0,
                Stops((1, 0, 0, 1), (0, 1, 0, 0), (0.5, 0, 1, 0)));

            var fn = (PdfDictionary)ShadingOf(b, name).Get(PdfName.Function);
            var first = (PdfDictionary)((PdfArray)fn.Get(PdfName.Function))[0];
            var c0 = (PdfArray)first.Get(PdfName.C0);

            Assert.Equal(1.0, ((PdfNumber)c0[0]).DoubleValue, 3);   // the 0.0 stop is red
        }

        [Fact]
        public void IdenticalGradients_ShareOneShadingResource()
        {
            var b = new PdfContentStreamBuilder();
            string first = b.AddAxialShading(0, 0, 100, 0, Stops((0, 1, 0, 0), (1, 0, 0, 1)));
            string second = b.AddAxialShading(0, 0, 100, 0, Stops((0, 1, 0, 0), (1, 0, 0, 1)));

            Assert.Equal(first, second);
        }

        [Fact]
        public void GradientsDifferingOnlyInTheirStops_AreDistinct()
        {
            var b = new PdfContentStreamBuilder();
            string red = b.AddAxialShading(0, 0, 100, 0, Stops((0, 1, 0, 0), (1, 0, 0, 1)));
            string green = b.AddAxialShading(0, 0, 100, 0, Stops((0, 0, 1, 0), (1, 0, 0, 1)));

            Assert.NotEqual(red, green);
        }

        [Fact]
        public void ShadingFill_EmitsTheShOperatorForTheNamedResource()
        {
            var b = new PdfContentStreamBuilder();
            string name = b.AddAxialShading(0, 0, 100, 0, Stops((0, 1, 0, 0), (1, 0, 0, 1)));
            b.ShadingFill(name);

            string ops = Encoding.GetEncoding("iso-8859-1").GetString(b.Build());
            Assert.Contains("/" + name + " sh", ops);
        }

        [Fact]
        public void EmptyStopList_RegistersNothing()
        {
            var b = new PdfContentStreamBuilder();

            Assert.Null(b.AddAxialShading(0, 0, 100, 0, new List<PdfContentStreamBuilder.ShadingStop>()));
            Assert.Null(b.AddRadialShading(0, 0, 10, null));
        }

        [Fact]
        public void NoShading_WritesNoShadingResourceEntry()
        {
            // The control: the resource dictionary must not gain a /Shading key for a page that
            // never asked for one.
            var b = new PdfContentStreamBuilder();
            b.AddStandardFont("Helvetica");

            int next = 1;
            var (_, resources, _) = b.BuildObjects(ref next);
            Assert.Null(resources.Get(PdfName.Shading));
        }
    }
}
