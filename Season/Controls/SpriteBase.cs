// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Color = Season.Basic.Color;

namespace Season.Controls;

/// <summary>
/// Shared base class for Sprite2D, Sprite3D, and Shape.
/// Holds common state such as ID, Name, Alpha, and Color, replacing the previously embedded Texture object.
/// Changed is maintained automatically by property setters.
/// Virtual properties such as Clock, Flip, Source*, TextureType, and Factor provide defaults,
/// and Sprite2D supplies the concrete implementation through overrides.
/// </summary>
public abstract class SpriteBase : Control
{
    string _ext;
    public string Ext
    {
        get => _ext;
        set { if (_ext != value) { _ext = value; Changed = true; } }
    }

    Color _color = Season.Basic.Colors.White;
    public Color Color
    {
        get => _color;
        set { if (_color != value) { _color = value; Changed = true; } }
    }

    // PosX / PosY / Width / Height have been moved up to Control
    // under the float-based unified positioning model, including PosZ and Depth,
    // with the same Changed gating behavior.

    public int OriginWidth { get; set; }
    public int OriginHeight { get; set; }

    public TextureUpdateSource TextureOverride { get; set; }

    // 2D rendering properties: virtual defaults overridden by Sprite2D.
    public virtual int Clock { get; set; }
    public virtual bool FlipX { get; set; }
    public virtual bool FlipY { get; set; }
    public virtual float SourceX { get; set; }
    public virtual float SourceY { get; set; }
    public virtual float SourceWidth { get; set; }
    public virtual float SourceHeight { get; set; }
    public virtual TextureType TextureType { get; set; } = TextureType.Texture;
    public virtual float Factor { get; set; }

    /// <summary>Shared texture-switching logic. Returns true when a reload is triggered.</summary>
    protected bool SetTextureInternal(string name, string? ext, bool forceReload)
    {
        bool sameName = _name == name;
        bool sameExt = ext is null || _ext == ext;

        if (!forceReload && sameName && sameExt)
            return false;

        _name = name;

        if (ext is null)
        {
            if (!sameName)
                _ext = null;
        }
        else
        {
            _ext = ext;
        }

        Ready = false;
        Changed = true;

        DeviceServices.BaseApp?.RequestLoad(this);

        return true;
    }
}
