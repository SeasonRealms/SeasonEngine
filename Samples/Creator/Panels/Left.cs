
namespace Creator;

internal class Left : Panel
{

    int start = 28;

    int button = 50;

    Shape background;

    Shape roundRect;
    Sprite2D query;
    Texts search;

    Sprite2D folder;
    Texts folderTexts;

    LeftNotes leftFolders;

    Sprite2D chat;
    Texts chatTexts;

    LeftNotes leftChats;

    LeftTask leftTask;

    internal Left()
    {
        background = new Shape()
        {
            Type = ShapeType.Dot,
            Color = new Season.Basic.Color(200, 200, 200, 255)
        };
        AddControl(background);

        roundRect = new Shape()
        {
            Type = ShapeType.RoundRect,
            Color = Season.Basic.Colors.White,
            OnClick = async () =>
            {
                var text = search.Content is "Search" ? "" : search.Content;

                var result = (await DeviceServices.Dialog.ShowKeyboard("Search", "", new string[] { "OK", "Cancel" }, text));

                if (result is null)
                {

                }
                else if (result is "")
                {
                    search.Content = "Search";
                }
                else
                {
                    search.Content = result;
                }
            }
        };
        AddControl(roundRect);
        query = new Sprite2D()
        {
            Name = "Assets/Query.png",
            Color = Season.Basic.Colors.Gray
        };
        AddControl(query);
        search = new Texts()
        {
            Content = "Search",
            Color = Season.Basic.Colors.Gray,
            Scale = Vector2.One * 0.8f
        };
        AddControl(search);

        folder = new Sprite2D()
        {
            Name = "Assets/Folder.png",
            OnClick = () =>
            {
                App.Instance.Bar.folder.OnClick?.Invoke();
            }
        };
        AddControl(folder);
        folderTexts = new Texts()
        {
            Content = "Folder",
            Color = Season.Basic.Colors.LightBlack,
            Scale = Vector2.One * 0.8f,
            OnClick = () =>
            {
                folder.OnClick?.Invoke();
            }
        };
        AddControl(folderTexts);

        leftFolders = new LeftNotes();
        AddPanel(leftFolders);

        chat = new Sprite2D()
        {
            Name = "Assets/Chat.png",
            OnClick = () =>
            {
                App.Instance.Bar.chat.OnClick?.Invoke();
            }
        };
        AddControl(chat);
        chatTexts = new Texts()
        {
            Content = "Chat",
            Color = Season.Basic.Colors.LightBlack,
            Scale = Vector2.One * 0.8f,
            OnClick = () =>
            {
                chat.OnClick?.Invoke();
            }
        };
        AddControl(chatTexts);

        leftChats = new LeftNotes();
        AddPanel(leftChats);

        leftTask = new LeftTask();
        AddPanel(leftTask);

        Load();
    }

    internal async void Load()
    {
        var folders = App.Instance.Creator.Folders.TakeLast(5);

        folders = folders.Reverse();

        leftFolders.Load(folders);

        var chats = App.Instance.Creator.Chats.TakeLast(5);

        chats = chats.Reverse();

        leftChats.Load(chats);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        background.Update(time, alpha: Alpha, posX: PosX, posY: PosY, width: Width, height: Height);

        roundRect.Update(time, alpha: Alpha, posX: PosX + start, posY: PosY + 130, width: Width - start * 2, height: 70);

        query.Update(time, alpha: Alpha, posX: roundRect.PosX + 10, posY: roundRect.PosY + 10, width: 45, height: 45);

        search.WidthRequest = (int)(roundRect.Width ?? 0) - 60;
        search.HeightRequest = 50;
        search.ShowDot = true;
        search.Update(time, alpha: Alpha, posX: roundRect.PosX + 60, posY: roundRect.PosY + (roundRect.Height - search.Height) / 2 - 5);

        folder.Color = folderTexts.Color = App.Instance.Page.Current == App.Instance.Page.Folder || folder.MouseOver || folderTexts.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        folder.Update(time, alpha: Alpha, width: (int)(button * 0.8f), height: (int)(button * 0.8f), posX: roundRect.PosX, posY: roundRect.PosY + 105);
        folderTexts.Update(time, alpha: Alpha, posX: folder.PosX + 70, posY: folder.PosY + folder.Height / 2 - folderTexts.Height / 2 - folderTexts.VisualOffsetTop / 2);

        leftFolders.Update(time, alpha: Alpha, posX: (int)folder.PosX, posY: (int)(folder.PosY + folder.Height) + 20, width: Width - start * 2);

        chat.Color = chatTexts.Color = App.Instance.Page.Current == App.Instance.Page.Chat || chat.MouseOver || chatTexts.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        chat.Update(time, alpha: Alpha, width: (int)(button * 0.8f), height: (int)(button * 0.8f), posX: roundRect.PosX, posY: roundRect.PosY + 500);
        chatTexts.Update(time, alpha: Alpha, posX: chat.PosX + 70, posY: chat.PosY + chat.Height / 2 - chatTexts.Height / 2 - chatTexts.VisualOffsetTop / 2);

        leftChats.Update(time, alpha: Alpha, posX: (int)chat.PosX, posY: (int)(chat.PosY + chat.Height) + 20, width: Width - start * 2);

        leftTask.Update(time, alpha: Alpha, posY: (int)App.Instance.ExtendResolution.Y - leftTask.Height, width: Width, height: 100);

        return result;
    }
}

