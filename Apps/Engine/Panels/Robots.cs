// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Data source for instanced rendering in Robots: each Person is the instance itself and derives from MeshInstanceTransform.
/// It is the same reference stored in robotField.Instances, so PosX, PosY, PosZ, Width, Height, and Depth
/// follow the unified placement convention directly, where Pos is the world position of the instance anchor,
/// namely the geometric center of the template bounds. External edits take effect immediately without per-frame copying.
/// Only two extra fields need per-frame conversion: Yaw, in radians around Y, becomes a Rotation quaternion,
/// and Animation, a clip name matched against InstancedModel.AnimationNames, becomes an AnimationClip index.
/// </summary>
internal class Person : MeshInstanceTransform
{
    /// <summary>Yaw angle around Y in radians, converted to Rotation every frame by Robots.Update.</summary>
    internal float Yaw { get; set; }

    /// <summary>Animation clip name matched against InstancedModel.AnimationNames. Null, empty, or unmatched names fall back to the default clip.</summary>
    internal string? Animation { get; set; }
}

/// <summary>
/// InstancedModel sample panel. The Persons list is the single source of truth for instance placement,
/// size, and animation, and each Person is the actual instance object.
/// Adding or removing Persons at runtime, including insertions and removals in the middle, updates the
/// instance list dynamically while preserving order. Position and size changes on a Person take effect immediately,
/// while Yaw and Animation are converted every frame.
/// </summary>
internal class Robots : Panel
{
    internal List<Person> Persons = new List<Person>();

    // Exposed as internal so App.Create can register it into the collision obstacle list,
    // where bounds are expanded per instance.
    internal InstancedModel robotField;

    Sprite3D happy;

    // Dialogue bubble above the head of the right-side Person.
    // This uses a screen-space approach: project the head anchor to the screen and attach
    // a 2D Shape and Texts control, following the same pattern as ObjectPicker's board,
    // without introducing Shape3D or Texts3D controls.
    Shape bubble;
    Texts bubbleText;

    internal Robots()
    {
        robotField = new InstancedModel()
        {
            ModelName = "Assets/3DGodotRobot.glb"
        };
        AddControl(robotField);

        var animations = new string[] { "Attack1", "Crouch", "Dive", "Emote1", "Emote2", "Fall-loop", "Fall2", "GroundSlide", "Hurt", "Idle-loop", "Jump", "Jump2", "Jump3", "Kick", "LongJump", "Run-loop", "Sprint-loop", "T-pose", "WallJump", "WallSlide" };

        for (var i = 0; i < 10; i++)
        {
            var person = new Person()
            {
                PosX = -2,
                PosY = 1f,
                PosZ = 6 + i * 3,
                Width = 1,
                Height = 1,
                Depth = 0.5f,
                Yaw = 0f,
                Animation = animations[i]
            };

            Persons.Add(person);
        }

        for (var i = 0; i < 10; i++)
        {
            var person = new Person()
            {
                PosX = 2,
                PosY = 1f,
                PosZ = 6 + i * 3,
                Width = 1,
                Height = 1,
                Depth = 0.5f,
                Yaw = 0f,
                Animation = animations[10 + i]
            };

            Persons.Add(person);
        }

        SyncInstances();

        happy = new Sprite3D()
        {
            Name = "Assets/Happy.png",
            Color = Season.Basic.Colors.White,
            PosX = Persons[0].PosX,
            PosY = Persons[0].PosY + Persons[0].Height / 3,
            PosZ = Persons[0].PosZ,
            Width = 0.3f,
            Height = 0.3f,
            Mode = BillboardMode.Spherical,
            Alpha = 1f
        };
        AddControl(happy);

        bubble = new Shape()
        {
            RenderDomain = RenderDomain.Overlay,
            Type = ShapeType.Dot,
            //Alpha = 0f, // Hidden until first-frame projection and layout are ready; UpdateBubble then takes over visibility.
            Color = Season.Basic.Colors.White
        };
        AddControl(bubble);

        var texts = new string[] { "Where do you come from?", "What's beyond the sea?" };

        bubbleText = new Texts()
        {
            RenderDomain = RenderDomain.Overlay,
            Content = texts[new Random().Next(0, texts.Length)],
            Color = Season.Basic.Colors.Black,
            Scale = Vector2.One * 0.7f,
            Alpha = 0f
        };
        AddControl(bubbleText);
    }

    /// <summary>
    /// Keeps the instance list one-to-one with Persons and preserves order.
    /// Because each Person is the instance itself, synchronization only needs tail growth or shrink
    /// followed by reference-based reordering, which supports runtime insertions and removals in the middle of Persons.
    /// </summary>
    void SyncInstances()
    {
        var instances = robotField.Instances;

        while (instances.Count < Persons.Count)
            instances.Add(Persons[instances.Count]);

        while (instances.Count > Persons.Count)
            instances.RemoveAt(instances.Count - 1);

        for (int i = 0; i < Persons.Count; i++)
        {
            if (!ReferenceEquals(instances[i], Persons[i]))
                instances[i] = Persons[i];
        }
    }

