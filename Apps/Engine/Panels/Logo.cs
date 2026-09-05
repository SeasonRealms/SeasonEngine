// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Logo : Panel
{
    internal Sprite2D sprite2D;

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
                    if (CelestialLighting.DayNightSpeed == 0f)
                    {
                        CelestialLighting.DayNightSpeed = 0.05f;
                    }
                    else
                    {
                        CelestialLighting.DayNightSpeed = 0f;
                    }
                    //// 开始
                    //await DeviceServices.Recorder.Start(new RecordSessionOptions { FramesPerSecond = 30 });

                    //await Task.Delay(TimeSpan.FromSeconds(10));

                    //// 结束
                    //var result = await DeviceServices.Recorder.Stop();
                    //// result.FilePath   → 输出的 mp4 路径
                    //// result.Stats      → 掉帧诚实度量（见下）
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
