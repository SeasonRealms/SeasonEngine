// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.AI.Panels;

internal class AINotice : BoardPanel
{
    Texts title;

    List<NoticeItem> noticeItems;

    FrameButton frameButton;

    Texts restore;
    Shape restoreLine;

    Texts download;
    Shape downloadLine;

    Texts info, message;

    Action query;

    internal AINotice()
        : base()
    {
        FrameColor = Season.Basic.Colors.DarkSlateGray;

        title = new Texts()
        {
            Content = "Select Option",
            Color = Season.Basic.Colors.DarkRed,
            Scale = Vector2.One * 1.2f
        };
        AddControl(title);

        noticeItems = new List<NoticeItem>()
        {
            new NoticeItem()
            {
                ID = "BaseModels",
                Text = "Base Models",
                Desc = "Limited backend support",
                Price = "(Free)"
            },
            new NoticeItem()
            {
                ID = AIPanel.Instance.AllModels, // "AllModels",
                Text = "All Models",
                Desc = "CUDA support",
                Price = "(Premium)"
            },
            new NoticeItem()
            {
                ID = AIPanel.Instance.SourceCode, // "SourceCode",
                Text = "Source Code",
                Desc = "Current & future updates",
                Price = "(Premium)"
            }
        };

        foreach (var noticeItem in noticeItems)
        {
            noticeItem.OnAction = () =>
            {
                if (noticeItem.Enable)
                {
                    noticeItems.ForEach(ni => ni.Current = false);

                    noticeItem.Current = true;
                }
            };
            AddPanel(noticeItem);
        }

        frameButton = new FrameButton()
        {
            Text = "Continue",
            Width = 180,
            Height = 60,
            OnClick = async () =>
            {
                if (Enable)
                {
                    
                }
                else
                {
                    DeviceServices.File.OpenLink("https://apps.microsoft.com/detail/9NHDQ4F67MHM");
                }
            }
        };
        AddPanel(frameButton);

        restore = new Texts()
        {
            Content = "Restore",
            Scale = Vector2.One,
            OnClick = async () =>
            {
                if (Enable)
                {
                    query.Invoke();
                }
            }
        };
        AddControl(restore);
        restoreLine = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(restoreLine);

        download = new Texts()
        {
            Content = "Download",
            Scale = Vector2.One,
            OnClick = async () =>
            {
                if (Enable)
                {

                }
            }
        };
        AddControl(download);
        downloadLine = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(downloadLine);

        info = new Texts()
        {
            Content = "* 3D generation, animation binding & more are under development, and are not part of any option above.",
            Color = Season.Basic.Colors.Gray,
            Scale = Vector2.One * 0.85f
        };
        AddControl(info);

        message = new Texts()
        {
            Color = Season.Basic.Colors.DarkRed,
            Scale = Vector2.One * 0.85f
        };
        AddControl(message);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        int size = 60;

        var padding = 30;

        var paddingH = 100;

        title.Update(time, posX: PosX + padding, posY: PosY + padding);

        download.Enable = downloadLine.Enable = false;

        for (var i = 0; i < noticeItems.Count; i++)
        {
            var noticeItem = noticeItems[i];

            noticeItem.Update(time, posX: title.PosX, posY: title.PosY + paddingH + 128 * i);

            if (noticeItem.ID == AIPanel.Instance?.SourceCode)
            {
                download.Color = downloadLine.Color = download.Enable ? (download.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.DarkSlateGray) : Season.Basic.Colors.Gray;
                download.Update(time, posX: noticeItem.PosX + 680, posY: noticeItem.PosY - 10);
                downloadLine.Update(time, posX: download.PosX, posY: download.PosY + download.Height + 15, width: download.Width, height: 2);
            }
        }

        if (Enable)
        {
            var noticeItem = noticeItems.FirstOrDefault(notice => notice.Current);

            if (noticeItem is null || noticeItem.Already)
            {
                frameButton.Text = "Continue";
            }
            else
            {
                frameButton.Text = "Unlock";
            }
        }
        else
        {
            message.Content = "Available in the Marketplace version.";
            frameButton.Text = "Download";
        }

        frameButton.Update(time, posX: PosX + (Width - frameButton.Width) / 2, posY: PosY + Height - padding - frameButton.Height);

        restore.Color = restore.Enable ? (restore.MouseOver ? Season.Basic.Colors.DarkRed : Season.Basic.Colors.DarkSlateGray) : Season.Basic.Colors.Gray;
        restore.Update(time, posX: frameButton.PosX + frameButton.Width + 100, posY: frameButton.PosY + 10);

        restoreLine.Color = restore.Color;
        restoreLine.Update(time, posX: restore.PosX, posY: restore.PosY + restore.Height + 15, width: restore.Width, height: 2);

        info.Update(time, posX: title.PosX, posY: noticeItems.Last().PosY + 120);

        message.Update(time, posX: title.PosX, posY: frameButton.PosY - 70);

        return result;
    }
}

