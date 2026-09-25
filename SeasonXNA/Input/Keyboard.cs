// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Basic;

namespace Microsoft.Xna.Framework.Input;

/// <summary>
/// XNA facade over the engine keyboard service. Every platform implementation feeds
/// <see cref="DeviceServices.Keyboard"/>; this facade converts its per-key down state
/// into the <see cref="KeyboardState"/> snapshot ported titles read once per frame.
/// </summary>
public static class Keyboard
{
    public static KeyboardState GetState()
    {
        var service = DeviceServices.Keyboard;

        if (service == null)
        {
            return default;
        }

        ulong word0 = 0, word1 = 0, word2 = 0, word3 = 0;

        foreach (var key in KeyMappings.EngineKeys)
        {
            if (service.IsDown(key) && KeyMappings.TryToKeys(key, out var xnaKey))
            {
                KeyboardState.SetBit(ref word0, ref word1, ref word2, ref word3, xnaKey);
            }
        }

        return new KeyboardState(word0, word1, word2, word3);
    }

    public static KeyboardState GetState(PlayerIndex playerIndex) => GetState();
}