    /// <summary>
    /// Converts only the two extra fields each frame: Yaw to Rotation and Animation name to AnimationClip index.
    /// Runtime edits to either field take effect immediately. Position and size come from MeshInstanceTransform
    /// and therefore need no copying. When the model is not ready yet, the clip-name list is empty and naturally
    /// falls back to the default clip until loading completes.
    /// In the selected state, locked by ObjectPicker, write ownership moves to the property panel:
    /// per-frame conversion of Yaw and Animation is skipped so panel edits are not overwritten,
    /// and Animation is reconstructed from the current clip index so Person remains the single source of truth.
    /// Once selection clears, Yaw and Animation become authoritative again.
    /// </summary>
    void ApplyPersons()
    {
        for (int i = 0; i < Persons.Count; i++)
        {
            var person = Persons[i];

            if (person.Selected)
            {
                var names = robotField.AnimationNames;
                person.Animation = person.AnimationClip >= 0 && person.AnimationClip < names.Count
                    ? names[person.AnimationClip]
                    : null;
                continue;
            }

            person.Rotation = Quaternion.CreateFromYawPitchRoll(person.Yaw, 0f, 0f);
            person.AnimationClip = ResolveAnimationClip(person.Animation);
        }
    }

    /// <summary>Resolves an animation clip index by name. Null, empty, unmatched, or not-yet-loaded cases all fall back to 0, the default clip.</summary>
    int ResolveAnimationClip(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;

        var names = robotField.AnimationNames;
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
                return i;
        }

        return 0;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        SyncInstances();
        ApplyPersons();
        robotField.Update(time);

        happy.Update(time);

        UpdateBubble(time);

        return result;
    }

    /// <summary>
    /// Updates the dialogue bubble every frame, using the same relationship between overlay board and bounds
    /// as ObjectPicker. It queries the instance world bounds through
    /// <see cref="Season.Controls.InstancedMesh3DBase.GetInstanceWorldBoundsRaw"/>, where the raw box matches the rendered body,
    /// projects all eight corners, and takes the screen-space min and max.
    /// The bubble is placed to the right of the projected bounds, with its left edge offset from the projected right edge
    /// and its top aligned with the projected top, again following the ObjectPicker board pattern.
    /// This keeps the bubble aligned with the top of the robot while staying clear of the emoji sprite above the head,
    /// and its size adapts to the measured text dimensions.
    /// If the model is not loaded yet or all corners fail to project, the whole bubble is hidden with Alpha=0.
    /// </summary>
    void UpdateBubble(float time)
    {
        var app = DeviceServices.BaseApp;
        var person = Persons[10];

        // Measured text size after Position() layout. It is zero on the first frame or before loading finishes,
        // so the bubble stays hidden until the next frame.
        float textW = bubbleText.Width ?? 0, textH = bubbleText.LineHeight;

        bool visible = app != null && textW > 0 && textH > 0;
        float sMinX = float.MaxValue, sMaxX = float.MinValue, sMinY = float.MaxValue;

        if (visible)
        {
            var bounds = robotField.GetInstanceWorldBoundsRaw(person);

            if (bounds.Extents == Vector3.Zero)
            {
                visible = false; // Model not ready yet, so template bounds have not been populated.
            }
            else
            {
                var bMin = bounds.Center - bounds.Extents;
                var bMax = bounds.Center + bounds.Extents;

                // Project all eight corners and collect min/max values.
                // This uses the same projection matrix as ObjectPicker, so picking and rendering pixel mapping stay aligned,
                // including on high-DPI displays. Corners behind the camera or otherwise unprojectable are skipped.
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? bMin.X : bMax.X,
                        ((i >> 1) & 1) == 0 ? bMin.Y : bMax.Y,
                        ((i >> 2) & 1) == 0 ? bMin.Z : bMax.Z);

                    if (Season.Rendering.Picking.ProjectToScreen(corner, app.Camera, app.ExtendResolution.X, app.ExtendResolution.Y, out var sx, out var sy))
                    {
                        if (sx < sMinX) sMinX = sx;
                        if (sx > sMaxX) sMaxX = sx;
                        if (sy < sMinY) sMinY = sy;
                    }
                }

                if (sMinX == float.MaxValue)
                    visible = false; // No corners could be projected, for example if the bounds are fully behind the camera.
            }
        }

        if (visible)
        {
            const float padding = 6f;
            float boxW = textW + padding * 2, boxH = textH + padding * 2;

            // Pin the bubble to the right side of the projected bounds, following the same pattern as the ObjectPicker board:
            // PosX starts at the projected right edge plus a gap, and PosY aligns to the projected top edge.
            // This keeps the top aligned with the robot while avoiding overlap with the emoji sprite above the head.
            bubble.Update(time, alpha: 0.85f, sMaxX, sMinY, boxW, boxH);
            bubbleText.Update(time, alpha: 1f, posX: bubble.PosX + padding, posY: bubble.PosY + padding);
        }
        else
        {
            bubble.Update(time, alpha: 0f);
            bubbleText.Update(time, alpha: 0f);
        }
    }

}
