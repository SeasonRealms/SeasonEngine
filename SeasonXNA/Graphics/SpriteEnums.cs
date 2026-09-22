// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Microsoft.Xna.Framework.Graphics;

[Flags]
public enum SpriteEffects
{
    None = 0,
    FlipHorizontally = 1,
    FlipVertically = 2
}

// Only Deferred is supported by the first drawing contract.
public enum SpriteSortMode
{
    Deferred = 0,
    Immediate = 1,
    Texture = 2,
    BackToFront = 3,
    FrontToBack = 4
}
