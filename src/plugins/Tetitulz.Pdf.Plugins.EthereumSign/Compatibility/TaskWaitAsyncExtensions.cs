// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tetitulz.Pdf.Plugins.EthereumSign
{
    /// <summary>
    /// <c>Task&lt;T&gt;.WaitAsync(CancellationToken)</c> for the netstandard2.0 leg.
    /// </summary>
    /// <remarks>
    /// <para>Compiled into the netstandard2.0 build ONLY — see the conditional <c>&lt;Compile&gt;</c>
    /// item in the csproj. The BCL added the instance method in .NET 6, and an instance method always
    /// wins over an extension, so on net10.0 this file would be dead code that merely looked
    /// load-bearing.</para>
    /// <para><b>The semantics are the ones the call sites depend on:</b> it abandons the
    /// <i>wait</i>, not the request. The underlying task keeps running and the endpoint may still
    /// see the call — exactly what <c>BlockchainSignatureProvider</c>'s comment says it is buying,
    /// which is the difference between a caller that has given up waiting 100 s for a hung RPC
    /// endpoint and one that returns immediately.</para>
    /// <para>Cancellation surfaces as <see cref="OperationCanceledException"/> carrying the token,
    /// matching the BCL, so the <c>catch (OperationCanceledException)</c> at each call site behaves
    /// identically on both legs.</para>
    /// </remarks>
    internal static class TaskWaitAsyncExtensions
    {
        public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            // Nothing to race: either the answer is already here, or nobody can cancel.
            if (task.IsCompleted || !cancellationToken.CanBeCanceled)
                return await task.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The registration is disposed on every path, so a long-running task does not leave a
            // callback attached to a token that outlives this call.
            using (cancellationToken.Register(
                state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancelled))
            {
                Task finished = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                if (finished != task)
                    throw new OperationCanceledException(cancellationToken);
            }

            // Awaited rather than returned from Result: this re-throws the task's own exception
            // unwrapped, as the BCL does, instead of an AggregateException.
            return await task.ConfigureAwait(false);
        }
    }
}
