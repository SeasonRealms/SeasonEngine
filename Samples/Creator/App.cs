
namespace Creator;

internal class App : BaseApp
{
    internal static App Instance => DeviceServices.BaseApp as App;

    internal Entities.SettingsExtend SettingsExtend { get; set; }

    internal Entities.Creator Creator { get; set; }

    internal Animation Animation;

    internal float AnimationElapsed;

    internal float AnimationTime = 0.5f;

    internal int LeftWidth = 400;

    internal Left Left;

    internal Page Page;

    internal Top Top;

    internal Bar Bar;

    internal Commands Commands;

    internal App()
    {
        Title = "Creator";

        RenderDomain = RenderDomain.Overlay;

        StorageService.DirectoryBase = "Creator";

        BackgroundColor = Season.Basic.Colors.White;

        BasicResolution = new Vector2(1280, 720);
    }

    public override async void Create()
    {
        base.Create();

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var fonts = new List<Season.Basic.Font>()
                    {
                        new Season.Basic.Font()
                        {
                            File = "Assets/NotoSansMono-VariableFont.ttf",
                            Name = "SansMono",
                            Language = "",
                            Size = FontSize,
                            ReadOnly = true,
                            Time = DateTime.Now.ToDateTimeMilliseconds()
                        },
                        new Season.Basic.Font()
                        {
                            File = "Assets/NotoSansSC-VariableFont_wght.ttf",
                            Name = "SansSC",
                            Language = "",
                            Size = FontSize,
                            ReadOnly = true,
                            Time = DateTime.Now.ToDateTimeMilliseconds()
                        },
                        new Season.Basic.Font()
                        {
                            File = "Assets/NotoSansTC-VariableFont_wght.ttf",
                            Name = "NotoSansTC",
                            Language = "",
                            Size = FontSize,
                            ReadOnly = true,
                            Time = DateTime.Now.ToDateTimeMilliseconds()
                        },
                        new Season.Basic.Font()
                        {
                            //https://github.com/mozilla/twemoji-colr
                            File = "Assets/Twemoji.ttf",
                            Name = "Twemoji",
                            Language = "",
                            Size = FontSize,
                            ReadOnly = true,
                            Time = DateTime.Now.ToDateTimeMilliseconds()
                        }
                    };

            for (var i = 0; i < fonts.Count; i++)
            {
                var font = fonts[i];

                if (font.File.IsNullOrWhiteSpace())
                {

                }
                else
                {
                    try
                    {
                        var fontInstance = await Season.Fonts.Font.CreateAsync(font.File, font.Size);

                        Season.Fonts.Font.Instance.Add(fontInstance);
                    }
                    catch (Exception ex)
                    {
                        AddLog(LogType.Error, $"{DateTime.UtcNow} [Font.CreateAsync] file={font.File} failed err={ex}");
                    }
                }
            }

            FontsCreated = true;
        });

        SettingsExtend = Entities.SettingsExtend.Load();

        Creator = new Entities.Creator()
        {
            Chats = new List<Entities.Chat> 
            {
                new Entities.Chat() { ID = 0, Folder = null, Title = "Texts1", Desc = "It's a text task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 1, Folder = null, Title = "Images1", Desc = "It's a image task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 2, Folder = null, Title = "Audio1", Desc = "It's a audio task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 3, Folder = null, Title = "Music1", Desc = "It's a music task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 4, Folder = null, Title = "Translate1", Desc = "It's a translate task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 0, Folder = null, Title = "Texts2", Desc = "It's a text task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 1, Folder = null, Title = "Image2", Desc = "It's a image task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 2, Folder = null, Title = "Audio2", Desc = "It's a audio task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 3, Folder = null, Title = "Music2", Desc = "It's a music task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 4, Folder = null, Title = "Translate2", Desc = "It's a translate task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 0, Folder = null, Title = "Texts3", Desc = "It's a text task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 1, Folder = null, Title = "Image3", Desc = "It's a image task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 2, Folder = null, Title = "Audio3", Desc = "It's a audio task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 3, Folder = null, Title = "Music3", Desc = "It's a music task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Chat() { ID = 4, Folder = null, Title = "Translate3", Desc = "It's a translate task", Image = "Assets/Dialogue.png", Begin = DateTime.Now, Last = DateTime.Now },
            },
            Folders = new List<Entities.Folder>
            {
                new Entities.Folder() { ID = 0, Title = "Texts", Desc = "Texts", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 1, Title = "Images", Desc = "Images", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 2, Title = "Musics", Desc = "Musics", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 3, Title = "Videos", Desc = "Videos", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 4, Title = "Audios", Desc = "Audios", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 0, Title = "Translates", Desc = "Translates", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 1, Title = "Articles", Desc = "Articles", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 2, Title = "Stories", Desc = "Stories", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 3, Title = "Models", Desc = "Models", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 4, Title = "Projects", Desc = "Projects", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Folder() { ID = 0, Title = "Dialogues", Desc = "Dialogues", Image = "Assets/Note.png", Begin = DateTime.Now, Last = DateTime.Now },
            },
            Tasks = new List<Entities.Task>
            {
                new Entities.Task() { ID = 0, Title = "Create images task", Desc = "", Image = "", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Task() { ID = 1, Title = "Create stories task", Desc = "", Image = "", Begin = DateTime.Now, Last = DateTime.Now },
                new Entities.Task() { ID = 1, Title = "Create audios task", Desc = "", Image = "", Begin = DateTime.Now, Last = DateTime.Now },
            }
        };

        Left = new Left()
        {

        };
        AddPanel(Left);

        Page = new Page()
        {
            Alpha = 1
        };
        AddPanel(Page);

        Top = new Top()
        {

        };
        AddPanel(Top);

        Bar = new Bar()
        {

        };
        AddPanel(Bar);

        Commands = new Commands()
        {

        };
        AddPanel(Commands);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (Commands.Update(time, alpha: 1f, width: Top.Width, height: Top.Height))
        {
            return true;
        }

        if (App.Instance.ExtendResolution.X >= App.Instance.ExtendResolution.Y)
        {
            if (Animation is Animation.None or Animation.Vertical)
            {
                Animation = Animation.HorizontalLeft;
            }
        }
        else
        {
            if (Animation is Animation.None or Animation.HorizontalLeft or Animation.HorizontalNoLeft or Animation.LeftFadeIn or Animation.LeftFadeOut)
            {
                Animation = Animation.Vertical;
            }
        }

        if (Animation is Animation.HorizontalLeft or Animation.HorizontalNoLeft or Animation.LeftFadeIn or Animation.LeftFadeOut)
        {
            Left.Alpha = 1f;
            Left.Width = LeftWidth;
            Left.Height = (int)App.Instance.ExtendResolution.Y;

            if (Animation is Animation.HorizontalNoLeft)
            {
                Page.PosX = 0;
            }
            else if (Animation is Animation.LeftFadeIn)
            {
                if (AnimationElapsed < AnimationTime)
                {
                    AnimationElapsed += time;

                    if (AnimationElapsed >= AnimationTime)
                    {
                        AnimationElapsed = AnimationTime;

                        Animation = Animation.HorizontalLeft;
                    }
                }

                Page.PosX = (int)(Left.Width * AnimationElapsed / AnimationTime);
            }
            else if (Animation is Animation.HorizontalLeft)
            {
                Page.PosX = (int)Left.Width;
            }
            else if (Animation is Animation.LeftFadeOut)
            {
                if (AnimationElapsed < AnimationTime)
                {
                    AnimationElapsed += time;

                    if (AnimationElapsed >= AnimationTime)
                    {
                        AnimationElapsed = AnimationTime;

                        Animation = Animation.HorizontalNoLeft;
                    }
                }

                Page.PosX = (int)(Left.Width * (AnimationTime - AnimationElapsed) / AnimationTime);
            }
            else
            {
                Page.PosX = (int)Left.Width;
            }
            Page.Width = (int)App.Instance.ExtendResolution.X - Page.PosX;
            Page.InnerHeight = (int)(App.Instance.ExtendResolution.Y - (Top.Height ?? 0));

            Bar?.Alpha = 0f;
        }
        else if (Animation is Animation.Vertical)
        {
            Left.Alpha = 0f;

            Page.PosX = 0;
            Page.Width = (int)App.Instance.ExtendResolution.X;
            Page.InnerHeight = (int)(App.Instance.ExtendResolution.Y - (Top.Height ?? 0) - (Bar.Height ?? 0));

            Bar.Alpha = 1f;
            Bar.Width = (int)App.Instance.ExtendResolution.X;
            Bar.PosY = (int)App.Instance.ExtendResolution.Y - (Bar.Height ?? 0);
        }

        Left.Update(time);

        Page.Update(time, alpha: 1f, height: (int)App.Instance.ExtendResolution.Y);

        Top.Update(time, alpha: 1f, posX: Page.PosX, width: Page.Width, height: 105);

        Bar?.Update(time);

        return result;
    }
}

internal enum Animation
{
    None,
    HorizontalLeft,
    LeftFadeOut,
    HorizontalNoLeft,
    LeftFadeIn,
    Vertical
}
