
namespace Sample;

internal class App : BaseApp
{
    internal App()
    {
        Title = "Sample";

        FullScreen = false;

        HideSystemBars = false;

        BasicResolution = new Vector2(1280, 720);
    }

    public override async void Create()
    {
        base.Create();

    }

}
