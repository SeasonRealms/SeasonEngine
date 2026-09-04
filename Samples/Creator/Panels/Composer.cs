
namespace Creator;

internal class Composer : Panel
{
    Shape background;

    internal Composer()
    {
        background = new Shape()
        {
            Type = ShapeType.RoundRect,
            Color = new Season.Basic.Color(255, 255, 255, 255)
        };
        AddControl(background);

    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        background.Update(time, alpha: Alpha, posX: PosX, posY: PosY, width: Width, height: Height);


        return result;
    }
}
