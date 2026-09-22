// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

/// <summary>Runs a synchronous host body and all teardown steps once, on its owning thread.</summary>
internal sealed class HostLifetime
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    internal Task Completion => _completion.Task;
    internal bool HasStarted => Volatile.Read(ref _started) != 0;

    internal void Execute(Action body, params Action[] cleanup)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(cleanup);
        foreach (var step in cleanup) ArgumentNullException.ThrowIfNull(step);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A host lifetime can only execute once.");

        var errors = new List<Exception>();
        try { body(); }
        catch (Exception error) { errors.Add(error); }
        foreach (var step in cleanup)
        {
            try { step(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 0) _completion.TrySetResult();
        else _completion.TrySetException(errors.Count == 1 ? errors[0] : new AggregateException(errors));
    }
}
