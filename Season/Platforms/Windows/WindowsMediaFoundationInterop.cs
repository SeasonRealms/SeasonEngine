// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Windows;

internal static class WindowsMediaFoundationInterop
{
    public const int MF_VERSION = 0x00020070;
    public const int MFSTARTUP_FULL = 0;
    public const long OneSecond = 10_000_000;
    public const int MFVideoInterlace_Progressive = 2;

    public static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    public static readonly Guid MFTranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd5f-66f502d8c837");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid MF_MT_FIXED_SIZE_SAMPLES = new("b8ebefaf-b718-4e04-b0a9-116775e3321b");
    public static readonly Guid MF_MT_SAMPLE_SIZE = new("dad3ab78-1990-408b-bce2-eba673dacc10");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-ce3c-43d2-896c-f6b19244d58f");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int MFCreateSinkWriterFromURL(
        string outputUrl,
        IntPtr byteStream,
        IMFAttributes? attributes,
        out IMFSinkWriter sinkWriter);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int MFCreateSourceReaderFromURL(
        string pwszURL,
        IMFAttributes? pAttributes,
        out IMFSourceReader ppSourceReader);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void CoUninitialize();

    public static void CheckHr(int hr, string message)
    {
        if (hr < 0)
            throw new COMException($"{message} (HRESULT=0x{hr:X8})", hr);
    }

    public static long FrameDurationFromFps(int framesPerSecond)
    {
        return OneSecond / framesPerSecond;
    }

    public static int MFSetAttributeSize(IMFAttributes attributes, Guid key, int width, int height)
    {
        return attributes.SetUINT64(key, PackToLong(width, height));
    }

    public static int MFSetAttributeRatio(IMFAttributes attributes, Guid key, int numerator, int denominator)
    {
        return attributes.SetUINT64(key, PackToLong(numerator, denominator));
    }

    static long PackToLong(int high, int low)
    {
        ulong packed = ((ulong)(uint)high << 32) | (uint)low;
        return unchecked((long)packed);
    }
}

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int pType);
    [PreserveSig] int CompareItem([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int GetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int punValue);
    [PreserveSig] int GetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out long punValue);
    [PreserveSig] int GetDouble([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int pcchLength);
    [PreserveSig] int GetString([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, int cchBufSize, out int pcchLength);
    [PreserveSig] int GetAllocatedString([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out int pcchLength);
    [PreserveSig] int GetBlobSize([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int pcbBlobSize);
    [PreserveSig] int GetBlob([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out IntPtr ip, out int pcbSize);
    [PreserveSig] int GetUnknown([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig] int SetItem([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, int unValue);
    [PreserveSig] int SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, long unValue);
    [PreserveSig] int SetDouble([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, double fValue);
    [PreserveSig] int SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
    [PreserveSig] int SetString([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr pBuf, int cbBufSize);
    [PreserveSig] int SetUnknown([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int pcItems);
    [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
}

[ComImport]
[Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType mediaType, out int streamIndex);
    [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType mediaType, IMFAttributes? encodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
    [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
    [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
    [PreserveSig] int NotifyEndOfSegment(int streamIndex);
    [PreserveSig] int Flush(int streamIndex);
    [PreserveSig] int Finalize_();
    [PreserveSig] int GetServiceForStream(int streamIndex, [MarshalAs(UnmanagedType.LPStruct)] Guid guidService, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvObject);
    [PreserveSig] int GetStatistics(int streamIndex, IntPtr stats);
}

[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    [PreserveSig] int GetItem([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int pType);
    [PreserveSig] int CompareItem([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int GetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int punValue);
    [PreserveSig] int GetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out long punValue);
    [PreserveSig] int GetDouble([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int pcchLength);
    [PreserveSig] int GetString([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, int cchBufSize, out int pcchLength);
    [PreserveSig] int GetAllocatedString([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out int pcchLength);
    [PreserveSig] int GetBlobSize([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int pcbBlobSize);
    [PreserveSig] int GetBlob([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out IntPtr ip, out int pcbSize);
    [PreserveSig] int GetUnknown([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig] int SetItem([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, int unValue);
    [PreserveSig] int SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, long unValue);
    [PreserveSig] int SetDouble([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, double fValue);
    [PreserveSig] int SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
    [PreserveSig] int SetString([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr pBuf, int cbBufSize);
    [PreserveSig] int SetUnknown([MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int pcItems);
    [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(int dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out int pdwBufferCount);
    [PreserveSig] int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex(int dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int pcbTotalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
}

[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(int cbCurrentLength);
    [PreserveSig] int GetMaxLength(out int pcbMaxLength);
}

[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(int dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
    [PreserveSig] int SetStreamSelection(int dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
    [PreserveSig] int GetNativeMediaType(int dwStreamIndex, int dwMediaTypeIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int GetCurrentMediaType(int dwStreamIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int SetCurrentMediaType(int dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
    [PreserveSig] int SetCurrentPosition([MarshalAs(UnmanagedType.LPStruct)] Guid guidTimeFormat, IntPtr varPosition);
    [PreserveSig] int ReadSample(
        int dwStreamIndex,
        int dwControlFlags,
        out int pdwActualStreamIndex,
        out int pdwStreamFlags,
        out long pllTimestamp,
        out IMFSample ppSample);
    [PreserveSig] int Flush(int dwStreamIndex);
    [PreserveSig] int GetServiceForStream(int dwStreamIndex, [MarshalAs(UnmanagedType.LPStruct)] Guid guidService, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvObject);
    [PreserveSig] int GetPresentationAttribute(int dwStreamIndex, [MarshalAs(UnmanagedType.LPStruct)] Guid guidAttribute, IntPtr pvarAttribute);
}
