// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

public class Sprite2D : SpriteBase, IRenderOrder
{
	public int Layer { get; set; }

	public int Order { get; set; }

	int _clock;
	public override int Clock
	{
		get => _clock;
		set { if (_clock != value) { _clock = value; Changed = true; } }
	}

	bool _flipX;
	public override bool FlipX
	{
		get => _flipX;
		set { if (_flipX != value) { _flipX = value; Changed = true; } }
	}

	bool _flipY;
	public override bool FlipY
	{
		get => _flipY;
		set { if (_flipY != value) { _flipY = value; Changed = true; } }
	}

	float _sourceX;
	public override float SourceX
	{
		get => _sourceX;
		set { if (_sourceX != value) { _sourceX = value; Changed = true; } }
	}

	float _sourceY;
	public override float SourceY
	{
		get => _sourceY;
		set { if (_sourceY != value) { _sourceY = value; Changed = true; } }
	}

	float _sourceWidth;
	public override float SourceWidth
	{
		get => _sourceWidth;
		set { if (_sourceWidth != value) { _sourceWidth = value; Changed = true; } }
	}

	float _sourceHeight;
	public override float SourceHeight
	{
		get => _sourceHeight;
		set { if (_sourceHeight != value) { _sourceHeight = value; Changed = true; } }
	}

    public override string ToString()
    {
        return Name;
    }

	public override async Task<bool> Load()
	{
		await Graphics.Instance.LoadSprite2D(this);

		return true;
	}

	public bool SetTexture(string name, string? ext = null, bool forceReload = false)
	{
		return SetTextureInternal(name, ext, forceReload);
	}

	public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? width = null, float? height = null, float? posZ = null, float? depth = null)
	{
		var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height, posZ: posZ, depth: depth);

        if (Name.IsNullOrWhiteSpace())
		{

		}
		else
		{
            if (Enable)
            {
                MouseOver = PosX < TouchService.PoX && TouchService.PoX < PosX + Width && PosY < TouchService.PoY && TouchService.PoY < PosY + Height;
            }
            else
            {
                MouseOver = false;
            }

			Graphics.Instance.UpdateSprite2D(this);
		}

		return result;
    }

	public override bool Draw()
	{
		var result = false;

		if (base.Draw())
		{
			if (Name.IsNullOrWhiteSpace() || Alpha == 0)
			{

			}
			else
			{
				Graphics.Instance.DrawSprite2D(this);

                result = true;
			}
		}

		return result;
	}

	public override void Dispose()
	{
		base.Dispose();

		Graphics.Instance.DisposeSprite2D(this);
	}
}
