
namespace Creator;

internal class Commands : Panel
{
    int startX = 28;

    int startY = 32;

    int Padding = 55;

    int buttonSize = 50;

    Sprite2D split, capture, create, folder, setting;

    Input folderInput;

    SimplePicker simplePicker;

    internal Commands()
    {
        split = new Sprite2D()
        {
            Name = "Assets/Split.png",
            OnClick = () =>
            {
                if (App.Instance.Animation is Animation.HorizontalLeft)
                {
                    App.Instance.Animation = Animation.LeftFadeOut;
                    App.Instance.AnimationElapsed = 0f;
                }
                else if (App.Instance.Animation is Animation.HorizontalNoLeft)
                {
                    App.Instance.Animation = Animation.LeftFadeIn;
                    App.Instance.AnimationElapsed = 0f;
                }
                else if (App.Instance.Animation is Animation.LeftFadeIn)
                {

                }
                else if (App.Instance.Animation is Animation.LeftFadeOut)
                {

                }
            }
        };
        AddControl(split);

        capture = new Sprite2D()
        {
            Name = "Assets/Capture.png",
            OnClick = async () =>
            {
                var image = await DeviceServices.Record.CaptureApp();
                //var image = await DeviceServices.Record.CaptureScreen();

                var bytes = DeviceServices.Image.SaveImage(image, Season.Basic.ImageFormat.Png);

                StorageService.SaveFile(StorageService.DirectoryBase, $"Capture-{DateTime.Now.ToString("yyyyMMddHHmmss")}.png", bytes);

            }
        };
        AddControl(capture);

        create = new Sprite2D()
        {
            Name = "Assets/Create.png"
        };
        AddControl(create);

        folder = new Sprite2D()
        {
            Name = "Assets/Folder.png",
            OnClick = () =>
            {
                var sources = App.Instance.Creator.Folders.Select(fo => new Season.Entities.EData()
                {
                    Key = fo.ID.ToString(),
                    Title = fo.Title,
                    Image = fo.Image,
                    Desc = fo.Desc
                }).NullToEmptyList();

                sources.Reverse();

                var result = new List<Season.Entities.EData> { };

                simplePicker = new Season.Panels.SimplePicker(sources, result)
                {
                    OnSelect = () =>
                    {
                        folderInput.Text = simplePicker.Results?.Count > 0 ? simplePicker.Results[0].Title : "";

                        simplePicker.OnClose?.Invoke();
                    },
                    OnClose = () =>
                    {
                        Panels.Remove(simplePicker);
                        simplePicker.Dispose();
                        simplePicker = null;
                    }
                };

                AddPanel(simplePicker);
            }
        };
        AddControl(folder);

        folderInput = new Input()
        {
            Abbreviate = false,
            ShowClear = true,
            OnAction = () =>
            {
                folder.OnClick?.Invoke();
            },
            OnClear = () =>
            {
                folderInput.Text = null;
            }
        };
        AddPanel(folderInput);

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
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (simplePicker != null)
        {
            if (simplePicker.Update(time, alpha: alpha, posX: (int)folder.PosX, posY: (int)(folder.PosY + folder.Height) + 20))
            {
                return true;
            }
        }

        if (App.Instance.Animation is Animation.Vertical)
        {
            split.Alpha = 0f;
            split.PosX = 0;
            capture.PosX = PosX + startX;
            create.PosX = capture.PosX + 125;
            var folderPos = (int)App.Instance.ExtendResolution.X - Padding - (folderInput.Width ?? 0) - 80;

            folder.PosX = folderPos;
            setting.Alpha = 0f;
        }
        else
        {
            split.Alpha = 1f;
            split.PosX = PosX + startX;
            capture.PosX = split.PosX + 125;
            create.PosX = capture.PosX + 125;
            folder.PosX = (App.Instance.Left.Width ?? 0) + startX;
            setting.Alpha = 1f;
            setting.PosX = (int)(App.Instance.ExtendResolution.X - Padding - (setting.Width ?? 0));
        }

        split.Color = split.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        split.Update(time, posY: PosY + startY, width: buttonSize, height: buttonSize);

        capture.Color = capture.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        capture.Update(time, alpha: Alpha, posY: split.PosY, width: buttonSize, height: buttonSize);

        create.Color = create.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        create.Update(time, alpha: Alpha, posY: split.PosY, width: buttonSize, height: buttonSize);

        folder.Alpha = folderInput.Alpha = App.Instance.Page.Current == App.Instance.Page.Chat ? Alpha : 0f;
        folder.Color = folderInput.Color = folder.MouseOver || folderInput.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        if (folder.Update(time, posY: split.PosY, width: buttonSize, height: buttonSize))
        {
            return true;
        }
        
        folderInput.WidthMin = 100;
        folderInput.Remove.Color = folderInput.Remove.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        if (folderInput.Update(time, posX: (int)folder.PosX + 80, posY: (int)(folder.PosY - (folderInput.Height - folder.Height) / 2), height: (int)folder.Height))
        {
            return true;
        }

        setting.Color = App.Instance.Page.Current == App.Instance.Page.Setting || setting.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        setting.Update(time, posY: split.PosY, width: buttonSize, height: buttonSize);

        return result;
    }
}
