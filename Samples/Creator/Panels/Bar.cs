
namespace Creator;

internal class Bar : Panel
{
    Shape ground, line;

    internal Sprite2D chat, folder, task, setting;

    Texts chatTexts, folderTexts, taskTexts, settingTexts;

    int buttonSize = 75;

    int textsPos = 75;

    int buttonPadding = 15;

    int paddingLeftRight = 100;

    internal Bar()
    {
        Height = 155;

        ground = new Shape()
        {
            Type = ShapeType.Dot,
            Color = new Season.Basic.Color(215, 215, 215, 255)
        };
        AddControl(ground);

        chat = new Sprite2D()
        {
            Name = "Assets/Chat.png",
            OnClick = () =>
            {
                if (App.Instance.Page.Chat == null)
                {
                    App.Instance.Page.Chat = new Chat();
                    App.Instance.Page.AddPanel(App.Instance.Page.Chat);
                }
                App.Instance.Page.Current = App.Instance.Page.Chat;
            }
        };
        AddControl(chat);
        chatTexts = new Texts()
        {
            Content = "Chat",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {
                chat.OnClick?.Invoke();
            }
        };
        AddControl(chatTexts);

        folder = new Sprite2D()
        {
            Name = "Assets/Folder.png",
            OnClick = () =>
            {
                if (App.Instance.Page.Folder == null)
                {
                    App.Instance.Page.Folder = new Folder();
                    App.Instance.Page.AddPanel(App.Instance.Page.Folder);
                }
                App.Instance.Page.Current = App.Instance.Page.Folder;
            }
        };
        AddControl(folder);
        folderTexts = new Texts()
        {
            Content = "Folder",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {
                chat.OnClick?.Invoke();
            }
        };
        AddControl(folderTexts);

        task = new Sprite2D()
        {
            Name = "Assets/Task.png",
            OnClick = () =>
            {
                if (App.Instance.Page.Task == null)
                {
                    App.Instance.Page.Task = new Task();
                    App.Instance.Page.AddPanel(App.Instance.Page.Task);
                }
                App.Instance.Page.Current = App.Instance.Page.Task;
            }
        };
        AddControl(task);
        taskTexts = new Texts()
        {
            Content = "Task",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {
                task.OnClick?.Invoke();
            }
        };
        AddControl(taskTexts);

        setting = new Sprite2D()
        {
            Name = "Assets/Setting.png",
            OnClick = () =>
            {
                if (App.Instance.Page.Setting == null)
                {
                    App.Instance.Page.Setting = new Setting();
                    App.Instance.Page.AddPanel(App.Instance.Page.Setting);
                }
                App.Instance.Page.Current = App.Instance.Page.Setting;
            }
        };
        AddControl(setting);
        settingTexts = new Texts()
        {
            Content = "Setting",
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {
                setting.OnClick?.Invoke();
            }
        };
        AddControl(settingTexts);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        var posY0 = PosY + buttonPadding;

        var padding = (int)(DeviceServices.BaseApp.ExtendResolution.X - paddingLeftRight * 2 - buttonSize * 4) / 3;

        chat.Color = chatTexts.Color = App.Instance.Page.Current == App.Instance.Page.Chat || chat.MouseOver || chatTexts.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        chat.Update(time, alpha: Alpha, width: buttonSize, height: buttonSize, posX: PosX + paddingLeftRight, posY: posY0);
        chatTexts.Update(time, alpha: Alpha, posY: posY0 + textsPos);
        chatTexts.PosX = chat.PosX + ((chat.Width ?? 0) - (chatTexts.Width ?? 0)) / 2;

        folder.Color = folderTexts.Color = App.Instance.Page.Current == App.Instance.Page.Folder || folder.MouseOver || folderTexts.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        folder.Update(time, alpha: Alpha, width: buttonSize, height: buttonSize, posX: chat.PosX + chat.Width + padding, posY: posY0);
        folderTexts.Update(time, alpha: Alpha, posY: posY0 + textsPos);
        folderTexts.PosX = folder.PosX + ((folder.Width ?? 0) - (folderTexts.Width ?? 0)) / 2;

        task.Color = taskTexts.Color = App.Instance.Page.Current == App.Instance.Page.Task || task.MouseOver || taskTexts.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        task.Update(time, alpha: Alpha, width: buttonSize, height: buttonSize, posX: folder.PosX + folder.Width + padding, posY: posY0);
        taskTexts.Update(time, alpha: Alpha, posY: posY0 + textsPos);
        taskTexts.PosX = task.PosX + ((task.Width ?? 0) - (taskTexts.Width ?? 0)) / 2;

        setting.Color = settingTexts.Color = App.Instance.Page.Current == App.Instance.Page.Setting || setting.MouseOver || settingTexts.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        setting.Update(time, alpha: Alpha, width: buttonSize, height: buttonSize, posX: task.PosX + task.Width + padding, posY: posY0);
        settingTexts.Update(time, alpha: Alpha, posY: posY0 + textsPos);
        settingTexts.PosX = setting.PosX + ((setting.Width ?? 0) - (settingTexts.Width ?? 0)) / 2;

        ground.Update(time, posX: PosX, posY: (int)App.Instance.ExtendResolution.Y - Height, width: Width, height: Height);

        return result;
    }
}
