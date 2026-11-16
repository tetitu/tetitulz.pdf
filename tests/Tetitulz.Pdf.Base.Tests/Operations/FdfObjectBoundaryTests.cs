// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An FDF file's objects end where their tokens say, not at the next <c>endobj</c> in the bytes.
/// </summary>
/// <remarks>
/// <para>The FDF reader scans for <c>N G obj</c> rather than trusting the cross-reference table,
/// and it had its own copy of that scan: a third spelling of the header rule, and an object's end
/// taken as the first <c>endobj</c> after its header. It now reads objects through the repair tool's
/// scanner, which uses the shared header rule and reads each object's tokens.</para>
/// </remarks>
public sealed class FdfObjectBoundaryTests
{
    private static FormData Read(string fdf)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(fdf));
        return PdfFormDataExchange.ReadFdf(input);
    }

    [Fact]
    public void AFieldValueHoldingTheWordEndobj_IsReadWhole()
    {
        // The value's "endobj" used to end the catalog, whose dictionary then never closed.
        var data = Read("%FDF-1.2\n" +
                        "1 0 obj\n<< /FDF << /Fields [<< /T (note) /V (see endobj here) >>] >> >>\nendobj\n" +
                        "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal("see endobj here", data.Fields.Single().Value);
    }

    [Fact]
    public void AnObjectMissingItsEndobj_DoesNotSwallowTheNextObject()
    {
        // The catalog's bytes used to run on to object 2's endobj, and the scan resumed after it —
        // so the field object was never read.
        var data = Read("%FDF-1.2\n" +
                        "1 0 obj\n<< /FDF << /Fields [2 0 R] >> >>\n" +
                        "2 0 obj\n<< /T (name) /V (Ada) >>\nendobj\n" +
                        "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal("Ada", data.Fields.Single().Value);
    }

    [Fact]
    public void AHeaderAfterADelimiter_IsTheParsersHeader()
    {
        // The shared rule: an object number must start a token. "x2 0 obj" is not object 2, so it
        // cannot stand in for the real object 2 as the later copy.
        var data = Read("%FDF-1.2\n" +
                        "1 0 obj\n<< /FDF << /Fields [2 0 R] >> >>\nendobj\n" +
                        "2 0 obj\n<< /T (name) /V (Ada) >>\nendobj\n" +
                        "x2 0 obj\n<< /T (name) /V (Wrong) >>\nendobj\n" +
                        "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal("Ada", data.Fields.Single().Value);
    }
}
