
namespace Creator.Entities;

internal class SettingsExtend
{
    internal Season.Basic.Color ButtonColorNormal { get; set; }

    internal Season.Basic.Color ButtonColorHover { get; set; }

    internal static SettingsExtend Load()
    {
        var settingExtend = new Entities.SettingsExtend()
        {
            ButtonColorNormal = Season.Basic.Colors.Black,
            ButtonColorHover = Season.Basic.Colors.DarkRed
        };

        var buttonColorNormal = App.Instance.Settings.KeyValues?.FirstOrDefault(kv => kv.Key is "ButtonColorNormal");
        if (buttonColorNormal != null)
        {
            var color = Season.Basic.Colors.FromName(buttonColorNormal.Value);

            if (color.HasValue)
            {
                settingExtend.ButtonColorNormal = color.Value;
            }
        }

        var buttonColorHover = App.Instance.Settings.KeyValues?.FirstOrDefault(kv => kv.Key is "ButtonColorHover");
        if (buttonColorHover != null)
        {
            var color = Season.Basic.Colors.FromName(buttonColorHover.Value);

            if (color.HasValue)
            {
                settingExtend.ButtonColorHover = color.Value;
            }
        }

        return settingExtend;
    }
}
