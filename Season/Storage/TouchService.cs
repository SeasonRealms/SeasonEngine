// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Storage;

/// <summary>
/// Per-frame pointer state for the UI.
///
/// Platforms report presses through <see cref="RaiseDown"/>, <see cref="RaiseUp"/> and
/// <see cref="RaiseCancel"/> instead of only writing <see cref="isDown"/>: a tap that begins
/// and ends between two <see cref="Update"/> calls never shows up in a per-frame sample of
/// the down level, and those taps used to be dropped entirely. isDown is still read as the
/// held level, so drag, scroll and press-and-hold consumers keep their semantics.
/// </summary>
public static class TouchService
{
    enum TouchEventType
    {
        Down,
        Up,
        Cancel
    }

    readonly struct TouchEvent
    {
        public readonly TouchEventType Type;

        public readonly Vector2 Position;

        public TouchEvent(TouchEventType type, Vector2 position)
        {
            Type = type;
            Position = position;
        }
    }

    // A press whose release moved further than this, in logical pixels, is a drag rather than
    // a tap. The previous 20 was roughly 13pt on an iPhone 11: tight enough that a finger
    // rolling slightly on the glass was classified as a drag and the click never fired.
    const float TapSlop = 30f;

    // Platform callbacks enqueue and Update drains. The lock matters on Android, where touches
    // arrive on the UI thread while Update runs on the render thread.
    static readonly object eventsLock = new();

    static readonly Queue<TouchEvent> pendingEvents = new();

    static readonly Queue<TouchEvent> frameEvents = new();

    internal static int? PoXPre;

    internal static int? PoYPre;

    internal static int? PoZPre;

    public static int? PoX { get; internal set; }

    public static int? PoY { get; internal set; }

    public static int? PoZ { get; set; }

    public static float MoveX { get; internal set; }

    public static float MoveY { get; internal set; }

    public static bool Enable { get; set; }

    internal static bool isDownPre;

    // Written by platform callbacks on the UI thread and read by Update on the render thread
    // (Android), so these cannot stay plain fields.
    internal static volatile bool isDown;

    internal static volatile bool isRightDown;

    public static bool IsDown { get; set; }

    public static bool IsRightDown { get; set; }

    public static bool IsReleased = false;

    public static bool IsMoved { get; internal set; }

    /// <summary>
    /// Position of the press currently being tracked, in logical pixels. It stays valid on
    /// the frame that reports the release so hit tests can use where the press began
    /// (MouseOver was never computed for a tap that fit between two frames), and is
    /// replaced by the next press or dropped by a cancel.
    /// </summary>
    public static Vector2? LastDownPos = null;

    /// <summary>Reports a primary press; called by the platform on its UI thread.</summary>
    internal static void RaiseDown(float x, float y)
    {
        PoX = (int)x;
        PoY = (int)y;

        isDown = true;

        lock (eventsLock)
        {
            pendingEvents.Enqueue(new TouchEvent(TouchEventType.Down, new Vector2(x, y)));
        }
    }

    /// <summary>Reports the end of the tracked press; called by the platform on its UI thread.</summary>
    internal static void RaiseUp(float x, float y)
    {
        PoX = (int)x;
        PoY = (int)y;

        isDown = false;

        lock (eventsLock)
        {
            pendingEvents.Enqueue(new TouchEvent(TouchEventType.Up, new Vector2(x, y)));
        }
    }

    /// <summary>
    /// Reports that the current gesture stopped being a click, for example when a second
    /// finger joins or the system cancels the touch. The pending press is dropped, so the
    /// eventual release cannot fire a click.
    /// </summary>
    internal static void RaiseCancel()
    {
        isDown = false;

        lock (eventsLock)
        {
            pendingEvents.Enqueue(new TouchEvent(TouchEventType.Cancel, Vector2.Zero));
        }
    }

    public static void Update(float gameTime, float scale)
    {
        Enable = true;

        IsDown = IsReleased = IsMoved = false;

        IsRightDown = isRightDown;

        MoveX = 0; MoveY = 0;

        DrainEvents();

        while (frameEvents.Count > 0)
        {
            var touchEvent = frameEvents.Dequeue();

            switch (touchEvent.Type)
            {
                case TouchEventType.Down:
                    LastDownPos = touchEvent.Position;

                    // Start the move delta from the new press rather than from wherever the
                    // pointer happened to be on the previous frame.
                    PoXPre = null;

                    PoYPre = null;

                    break;

                case TouchEventType.Up:
                    // The release carries its own position: the pointer may already be
                    // somewhere else by the time this frame samples it.
                    if (LastDownPos is Vector2 downPos && Vector2.Distance(downPos, touchEvent.Position) <= TapSlop)
                    {
                        IsReleased = true;
                    }
                    else
                    {
                        IsMoved = true;
                    }

                    // LastDownPos stays for this frame so Control can hit-test the press.
                    break;

                case TouchEventType.Cancel:
                    LastDownPos = null;

                    break;
            }
        }

        // Platforms that only report the isDown level (desktop mouse, web snapshot) keep the
        // previous behavior through the level edges below. Reading the level also covers
        // platforms that report both a level and an event without double-counting it: the
        // event position and the level position are the same.
        if (!isDownPre && isDown)
        {
            LastDownPos = CurrentPos();

            PoXPre = null;

            PoYPre = null;
        }
        else if (isDownPre && !isDown && LastDownPos is Vector2 levelDownPos && CurrentPos() is Vector2 levelUpPos)
        {
            if (Vector2.Distance(levelDownPos, levelUpPos) <= TapSlop)
            {
                IsReleased = true;
            }
            else
            {
                IsMoved = true;
            }
        }

        if (isDown)
        {
            if (PoX != null && PoXPre != null && PoY != null && PoYPre != null)
            {
                MoveX = (int)PoX - (int)PoXPre;

                MoveY = (int)PoY - (int)PoYPre;

                if (MoveX != 0 || MoveY != 0)
                {
                    IsMoved = true;
                }
            }

            IsDown = true;

            PoXPre = PoX;

            PoYPre = PoY;

            isDownPre = true;
        }
        else
        {
            PoXPre = null;

            PoYPre = null;

            isDownPre = false;
        }

        // A release always wins over a press that begins in the same frame: the user finished
        // a tap, and leaving IsDown set would let OnTouch swallow the click.
        if (IsReleased)
        {
            IsDown = false;
        }
    }

    static Vector2? CurrentPos()
    {
        return PoX is int x && PoY is int y ? new Vector2(x, y) : null;
    }

    static void DrainEvents()
    {
        lock (eventsLock)
        {
            while (pendingEvents.Count > 0)
            {
                frameEvents.Enqueue(pendingEvents.Dequeue());
            }
        }
    }
}
