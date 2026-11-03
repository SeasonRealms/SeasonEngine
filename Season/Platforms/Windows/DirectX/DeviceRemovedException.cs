// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// The GPU device is gone (TDR watchdog or a driver reset), so <b>every</b> later D3D12 call faults instead
/// of returning an HRESULT. Derives from <see cref="InvalidOperationException"/> so existing catch-all
/// handlers keep their behaviour, while the frame loop can catch this single type and shut the renderer
/// down as a known hardware condition instead of reporting an unexplained crash: nothing renders again
/// until the process recreates the device.
/// </summary>
internal sealed class DeviceRemovedException : InvalidOperationException
{
    internal DeviceRemovedException(string message, Exception? inner = null) : base(message, inner) { }
}
