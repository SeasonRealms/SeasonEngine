
using Season.Platforms.MacCatalyst;

namespace Creator;

internal class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        MacCatalystApp.Run(app);
    }
}
