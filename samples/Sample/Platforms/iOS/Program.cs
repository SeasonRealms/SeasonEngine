
using Season.Platforms.iOS;

namespace Sample;

public class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        iOSApp.Run(app);
    }
}
