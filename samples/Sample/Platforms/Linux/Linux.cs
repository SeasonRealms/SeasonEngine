
using Season.Platforms.Linux;

namespace Sample;

internal class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        LinuxApp.Run(app);
    }
}
