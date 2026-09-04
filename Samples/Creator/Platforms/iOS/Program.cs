
using Season.Platforms.iOS;

namespace Creator;

internal class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        iOSApp.Run(app);
    }
}
