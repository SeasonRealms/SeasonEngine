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

    // Set by RestartAnimations and consumed by the next ApplyPersons. See there for why it takes two frames.
    bool _restartAnimations;

    /// <summary>
    /// One plain <see cref="Model"/> beside the instanced field, created only when <c>SEASON_AB_SINGLE</c> is set. Every other
    /// sample in this panel is an InstancedModel, so the single-model GPU route - which shares the kernel with the instanced one
    /// and none of the instancing around it - has nothing here to be compared against. Cross-run comparison is sound at this
    /// placement: two CPU-path runs of this scene differ by zero pixels inside the probe's rectangle.
    ///
    /// The value selects the playback mode. <c>stop</c> parks the probe on the clip's first frame, which is the only setting where
    /// both routes sample the identical time and a difference can only be a plumbing defect rather than the kernel lerping
    /// between 60 Hz keys the CPU sampler interpolated from the original curve. Any other value restarts the clip and lets that
    /// documented residual back in, which is the setting to use when checking that animation actually plays.
    /// </summary>
    internal Model? singleProbe;

    /// <summary>Set by <see cref="RestartAnimations"/> until the probe's clock has actually been pinned, so a frame boundary
    /// that arrives before the asset finished loading does not leave the two runs on different phases.</summary>
    bool _restartSingleProbe;

    /// <summary>Whether the probe holds its first frame instead of playing. Read once from the variable at creation so the
    /// restart below and the route selection cannot disagree about which mode the run is in.</summary>
    bool _singleProbeStop;

    const string SingleProbeVariable = "SEASON_AB_SINGLE";

    const string SingleProbeClip = "Run-loop";

    internal Robots()
    {
        // P1: this panel's animation is entirely joint-driven, so the load-time bake applies and the FrameStart kernel can
        // build the palette. Twenty instances each on a different clip is also the worst case for P3 pooling - no two of
        // them share a bucket - so a pooled frame has to reproduce this image exactly while evaluating every instance.
        robotField = new InstancedModel()
        {
            ModelName = "Assets/3DGodotRobot.glb",
            SkinningMode = InstanceSkinningMode.Gpu
        };
        AddControl(robotField);

        var animations = new string[] { "Attack1", "Crouch", "Dive", "Emote1", "Emote2", "Fall-loop", "Fall2", "GroundSlide", "Hurt", "Idle-loop", "Jump", "Jump2", "Jump3", "Kick", "LongJump", "Run-loop", "Sprint-loop", "T-pose", "WallJump", "WallSlide" };

        for (var i = 0; i < 10; i++)
        {
            var person = new Person()
            {
                PosX = -2,
                // 0.5 = grass at y=0 plus half the body: Pos is the world position of the bounding-box
                // centre (see InstancedMesh3DBase.TemplateAnchorLocal), so the feet sit at PosY - Height/2.
                // This used to read 1f, which was "standing on the ground" under the retired top-face
                // anchor (Min.X, Max.Y, Min.Z). Moving the anchor to the centre left it behind, lifting
                // the whole row half a body off the grass.
                PosY = 0.5f,
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
                PosY = 0.5f,   // Same centre-anchor placement as the left-hand row: feet on the grass at y=0.
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
            // PosY is the box centre now, so the head is half a body above it; the emoji keeps the
            // third-of-a-body clearance over that head, i.e. the absolute height it has always had.
            PosY = Persons[0].PosY + Persons[0].Height * 0.5f + Persons[0].Height / 3,
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

        // 每进程抽一次，让重开时换句问候。逐像素 A/B 却要两轮画出同一行字：无种子抽取在那里是五成硬币，
        // 实测在 x≈1520..1719 / y≈820..859 上贡献了 worst 739 的差值 —— 比整条被测路由的残差大两个数量级。
        // 取证运行时直接取第一句，抽取本身在正常运行时照旧。
        bubbleText = new Texts()
        {
            RenderDomain = RenderDomain.Overlay,
            Content = texts[AbCapture.Enabled ? 0 : new Random().Next(0, texts.Length)],
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

            // Park every person on a neighbouring clip for exactly one frame; the next frame resolves the real clip again and the
            // clip change makes the platform group restart playback from AnimationTimeOffset. A same-clip write would not do it,
            // because the playback state only re-initializes when the clip index it stored differs from the incoming one.
            if (_restartAnimations && robotField.AnimationNames.Count > 1)
                person.AnimationClip = (person.AnimationClip + 1) % robotField.AnimationNames.Count;
        }
        _restartAnimations = false;
    }

    /// <summary>
    /// Creates or ticks the single-model probe. Placement is beside the instanced field rather than in it: the field occupies
    /// x = ±2, z = 6..33, so x = -6 keeps the probe fully lit and clear of the field's own silhouettes, which is what lets a
    /// capture be reduced to one rectangle instead of the whole frame.
    /// </summary>
    void UpdateSingleProbe(float time)
    {
        if (singleProbe == null)
        {
            var raw = Environment.GetEnvironmentVariable(SingleProbeVariable);
            if (string.IsNullOrEmpty(raw)) return;

            // Two independent dimensions carried by one variable: "gpu" picks the route, "stop" the playback mode. They are
            // spelled together because the comparison that decides anything - both routes parked on the clip's first frame,
            // where a difference cannot be the kernel resampling the 60 Hz keys the CPU sampler interpolated from the
            // original curve - is one run of each, and one variable keeps that pair from drifting apart. Neither word
            // contains the other, so a contains check each is unambiguous.
            _singleProbeStop = raw.Contains("stop", StringComparison.OrdinalIgnoreCase);

            singleProbe = new Model
            {
                Name = "Assets/3DGodotRobot.glb",
                PosX = -6f,
                PosY = 1f,
                PosZ = 6f,
                Width = 1f,
                Height = 1f,
                Depth = 1f,
                Rotation = MathF.PI,
                SkinningMode = raw.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                    ? InstanceSkinningMode.Gpu
                    : InstanceSkinningMode.Cpu
            };
            AddControl(singleProbe);
        }

        // Pin the clock from here on rather than from the asset load. StopAnimationAtRest zeroes the playback time and evaluates
        // the clip's first frame; PlayAnimation on that same clip flips the animating flag back on without touching the time, so
        // the two calls together are "restart at zero" without the asset needing a seek API. Retried until it lands, because
        // Ready is false on any frame before the asynchronous load finishes.
        //
        // The holding mode re-pins every frame instead of once. A single attempt is enough for the restart path because
        // PlayAnimation leaves the clock running and therefore visible, but this mode's whole purpose is a pose that does not
        // depend on when anything happened: the asset starts playing from PlayAnimation in Load, the load frame varies by run, and
        // a stop that lands once - or lands before the load - leaves the probe at whatever phase the rest of the run accumulated,
        // which makes two runs of the same settings differ. Re-pinning is idempotent and costs one CPU sample per frame, the same
        // work the CPU route does anyway.
        if (_singleProbeStop)
        {
            singleProbe.StopAnimationAtRest();
        }
        else if (_restartSingleProbe)
        {
            singleProbe.StopAnimationAtRest();
            _restartSingleProbe = singleProbe.PlayAnimation(SingleProbeClip) == null;
        }

        singleProbe.Update(time);
    }

    /// <summary>
    /// Restarts every person's animation from its time offset, two frames from now. Only the per-pixel A/B harness calls it:
    /// playback time accumulates from the frame the model finished its asynchronous load, which differs per run, so without a
    /// restart at a fixed frame number two otherwise identical runs capture the robots in different poses.
    /// </summary>
    internal void RestartAnimations()
    {
        _restartAnimations = true;
        _restartSingleProbe = true;
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

        UpdateSingleProbe(time);

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

                    if (Season.Rendering.Picking.ProjectToScreen(corner, Season.Rendering.RenderView.Current, app.ExtendResolution.X, app.ExtendResolution.Y, out var sx, out var sy))
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
