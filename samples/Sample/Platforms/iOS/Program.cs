
using Season.Platforms.Shared.Apple;

namespace Sample;

public class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        AppleApp.Run(app);
    }
}
