// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// <see cref="IndependentPdfCheck"/>'s Algorithm 2.B against two AES-256 files other producers wrote,
/// chosen because their two readings of the termination rule ("the round number minus 32") differ: the
/// checker opens each only if it reads the rule as they do. Its first version read it one round short
/// and failed on about one random salt in thirty-eight — the mistake this library itself once made.
/// </summary>
public class IndependentPdfCheckR6Tests
{
    [Theory]
    [InlineData("A", "u21")]
    [InlineData("B", "u15")]
    public void AnotherProducersAes256File_OpensWithItsPassword_AndNotAWrongOne(string producer, string password)
    {
        byte[] pdf = System.Convert.FromBase64String(producer == "A" ? ProducerADivergentPdfBase64 : ProducerBDivergentPdfBase64);
        var outcome = IndependentPdfCheck.Run(pdf, password);
        Assert.True(outcome.Opened, outcome.ToString());
        Assert.True(outcome.StreamsDecrypted >= 1, $"only {outcome.StreamsDecrypted} streams were decrypted");
        Assert.True(outcome.Clean, outcome.ToString());
        Assert.False(IndependentPdfCheck.Run(pdf, password + "x").Opened);
    }

    // The files of HashR6ExternalVectorTests (Encryption.Tests), copied: one page drawing "R6".
    private const string ProducerADivergentPdfBase64 =
        "JVBERi0xLjcKJb/3ov4KMSAwIG9iago8PCAvRXh0ZW5zaW9ucyA8PCAvQURCRSA8PCAvQmFzZVZlcnNpb24gLzEuNyAvRXh0"
        + "ZW5zaW9uTGV2ZWwgOCA+PiA+PiAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0Nv"
        + "dW50IDEgL0tpZHMgWyAzIDAgUiBdIC9UeXBlIC9QYWdlcyA+PgplbmRvYmoKMyAwIG9iago8PCAvQ29udGVudHMgNCAwIFIg"
        + "L01lZGlhQm94IFsgMCAwIDIwMCAyMDAgXSAvUGFyZW50IDIgMCBSIC9SZXNvdXJjZXMgPDwgPj4gL1R5cGUgL1BhZ2UgPj4K"
        + "ZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA2NCAvRmlsdGVyIC9GbGF0ZURlY29kZSA+PgpzdHJlYW0K8wBuZB8OlK0DHit+"
        + "JtcC8uP498t/S50Pq9LNrZ82fHVCHClyIzbWunyleW+Vr2WwpslmvR3weGqR/RWJAPnRlWVuZHN0cmVhbQplbmRvYmoKNSAw"
        + "IG9iago8PCAvQ0YgPDwgL1N0ZENGIDw8IC9BdXRoRXZlbnQgL0RvY09wZW4gL0NGTSAvQUVTVjMgL0xlbmd0aCAzMiA+PiA+"
        + "PiAvRmlsdGVyIC9TdGFuZGFyZCAvTGVuZ3RoIDI1NiAvTyA8YTExMGNhZDM1NzY2NTJjYjg3N2JiZDQ3OTFiMTZhZWM5NGIy"
        + "ZThlZTkwNTA0NzRlMmM1NTYzYzc1Mjg3OTkyMjg3NjI5YzhkNDY5MDMwY2M0NDhmOTFhMWJlMDlkZTQ1PiAvT0UgPGEwYTk2"
        + "Yzk2MTU1MTFjMmZhZDQ0NTQ2ZGZhNjk5OWEyNTc1ZTU3YWJmOGVlYzdmMTNmMjJmZWIzZDI2YWJkNWM+IC9QIC00IC9QZXJt"
        + "cyA8ZWUxN2RhNjA3YTBhZTQ0MmNjODdkMTNiYzE2ZjcxYzA+IC9SIDYgL1N0bUYgL1N0ZENGIC9TdHJGIC9TdGRDRiAvVSA8"
        + "NjFjZmFkYzRmN2U5YjZlNTEyYzI2Y2U3NmJjZmE0YjZlYzA4Y2Y4Y2RjYzUxNDIxNmMzYTA1MjhlMzFlMGFjZGJlNDczZWQ1"
        + "YWMxZDM1N2QzZjYxMWNkMjY1MmEyODNmPiAvVUUgPDJhMzc4OWE0ZmI3ZGU0ZTQzMWI0ZjMyZGNmNjgzNDIyODg4M2IzZDhi"
        + "MTY1YmYyMDA5NzMzMWM2YjI3MjRmNDM+IC9WIDUgPj4KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAw"
        + "MDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMTMwIDAwMDAwIG4gCjAwMDAwMDAxODkgMDAwMDAgbiAKMDAwMDAwMDI5NSAwMDAw"
        + "MCBuIAowMDAwMDAwNDI5IDAwMDAwIG4gCnRyYWlsZXIgPDwgL1Jvb3QgMSAwIFIgL1NpemUgNiAvSUQgWzxhNWQzNWM4NDFh"
        + "OTYwNjcyNDI0YzgxNzU2MzY2MjY4Zj48YTVkMzVjODQxYTk2MDY3MjQyNGM4MTc1NjM2NjI2OGY+XSAvRW5jcnlwdCA1IDAg"
        + "UiA+PgpzdGFydHhyZWYKOTc2CiUlRU9GCg==";

