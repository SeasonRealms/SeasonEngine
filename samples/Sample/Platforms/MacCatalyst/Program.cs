
using Season.Platforms.MacCatalyst;

namespace Sample;

public class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        MacCatalystApp.Run(app);
    }
}
