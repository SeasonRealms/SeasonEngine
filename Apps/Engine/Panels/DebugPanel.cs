// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class DebugPanel : Panel
{
    internal Texts message;

    internal DebugPanel()
        :base()
    {
        message = new Texts()
        {
            Color = Season.Basic.Colors.Black
        };
        AddControl(message);

    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        message.Content = $"JitterScale:{App.Instance.Settings.RenderQuality.JitterScale.ToString()};TaaSharpness:{App.Instance.Settings.RenderQuality.TaaSharpness.ToString()};TaaVarianceClipGamma:{App.Instance.Settings.RenderQuality.TaaVarianceClipGamma.ToString()}";
        message?.Update(time, posX: 200, posY: 100);

        return result;
    }
}
