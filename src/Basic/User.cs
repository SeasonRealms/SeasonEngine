// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public class User
{
    public string Guid { get; set; }    //GUID

    public string Name { get; set; }    //Machine Name

    public string Model { get; set; }   //Machine Model

    public string Version { get; set; } //OS Version

    public string IP { get; set; }      //IPV6

    public string Platform { get; set; }   //Windows Linux MacOS Android iOS

    public string Architecture { get; set; }

    public string Channel { get; set; }    //Microsoft Apple Google

    public string Ver { get; set; }   //App Version

    public string App { get; set; }   //App Name

    public string Language { get; set; }   //Language

    public string ID { get; set; }       //User ID

    public string UserName { get; set; }   //User Name

    public string Token { get; set; }   //User Token

    public string Ping { get; set; }

    public string Status { get; set; }

    public string[] Times { get; set; }  //Create Time, Update Time, Last Time
}

public static class UserManager
{
    //Pay attention to the privacy permission statement
    public static User GetLocalUser()
    {
        var user = new User()
        {
            //Name = System.Environment.UserName,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString()
        };

        if (DeviceServices.Core.Platform is Platform.Linux)
        {

        }
        else
        {
            user.Model = DeviceInfo.Model;
            user.Version = DeviceInfo.VersionString;
        }

        //DeviceInfo.Name  DeviceInfo.Idiom  DeviceInfo.Manufacturer DeviceInfo.Platform DeviceInfo.DeviceType
        //System.Environment.MachineName SystemInformation.UserDomainName
        //Dns.GetHostName()  Dns.GetHostEntry("localhost").HostName

        return user;
    }

}
