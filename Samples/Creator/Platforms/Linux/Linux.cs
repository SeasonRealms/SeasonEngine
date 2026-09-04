
using Season.Platforms.Linux;

namespace Creator;

internal class Program
{
    static void Main(string[] args)
    {
        var app = new App();

        LinuxApp.Run(app);
    }
}
