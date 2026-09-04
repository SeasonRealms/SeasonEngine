
namespace Creator;

internal class Top : Panel
{
    Shape ground;

    Shape line;

    internal Top()
    {
        ground = new Shape()
        {
            Alpha = 1,
            Type = ShapeType.Dot,
            Color = new Season.Basic.Color(215, 215, 215, 255)
        };
        AddControl(ground);

        line = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(line);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        ground.Update(time, alpha: Alpha, width: Width, height: Height, posX: PosX, posY: PosY);

        line.Update(time, alpha: Alpha, posX: PosX, posY: PosY + Height - 2, width: Width, height: 2);

        return result;
    }
}
