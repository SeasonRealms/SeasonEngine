// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

internal unsafe class SwapChain : IDisposable
{
    public IDXGISwapChain3* NativeSwapChain { get; private set; }

    public uint FrameCount { get; }

    public Format BackBufferFormat { get; }

    public uint CurrentBackBufferIndex => NativeSwapChain->GetCurrentBackBufferIndex();

    /// <summary>
    /// DXGI Frame Latency Waitable Object. Wait on this handle before each frame starts rendering.
    /// DXGI only signals it after the compositor releases the back buffer, removing the CPU/compositor race.
    /// </summary>
    public IntPtr FrameLatencyWaitableObject { get; private set; }

    private readonly IDXGIFactory4* _factory;

    private readonly ID3D12CommandQueue* _commandQueue;

    private ID3D12Resource*[] _backBuffers;

    /// <summary>Flags used when creating the SwapChain. Resize must use the same flags.</summary>
    private const uint SwapChainCreationFlags = (uint)SwapChainFlag.FrameLatencyWaitableObject;

    public SwapChain(IDXGIFactory4* factory, ID3D12CommandQueue* commandQueue, uint frameCount, Format backBufferFormat)
    {
        _factory = factory;
        _commandQueue = commandQueue;
        FrameCount = frameCount;
        BackBufferFormat = backBufferFormat;
        _backBuffers = new ID3D12Resource*[frameCount];
    }

    public void CreateForSwapChainPanel(object swapChainPanel, int width, int height)
    {
        var panel = swapChainPanel as Microsoft.UI.Xaml.Controls.SwapChainPanel;
        if (panel == null) throw new ArgumentException("Invalid SwapChainPanel");

        IntPtr pUnk = Marshal.GetIUnknownForObject(panel);
        IUnknown* pUnknown = (IUnknown*)pUnk.ToPointer();

        using ComObject comObject = ComObject.FromPtr(pUnknown);
        var iid = typeof(ISwapChainPanelNative).GUID;
        var result = comObject.QueryInterface(ref iid, out ComObject? nativePanel);

        if (result != 0) throw new Exception($"Failed to get ISwapChainPanelNative: {result}");

        using (nativePanel)
        {
            ISwapChainPanelNative swapChainPanelNative = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown((IntPtr)nativePanel.Handle);

            var swapChainDesc = new SwapChainDesc1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = BackBufferFormat,
                Stereo = false,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                BufferUsage = DXGI.UsageRenderTargetOutput,
                BufferCount = FrameCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Unspecified, // CreateSwapChainForComposition does not allow Ignore. Unspecified means DWM ignores the alpha channel and is the safest compatible choice.
                Flags = SwapChainCreationFlags
            };

            IDXGISwapChain1* swapChain1;
            int hr = _factory->CreateSwapChainForComposition((IUnknown*)_commandQueue, &swapChainDesc, null, &swapChain1);
            if (hr != 0 || swapChain1 == null)
                throw new Exception($"CreateSwapChainForComposition failed: HRESULT=0x{hr:X8}");

            swapChainPanelNative.SetSwapChain((IntPtr)swapChain1);

            // Upgrade to IDXGISwapChain3.
            IDXGISwapChain3* swapChain3;
            Guid iidSwapChain3 = typeof(IDXGISwapChain3).GUID;
            swapChain1->QueryInterface(&iidSwapChain3, (void**)&swapChain3);
            Marshal.Release((IntPtr)swapChain1);

            NativeSwapChain = swapChain3;

            // Limit the CPU to at most 2 frames ahead of the compositor, including the current frame.
            swapChain3->SetMaximumFrameLatency(2);

            // Get the waitable handle. It is signaled only after the compositor finishes presenting the back buffer.
            FrameLatencyWaitableObject = (IntPtr)swapChain3->GetFrameLatencyWaitableObject();
        }

        // Acquire back-buffer references.
        AcquireBackBuffers();
    }

    public void Resize(int width, int height)
    {
        if (NativeSwapChain == null) return;

        // Release the old back-buffer references.
        ReleaseBackBuffers();

        // DXGI strictly requires that if the SwapChain was created with the
        // FrameLatencyWaitableObject flag, the old waitable handle must be closed before ResizeBuffers,
        // and a new handle must be acquired after resize succeeds.
        // Otherwise the DXGI debug layer can trigger an SEHException.
        if (FrameLatencyWaitableObject != IntPtr.Zero)
        {
            _ = CloseHandle(FrameLatencyWaitableObject);
            FrameLatencyWaitableObject = IntPtr.Zero;
        }

        var result = NativeSwapChain->ResizeBuffers(FrameCount, (uint)width, (uint)height, BackBufferFormat, SwapChainCreationFlags);
        if (result != 0) throw new Exception($"Failed to resize swap chain buffers: {result}");

        // Reacquire back-buffer references.
        AcquireBackBuffers();

        // ResizeBuffers with Flags=FrameLatencyWaitableObject creates a new waitable handle.
        // It must be reacquired so the frame-latency synchronization in BeforeRender keeps working correctly.
        FrameLatencyWaitableObject = (IntPtr)NativeSwapChain->GetFrameLatencyWaitableObject();
        if (FrameLatencyWaitableObject != IntPtr.Zero)
        {
            NativeSwapChain->SetMaximumFrameLatency(2);
        }
    }

    public void Present(uint syncInterval = 1, uint flags = 0)
    {
        NativeSwapChain->Present(syncInterval, flags);
    }

    public ID3D12Resource* GetBackBuffer(uint index)
    {
        if (index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _backBuffers[index];
    }

    private void AcquireBackBuffers()
    {
        var iid = ID3D12Resource.Guid;
        for (uint i = 0; i < FrameCount; i++)
        {
            ID3D12Resource* buffer;
            var result = NativeSwapChain->GetBuffer(i, &iid, (void**)&buffer);
            if (result != 0) throw new Exception($"Failed to get back buffer {i}: {result}");
            _backBuffers[i] = buffer;
        }
    }

    private void ReleaseBackBuffers()
    {
        for (int i = 0; i < _backBuffers.Length; i++)
        {
            if (_backBuffers[i] != null)
            {
                _backBuffers[i]->Release();
                _backBuffers[i] = null;
            }
        }
    }

    public void Dispose()
    {
        ReleaseBackBuffers();

        if (FrameLatencyWaitableObject != IntPtr.Zero)
        {
            _ = CloseHandle(FrameLatencyWaitableObject);
            FrameLatencyWaitableObject = IntPtr.Zero;
        }

        if (NativeSwapChain != null)
        {
            NativeSwapChain->Release();
            NativeSwapChain = null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

// Define the ISwapChainPanelNative COM interface.
[ComImport]
//[Guid("63DAD0F2-9CA2-4B1E-9AB7-2E6177A1F557")]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISwapChainPanelNative
{
    // The only method: set the swap chain.
    void SetSwapChain(IntPtr swapChain);
}