internal class NoticeItem : Panel
{
    internal string ID;

    internal string Text;

    internal string Price;

    internal string Desc;

    internal bool Already;

    internal bool Current;

    internal Action OnAction;

    Shape ground, frame;

    Texts tick;

    Texts title, price, desc;

    internal NoticeItem()
        : base()
    {
        ground = new Shape()
        {
            Type = ShapeType.Dot,
            Width = 70,
            Height = 70,
            OnClick = () =>
            {
                OnAction?.Invoke();
            }
        };
        AddControl(ground);

        frame = new Shape()
        {
            Type = ShapeType.RectFrame,
            Width = ground.Width,
            Height = ground.Height,
            Color = Season.Basic.Colors.LightBlack,
            Border = 3
        };
        AddControl(frame);

        tick = new Texts()
        {
            Content = "√",
            Scale = Vector2.One * 2f
        };
        AddControl(tick);

        title = new Texts()
        {
            Color = Season.Basic.Colors.DarkSlateGray,
            Scale = Vector2.One * 1f
        };
        AddControl(title);

        price = new Texts()
        {
            Color = Season.Basic.Colors.DarkRed,
            Scale = Vector2.One * 1f
        };
        AddControl(price);

        desc = new Texts()
        {
            Color = Season.Basic.Colors.Gray,
            Scale = Vector2.One * 0.85f
        };
        AddControl(desc);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        int size = 60;

        var padding = 30;

        if (Enable)
        {
            bool hover = ground.MouseOver;

            if (Already)
            {
                if (Current || ground.MouseOver)
                {
                    ground.Color = Season.Basic.Colors.DarkRed;

                    tick.Color = Season.Basic.Colors.White;
                }
                else
                {
                    ground.Color = Season.Basic.Colors.White;

                    tick.Color = Season.Basic.Colors.DarkSlateGray;
                }
            }
            else
            {
                if (Current || ground.MouseOver)
                {
                    ground.Color = Season.Basic.Colors.DarkRed;

                    tick.Color = Season.Basic.Colors.White;
                }
                else
                {
                    ground.Color = Season.Basic.Colors.White;

                    tick.Color = Season.Basic.Colors.White;
                }
            }
        }
        else
        {
            ground.Color = Season.Basic.Colors.Gray;

            tick.Color = Season.Basic.Colors.DarkSlateGray;
        }

        ground.Update(time, posX: PosX, posY: PosY);

        frame.Update(time, posX: ground.PosX, posY: ground.PosY);

        tick.Update(time, posX: ground.PosX + (ground.Width - tick.Width) / 2, posY: ground.PosY - 10);

        title.Content = Text;
        title.Update(time, posX: ground.PosX + 120, posY: ground.PosY - 10);

        price.Content = Price;
        price.Update(time, posX: title.PosX + 300, posY: title.PosY);

        desc.Content = Desc;
        desc.Update(time, posX: title.PosX, posY: ground.PosY + title.Height + 10);

        return result;
    }
}
