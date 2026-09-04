// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

public enum BillboardMode
{
        /// <summary>Orientation is determined entirely by the Rotation property and does not align to the camera.</summary>
	None,
        /// <summary>Always fully faces the camera, rotating on both the X and Y axes.</summary>
	Spherical,
        /// <summary>Aligns to the camera direction only around the world Y axis.</summary>
	Cylindrical,
}

public class Sprite3D : SpriteBase, ITransparentSortable
{
        // Unified positioning model: position and size reuse Control.PosX/PosY/PosZ/Width/Height
        // in world meters, with Changed maintained automatically by setters.
        // The anchor is the sprite center. When Width or Height is null, it falls back to 1 meter.
        // Depth has no meaning for a flat billboard and is not used in rendering.

        /// <summary>Transparent-sort reference point: the unified positioning model uses the anchor's world position.</summary>
	public Vector3 TransparentSortPosition => new Vector3(PosX, PosY, PosZ);

	/// <summary>
        /// Always participates in transparent sorting. Sprite3D always uses the Transparent pipeline,
        /// regardless of Alpha, with alpha blending and DepthWrite = Zero.
        /// It does not write depth itself, so correct occlusion depends entirely on draw order.
        /// It must be drawn after opaque controls on the same layer, which the sorter guarantees,
        /// and must be mixed back-to-front with other transparent objects.
        /// If it were gated only by Alpha &lt; 1, sprites with Alpha = 1 would be interleaved with opaque geometry by Index,
        /// and if drawn earlier, later opaque objects would overwrite their pixels while ignoring true depth.
        /// Across panels, Panel.Layer and Order must also be respected.
	/// </summary>
	public bool EnableTransparentSort => true;

        /// <summary>Fixed rotation used only when Mode = None.</summary>
	public Quaternion Rotation { get; set; } = Quaternion.Identity;

        /// <summary>Billboard orientation mode. Defaults to Spherical.</summary>
	public BillboardMode Mode { get; set; } = BillboardMode.Spherical;

	public override async Task<bool> Load()
	{
		await Graphics.Instance.LoadSprite3D(this);

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
            Graphics.Instance.UpdateSprite3D(this, time);
        }

		return result;
	}

	public override bool Draw()
	{
		var result = false;

		if (base.Draw())
		{
			if (Name.IsNullOrWhiteSpace())
			{

			}
			else
			{
				Graphics.Instance.DrawSprite3D(this);

                result = true;
			}
        }

		return result;
	}

	public override void Dispose()
	{
		base.Dispose();

		Graphics.Instance.DisposeSprite3D(this);
	}
}
