// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.AI.Panels;

public class AIPanel : BoardPanel
{
    public static AIPanel Instance;

    internal string AllModels = "AllModels";

    internal string SourceCode = "SourceCode";

    internal string EarlyAccess = "EarlyAccess";

    float Time;

    Texts title;

    Shape line;

    internal Texts text, translate, image, video, music, stt, tts, vision;

    internal AINotice aiNotice;

    internal Panel currentPanel;

    internal FullView FullView;

    public AIPanel()
        : base()
    {
        Instance = this;

        FrameColor = Season.Basic.Colors.DarkSlateGray;
        
        title = new Texts()
        {
            Content = "AI", 
            Color = Season.Basic.Colors.DarkRed,
            Scale = Vector2.One * 1.2f
        };
        AddControl(title);

        text = new Texts()
        {
            Content = "Text",
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {

            }
        };
        AddControl(text);

        translate = new Texts()
        {
            Content = "Translate",
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {

            }
        };
        AddControl(translate);

        image = new Texts()
        {
            Content = "Image",
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {

            }
        };
        AddControl(image);

        video = new Texts()
        {
            Content = "Video",
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {

            }
        };
        AddControl(video);

        music = new Texts()
        {
            Content = "Music",
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {

            }
        };
        AddControl(music);

        stt = new Texts()
        {
            Content = "STT",
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {

            }
        };
        AddControl(stt);

        tts = new Texts()
        {
            Content = "TTS",
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {

            }
        };
        AddControl(tts);

        vision = new Texts()
        {
            Content = "Vision",
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {
                
            }
        };
        AddControl(vision);

        //text.OnClick?.Invoke();

        line = new Shape()
        {
            Type = ShapeType.Dot
        };
        AddControl(line);

        aiNotice = new AINotice()
        {
            Enable = false,
            OnClose = () =>
            {
                if (aiNotice.Enable)
                {
                    if (AIPanel.Instance != null && AIPanel.Instance.aiNotice != null)
                    {
                        AIPanel.Instance.RemovePanel(AIPanel.Instance.aiNotice);
                        AIPanel.Instance.aiNotice = null;
                    }
                }
                else
                {
                    OnClose?.Invoke();
                }
            }
        };
        AddPanel(aiNotice);

        FullView = new FullView()
        {
            Order = 99
        };
        AddPanel(FullView);
    }

    void ClearSelects()
    {
        text?.Selected = false;
        translate?.Selected = false;
        image?.Selected = false;
        video?.Selected = false;
        music?.Selected = false;
        stt?.Selected = false;
        tts?.Selected = false;
        vision?.Selected = false;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        if (FullView.Update(time))
        {
            return true;
        }

        Time += time;

        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        var size = 60;
        var padding = 25;
        var paddingH = 100;

        if (aiNotice != null)
        {
            aiNotice.Update(time, posX: PosX + (Width - aiNotice.Width) / 2, posY: PosY + (Height - aiNotice.Height) / 2, width: Width * 3 / 4, height: Height * 5 / 6);

            TouchService.Enable = false;
        }

        title.Update(time, posX: PosX + padding, posY: PosY + padding);

        text.Color = text.Selected || text.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.DarkSlateGray;
        text.Update(time, posX: PosX + padding, posY: title.PosY + padding + paddingH);
        translate.Color = translate.Selected || translate.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.DarkSlateGray;
        translate.Update(time, posX: PosX + padding, posY: text.PosY + paddingH);
        image.Color = image.Selected || image.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.DarkSlateGray;
        image.Update(time, posX: PosX + padding, posY: translate.PosY + paddingH);
        video.Color = video.Selected || video.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.DarkSlateGray;
        video.Update(time, posX: PosX + padding, posY: image.PosY + paddingH);
        music.Color = music.Selected || music.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.DarkSlateGray;
        music.Update(time, posX: PosX + padding, posY: video.PosY + paddingH);
        stt.Color = stt.Selected || stt.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.DarkSlateGray;
        stt.Update(time, posX: PosX + padding, posY: music.PosY + paddingH);
        tts.Color = tts.Selected || tts.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.DarkSlateGray;
        tts.Update(time, posX: PosX + padding, posY: stt.PosY + paddingH);
        vision.Color = vision.Selected || vision.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.DarkSlateGray;
        vision.Update(time, posX: PosX + padding, posY: tts.PosY + paddingH);

        line.Update(time);

        return result;
    }
}
