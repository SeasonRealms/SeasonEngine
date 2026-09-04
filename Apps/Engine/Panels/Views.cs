// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Views : Panel
{
    //// Step 5 probe validity classification switch: clicking toggles Settings.RenderQuality.GiProbeValidity
    //// as a runtime knob. It is uploaded to uExtent.w every frame and persisted to Settings.json from Step 6 onward.
    //// Off restores the pre-Step-5 image, where valid is always 1: more stable and clearer, but with weaker light-leak suppression
    //// because probes near walls or objects no longer classify back-face ratios.
    //Sprite2D step5;
    //Texts step5Texts;

    // 1-6 visual target for the built-in plasma compute effect. The texture is written every frame by GPU compute and sampled by name with no extra wiring.
    Sprite2D plasma;

    // 2-1 Step A first validation target for SceneColor thumbnail copy.
    // It uses the AfterScene phase, Target input, and rgba16float storage.
    // The expected look is a one-frame-delayed recursive picture-in-picture, which directly proves the AfterScene timing is correct.
    Sprite2D sceneCopy;

    // 2-1 Step B debug view of an intermediate bloom mip.
    // down1 is the bright-pass image at one-quarter resolution, which confirms the downsample chain is active.
    // Final compositing is verified directly on the fullscreen output through the FinalBlit tonemap+bloom variant.
    Sprite2D bloomMip;

    // 2-2 Step A first validation target for linearized SceneDepth.
    // This verifies the compute-input depth path. The expected view is a grayscale image
    // where nearby geometry is dark, distant geometry is white, and empty sky is pure white.
    Sprite2D depthView;

    // 2-2 Step B visualization of GTAO output. The red channel stores visibility,
    // so creases and contact areas should darken. Final compositing is checked directly
    // on the fullscreen output through the FinalBlit or uber AO variant.
    Sprite2D aoView;

    // 2-3 Step A first validation target for motion-vector visualization:
    // direction maps to hue and magnitude maps to brightness.
    // Expected appearance: full black when static, a uniform fullscreen hue while the camera moves,
    // and color only on independently moving objects.
    Sprite2D velocityView;

    // 2-3 Step B visualization of the TAA resolve output.
    // It shows one side of the taa0 ping-pong buffer, so it refreshes every other frame.
    // This control is created only when TaaEffect registers successfully, so the presence or absence
    // of the thumbnail directly indicates whether TAA is active.
    // The final composited result is viewed fullscreen because SceneColorOverride already feeds it
    // into bloom and the final present path.
    Sprite2D taaView;

    // 1-8 visualization target for 3D SDF slices, which is the only acceptance image for the expanded compute 3D resource path.
    // It shows the 256x256 output of the 3D-to-2D slice kernel. The 3D texture lives in a platform-specific dictionary
    // and cannot be consumed directly by Sprite2D.
    // Validation criteria are documented in Sdf3DViewEffect: smooth trilinear slices, continuous sweeping along W with clamped end faces,
    // 64 pulsing bars at the bottom from a 64x1x1 read-write buffer, and the cyan-to-magenta palette through the >128-byte constant path.
    Sprite2D sdf3dView;

    // 2-4 Step 1 debug slice of the proxy SDF volume, output by DdgiEffect's debug kernel.
    // Validation criteria are in the DdgiEffect header: warm solid blocks in the main area mean inside proxies,
    // cool contour lines appear every 1 world meter, the pattern moves oppositely to camera translation without jitter
    // because of voxel snapping, and the number of lit cells at the bottom equals the proxy count for the frame.
    // Cell color matches each proxy's Control.GiAlbedo; pure black means collection or upload failed.
    Sprite2D ddgiSdfView;

    public Views()
    {
        RenderDomain = Season.Controls.RenderDomain.Overlay;

        plasma = new Sprite2D()
        {
            Name = Season.Rendering.Effects.PlasmaEffect.TextureName,
            Color = Season.Basic.Colors.White,
            PosX = 20,
            PosY = 300,
            Width = (int)Season.Rendering.Effects.PlasmaEffect.Size,
            Height = (int)Season.Rendering.Effects.PlasmaEffect.Size,
            Alpha = 1f
        };
        AddControl(plasma);

        sceneCopy = new Sprite2D()
        {
            Name = Season.Rendering.Effects.SceneColorCopyEffect.TextureName,
            Color = Season.Basic.Colors.White,
            PosX = 20,
            PosY = 580,
            Width = (int)Season.Rendering.Effects.SceneColorCopyEffect.Width,
            Height = (int)Season.Rendering.Effects.SceneColorCopyEffect.Height,
            Alpha = 1f
        };
        AddControl(sceneCopy);

        taaView = new Sprite2D()
        {
            Name = Season.Rendering.Effects.TaaEffect.TextureName0,
            Color = Season.Basic.Colors.White,
            PosX = 1300,
            PosY = 430,
            Width = 240,
            Height = 135,
            Alpha = 1f
        };
        AddControl(taaView);

        bloomMip = new Sprite2D()
        {
            Name = Season.Rendering.Effects.BloomEffect.TextureNamePrefix + "down1",
            Color = Season.Basic.Colors.White,
            PosX = 520,
            PosY = 580,
            Width = 240,
            Height = 135,
            Alpha = 1f
        };
        AddControl(bloomMip);

        depthView = new Sprite2D()
        {
            Name = Season.Rendering.Effects.DepthViewEffect.TextureName,
            Color = Season.Basic.Colors.White,
            PosX = 780,
            PosY = 580,
            Width = 240,
            Height = 135,
            Alpha = 1f
        };
        AddControl(depthView);

        aoView = new Sprite2D()
        {
            Name = Season.Rendering.Effects.GtaoEffect.TextureName,
            Color = Season.Basic.Colors.White,
            PosX = 1040,
            PosY = 580,
            Width = 240,
            Height = 135,
            Alpha = 1f
        };
        AddControl(aoView);

        velocityView = new Sprite2D()
        {
            Name = Season.Rendering.Effects.VelocityViewEffect.TextureName,
            Color = Season.Basic.Colors.White,
            PosX = 1300,
            PosY = 580,
            Width = 240,
            Height = 135,
            Alpha = 1f
        };
        AddControl(velocityView);

        sdf3dView = new Sprite2D()
        {
            Name = Season.Rendering.Effects.Sdf3DViewEffect.TextureName,
            Color = Season.Basic.Colors.White,
            PosX = 300,
            PosY = 300,
            Width = (int)Season.Rendering.Effects.Sdf3DViewEffect.Size,
            Height = (int)Season.Rendering.Effects.Sdf3DViewEffect.Size,
            Alpha = 1f
        };
        AddControl(sdf3dView);

        ddgiSdfView = new Sprite2D()
        {
            Name = Season.Rendering.Effects.DdgiEffect.DebugTextureName,
            Color = Season.Basic.Colors.White,
            PosX = 600,
            PosY = 300,
            Width = (int)Season.Rendering.Effects.DdgiEffect.DebugSize,
            Height = (int)Season.Rendering.Effects.DdgiEffect.DebugSize,
            Alpha = 1f
        };
        AddControl(ddgiSdfView);

        //        // picture = new Sprite2D()
        //        // {
        //        //     Name = "Assets/Sun.png",
        //        //     Color = Season.Basic.Colors.White
        //        // };
        //        // AddControl(picture);

        //        texts = new Texts()
        //        {
        //            Color = Season.Basic.Colors.Black,
        //            Scale = Vector2.One * 0.7f,
        //            WidthRequest = null,
        //            Alpha = 1f
        //        };
        //        AddControl(texts);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        plasma?.Update(time, alpha: Alpha);

        sceneCopy?.Update(time, alpha: Alpha);

        bloomMip?.Update(time, alpha: Alpha);

        depthView?.Update(time, alpha: Alpha);

        aoView?.Update(time, alpha: Alpha);

        velocityView?.Update(time, alpha: Alpha);

        taaView?.Update(time, alpha: Alpha);

        sdf3dView?.Update(time, alpha: Alpha);

        ddgiSdfView?.Update(time, alpha: Alpha);

        //        // Video playback: consume frames pushed by VideoPlayer.
        //        // var frame = Interlocked.Exchange(ref _pendingVideoFrame, null);
        //        // if (frame != null)
        //        // {
        //        //     picture.TextureOverride = TextureUpdateSource.FromImage(frame);
        //        //     // The engine disposes frame after GPU upload.
        //        // }

        //        // picture?.Update(time: time, alpha: 1f, width: 500, height: 400, posX: 30, posY: 120);

        //        texts?.PosX = 550;
        //        texts?.PosY = 120;
        //        texts?.WidthRequest = (int)(App.Instance.ExtendResolution.X - texts.PosX - 30);
        //        texts?.HeightRequest = (int)(App.Instance.ExtendResolution.Y - texts.PosY - 30);
        //        texts?.Update(time, alpha: 1f);

        return result;
    }
}
