
namespace Creator;

internal class Page : Panel
{
    internal int Padding = 20;

    internal int InnerHeight;

    Shape ground;

    internal Panel Current;

    internal Chat Chat;

    internal Folder Folder;

    internal Task Task;

    internal Setting Setting;

    internal Page()
    {
        ground = new Shape()
        {
            Alpha = 1,
            Type = ShapeType.Dot,
            Color = new Season.Basic.Color(215, 215, 215, 255)
        };
        AddControl(ground);

        Chat = new Chat()
        {

        };
        AddPanel(Chat);
        Current = Chat;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        ground.Update(time, alpha: Alpha, width: Width, height: Height, posX: PosX, posY: PosY);

        Current?.PosX = PosX;
        Current?.PosY = PosY + App.Instance.Top.Height ?? 0;
        Current?.Width = Width;
        Current?.Height = InnerHeight;

        Chat?.Update(time, alpha: Chat == Current ? Alpha : 0f);
        Folder?.Update(time, alpha: Folder == Current ? Alpha : 0f);
        Task?.Update(time, alpha: Task == Current ? Alpha : 0f);
        Setting?.Update(time, alpha: Setting == Current ? Alpha : 0f);

        return result;
    }
}
