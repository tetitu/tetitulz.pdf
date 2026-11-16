// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Serializes every test class that reads or writes <c>Base.Encryption.PdfDecryptorHolder.Current</c>.
/// </summary>
/// <remarks>
/// <para>
/// The holder is a <b>process-wide singleton</b> — that is the whole point of it, since it is how
/// Base reaches a decryptor it cannot reference. xUnit runs test classes in parallel across
/// collections, so a class that nulls it to exercise "no decryptor registered" races with any class
/// whose constructor calls <c>PdfEncryptionOperations.Register()</c>: the decryptor reappears
/// mid-test and the assertion sees a <c>PdfException</c> where it expected a reported empty read.
/// </para>
/// <para>
/// This is the same hazard, and the same fix, as <see cref="PluginHostCollection"/>. It was found
/// the way that one was: a test that passed alone and failed inside the full run — here
/// <c>EncryptedFacadeReadOverloadTests.WithNoDecryptorRegistered_ConvertedReadersSayWhyTheyFoundNothing</c>,
/// which threw "Invalid password" on its third reader because a neighbour had re-registered between
/// the first and the third.
/// </para>
/// <para>
/// <b>Thread-filtering is not an option here, unlike the diagnostics sinks.</b> A <c>Last*</c> slot
/// can be <c>[ThreadStatic]</c> so a test only sees its own reports; the decryptor holder is the
/// production lookup, and making it per-thread would change the shipped behaviour to satisfy a
/// test. Serialising the classes that mutate it is the honest cost.
/// </para>
/// <para>Any new test class that calls <c>PdfEncryptionOperations.Register()</c> or assigns
/// <c>PdfDecryptorHolder.Current</c> must join this collection.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DecryptorHolderCollection
{
    public const string Name = "PdfDecryptorHolder (process-wide singleton)";
}
