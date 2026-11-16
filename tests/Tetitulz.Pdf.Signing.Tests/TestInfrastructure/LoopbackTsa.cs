// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;

namespace Tetitulz.Pdf.Signing.Tests.TestInfrastructure;

/// <summary>
/// An RFC 3161 timestamp authority on the loopback interface, answering <c>TsaClient</c>'s real HTTP
/// POST — so the signing path's timestamp round trip runs end to end without the network.
/// </summary>
/// <remarks>
/// <para>A raw <see cref="TcpListener"/> rather than <c>HttpListener</c>, which needs a URL
/// reservation on Windows. It speaks just enough HTTP/1.1 for one request per connection: the
/// request line and headers, a <c>100 Continue</c> when the client asks for one, a body of
/// <c>Content-Length</c> bytes, and a reply that closes the connection.</para>
/// <para>The token is minted over the request's own message imprint, so whatever the signer asked to
/// be timestamped is exactly what the token certifies — which is what makes a wrong imprint visible
/// to the verifier rather than to this fixture.</para>
/// </remarks>
internal sealed class LoopbackTsa : IDisposable
{
    private readonly TcpListener _listener;
    private readonly SigningTestCrypto.Issued _tsa;
    private readonly X509Certificate[] _chain;
    private int _requests;
    private long _serial = 5000;

    public LoopbackTsa(SigningTestCrypto.Issued tsa, params X509Certificate[] chain)
    {
        _tsa = tsa;
        _chain = chain;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/tsa";
        _ = Task.Run(ServeAsync);
    }

    /// <summary>The URL to hand the signer.</summary>
    public string Url { get; }

    /// <summary>How many timestamp requests were accepted and answered.</summary>
    /// <remarks>
    /// <b>Counted before the reply is written, which is the only ordering a caller can rely on.</b>
    /// Incrementing afterwards leaves a window in which the client has the whole reply — so
    /// <c>PdfSigner</c> returns and the test asserts — while this server thread has not yet run its
    /// next statement. Nothing makes it run: it is a thread-pool thread, and under a full-solution
    /// run there are far more runnable threads than cores. `Assert.Equal(1, tsa.Requests)` then
    /// reads 0 against a timestamp that was in fact issued, which is a test that fails while the
    /// code under test is correct.
    /// </remarks>
    public int Requests => Volatile.Read(ref _requests);

    public void Dispose() => _listener.Stop();

    private async Task ServeAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            using (client)
            {
                try { Answer(client.GetStream()); }
                catch (IOException) { /* the client went away; nothing to answer */ }
            }
        }
    }

    private void Answer(NetworkStream stream)
    {
        var headers = ReadHeaders(stream);
        if (headers.TryGetValue("expect", out string? expect)
            && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            Write(stream, Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));

        int length = int.Parse(headers["content-length"]);
        var body = new byte[length];
        for (int read = 0; read < length;)
        {
            int n = stream.Read(body, read, length - read);
            if (n <= 0) throw new IOException("the request body ended early");
            read += n;
        }

        var request = new TimeStampRequest(body);

        // Counted here, not after the write: see Requests. A well-formed request has arrived and is
        // about to be answered, and from this point on nothing the client observes can precede the
        // count. A reply that then fails to reach the client throws IOException and is swallowed by
        // ServeAsync — over-reporting in a case no test asserts about, which is the safe direction.
        Interlocked.Increment(ref _requests);

        var generator = new TimeStampTokenGenerator(_tsa.Private, _tsa.Cert, TspAlgorithms.Sha256, "1.2.3.4.5");
        var certificates = new List<X509Certificate> { _tsa.Cert };
        certificates.AddRange(_chain);
        generator.SetCertificates(CollectionUtilities.CreateStore(certificates));

        byte[] reply = new TimeStampResponseGenerator(generator, TspAlgorithms.Allowed)
            .Generate(request, BigInteger.ValueOf(Interlocked.Increment(ref _serial)), DateTime.UtcNow)
            .GetEncoded();

        Write(stream, Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/timestamp-reply\r\n" +
            $"Content-Length: {reply.Length}\r\nConnection: close\r\n\r\n"));
        Write(stream, reply);
    }

    /// <summary>Reads up to the blank line that ends the headers, one byte at a time so no body byte is consumed.</summary>
    private static Dictionary<string, string> ReadHeaders(NetworkStream stream)
    {
        var text = new StringBuilder();
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int b = stream.ReadByte();
            if (b < 0) throw new IOException("the request ended inside its headers");
            text.Append((char)b);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line.Substring(0, colon).Trim().ToLowerInvariant()] = line.Substring(colon + 1).Trim();
        }
        return headers;
    }

    private static void Write(NetworkStream stream, byte[] bytes)
    {
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }
}