    private const string ProducerBDivergentPdfBase64 =
        "JVBERi0xLjcKJfbk/N8KMSAwIG9iago8PAovVHlwZSAvQ2F0YWxvZwovUGFnZXMgMiAwIFIKPj4KZW5kb2JqCjQgMCBvYmoK"
        + "PDwKL0xlbmd0aCA2NAo+PgpzdHJlYW0NCvotqgw9iyAIcDnzjng5Iw+h2eyAjY+q6zCBeC8OEb+YIYu5e82Rc8tBUnIHEffQ"
        + "Fiu8PfOHoN+M3o5Tcnz9x6INCmVuZHN0cmVhbQplbmRvYmoKNSAwIG9iago8PAovTGVuZ3RoIDEyOAovVHlwZSAvT2JqU3Rt"
        + "Ci9OIDIKL0ZpbHRlciAvRmxhdGVEZWNvZGUKL0ZpcnN0IDkKPj4Kc3RyZWFtDQoVtJevF8q0d6k88heur5YzIsaO/IniwTjz"
        + "F5pVaYS7Bv8UBCYVeUaaFs6mOA4cKCpQOnBoZEul3VhW7zYITdXn267hSRgfWzqjAfF/YE5KWa2XUNKa+SkVK+JmirgMSHkf"
        + "hlHc9XFNxUvno7E8yRgAD0t++UYR18HS7W2szizrTg0KZW5kc3RyZWFtCmVuZG9iago2IDAgb2JqCjw8Ci9GaWx0ZXIgL1N0"
        + "YW5kYXJkCi9WIDUKL1IgNgovTGVuZ3RoIDI1NgovUCAtNAovVSA8QkYyNUI2RTJEMEJFNjc4NUIxNDJFRjI1RDUzNkVENjQw"
        + "MkRFRDI2RTdFQ0M2REM3MTZFMkQ2MTBBMjY4QjM3RTczNDM4Rjg1M0RBNTk3RkE1NjRCNTI4RjVDNTEzOTI2PgovVUUgPDdB"
        + "NjI2OEMyMTNGMjk3QThEOTExQTA4MkUwQzgyMEY3NzNGQ0QxOTlBOTA2OUUyRkZGNzM1RkY2RDAxNDZGRDA+Ci9PIDxEMTFE"
        + "REUxNDEwQTlGRjhBRjY0OEQ4MkEwQ0EzRTBFOTlGRTQ2NjdBNTJEMEQ2MjRCQkZDREU0MTM1RDZGMTdDNTg5RjE4NUE0MTc4"
        + "ODE2NTAwNjQ3MjE0RjgyNDlEMzM+Ci9PRSA8QzI2QTcxMTM5QTUxREQyNjc1NEVBMzY4ODBEOEFGMzIyQUEzQTIzNTI3RDgw"
        + "OUUyRDAwQkYyQTQ2REQxQTE5Qz4KL0NGIDw8Ci9TdGRDRiA8PAovQ0ZNIC9BRVNWMwovTGVuZ3RoIDI1Ngo+Pgo+PgovU3Rt"
        + "RiAvU3RkQ0YKL1N0ckYgL1N0ZENGCi9QZXJtcyA8RjQyMzk5MjU2OUI5MDJGRkZENjRCNUVDQjhERUQ5MTU+Cj4+CmVuZG9i"
        + "ago3IDAgb2JqCjw8Ci9MZW5ndGggMzQKL1Jvb3QgMSAwIFIKL0VuY3J5cHQgNiAwIFIKL0lEIFs8MTEzRjk2RUY0MEQ5OUM0"
        + "OTcxMjUxMTA1OTJDMUUzODQ+IDwxMTNGOTZFRjQwRDk5QzQ5NzEyNTExMDU5MkMxRTM4ND5dCi9UeXBlIC9YUmVmCi9TaXpl"
        + "IDgKL0luZGV4IFswIDddCi9XIFsxIDIgMV0KL0ZpbHRlciAvRmxhdGVEZWNvZGUKPj4Kc3RyZWFtDQp4nGNgYPjPyMDPwMTA"
        + "CsKMjAwODIwMWxgYGWcxAAAjYQKxDQplbmRzdHJlYW0KZW5kb2JqCnN0YXJ0eHJlZgo5MzgKJSVFT0YK";
}
