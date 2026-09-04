
namespace Creator;

internal class Folder : Panel
{
    int Padding = 55;

    int ItemHeight = 130;

    MovePanel movePanel;

    List<FolderItem> folderItems;

    internal Folder()
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
        if (folderItems?.Count > 0)
        {
            folderItems.ForEach(ci =>
            {
                Panels.Remove(ci);
                ci.Dispose();
            });
            folderItems.Clear();
        }

        folderItems = new List<FolderItem>();

        var folders = App.Instance.Creator.Folders;

        folders.Reverse();

        for (var i = 0; i < folders.Count; i++)
        {
            var folder = App.Instance.Creator.Folders[i];

            var folderItem = new FolderItem(folder);
            folderItems.Add(folderItem);

            AddPanel(folderItem);
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (Alpha > 0)
        {
            movePanel?.Color = Season.Basic.Colors.DarkSlateGray;
            movePanel.PosX = PosX;
            movePanel.PosY = PosY;
            movePanel.Width = Width;
            movePanel.Height = Height;
            movePanel?.SizeX = movePanel.Width ?? 0;
            if (folderItems?.Count > 0)
            {
                movePanel?.SizeY = 20 + folderItems.Sum(ci => ci.Height ?? 0) + 20;
            }

            if (movePanel != null && movePanel?.SizeY < movePanel?.Height)
            {
                movePanel?.SizeY = movePanel.Height ?? 0;
            }
            movePanel?.Update(time, alpha: 0.8f);

            for (var i = 0; i < folderItems.Count; i++)
            {
                var folderItem = folderItems[i];

                folderItem.Update(time, alpha: Alpha, posX: PosX + Padding, posY: PosY + Padding + ItemHeight * i - (int)MathF.Round(movePanel.Scroll), width: Width - Padding * 2, height: ItemHeight);
            }
        }

        return result;
    }
}

internal class FolderItem : Panel
{
    internal Sprite2D Icon { get; set; }

    internal Texts Title { get; set; }

    internal Texts Time { get; set; }

    internal Texts Desc { get; set; }

    internal FolderItem(Entities.Folder folder)
    {
        Icon = new Sprite2D()
        {
            Name = folder.Image,
            Color = Season.Basic.Colors.Gray
        };
        AddControl(Icon);
        Title = new Texts()
        {
            Content = folder.Title,
            Color = Season.Basic.Colors.MiddleBlack,
            Scale = Vector2.One * 1f
        };
        AddControl(Title);
        Time = new Texts()
        {
            Content = folder.Last == null ? null : ((DateTime)folder.Last).ToSmartDisplayShort(),
            Color = Season.Basic.Colors.Gray,
            Scale = Vector2.One * 0.85f
        };
        AddControl(Time);
        Desc = new Texts()
        {
            Content = folder.Desc,
            Color = Season.Basic.Colors.Gray,
            Scale = Vector2.One * 0.85f
        };
        AddControl(Desc);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        Icon.Update(time: time, alpha: Alpha, width: 40, height: 40, posX: PosX, posY: PosY);

        Title.WidthRequest = (int)((Width ?? 0) - 60 - 100);
        Title.Update(time: time, alpha: Alpha, posX: PosX + 60, posY: PosY);

        Time.Update(time: time, alpha: Alpha, posY: PosY);
        Time.PosX = (int)(PosX + (Width ?? 0) - (Time.Width ?? 0));

        Desc.WidthRequest = (int)((Width ?? 0) - 60);
        Desc.Update(time: time, alpha: Alpha, posX: PosX + 60, posY: PosY + 60);

        return result;
    }
}
