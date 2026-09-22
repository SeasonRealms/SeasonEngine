// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using NativeCanvas = global::Season.Rendering.Draw2D;
using Affine = System.Numerics.Matrix3x2;
using Microsoft.Xna.Framework.Graphics;

namespace SeasonXNA.Hosting;

/// <summary>
/// Instance-local, synchronous binding to a host-owned recording canvas.
/// Use one context per host; never hold a scope across await or frame boundaries.
/// </summary>
public sealed class DrawContext
{
    private NativeCanvas? _canvas;
    private int _thread;
    private long _generation;
    private readonly HashSet<SpriteBatch> _batches = new();

    public bool IsBound => _canvas is not null;

    public NativeCanvas Canvas
    {
        get
        {
            if (_canvas is null) throw new InvalidOperationException("No Draw2D callback is bound.");
            CheckThread();
            ValidateRecording(_canvas);
            return _canvas;
        }
    }

    public FrameScope Bind(NativeCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (_canvas is not null) throw new InvalidOperationException("Nested binding on the same context is not supported.");
        ValidateRecording(canvas);
        _thread = Environment.CurrentManagedThreadId;
        _generation = checked(_generation + 1);
        _canvas = canvas;
        return new FrameScope(this, _generation);
    }

    private static void ValidateRecording(NativeCanvas canvas)
    {
        // The native API has no public recording flag. This balanced no-op validates
        // the frame thread/state without starting, ending, clearing or submitting it.
        canvas.PushTransform(Affine.Identity);
        canvas.PopTransform();
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("The binding may only be used on its recording thread.");
    }

    private void Release(long generation)
    {
        if (_canvas is null || generation != _generation) return;
        CheckThread();
        bool unfinished = _batches.Count != 0;
        CancelPendingBatches();
        _canvas = null;
        if (unfinished) throw new InvalidOperationException("A SpriteBatch was not ended. Pending sprites were discarded.");
    }

    /// <summary>Call in the host's exception path before leaving the frame scope.</summary>
    public void CancelPendingBatches()
    {
        if (_canvas is null) return;
        CheckThread();
        foreach (var batch in _batches) batch.CancelFromContext();
        _batches.Clear();
    }

    internal void Register(SpriteBatch batch) { _ = Canvas; _batches.Add(batch); }
    internal void Unregister(SpriteBatch batch) { CheckThread(); _batches.Remove(batch); }

    public readonly struct FrameScope : IDisposable
    {
        private readonly DrawContext? _owner;
        private readonly long _generation;
        internal FrameScope(DrawContext owner, long generation) { _owner = owner; _generation = generation; }
        public void Dispose() => _owner?.Release(_generation);
    }
}
