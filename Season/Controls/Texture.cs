// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Color = Season.Basic.Color;

namespace Season.Controls;

public interface ISetMode
{
    void SetMode(string mode);
}

/// <summary>
/// Texture holder interface used to provide unified access to the Texture property.
/// </summary>
public interface ITextureHolder
{
    Texture Texture { get; set; }
}

public enum TextureType
{
    Texture = 0,
    Text = 1,
    Emoji = 2,
    TextMsdf = 3
}

public static class TextCoords
{
    static Vector4[] A1 =
    {
        new Vector4(-1, 1, 0, 0),
        new Vector4(1, 1, 1, 0),
        new Vector4(-1, -1, 0, 1),
        new Vector4(1, -1, 1, 1)
    };

    static Vector4[] B2 =
    {
        new Vector4(-1, 1, 1, 0),
        new Vector4(1, 1, 0, 0),
        new Vector4(-1, -1, 1, 1),
        new Vector4(1, -1, 0, 1),
    };

    static Vector4[] C5 =
    {
        new Vector4(-1, 1, 0, 1),
        new Vector4(1, 1, 1, 1),
        new Vector4(-1, -1, 0, 0),
        new Vector4(1, -1, 1, 0),
    };

    static Vector4[] D5 =
    {
        new Vector4(-1, 1, 1, 1),
        new Vector4(1, 1, 0, 1),
        new Vector4(-1, -1, 1, 0),
        new Vector4(1, -1, 0, 0),
    };

    static Vector4[] C2 =
    {
        new Vector4(-1, 1, 0, 1),
        new Vector4(1, 1, 0, 0),
        new Vector4(-1, -1, 1, 1),
        new Vector4(1, -1, 1, 0),
    };

    static Vector4[] D4 =
    {
        new Vector4(-1, 1, 1, 1),
        new Vector4(1, 1, 1, 0),
        new Vector4(-1, -1, 0, 1),
        new Vector4(1, -1, 0, 0),
    };

    static Vector4[] A3 =
    {
        new Vector4(-1, 1, 0, 0),
        new Vector4(1, 1, 0, 1),
        new Vector4(-1, -1, 1, 0),
        new Vector4(1, -1, 1, 1),
    };

    static Vector4[] B6 =
    {
        new Vector4(-1, 1, 1, 0),
        new Vector4(1, 1, 1, 1),
        new Vector4(-1, -1, 0, 0),
        new Vector4(1, -1, 0, 1),
    };

    public static Vector4[] GetTransforms(int clock, bool flipX, bool flipY)
    {
        Vector4[] result = null;

        if (clock == 0)
        {
            if (!flipX && !flipY)
            {
                result = A1;
            }
            else if (flipX && !flipY)
            {
                result = B2;
            }
            else if (!flipX && flipY)
            {
                result = C5;
            }
            else
            {
                result = D5;
            }
        }
        else if (clock == 90)
        {
            if (!flipX && !flipY)
            {
                return C2;
            }
            else if (flipX && !flipY)
            {
                return A3;
            }
            else if (!flipX && flipY)
            {
                return D4;
            }
            else
            {
                return B6;
            }
        }
        else if (clock == 180)
        {
            if (!flipX && !flipY)
            {
                return D5;
            }
            else if (flipX && !flipY)
            {
                return C5;
            }
            else if (!flipX && flipY)
            {
                return B2;
            }
            else
            {
                return A1;
            }
        }
        else if (clock == 270)
        {
            if (!flipX && !flipY)
            {
                return B6;
            }
            else if (flipX && !flipY)
            {
                return D4;
            }
            else if (!flipX && flipY)
            {
                return A3;
            }
            else
            {
                return C2;
            }
        }
        return result;
    }

}

public class Texture
{
    static long _nextID;
    internal static long NextID() => System.Threading.Interlocked.Increment(ref _nextID);

    //public static int IDNow = 0;

    public long ID;

    public TextureType TextureType;

    public bool Ready;

    public bool Changed { get; set; }

