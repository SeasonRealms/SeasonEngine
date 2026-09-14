// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Logo : Panel
{
    internal Sprite2D sprite2D;

    internal bool IsRecording = false;

    internal Logo()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

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
                            DeviceServices.Download.DownloadDel(null, name);
                            DeviceServices.Download.DownloadSave(null, name, bytes, true);
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

        return result;
    }
}
