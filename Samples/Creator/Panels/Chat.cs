
namespace Creator;

internal class Chat : Panel
{
    int Padding = 55;

    int ItemHeight = 130;

    int ComposerHeight = 200;

    int composerPadding = 50;

    MovePanel movePanel;

    List<ChatItem> chatItems;

    Composer composer;

    internal Chat()
    {
        movePanel = new MovePanel()
        {
            MoveType = MoveType.Y,
            DisplayLine = true,
            EnableStartMoving = true,
            EnableEndMoving = true
        };
        AddPanel(movePanel);

        Load();
    }

    internal async void Load()
    {
        if (chatItems?.Count > 0)
        {
            chatItems.ForEach(ci =>
            {
                Panels.Remove(ci);
                ci.Dispose();
            });
            chatItems.Clear();
        }

        chatItems = new List<ChatItem>();

        var chats = App.Instance.Creator.Chats;

        chats.Reverse();

        for (var i = 0; i < chats.Count; i++)
        {
            var chat = chats[i];

            var chatItem = new ChatItem(chat);
            chatItems.Add(chatItem);

            AddPanel(chatItem);
        }

        if (composer != null)
        {
            composer.Dispose();
            Panels.Remove(composer);
        }

        composer = new Composer()
        {

        };
        AddPanel(composer);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (Alpha > 0)
        {
            movePanel?.Color = Season.Basic.Colors.DarkSlateGray;
            movePanel?.PosX = PosX;
            movePanel?.PosY = PosY;
            movePanel?.Width = Width;
            movePanel?.Height = (Height ?? 0) - ComposerHeight;
            movePanel?.SizeX = movePanel.Width ?? 0;
            if (chatItems?.Count > 0)
            {
                movePanel?.SizeY = 20 + chatItems.Sum(ci => ci.Height ?? 0) + 20;
            }

            if (movePanel != null && movePanel?.SizeY < movePanel?.Height)
            {
                movePanel?.SizeY = movePanel.Height ?? 0;
            }
            movePanel?.Update(time, alpha: 0.8f);

            for (var i = 0; i < chatItems.Count; i++)
            {
                var chatItem = chatItems[i];
                int itemPosY = (int)PosY + Padding + ItemHeight * i - (int)MathF.Round(movePanel.Scroll);
                int viewportBottom = (int)movePanel.PosY + (int)(movePanel.Height ?? 0);

                chatItem.Alpha = itemPosY < viewportBottom + (chatItem.Height ?? 0) ? Alpha : 0f;

                chatItem.Update(time, posX: PosX + Padding, posY: itemPosY, width: Width - Padding * 2, height: ItemHeight);
            }

            var composerPos = (int)App.Instance.ExtendResolution.Y - (composer.Height ?? 0);

            if (App.Instance.Animation is Animation.Vertical)
            {
                composer.PosY = composerPos - (App.Instance.Bar.Height ?? 0);
            }
            else
            {
                composer.PosY = composerPos;
            }

            composer.Update(time, alpha: Alpha, posX: PosX + composerPadding, width: (Width ?? 0) - composerPadding * 2, height: ComposerHeight);
        }

        return result;
    }
}

internal class ChatItem : Panel
{
    internal Sprite2D Icon { get; set; }

    internal Texts Title { get; set; }

    internal Texts Time { get; set; }

    internal Texts Desc { get; set; }

    internal ChatItem(Entities.Chat chat)
    {
        Icon = new Sprite2D()
        {
            Name = chat.Image,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(Icon);
        Title = new Texts()
        {
            Content = chat.Title,
            Color = Season.Basic.Colors.MiddleBlack,
            Scale = Vector2.One * 1f
        };
        AddControl(Title);
        Time = new Texts()
        {
            Content = chat.Last == null ? null : ((DateTime)chat.Last).ToSmartDisplayShort(),
            Color = Season.Basic.Colors.Gray,
            Scale = Vector2.One * 0.85f
        };
        AddControl(Time);
        Desc = new Texts()
        {
            Content = chat.Desc,
            Color = Season.Basic.Colors.Gray,
            Scale = Vector2.One * 0.85f
        };
        AddControl(Desc);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        Icon.Update(time: time, alpha: Alpha, width: 40, height: 40, posX: PosX, posY: PosY);

        Time.Update(time: time, alpha: Alpha, posY: PosY);
        Time.PosX = (int)(PosX + (Width ?? 0) - (Time.Width ?? 0));

        Title.WidthRequest = (int)(Width ?? 0) - 60 - 100;
        Title.Update(time: time, alpha: Alpha, posX: PosX + 60, posY: PosY);

        Desc.WidthRequest = (int)(Width ?? 0) - 60;
        Desc.Update(time: time, alpha: Alpha, posX: PosX + 60, posY: PosY + 60);

        return result;
    }
}
