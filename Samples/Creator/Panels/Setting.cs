
namespace Creator;

internal class Setting : Panel
{
    Texts desc, desc2;

    internal Setting()
    {
        desc = new Texts()
        {
            Content = "AI-generated content",
            Color = Season.Basic.Colors.Gray,
            Scale = Vector2.One * 2f
        };
        AddControl(desc);

        desc2 = new Texts()
        {
            Content = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 +-*/!@#$%^&~",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f
        };
        AddControl(desc2);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (Alpha > 0)
        {
            desc.Content = "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789" + DeviceServices.BaseApp.ExtendResolution.X + ":" + DeviceServices.BaseApp.ExtendResolution.Y + " " + DeviceServices.BaseApp.DeviceResolution.X + ":" + DeviceServices.BaseApp.DeviceResolution.Y + " " + DeviceServices.BaseApp.Scale;
            desc.WidthRequest = (int)(Width ?? 0);
            desc.Update(time, alpha: Alpha, posX: PosX, posY: PosY + 120);

            desc2.WidthRequest = (int)(Width ?? 0);
            desc2.Update(time, alpha: Alpha, posX: PosX + 20, posY: 350);
        }

        return result;
    }
}
