// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Panels;

public class ImageView : Panel
{
    float? _width;
    public override float? Width
    {
        get => _width;
        set { if (_width != value) { _width = value; Changed = true; } }
    }

    float? _height;
    public override float? Height
    {
        get => ImageHeight;
        set { if (ImageHeight != value) { ImageHeight = (int)(value ?? 0f); Changed = true; } }
    }

    public int ImageWidth { get; set; } = 60;

    public int ImageHeight { get; set; } = 60;

    public bool ShowClear { get; set; } = false;

    public Sprite2D Image;
    
    Sprite2D clear;

    public override string Name 
    {
        get => _name;
        set 
        { 
            if (_name != value) 
            { 
                _name = value;

                Image.SetTexture(_name);

                Changed = true;
            }
        }
    }

    public int? ImageOriginWidth
    { 
        get
        {
            return Image?.OriginWidth;
        }
    }

    public int? ImageOriginHeight
    {
        get
        {
            return Image?.OriginHeight;
        }
    }

    public Action OnClear;

    public ImageView()
        : base()
    {
        Image = new Sprite2D()
        {
            OnClick = () =>
            {
                OnClick?.Invoke();
            }
        };
        AddControl(Image);

        clear = new Sprite2D()
        {
            Name = @"Assets/Clear.png",
            OnClick = () =>
            {
                OnClear?.Invoke();

                Image.Name = null;
                //Image.Dispose();
            }
        };
        AddControl(clear);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        Image.Alpha = Alpha;
        Image.Enable = Enable;
        Image.Color = Enable ? Season.Basic.Colors.White : Season.Basic.Colors.Gray;
        Image.Update(time, posX: PosX, posY: PosY, width: ImageWidth, height: ImageHeight);

        clear.Alpha = ShowClear ? Alpha : 0f;
        clear.Enable = Enable;
        clear.Color = Enable ? (clear.MouseOver ? Season.Basic.Colors.Red : Season.Basic.Colors.LightBlack) : Season.Basic.Colors.Gray;
        clear.Update(time, posX: Image.PosX + Image.Width + 20, posY: Image.PosY, width: ImageWidth, height: ImageHeight);

        return result;
    }
}

public class FullView : Panel
{
    public bool Stretching { get; set; }

    float Time { get; set; }

    float ImagePosX { get; set; }

    float ImagePosY { get; set; }

    Sprite2D Image;

    Sprite2D Sprite2D;

    Shape ground;

    public FullView()
        : base()
    {
        ground = new Shape()
        {
            Type = ShapeType.Dot,
            OnClick = () =>
            {
                ClearImage();
            }
        };
        AddControl(ground);
    }

    public void SetImage(Sprite2D image, float imagePosX, float imagePosY)
        => SetImage(image, image.Name, imagePosX, imagePosY);

    /// <summary>
    /// Overload with an explicit texture name.
    /// The source image Name may only be a placeholder key, such as "Dot" for generated output.
    /// When the real texture has been written to disk or replaced through TextureOverride,
    /// the display carrier reloads by using textureName instead.
    /// </summary>
    public void SetImage(Sprite2D image, string textureName, float imagePosX, float imagePosY)
    {
        Stretching = true;

        Time = 0f;

        Image = image;

        ImagePosX = imagePosX;

        ImagePosY = imagePosY;

        // Remove the previous display carrier before repeated SetImage calls
        // to avoid stale Sprite instances stacking and drawing together.
        if (Sprite2D != null)
        {
            RemoveControl(Sprite2D);

            Sprite2D = null;
        }

        Sprite2D = new Sprite2D()
        {
            Name = textureName,

            // The initial size must not be null.
            // LoadSprite2D casts Width and Height to int internally,
            // and null throws an exception that is swallowed as a false success,
            // leaving DictionarySprite unregistered and the carrier permanently invisible.
            Width = 1,
            Height = 1
        };
        AddControl(Sprite2D);
    }

    void ClearImage()
    {
        Stretching = false;

        Time = 0f;

        Image = null;

        ImagePosX = 0;

        ImagePosY = 0;

        if (Sprite2D != null)
        {
            RemoveControl(Sprite2D);

            Sprite2D = null;
        }
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        float targetX = 0, targetY = 0, targetH = 0, targetW = 0;

        if (Stretching)
        {
            Time += time;

            if (Time >= 0.5f)
            {
                Time = 0.5f;
            }

            Alpha = Time / 0.5f;

            if (DeviceServices.BaseApp.ExtendResolution.X > DeviceServices.BaseApp.ExtendResolution.Y)
            {
                targetH = (int)DeviceServices.BaseApp.ExtendResolution.Y * 2 / 3;

                targetW = Image.OriginWidth * targetH / Image.OriginHeight;
            }
            else
            {
                targetW = (int)DeviceServices.BaseApp.ExtendResolution.X * 2 / 3;

                targetH = targetW * Image.OriginHeight / Image.OriginWidth;
            }

            targetX = (int)(DeviceServices.BaseApp.ExtendResolution.X - targetW) / 2;

            targetY = (int)(DeviceServices.BaseApp.ExtendResolution.Y - targetH) / 2;

            Width = Image.Width + (targetW - Image.Width) * Alpha;

            Height = Image.Height + (targetH - Image.Height) * Alpha;

            result = true;
        }
        else
        {
            Alpha = 0f;
        }

        PosX = ImagePosX + (targetX - ImagePosX) * Alpha;

        PosY = ImagePosY + (targetY - ImagePosY) * Alpha;

        ground.Alpha = 0.5f * Alpha;

        if (ground.Update(time, posX: 0, posY: 0, width: DeviceServices.BaseApp.ExtendResolution.X, height: DeviceServices.BaseApp.ExtendResolution.Y))
        {
            result = true;
        }

        Sprite2D?.Update(time, alpha: Alpha, posX: PosX, posY: PosY, width: Width, height: Height);

        return result;
    }
}
