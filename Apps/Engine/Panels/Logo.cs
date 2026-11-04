// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Logo : Panel
{
    internal Sprite2D sprite2D;

    internal bool IsRecording = false;

    /// <summary>Contract 1-9 readout: one small line under the logo, e.g. "FPS 60 / GPU 200M / CPU 500M".</summary>
    Texts statisticsText;

    // Last numbers the line was built from. The statistics are sampled at most twice per second, so the string and the
    // relayout it triggers only happen when a published number actually moved.
    float statisticsFps;
    long statisticsGpuUsed;
    long statisticsCpu;
    bool statisticsGpuKnown;

    internal Logo()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        statisticsText = new Texts()
        {
            Color = Season.Basic.Colors.DarkRed,
            Scale = new Vector2(0.75f, 0.75f)
        };

        AddControl(statisticsText);

        sprite2D = new Sprite2D()
        {
            Name = "Assets/favicon.png",
            OnClick = async () =>
            {
                if (App.Instance.Mode is Mode.Play)
                {
                    if (IsRecording)
                    {
                        var result = await DeviceServices.Recorder.Stop();

                        var name = Path.GetFileName(result.FilePath);

                        if (StorageService.TryGetBytes(StorageService.DirectoryBase, name, out byte[] bytes, out string errMsg))
                        {
#if MACCATALYST
                            // App Store compliance: the engine's MacCatalyst download service writes straight
                            // into the real ~/Downloads folder, which would require the
                            // 'com.apple.security.files.downloads.read-write' entitlement the App Store does
                            // not allow. Hand the recording to the system save panel (UIDocumentPicker
                            // ExportToService) instead: it runs on 'com.apple.security.files.user-selected.read-write'
                            // and lets the user pick the destination folder and file name explicitly. No
                            // DownloadDel counterpart here: the panel settles the destination itself.
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var stream = new MemoryStream(bytes);

                                    await DeviceServices.File.SaveFile(name, stream, CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    // Dismissing the save panel is a normal outcome and must not read as a failure.
                                    if (ex.Message is not "Cancelled")
                                        DeviceServices.BaseApp?.AddLog(LogType.Error, $"{DateTime.UtcNow} [Recorder] save panel for {name} failed: {ex}");
                                }
                            });
#else
                            DeviceServices.Download.DownloadDel(null, name);
                            DeviceServices.Download.DownloadSave(null, name, bytes, true);
#endif
                        }
                    }
                    else
                    {
                        IsRecording = true;

                        await DeviceServices.Recorder.Start(new RecordSessionOptions { FramesPerSecond = 60 });
                    }

                    // result.FilePath   → Output mp4 path
                    // result.Stats      → Frame Drop Honesty Measurement
                }
                else if (App.Instance.Mode is Mode.Edit)
                {
                    if (App.Instance.ViewType is ViewType.Ming)
                    {
                        App.Instance.ViewType = ViewType.Grid;

                        App.Instance.CameraPos = new Vector3(0, 100, -1);

                        App.Instance.CameraTarget = new Vector3(0, 0, 0);

                        sprite2D.SetTexture(@"Assets/Grid.png");
                    }
                    else
                    {
                        App.Instance.ViewType = ViewType.Ming;

                        App.Instance.BindCamera();

                        sprite2D.SetTexture(@"Assets/Ming.png");
                    }
                }
            }
        };
        AddControl(sprite2D);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        int size = 80;

        if (App.Instance.Mode is Mode.Edit)
        {
            sprite2D.Color = sprite2D.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.White;
        }
        else
        {
            sprite2D.Color = Season.Basic.Colors.White;
        }

        if (sprite2D.Update(time, alpha: 1f, posX: App.Instance.ExtendResolution.X - size * 2, posY: size, width: size, height: size))
        {
            result = true;
        }

        result |= UpdateStatistics(time, size);

        return result;
    }

    /// <summary>
    /// Contract 1-9: draws the frame-rate / GPU-memory / CPU-memory line under the logo. The numbers come from the shared
    /// <see cref="GraphicsStatistics"/>, which every backend feeds through the single per-frame entry point, so this panel
    /// needs no platform knowledge of its own. Unknown GPU usage prints as "--" rather than a fabricated zero.
    ///
    /// Right alignment uses the width measured by the previous layout: Texts lays out from a top-left origin and has no
    /// alignment of its own, so the line's right edge is kept on the logo's right edge instead of its left edge being
    /// pinned, which would make the line grow off-screen as the numbers widen.
    /// </summary>
    bool UpdateStatistics(float time, int size)
    {
        var statistics = GraphicsStatistics.Current;

        if (statistics.Fps != statisticsFps
            || statistics.GpuUsedBytes != statisticsGpuUsed
            || statistics.GpuUsageKnown != statisticsGpuKnown
            || statistics.CpuBytes != statisticsCpu)
        {
            statisticsFps = statistics.Fps;
            statisticsGpuUsed = statistics.GpuUsedBytes;
            statisticsGpuKnown = statistics.GpuUsageKnown;
            statisticsCpu = statistics.CpuBytes;

            statisticsText.Content = statistics.ToCompactString();
        }

        float width = statisticsText.Width ?? 0f;

        return statisticsText.Update(time, alpha: 0.75f, posX: App.Instance.ExtendResolution.X - size - width, posY: (float)(size * 2.5));
    }
}