internal class LeftNotes : Panel
{
    List<EntityItem> entityItems = new List<EntityItem>();

    internal int ItemHeight = 65;

    internal LeftNotes()
    {

    }

    internal async void Load(IEnumerable<Entities.Entity> entities)
    {
        if (entityItems?.Count > 0)
        {
            entityItems.ForEach(no =>
            {
                Panels.Remove(no);
                no.Dispose();
            });

            entityItems.Clear();
        }

        entityItems = new List<EntityItem>();

        foreach (var entity in entities)
        {
            var entityItem = new EntityItem(entity.Image, entity.Title);

            entityItems.Add(entityItem);

            AddPanel(entityItem);
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        var heightNow = 0;

        for (var i = 0; i < entityItems.Count; i++)
        {
            var note = entityItems[i];

            note.Update(time, alpha: alpha, posX: posX, posY: posY + heightNow, width: width, height: ItemHeight);

            heightNow += ItemHeight;
        }

        Height = heightNow;

        return result;
    }
}

internal class EntityItem : Panel
{
    Sprite2D note;
    Texts noteTexts;

    internal EntityItem(string image, string title)
    {
        if (image != null)
        {
            note = new Sprite2D()
            {
                Name = image,
                Color = Season.Basic.Colors.LightBlack,
                OnClick = () =>
                {
                    App.Instance.Bar.chat.OnClick?.Invoke();
                }
            };
            AddControl(note);
        }

        noteTexts = new Texts()
        {
            Content = title,
            Color = Season.Basic.Colors.LightBlack,
            Scale = Vector2.One * 1f,
            ShowDot = true,
            OnClick = () =>
            {
                note?.OnClick?.Invoke();
            }
        };
        AddControl(noteTexts);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        note?.Update(time, alpha: alpha, posX: posX, posY: posY, width: 30, height: 30);

        noteTexts.WidthRequest = (int)(note == null ? (width ?? 0) : (width ?? 0) - 50);
        noteTexts.HeightRequest = 50;
        noteTexts.Update(time, alpha: alpha, posX: note == null ? posX : posX + 50, posY: note == null ? posY : (posY + (note.Height - noteTexts.Height)/2));

        return result;
    }
}

internal class LeftTask : Panel
{
    int button = 50;

    int start = 28;

    Shape taskGround;

    Sprite2D task;
    Texts taskTexts;

    internal LeftTask()
    {
        taskGround = new Shape()
        {
            Type = ShapeType.Dot,
            Color = Season.Basic.Colors.White
        };
        AddControl(taskGround);

        task = new Sprite2D()
        {
            Name = "Assets/Task.png",
            OnClick = () =>
            {
                App.Instance.Bar.task.OnClick?.Invoke();
            }
        };
        AddControl(task);
        taskTexts = new Texts()
        {
            Content = "Task",
            Color = Season.Basic.Colors.LightBlack,
            Scale = Vector2.One * 1f,
            OnClick = () =>
            {
                task.OnClick?.Invoke();
            }
        };
        AddControl(taskTexts);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        taskGround.Update(time, alpha: Alpha, posY: posY, width: Width, height: height);

        task.Color = taskTexts.Color = App.Instance.Page.Current == App.Instance.Page.Task || task.MouseOver || taskTexts.MouseOver ? App.Instance.SettingsExtend.ButtonColorHover : App.Instance.SettingsExtend.ButtonColorNormal;
        task.Update(time, alpha: Alpha, width: button, height: button, posX: start, posY: (int)App.Instance.ExtendResolution.Y - start - task.Height);
        taskTexts.Update(time, alpha: Alpha, posX: task.PosX + 80, posY: task.PosY - (taskTexts.Height - task.Height) / 2 - taskTexts.VisualOffsetTop);

        return result;
    }

}