    //public bool MouseOver { get; set; }

    //public Action OnClick;

    //string name;
    //public string Name
    //{
    //    get
    //    {
    //        return name;
    //    }
    //    set
    //    {
    //        if (name != value)
    //        {
    //            name = value;
    //            Changed = true;
    //        }
    //    }
    //}

    //string ext;
    //public string Ext
    //{
    //    get
    //    {
    //        return ext;
    //    }
    //    set
    //    {
    //        if (ext != value)
    //        {
    //            ext = value;
    //            Changed = true;
    //        }
    //    }
    //}

    int clock;
    public int Clock
    {
        get
        {
            return clock;
        }
        set
        {
            if (clock != value)
            {
                clock = value;
                Changed = true;
            }
        }
    }

    bool flipX;
    public bool FlipX
    {
        get
        {
            return flipX;
        }
        set
        {
            if (flipX != value)
            {
                flipX = value;
                Changed = true;
            }
        }
    }

    bool flipY;
    public bool FlipY
    {
        get
        {
            return flipY;
        }
        set
        {
            if (flipY != value)
            {
                flipY = value;
                Changed = true;
            }
        }
    }

    Color color;
    public Color Color
    {
        get
        {
            return color;
        }
        set
        {
            if (color != value)
            {
                color = value;
                Changed = true;
            }
        }
    }

    float alpha;
    public float Alpha
    {
        get
        {
            return alpha;
        }
        set
        {
            if (alpha != value)
            {
                alpha = value;
                Changed = true;
            }
        }
    }

    float sourceX;
    public float SourceX
    {
        get
        {
            return sourceX;
        }
        set
        {
            if (sourceX != value)
            {
                sourceX = value;
                Changed = true;
            }
        }
    }

    float sourceY;
    public float SourceY
    {
        get
        {
            return sourceY;
        }
        set
        {
            if (sourceY != value)
            {
                sourceY = value;
                Changed = true;
            }
        }
    }

    float sourceWidth;
    public float SourceWidth
    {
        get
        {
            return sourceWidth;
        }
        set
        {
            if (sourceWidth != value)
            {
                sourceWidth = value;
                Changed = true;
            }
        }
    }

    float sourceHeight;
    public float SourceHeight
    {
        get
        {
            return sourceHeight;
        }
        set
        {
            if (sourceHeight != value)
            {
                sourceHeight = value;
                Changed = true;
            }
        }
    }

    float rotation;
    public float Rotation
    {
        get
        {
            return rotation;
        }
        set
        {
            if (rotation != value)
            {
                rotation = value;
                Changed = true;
            }
        }
    }

    int posX;
    public int PosX
    {
        get
        {
            return posX;
        }
        set
        {
            if (posX != value)
            {
                posX = value;
                Changed = true;
            }
        }
    }

    int posY;
    public int PosY
    {
        get
        {
            return posY;
        }
        set
        {
            if (posY != value)
            {
                posY = value;
                Changed = true;
            }
        }
    }

    public int OriginWidth;

    public int OriginHeight;

    int width;
    public int Width
    {
        get
        {
            return width;
        }
        set
        {
            if (width != value)
            {
                width = value;
                Changed = true;
            }
        }
    }

    int height;
    public int Height
    {
        get
        {
            return height;
        }
        set
        {
            if (height != value)
            {
                height = value;
                Changed = true;
            }
        }
    }

    public float Factor;

    //public float Factor2;

    //public string[] Times;

    //public float Time;

    /// <summary>
    /// Texture replacement source: when set to a non-empty value, the current texture is replaced
    /// with the new image during the next Update frame.
    /// After consumption it is automatically reset to default, with both Path and Image null,
    /// so it does not trigger repeatedly.
    /// Supports implicit conversions, for example `mySprite.TextureOverride = "path.png"`
    /// or `= new NativeImageData(...)`.
    /// </summary>
    //public TextureUpdateSource TextureOverride { get; set; }

    public Texture()
    {
        ID = NextID();
    }
}
