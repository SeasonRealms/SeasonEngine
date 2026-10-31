// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

// SeasonEngine WebGPU - C# JSInterop rendering engine
// The single source of truth for WGSL shaders lives in C# WebGPUPipeline.cs
// (Mesh3DShader/BlitShader, sent once during initialize). This file contains no shader source.
// NOTE: Keep mirrored copies in sync between src/Platforms/Web/js and samples/*Web/wwwroot/js.
//
// 1-1 JS-side pass orchestration rules
// (Steps 0-3 finalized; see the class header in Platforms/Web/Graphics.cs for the C# master index):
// - The pass state machine lives in this file: _passEncoder switches globally and all draw functions
//   route implicitly. beginFrame only creates the encoder / depth texture / resets pool cursors.
//   Pass open/close is driven by C# FrameSchedule via beginPass/endPass.
//   Debug groups are attached at the commandEncoder level and wrap the whole pass
//   (_passLabels are ordered to match C# RenderPassId).
// - Attachment-set isomorphism: every color pass always uses scene-format color + depth24plus depth
//   (scene format = preferred format in the LDR tier / rgba16float in the HDR tier, 1-4 Step A;
//   offscreen RTs own matching depth, and the blit pipeline also carries depthStencil
//   and always bakes the preferred format for backbuffer rendering).
//   Depth-only passes (shadow) use only a depth32float attachment and require a dedicated pipeline (1-5).
// - Offscreen RTs are name-as-handle: real resources live in _renderTargets[name].
//   MatchBackbuffer RTs are rebuilt lazily during beginPass resolution using canvas size
//   while the C# handle stays unchanged. Blit has four variants:
//   point/linear are selected automatically by size, tonemap switches automatically for rgba16float sources,
//   and exposure x ACES + gamma is applied there, with exposure uploaded each frame through binding 2
//   uniform (1-4 Step B).
// - Merged submission: one encoder, one submit, and multiple passes are just multiple encoded sections.
//   endFrame defensively ends any pass that was left open.
// - 1-5 shadow JS-side rules: beginPass(depthOnly) sets _passDepthOnly.
//   drawMesh3DBatch / drawInstancedMesh3D then automatically switch to _shadowPipeline
//   (vertex-only, cullMode none, baked depthBias) + a dedicated shadow bind group
//   (bindings 0/7/8/9 only, no atlas, avoiding validation errors from sampling an attachment)
//   and skip transparent objects.
//   Binding 11/12 of the main-pass bind group
//   (depth texture + comparison sampler) are resolved by name through setShadowAtlas,
//   with a 1x1 dummy depth view as fallback when not registered.
//   setShadowViewport takes effect immediately, and C# is responsible for flushing batches
//   before switching quadrants.
// - 2-3 velocity JS-side rules: if the 11th beginPass parameter velocityTargetName is non-null,
//   the Scene pass becomes a dual-color-attachment pass
//   (slot 0 = scene color / slot 1 = rg16float velocity, always cleared to 0)
//   and sets _passVelocity.
//   The 8 mesh draw sites implicitly switch to the _mesh3DPipelineVelocity table via
//   _activeMeshPipelines()
//   (fs_main_mrt + dual targets, reusing the exact same bindGroupLayout as the main table,
//   so bind groups are fully reusable).
//   endPass resets the flag. Velocity RT uses formatKind 4
//   (rg16float color, no companion depth, no blit bind group,
//   with the depth plane provided by SceneDepth / backbuffer depth).
//   When the feature is off, the table stays null, the attachment stays single, and the path leaves no residue.
// - 1-7 cubemap + environment IBL JS-side rules: createTextureCube creates a 6-layer rgba8unorm texture
//   (size:[s,s,6], single mip) and stores
//   { texture, view: createView({dimension:'cube'}) } in its own _textureCubes registry
//   instead of _textures.
//   Binding 15 of the main-pass bind group is resolved by name through setEnvCube
//   (name-as-handle, same pattern as setShadowAtlas).
//   When not registered, a 1x1 all-black cube is used as fallback
//   because WGSL-side uEnvCube is a static fs_main reference and always needs a valid binding;
//   the on/off switch is carried by envParams at the tail of the lighting UBO.
//   The sampler reuses binding 1, and _shadowBindGroupLayout is not expanded
//   because the shadow pipeline has no fragment stage and pure FS resources do not participate
//   in its static-reference analysis.

window.seasonWebGPU = (() => {
    let _device = null, _context = null, _format = null, _canvas = null;
    // 1-8 Step 0: optional feature names detected and successfully enabled during initialize
    // ('texture-formats-tier1' / 'float32-filterable'),
    // plus two device limits related to 3D compute. _mapStorageFormat is the only consumer.
    const _gpuFeatures = new Set();
    const _gpuLimits = { maxComputeInvocationsPerWorkgroup: 256, maxTextureDimension3D: 2048 };
    // Scene target format (1-4 Step A):
    // LDR tier = _format, HDR tier = 'rgba16float'.
    // The 7 main-pipeline variants bake their color target from this
    // (the HDR-tier Scene pass always renders offscreen and never touches the backbuffer).
    let _sceneFormat = null;

    // _debugLog switch:
    // toggled dynamically by setDebugLog(bool) and synchronized by C# WebDebug.SetEnabled.
    // When disabled, _log skips output.
    let _debugLog = false;
    function _log(...args) { if (_debugLog) console.log(...args); }

    // Pipeline variants: opaque (writes depth) / fade (blends and writes depth) /
    // transparent (blends without depth writes)
    let _mesh3DPipeline = null, _mesh3DShader = null, _identityInstanceBuffer = null, _depthTexture = null;
    // 2-3 contract clauses 2/3:
    // velocity variant table (fs_main_mrt + dual color targets).
    // It stays null when MotionVectors are disabled,
    // leaving the main table unchanged and the path residue-free.
    // It shares the same bindGroupLayout as the main table,
    // so bind groups can be reused across tables.
    let _mesh3DPipelineVelocity = null, _velocityOutput = false;
    // A0.2: SceneNormal (MRT slot 2, rgba16float). Fixed at initialize and only ever true with _velocityOutput, so
    // it does not add a table: it changes the velocity table itself to fs_main_mrtn + three targets.
    let _normalOutput = false;
    // Overlay PSO family (backbuffer format + depth off):
    // the Overlay pass (passId===5) renders directly to the backbuffer.
    // In HDR mode, attachment state is incompatible with _sceneFormat (rgba16float),
    // so WebGPU setPipeline reports an async validation error
    // ("Attachment state of RenderPipeline is not compatible with RenderPassEncoder", rule 3).
    // Depth attachments are loaded after the first pass used storeOp=discard,
    // so contents are undefined; therefore this family disables both depth testing and depth writes,
    // mirroring Vulkan overlay behavior.
    let _mesh3DPipelineOverlay = null;
    let _defaultBoneBuffer = null, _defaultMorphMetaBuffer = null, _defaultMorphDataBuffer = null;
    // 2-3 Step C: default fallback sentinel for the prev-instance byte stream (binding 14) -
    // 80 bytes of zeros for one instance.
    // When hasPrevInstanceWorld / hasPrevMorph are 0, VS does not read it,
    // so contents do not matter; it exists only to satisfy the explicit bindGroupLayout rule
    // that every entry must be bound (this backend does not use layout:'auto').
    // The fallback for prev bone palette (binding 13) directly reuses _defaultBoneBuffer:
    // binding the same read-only storage buffer to multiple bindings is legal
    // and saves one permanently resident memory block.
    let _defaultPrevInstanceBuffer = null;
    const MAX_SKINNED_BONES = 100, BYTES_PER_MATRIX4X4 = 16 * 4, MIN_SKINNED_BONE_BUFFER_BYTES = MAX_SKINNED_BONES * BYTES_PER_MATRIX4X4;

    const _textures = {}, _textureMeta = {}, _textureViews = {}, _samplers = {};
    const _skinnedBoneBuffers = {};
    // 2-3 Step C (contract clause 6: previous-frame data is kept in CPU shadow copies,
    // and GPU historical frames must never be read back):
    // JS-side shadow copy of the prev bone palette.
    // Each frame, uploadSkinnedBones writes the retained previous-frame bytes into the prev buffer first,
    // then writes current-frame bytes, and finally retains the current-frame byte reference.
    // Therefore the C# side needs no extra upload call and only needs to set hasPrevBones
    // after bone data has been ready for two consecutive frames
    // (mirroring Vulkan SetPrevBonesReady).
    // The retained data is an independent Uint8Array produced by _interopToU8
    // (the wasm linear-memory view has already been detached by slice),
    // so it can be safely held across frames.
    const _prevSkinnedBoneBuffers = {};
    const _prevSkinnedBoneBytes = {};
    // uploadSkinnedBones may be called multiple times per frame for the same skinKey
    // (once for the main pass and once for the shadow pass, with identical content).
    // If the shadow copy were rolled forward on every call,
    // prev would be overwritten by current-frame data and bone velocity would stay zero.
    // Therefore a frame-serial gate ensures it rolls exactly once per frame per skinKey:
    // the first call is effective, later calls in the same frame only write current data.
    const _prevSkinnedBoneFrame = {};
    let _frameSerial = 0;
    let _skinnedLogCount = 0;
    const _instancedDiag = {
        drawCalls: 0,
        lastCacheKey: '',
        lastInstanceCount: 0,
        lastInstanceBytes: 0,
        lastModeKey: '',
        deviceLost: false,
        deviceLostReason: '',
        uncapturedError: '',
        lastError: ''
    };

    let _commandEncoder = null, _passEncoder = null, _frameStarted = false;
    let _activePassId = -1, _activeColorTarget = null, _activeColorView = null, _activeDepthView = null;

    function _clearActivePass() {
        _activePassId = -1; _activeColorTarget = _activeColorView = _activeDepthView = null;
        _passDepthOnly = _passVelocity = _passNormal = _passOutlineMask = _passOverlay = false;
    }

    // Buffer pools: grouped by usage and reused by resetting cursors each frame
    const _bufferPool = { vertex: [], index: [], uniform: [], storage: [] };
    // 2-3 Step C: the storage pool carries prev-instance byte streams (binding 14).
    // Like the vertex pool, it serves per-draw transient resources,
    // but usage must be STORAGE instead of VERTEX,
    // so it remains a separate pool
    // because one buffer cannot serve both roles.
    let _poolCursor = { vertex: 0, index: 0, uniform: 0, storage: 0 };

    function _acquireBuffer(type, byteLength, usage) {
        const pool = _bufferPool[type];
        const idx = _poolCursor[type]++;
        if (idx < pool.length) {
            const slot = pool[idx];
            if (slot.capacity >= byteLength) return slot.buffer;
            slot.buffer.destroy();
            const cap2 = Math.max(byteLength, Math.ceil(byteLength * 1.5 / 256) * 256);
            slot.buffer = _device.createBuffer({ size: cap2, usage });
            slot.capacity = cap2;
            return slot.buffer;
        }
        const cap = Math.max(byteLength, Math.ceil(byteLength * 1.5 / 256) * 256);
        const buffer = _device.createBuffer({ size: cap, usage });
        pool.push({ buffer, capacity: cap });
        return buffer;
    }

    function _writeIdentityBones(buffer) {
        const TEMP_LEN = MAX_SKINNED_BONES * 16;
        const temp = new Float32Array(TEMP_LEN);
        for (let b = 0; b < MAX_SKINNED_BONES; b++) {
            const off = b * 16;
            temp[off] = 1; temp[off + 5] = 1; temp[off + 10] = 1; temp[off + 15] = 1;
        }
        _device.queue.writeBuffer(buffer, 0, temp);
    }

    function _createMorphBuffer(morphBytes, morphTargetCount, morphVertexCount) {
        if (!morphBytes || morphTargetCount <= 0 || morphVertexCount <= 0) return null;
        if (!(morphBytes instanceof Uint8Array)) morphBytes = new Uint8Array(morphBytes);

        const metaData = new Uint32Array(4);
        metaData[0] = morphTargetCount >>> 0;
        metaData[1] = morphVertexCount >>> 0;
        const metaBuffer = _device.createBuffer({
            size: 16,
            usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
        });
        metaBuffer.size = 16;
        _device.queue.writeBuffer(metaBuffer, 0, metaData);

        const alignedDataSize = Math.max(4, Math.ceil(morphBytes.byteLength / 4) * 4);
        const dataBuffer = _device.createBuffer({
            size: alignedDataSize,
            usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
        });
        dataBuffer.size = alignedDataSize;
        _device.queue.writeBuffer(dataBuffer, 0, morphBytes);

        return { metaBuffer, dataBuffer };
    }

    // Persistent cache for static meshes
    // geometry is reused across frames and only uniforms are updated per frame
    const _staticMeshes = {};

    function _uploadStaticMeshInternal(cacheKey, vertexData, indexData, textureName, normalTextureName, mrTextureName, aoTextureName, emissiveTextureName, vertexStrideFloats, indexFormat, doubleSided, skinned, morphBytes = null, morphTargetCount = 0, morphVertexCount = 0) {
        const resolvedIndexFormat = indexFormat === 'uint32' ? 'uint32' : 'uint16';
        if (_staticMeshes[cacheKey]) {
            if (textureName !== undefined) _staticMeshes[cacheKey].textureName = textureName;
            if (normalTextureName !== undefined) _staticMeshes[cacheKey].normalTextureName = normalTextureName;
            if (mrTextureName !== undefined) _staticMeshes[cacheKey].mrTextureName = mrTextureName;
            if (aoTextureName !== undefined) _staticMeshes[cacheKey].aoTextureName = aoTextureName;
            if (emissiveTextureName !== undefined) _staticMeshes[cacheKey].emissiveTextureName = emissiveTextureName;
            if (vertexStrideFloats !== undefined) _staticMeshes[cacheKey].vertexStrideFloats = vertexStrideFloats;
            if (indexFormat !== undefined) _staticMeshes[cacheKey].indexFormat = resolvedIndexFormat;
            if (doubleSided !== undefined) _staticMeshes[cacheKey].doubleSided = !!doubleSided;
            _staticMeshes[cacheKey].skinned = !!skinned;
            // keep-if-null:
            // rebind-only calls (morphBytes is empty) must not touch morph buffers or counts,
            // avoiding accidental destruction of GPU morph data during texture rebinding
            if (morphBytes) {
                const morphBuffers = _createMorphBuffer(morphBytes, morphTargetCount, morphVertexCount);
                if (_staticMeshes[cacheKey].morphMetaBuffer) {
                    _queueComputeRetirement(_staticMeshes[cacheKey].morphMetaBuffer);
                    _staticMeshes[cacheKey].morphMetaBuffer = null;
                }
                if (_staticMeshes[cacheKey].morphDataBuffer) {
                    _queueComputeRetirement(_staticMeshes[cacheKey].morphDataBuffer);
                    _staticMeshes[cacheKey].morphDataBuffer = null;
                }
                _staticMeshes[cacheKey].morphTargetCount = morphTargetCount >>> 0;
                _staticMeshes[cacheKey].morphVertexCount = morphVertexCount >>> 0;
                _staticMeshes[cacheKey].morphMetaBuffer = morphBuffers ? morphBuffers.metaBuffer : null;
                _staticMeshes[cacheKey].morphDataBuffer = morphBuffers ? morphBuffers.dataBuffer : null;
            }
            return;
        }
        if (vertexData instanceof Uint8Array) {
            vertexData = new Float32Array(vertexData.buffer, vertexData.byteOffset, vertexData.byteLength / 4);
        } else if (!ArrayBuffer.isView(vertexData)) {
            vertexData = new Float32Array(vertexData);
        }

        if (indexData instanceof Uint8Array) {
            indexData = resolvedIndexFormat === 'uint32'
                ? new Uint32Array(indexData.buffer, indexData.byteOffset, indexData.byteLength / 4)
                : new Uint16Array(indexData.buffer, indexData.byteOffset, indexData.byteLength / 2);
        } else if (!ArrayBuffer.isView(indexData)) {
            indexData = resolvedIndexFormat === 'uint32' ? new Uint32Array(indexData) : new Uint16Array(indexData);
        }

        const vBuf = _device.createBuffer({
            size: Math.max(vertexData.byteLength, 4),
            usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST,
        });
        vBuf.size = Math.max(vertexData.byteLength, 4);
        _device.queue.writeBuffer(vBuf, 0, vertexData);

        const iByteLength = (indexData.byteLength + 3) & ~3;
        const iBuf = _device.createBuffer({
            size: Math.max(iByteLength, 4),
            usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST,
        });
        if (indexData.byteLength === iByteLength) {
            _device.queue.writeBuffer(iBuf, 0, indexData);
        } else {
            const paddedIndexBytes = new Uint8Array(iByteLength);
            paddedIndexBytes.set(new Uint8Array(indexData.buffer, indexData.byteOffset, indexData.byteLength));
            _device.queue.writeBuffer(iBuf, 0, paddedIndexBytes);
        }

        const morphBuffers = _createMorphBuffer(morphBytes, morphTargetCount, morphVertexCount);
        _staticMeshes[cacheKey] = {
            vBuffer: vBuf,
            iBuffer: iBuf,
            morphMetaBuffer: morphBuffers ? morphBuffers.metaBuffer : null,
            morphDataBuffer: morphBuffers ? morphBuffers.dataBuffer : null,
            indexCount: indexData.length,
            boundsCenter: _meshBoundsCenter(vertexData, vertexStrideFloats || 20),
            indexFormat: resolvedIndexFormat,
            doubleSided: !!doubleSided,
            textureName: textureName || 'White',
            normalTextureName: normalTextureName || 'White',
            mrTextureName: mrTextureName || 'White',
            aoTextureName: aoTextureName || 'White',
            emissiveTextureName: emissiveTextureName || 'White',
            vertexStrideFloats: vertexStrideFloats || 20,
            skinned: !!skinned,
            morphTargetCount: morphTargetCount >>> 0,
            morphVertexCount: morphVertexCount >>> 0,
        };
        if (_debugLog) console.log(`[uploadStaticMesh] ${cacheKey} v${vertexData.byteLength} i${indexData.length} ${textureName} sk=${!!skinned}`);
    }

    function uploadStaticMesh(cacheKey, vertexData, indexData, textureName, normalTextureName, mrTextureName, aoTextureName, emissiveTextureName, vertexStrideFloats, indexFormat = 'uint16', doubleSided = false, morphBytes = null, morphTargetCount = 0, morphVertexCount = 0) {
        _uploadStaticMeshInternal(cacheKey, vertexData, indexData, textureName, normalTextureName, mrTextureName, aoTextureName, emissiveTextureName, vertexStrideFloats, indexFormat, doubleSided, false, morphBytes, morphTargetCount, morphVertexCount);
    }

    function uploadStaticSkinnedMesh(cacheKey, vertexData, indexData, textureName, normalTextureName, mrTextureName, aoTextureName, emissiveTextureName, vertexStrideFloats, indexFormat = 'uint16', doubleSided = false, morphBytes = null, morphTargetCount = 0, morphVertexCount = 0) {
        _uploadStaticMeshInternal(cacheKey, vertexData, indexData, textureName, normalTextureName, mrTextureName, aoTextureName, emissiveTextureName, vertexStrideFloats, indexFormat, doubleSided, true, morphBytes, morphTargetCount, morphVertexCount);
        if (_debugLog && _skinnedLogCount < 6) {
            _log(`[WebSkinningJS] uploadStaticSkinnedMesh key=${cacheKey} vBytes=${vertexData ? vertexData.byteLength : 0} iCount=${indexData ? indexData.length : 0}`);
            _skinnedLogCount++;
        }
    }

    // [JSImport] variant (Phase 4):
    // full-parameter form because JSImport has no default arguments.
    // Empty Span values are normalized to null so existing guards in _uploadStaticMeshInternal
    // can be reused
    // (rebind-only early-returns without touching bytes, and morph counts <= 0 are skipped).
    function uploadStaticMeshInterop(cacheKey, vertexBytes, indexBytes,
        textureName, normalTextureName, mrTextureName, aoTextureName, emissiveTextureName,
        vertexStrideFloats, indexFormat, doubleSided, skinned,
        morphBytes, morphTargetCount, morphVertexCount) {
        vertexBytes = _interopToU8(vertexBytes);
        indexBytes = _interopToU8(indexBytes);
        morphBytes = _interopToU8(morphBytes);
        if (vertexBytes && vertexBytes.byteLength === 0) vertexBytes = null;
        if (indexBytes && indexBytes.byteLength === 0) indexBytes = null;
        if (morphBytes && morphBytes.byteLength === 0) morphBytes = null;
        _uploadStaticMeshInternal(cacheKey, vertexBytes, indexBytes,
            textureName, normalTextureName, mrTextureName, aoTextureName, emissiveTextureName,
            vertexStrideFloats, indexFormat, doubleSided, !!skinned,
            morphBytes, morphTargetCount, morphVertexCount);
    }

    // [JSImport] Span<byte> parameters arrive in JS as .NET MemoryView
    // (they have slice/copyTo and are not array-like).
    // Constructing Uint8Array(view) directly yields an all-zero array,
    // so slice() is used to extract an independent Uint8Array copy.
    // Wasm linear-memory views are valid only during the synchronous call
    // and must never be retained across calls.
    function _interopToU8(x) {
        if (!x || x instanceof Uint8Array) return x;
        if (typeof x.copyTo === 'function' && typeof x.slice === 'function') return x.slice();
        return new Uint8Array(x);
    }

    // 已被本帧绘制快照引用的持久缓冲只读；后续写入采用 copy-on-write。
    // 跨帧首次写入仍复用原缓冲，queue 顺序保证上一帧先消费。
    const _sceneBufferReads = new WeakMap();
    function _readSceneBuffer(buffer) {
        if (buffer && _frameStarted) _sceneBufferReads.set(buffer, _computeFrameId);
        return buffer;
    }

    function _writeSceneBuffer(current, bytes, usage, minimum = 4) {
        if (!bytes || bytes.byteLength === 0 || bytes.byteLength % 4 !== 0)
            throw new Error('场景缓冲上传必须非空且按 4 字节对齐');
        const replace = !current || current.size < bytes.byteLength
            || (_frameStarted && _sceneBufferReads.get(current) === _computeFrameId);
        let buffer = current;
        if (replace) buffer = _device.createBuffer({
            size: Math.ceil(Math.max(bytes.byteLength, minimum) / 256) * 256,
            usage: usage | GPUBufferUsage.COPY_DST });
        try { _device.queue.writeBuffer(buffer, 0, bytes); }
        catch (error) { if (replace) _queueComputeRetirement(buffer); throw error; }
        if (replace && current) _queueComputeRetirement(current);
        return buffer;
    }

    function _setSceneVertexBuffer(slot, buffer) {
        _passEncoder.setVertexBuffer(slot, _readSceneBuffer(buffer));
    }

    function uploadSkinnedBones(skinKey, boneBytes) {
        boneBytes = _interopToU8(boneBytes);
        if (!boneBytes || boneBytes.byteLength === 0) return;

        if (boneBytes.byteLength < MIN_SKINNED_BONE_BUFFER_BYTES) {
            const paddedBytes = new Uint8Array(MIN_SKINNED_BONE_BUFFER_BYTES);
            paddedBytes.set(boneBytes);
            boneBytes = paddedBytes;
        }

        const buffer = _writeSceneBuffer(_skinnedBoneBuffers[skinKey], boneBytes, GPUBufferUsage.STORAGE);
        _skinnedBoneBuffers[skinKey] = buffer;

        // 2-3 Step C: roll forward the prev shadow copy
        // (prev first, then current; the order itself is the semantics, and it happens exactly once per frame).
        // If the previous-frame byte length differs from the current frame
        // (bone count changed), treat it as having no history and skip the roll for this frame.
        // The next frame recovers naturally.
        if (_prevSkinnedBoneFrame[skinKey] !== _frameSerial) {
            _prevSkinnedBoneFrame[skinKey] = _frameSerial;
            const prevBytes = _prevSkinnedBoneBytes[skinKey];
            if (prevBytes && prevBytes.byteLength === boneBytes.byteLength) {
                _prevSkinnedBoneBuffers[skinKey] = _writeSceneBuffer(
                    _prevSkinnedBoneBuffers[skinKey], prevBytes, GPUBufferUsage.STORAGE);
            } else {
                const previous = _prevSkinnedBoneBuffers[skinKey];
                delete _prevSkinnedBoneBuffers[skinKey];
                if (previous) _queueComputeRetirement(previous);
            }
        }
        // MemoryView 归调用者所有；保存独立的最后一次上传，不能跨帧持有可变 WASM 内存。
        _prevSkinnedBoneBytes[skinKey] = boneBytes.slice();
        if (_debugLog && _skinnedLogCount < 6) {
            _log(`[WebSkinningJS] uploadSkinnedBones skinKey=${skinKey} boneBytes=${boneBytes.byteLength}`);
            _skinnedLogCount++;
        }
    }

    function updateStaticMeshVertices(cacheKey, vertexBytes) {
        const mesh = _staticMeshes[cacheKey];
        if (!mesh) {
            console.error(`updateStaticMeshVertices: mesh '${cacheKey}' not found`);
            return false;
        }

        vertexBytes = _interopToU8(vertexBytes);
        if (!vertexBytes || vertexBytes.byteLength === 0) return false;

        mesh.vBuffer = _writeSceneBuffer(mesh.vBuffer, vertexBytes, GPUBufferUsage.VERTEX);
        mesh.boundsCenter = _meshBoundsCenter(new Float32Array(vertexBytes.buffer, vertexBytes.byteOffset,
            vertexBytes.byteLength / 4), mesh.vertexStrideFloats || 20);
        return true;
    }

    let _sprite3DVertexBuffer = null;
    function _ensureSprite3DBuffer() {
        if (_sprite3DVertexBuffer) return;
        _sprite3DVertexBuffer = _device.createBuffer({
            size: _SPRITE3D_VERTEX.byteLength,
            usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST,
        });
        _device.queue.writeBuffer(_sprite3DVertexBuffer, 0, _SPRITE3D_VERTEX);
    }

    function requestFrame() {
        return new Promise(resolve => requestAnimationFrame(t => resolve(t)));
    }

    function _waitNextAnimationFrame() {
        return new Promise(resolve => requestAnimationFrame(() => resolve()));
    }

    const _input = { isDown: false, poX: 0, poY: 0, poZDelta: 0 };
    let _pinchPrev = 0;
    let _isPinching = false;
    // Page focus gate:
    // the C# side mirrors this into BaseApp.IsActive, which the game layer
    // (InputManager.Update) requires before any press is processed - the same
    // contract WindowsApp/LinuxApp fulfill from window activation events.
    // A blurred page must report inactive so a press held while switching away
    // cannot read as stuck-down, and focus regained re-enables input.
    let _pageActive = true;

    // Resize handling:
    // apply window-size changes at the beginning of the next frame
    // to avoid GPU texture tearing
    let _needsResize = false;
    let _resizeWidth = 0;
    let _resizeHeight = 0;

    function _onWindowResize() {
        if (!_canvas) return;
        const dpr = window.devicePixelRatio || 1;
        const w = Math.max(1, Math.floor((_canvas.clientWidth || 1280) * dpr));
        const h = Math.max(1, Math.floor((_canvas.clientHeight || 720) * dpr));
        if (w !== _canvas.width || h !== _canvas.height) {
            _needsResize = true;
            _resizeWidth = w;
            _resizeHeight = h;
        }
    }

    function _canvasBackingScale() {
        const rect = _canvas.getBoundingClientRect();
        const sx = rect.width > 0 ? (_canvas.width / rect.width) : 1;
        const sy = rect.height > 0 ? (_canvas.height / rect.height) : 1;
        return { sx, sy, rect };
    }

    function _setPointerPos(e) {
        const { sx, sy, rect } = _canvasBackingScale();
        _input.poX = Math.round((e.clientX - rect.left) * sx);
        _input.poY = Math.round((e.clientY - rect.top) * sy);
    }

    function _pinchDistance(touches) {
        const dx = touches[0].clientX - touches[1].clientX;
        const dy = touches[0].clientY - touches[1].clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }

    function _attachInputHandlers() {
        if (!_canvas) return;

        _canvas.addEventListener('mousedown', (e) => {
            if (e.button !== 0) return;
            _input.isDown = true;
            _setPointerPos(e);
            e.preventDefault();
        });
        window.addEventListener('mouseup', (e) => {
            if (e.button !== 0) return;
            _input.isDown = false;
        });
        _canvas.addEventListener('mousemove', (e) => _setPointerPos(e));

        _canvas.addEventListener('wheel', (e) => {
            _input.poZDelta += e.deltaY;
            e.preventDefault();
        }, { passive: false });

        _canvas.addEventListener('touchstart', (e) => {
            if (e.touches.length === 1) {
                _input.isDown = true;
                _setPointerPos(e.touches[0]);
                _isPinching = false;
                _pinchPrev = 0;
            } else if (e.touches.length >= 2) {
                _isPinching = true;
                _input.isDown = false;
                _pinchPrev = _pinchDistance(e.touches);
            }
            e.preventDefault();
        }, { passive: false });

        _canvas.addEventListener('touchmove', (e) => {
            if (_isPinching && e.touches.length >= 2) {
                const cur = _pinchDistance(e.touches);
                const delta = cur - _pinchPrev;
                _pinchPrev = cur;
                if (delta !== 0) _input.poZDelta += delta;
            } else if (e.touches.length === 1) {
                _input.isDown = true;
                _setPointerPos(e.touches[0]);
            }
            e.preventDefault();
        }, { passive: false });

        _canvas.addEventListener('touchend', (e) => {
            if (e.touches.length === 0) {
                _input.isDown = false;
                _isPinching = false;
                _pinchPrev = 0;
            } else if (e.touches.length < 2) {
                _isPinching = false;
                _pinchPrev = 0;
            }
            e.preventDefault();
        }, { passive: false });

        _canvas.addEventListener('touchcancel', () => {
            _input.isDown = false;
            _isPinching = false;
            _pinchPrev = 0;
        });

        _canvas.addEventListener('gesturestart', (e) => e.preventDefault());
        _canvas.addEventListener('contextmenu', (e) => e.preventDefault());

        // Page focus tracking (see _pageActive): initial state comes from the live
        // document, then window focus/blur and tab visibility keep it current.
        _pageActive = document.hasFocus();
        window.addEventListener('focus', () => { _pageActive = true; });
        window.addEventListener('blur', () => {
            _pageActive = false;
            // A mouseup missed while the page was away must not be read as a
            // held press on the next activation (matching Windows pointer-capture-lost
            // and Linux focus-lost handling).
            _input.isDown = false;
            _isPinching = false;
            _pinchPrev = 0;
        });
        document.addEventListener('visibilitychange', () => {
            _pageActive = !document.hidden && document.hasFocus();
        });

        _attachKeyboardHandlers();
    }

    // ── Keyboard input: window-level physical-key capture ──
    // event.code is the physical key (layout independent), matching the Windows
    // VirtualKey and Linux SDL_Scancode semantics. Order must match
    // WebKeyboardService.MappedKeys exactly; keyboardSnapshot returns
    // [down(0/1)×N, pressed×N, released×N] in this order.
    const KEY_CODES = [
        'KeyA', 'KeyB', 'KeyC', 'KeyD', 'KeyE', 'KeyF', 'KeyG', 'KeyH', 'KeyI',
        'KeyJ', 'KeyK', 'KeyL', 'KeyM', 'KeyN', 'KeyO', 'KeyP', 'KeyQ', 'KeyR',
        'KeyS', 'KeyT', 'KeyU', 'KeyV', 'KeyW', 'KeyX', 'KeyY', 'KeyZ',
        'Digit0', 'Digit1', 'Digit2', 'Digit3', 'Digit4', 'Digit5', 'Digit6', 'Digit7', 'Digit8', 'Digit9',
        'Space', 'Enter', 'Escape', 'Tab', 'Backspace',
        'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown',
        'ShiftLeft', 'ShiftRight', 'ControlLeft', 'ControlRight', 'AltLeft', 'AltRight',
        'F1', 'F2', 'F3', 'F4', 'F5', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12',
    ];
    const _keyStates = new Map(); // code -> { down, pressed, released }

    function _keyState(code) {
        let s = _keyStates.get(code);
        if (!s) { s = { down: false, pressed: 0, released: 0 }; _keyStates.set(code, s); }
        return s;
    }

    function _attachKeyboardHandlers() {
        window.addEventListener('keydown', (e) => {
            if (e.isComposing || !KEY_CODES.includes(e.code)) return;
            const s = _keyState(e.code);
            // Auto-repeat keydown (e.repeat) is deduplicated by the down state, so the
            // press counter only advances on a physical press.
            if (!s.down) { s.down = true; s.pressed++; }
            e.preventDefault();
        });
        window.addEventListener('keyup', (e) => {
            const s = _keyStates.get(e.code);
            if (s && s.down) { s.down = false; s.released++; }
        });
        window.addEventListener('blur', () => {
            // Keys released while the page is unfocused never arrive; clear them all.
            for (const s of _keyStates.values()) {
                if (s.down) { s.down = false; s.released++; }
            }
        });
    }

    function keyboardSnapshot() {
        const n = KEY_CODES.length;
        const out = new Array(n * 3).fill(0);
        for (let i = 0; i < n; i++) {
            const s = _keyStates.get(KEY_CODES[i]);
            if (s) {
                out[i] = s.down ? 1 : 0;
                out[n + i] = s.pressed;
                out[2 * n + i] = s.released;
            }
        }
        return out;
    }

    function pollInput() {
        const snapshot = {
            isDown: _input.isDown,
            poX: _input.poX,
            poY: _input.poY,
            poZDelta: _input.poZDelta,
            active: _pageActive,
        };
        _input.poZDelta = 0;
        return snapshot;
    }

    // [JSImport] variant (Phase 2):
    // return a plain numeric array and bypass JSON serialization/deserialization.
    // Layout: [isDown(0/1), poX, poY, poZDelta, active(0/1)].
    // After reading, poZDelta is cleared, matching pollInput semantics.
    // The trailing active flag drives BaseApp.IsActive on the C# side; keep this
    // in sync with WebGPUInterop.PollInput (older copies may omit it, in which
    // case the C# reader falls back to always-active).
    function pollInputPacked() {
        const packed = [_input.isDown ? 1 : 0, _input.poX, _input.poY, _input.poZDelta, _pageActive ? 1 : 0];
        _input.poZDelta = 0;
        return packed;
    }

    // Static scratch TypedArray to avoid per-frame allocations
    const _SPRITE3D_VERTEX = new Float32Array([
        // pos(3)+uv(2)+normal(3)+tangent(4)+joints(4)+weights(4) = 20 floats
        -0.5,  0.5, 0,  0, 0,  0, 0, -1,  1, 0, 0, 1,  0, 0, 0, 0,  0, 0, 0, 0,
         0.5,  0.5, 0,  1, 0,  0, 0, -1,  1, 0, 0, 1,  0, 0, 0, 0,  0, 0, 0, 0,
        -0.5, -0.5, 0,  0, 1,  0, 0, -1,  1, 0, 0, 1,  0, 0, 0, 0,  0, 0, 0, 0,
         0.5,  0.5, 0,  1, 0,  0, 0, -1,  1, 0, 0, 1,  0, 0, 0, 0,  0, 0, 0, 0,
         0.5, -0.5, 0,  1, 1,  0, 0, -1,  1, 0, 0, 1,  0, 0, 0, 0,  0, 0, 0, 0,
        -0.5, -0.5, 0,  0, 1,  0, 0, -1,  1, 0, 0, 1,  0, 0, 0, 0,  0, 0, 0, 0,
    ]);

    // Sprite2D scratch:
    // vertices + uniform are rewritten every frame
    // (108 floats aligned to WGSL Uniforms;
    // [48..83]/[104..107] are retired reserved slots from 1-2 -
    // camera / lighting / exposure now come from the shared UBO at binding(10),
    // so keeping them at 0 is sufficient)
    const _SPRITE2D_VERTEX_SCRATCH = new Float32Array(120);
    const _SPRITE2D_UNIFORM_SCRATCH = new Float32Array(108);
    const _SPRITE2D_UNIFORM_I32 = new Int32Array(_SPRITE2D_UNIFORM_SCRATCH.buffer, 96 * 4, 4);
    _SPRITE2D_UNIFORM_SCRATCH[0]=1; _SPRITE2D_UNIFORM_SCRATCH[5]=1; _SPRITE2D_UNIFORM_SCRATCH[10]=1; _SPRITE2D_UNIFORM_SCRATCH[15]=1;
    _SPRITE2D_UNIFORM_SCRATCH[16]=1; _SPRITE2D_UNIFORM_SCRATCH[21]=1; _SPRITE2D_UNIFORM_SCRATCH[26]=1; _SPRITE2D_UNIFORM_SCRATCH[31]=1;
    _SPRITE2D_UNIFORM_SCRATCH[32]=1; _SPRITE2D_UNIFORM_SCRATCH[37]=1; _SPRITE2D_UNIFORM_SCRATCH[42]=1; _SPRITE2D_UNIFORM_SCRATCH[47]=1;
    _SPRITE2D_UNIFORM_SCRATCH[91]=1; _SPRITE2D_UNIFORM_SCRATCH[93]=1; _SPRITE2D_UNIFORM_SCRATCH[95]=0.5;
    _SPRITE2D_UNIFORM_I32.fill(0); _SPRITE2D_UNIFORM_I32[2]=2;

    // Dedicated scratch for text GPU instancing:
    // separate from Sprite2D scratch so the instancing bit in flags.w
    // cannot leak into later sprite draws.
    // 108 floats aligned to WGSL Uniforms;
    // [104] is the retired hdrExposure slot
    // (1-2 contract 8: text exposure now reads uLights.params0.y).
    const _TEXT_UNIFORM_SCRATCH = new Float32Array(108);
    const _TEXT_UNIFORM_I32 = new Int32Array(_TEXT_UNIFORM_SCRATCH.buffer, 96 * 4, 4);
    _TEXT_UNIFORM_SCRATCH[0]=1; _TEXT_UNIFORM_SCRATCH[5]=1; _TEXT_UNIFORM_SCRATCH[10]=1; _TEXT_UNIFORM_SCRATCH[15]=1;
    _TEXT_UNIFORM_SCRATCH[16]=1; _TEXT_UNIFORM_SCRATCH[21]=1; _TEXT_UNIFORM_SCRATCH[26]=1; _TEXT_UNIFORM_SCRATCH[31]=1;
    _TEXT_UNIFORM_SCRATCH[32]=1; _TEXT_UNIFORM_SCRATCH[37]=1; _TEXT_UNIFORM_SCRATCH[42]=1; _TEXT_UNIFORM_SCRATCH[47]=1;
    _TEXT_UNIFORM_SCRATCH[91]=1; _TEXT_UNIFORM_SCRATCH[93]=1; _TEXT_UNIFORM_SCRATCH[95]=0.5;
    // flags:
    // x = reserved 0 (old lightCount, punctual count now reads uLights.params0.x),
    // y = renderMode=2 (TextMsdf),
    // z = alphaMode=2 (Blend),
    // w = 16 (GPU instancing bit)
    _TEXT_UNIFORM_I32.fill(0); _TEXT_UNIFORM_I32[1]=2; _TEXT_UNIFORM_I32[2]=2; _TEXT_UNIFORM_I32[3]=16;

    // FPS / frame-time statistics
    let _fpsLastSec = 0, _fpsFrameCount = 0, _fpsMaxFrameMs = 0, _fpsFrameStartMs = 0;

    // overlayShaderSource is accepted but not yet consumed: the C# side already derives an HDR_CHAIN=false module for
    // the Overlay family and passes it here, while this backend still bakes the overlay PSOs from the main module.
    // The parameter is declared so the positional slot is visible rather than silently dropped, which is what let the
    // trailing maxAnisotropy argument be added safely behind it.
    async function initialize(canvasId, shaderSource, blitShaderSource, hdrSceneColor, shadowDepthBias, shadowSlopeBias, velocityOutput, overlayShaderSource, maxAnisotropy = 1, fogSampling = false, normalOutput = false) {
        if (_device) throw new Error('Reload the page before starting a second WebGPU host');
        _mesh3DShader = shaderSource;
        _blitShaderWGSL = blitShaderSource;
        // 1-5 contract 4:
        // bias is finalized during initialization and baked into the shadow pipeline
        // (D3D12 DepthBias integer semantics = WebGPU depthBias)
        _shadowDepthBias = shadowDepthBias | 0;
        _shadowSlopeBias = shadowSlopeBias || 0;
        // 2-3 contract clauses 1/3:
        // MotionVectors tier is already decided on the C# side and must be assigned before
        // _createMesh3DPipeline.
        // It determines whether the velocity variant table is baked.
        // The WGSL VELOCITY_OUTPUT constant has already been injected in sync
        // through C# string replacement.
        _velocityOutput = !!velocityOutput;
        // A0.2: the WGSL NORMAL_OUTPUT constant has been injected in sync by the same C# substitution.
        _normalOutput = _velocityOutput && !!normalOutput;
        if (!navigator.gpu) throw new Error('WebGPU not supported.');
        const adapter = await navigator.gpu.requestAdapter();
        if (!adapter) throw new Error('WebGPU adapter unavailable.');

        // 1-8 Step 0 feature negotiation:
        // using r16float/r8unorm/rg16float as STORAGE_BINDING belongs to texture-formats-tier1,
        // and trilinear filtering for r32float belongs to float32-filterable.
        // Neither is a core feature.
        // Both must first be probed from adapter.features and only then conditionally added to
        // requiredFeatures; requiring them unconditionally would make requestDevice reject
        // and the whole Web backend would fail to start.
        // The probe results are stored in _gpuFeatures
        // and consumed only by the downgrade chain in _mapStorageFormat
        // (see Compute.cs decision 3).
        const _optionalFeatures = ['texture-formats-tier1', 'float32-filterable'];
        const _requiredFeatures = [];
        for (const f of _optionalFeatures)
            if (adapter.features.has(f)) { _requiredFeatures.push(f); _gpuFeatures.add(f); }

        _device = await adapter.requestDevice({ requiredFeatures: _requiredFeatures });
        _gpuLimits.maxComputeInvocationsPerWorkgroup = _device.limits.maxComputeInvocationsPerWorkgroup | 0;
        _gpuLimits.maxTextureDimension3D = _device.limits.maxTextureDimension3D | 0;
        console.log(`[WebGPU] adapter features = [${[...adapter.features].join(', ')}]`);
        console.log(`[WebGPU] enabled = [${_requiredFeatures.join(', ')}], `
            + `maxComputeInvocationsPerWorkgroup = ${_gpuLimits.maxComputeInvocationsPerWorkgroup}, `
            + `maxTextureDimension3D = ${_gpuLimits.maxTextureDimension3D}`);

        _device.lost.then((info) => {
            _instancedDiag.deviceLost = true;
            _instancedDiag.deviceLostReason = `reason=${info.reason}, msg=${info.message}`;
            console.error(`WebGPU device lost: ${info.reason}`);
        });
        _device.addEventListener('uncapturederror', (e) => {
            _instancedDiag.uncapturedError = e?.error?.message || 'uncapturederror';
        });

        _canvas = document.getElementById(canvasId);
        if (!_canvas) throw new Error(`Canvas '${canvasId}' not found.`);

        // backing size = CSS size x devicePixelRatio
        const dpr = window.devicePixelRatio || 1;
        _canvas.width = Math.max(1, Math.floor((_canvas.clientWidth || 1280) * dpr));
        _canvas.height = Math.max(1, Math.floor((_canvas.clientHeight || 720) * dpr));

        _context = _canvas.getContext('webgpu');
        _format = navigator.gpu.getPreferredCanvasFormat();
        _sceneFormat = hdrSceneColor ? 'rgba16float' : _format;

        _context.configure({
            device: _device,
            format: _format,
            alphaMode: 'premultiplied',
            usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC,
        });

        // 2-6 clause 5: mipmapFilter defaults to 'nearest' in WebGPU, which would snap between levels at the LOD
        // boundary and make a mipmapped texture pop more visibly than an unfiltered one - the opposite of the point.
        // It has no effect on the single-level textures this sampler also serves, so it is set unconditionally.
        //
        // 2-6 clause 6: maxAnisotropy is only honoured when magFilter, minFilter and mipmapFilter are all 'linear',
        // which the three lines below guarantee. The value is already clamped to [1,16] on the C# side; WebGPU clamps
        // further to whatever the implementation supports, so an unsupported count degrades rather than throwing.
        const _aniso = Math.max(1, Math.min(16, maxAnisotropy | 0));
        _samplers['linear'] = _device.createSampler({
            magFilter: 'linear',
            minFilter: 'linear',
            mipmapFilter: 'linear',
            addressModeU: 'clamp-to-edge',
            addressModeV: 'clamp-to-edge',
            maxAnisotropy: _aniso,
        });

        // 2-5 Step C: wrap sampler for cloud noise (binding 20, Repeat).
        // The noise tiles periodically, and wind offsets can push uv outside [0,1].
        // Using Clamp would stretch the outermost column into a static horizontal band in the sky
        // (same as DX s2 / Vulkan bindings[19]).
        _samplers['repeat'] = _device.createSampler({
            magFilter: 'linear',
            minFilter: 'linear',
            mipmapFilter: 'linear',
            addressModeU: 'repeat',
            addressModeV: 'repeat',
        });

        // 1-5: comparison sampler
        // (hardware PCF, consumed by textureSampleCompareLevel)
        // plus a 1x1 dummy shadow view
        // as bind-group fallback before the atlas RT exists.
        // When shadows are fully disabled, ShadowParams stay zero and the shader does not sample.
        _samplers['shadow'] = _device.createSampler({
            magFilter: 'linear',
            minFilter: 'linear',
            addressModeU: 'clamp-to-edge',
            addressModeV: 'clamp-to-edge',
            compare: 'less-equal',
        });
        // 1-5 clause 15: non-filtering sampler for reading the binding-11 atlas as a plain depth value, which the
        // comparison sampler at binding 12 cannot do - it yields pass/fail. WebGPU only permits non-comparison sampling of a
        // depth texture through a non-filtering sampler, which is also the correct filter here: a depth averaged across a
        // silhouette lies on no surface, so a blocker distance derived from it would be fiction. Nearest on both filters is
        // what makes it non-filtering as far as validation is concerned.
        _samplers['shadowPoint'] = _device.createSampler({
            magFilter: 'nearest',
            minFilter: 'nearest',
            addressModeU: 'clamp-to-edge',
            addressModeV: 'clamp-to-edge',
        });
        _defaultShadowTexture = _device.createTexture({
            size: [1, 1], format: 'depth32float',
            usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
        });
        _defaultShadowView = _defaultShadowTexture.createView();

        // 1-7: 1x1 all-black fallback cube (6 layers).
        // WGSL-side uEnvCube is a static fs_main reference, and this backend does not use layout:'auto',
        // so every entry must have a resource.
        // Even without an environment map, a legal cube view must still be provided,
        // so it is created once here and kept resident.
        // All-black means that even if envParams.w were accidentally enabled,
        // it would only add zero and would not contaminate the image.
        _defaultEnvCubeTexture = _device.createTexture({
            size: [1, 1, 6], format: 'rgba8unorm',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
        });
        _device.queue.writeTexture(
            { texture: _defaultEnvCubeTexture },
            new Uint8Array(4 * 6),
            { bytesPerRow: 4, rowsPerImage: 1 },
            { width: 1, height: 1, depthOrArrayLayers: 6 });
        _defaultEnvCubeView = _defaultEnvCubeTexture.createView({ dimension: 'cube' });

        // 2-5 Step E: 1x1x1 all-zero 3D fallback.
        // uAerialLut at binding 19 must always remain valid, following the same fallback pattern as envCube.
        // All-zero is the additive identity element:
        // even if apParams0.x gating failed unexpectedly,
        // the signal would still pass through unchanged and not contaminate the image.
        _defaultAerialLutTexture = _device.createTexture({
            size: [1, 1, 1], dimension: '3d', format: 'rgba16float',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
        });
        _device.queue.writeTexture(
            { texture: _defaultAerialLutTexture },
            new Uint8Array(8),
            { bytesPerRow: 8, rowsPerImage: 1 },
            { width: 1, height: 1, depthOrArrayLayers: 1 });
        _defaultAerialLutView = _defaultAerialLutTexture.createView({ dimension: '3d' });

        await _createWhiteTexture();

        // Create the unified pipeline
        // both skeletal skinning and instancing are already covered by it
        if (fogSampling && hdrSceneColor) _initializeFogSampling();
        _createMesh3DPipeline();

        // Identity instance buffer (80B)
        _identityInstanceBuffer = _device.createBuffer({ size: 80, usage: GPUBufferUsage.VERTEX, mappedAtCreation: true });
        const idInst = new Float32Array(_identityInstanceBuffer.getMappedRange());
        idInst.fill(0); idInst[0] = 1; idInst[5] = 1; idInst[10] = 1; idInst[15] = 1;
        _identityInstanceBuffer.unmap();

        // Default bone / morph buffers
        _defaultBoneBuffer = _device.createBuffer({ size: MIN_SKINNED_BONE_BUFFER_BYTES, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        _writeIdentityBones(_defaultBoneBuffer);
        _defaultMorphMetaBuffer = _device.createBuffer({ size: 16, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        _device.queue.writeBuffer(_defaultMorphMetaBuffer, 0, new Uint32Array(4));
        _defaultMorphDataBuffer = _device.createBuffer({ size: 4, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        _device.queue.writeBuffer(_defaultMorphDataBuffer, 0, new Uint32Array(1));
        // 2-3 Step C: fallback sentinel for the prev-instance byte stream
        // (one instance = 5 vec4 = 80B, all zeros)
        _defaultPrevInstanceBuffer = _device.createBuffer({ size: 80, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        _device.queue.writeBuffer(_defaultPrevInstanceBuffer, 0, new Float32Array(20));

        _attachInputHandlers();
        window.addEventListener('resize', _onWindowResize);

        _log('SeasonEngine WebGPU initialized successfully.');
    }

    async function _createWhiteTexture() {
        const size = 4, data = new Uint8Array(size * size * 4); data.fill(255);
        const texture = _device.createTexture({
            size: [size, size], format: 'rgba8unorm',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
        });
        _device.queue.writeTexture({ texture }, data, { bytesPerRow: size * 4 }, { width: size, height: size });
        _textures['White'] = texture;
        _textureViews['White'] = texture.createView();
    }

    // Texture loading

    function _getTextureResult(name, success = true) {
        const meta = _textureMeta[name] || { width: 0, height: 0 };
        return { success, width: meta.width || 0, height: meta.height || 0 };
    }

    const _sceneTextureSources = new WeakMap(), _sceneTextureReads = new WeakMap(), _atlasShadows = new WeakMap();
    function _readSceneTextureView(view) {
        const texture = view && _sceneTextureSources.get(view);
        if (texture && _frameStarted) _sceneTextureReads.set(texture, _computeFrameId);
        return view;
    }

    function _sceneTextureInUse(texture) {
        return _frameStarted && _sceneTextureReads.get(texture) === _computeFrameId;
    }

    function _storeTexture(name, texture, width, height, mipLevelCount = 1) {
        const view = texture.createView(), previous = _textures[name];
        _textures[name] = texture;
        _textureViews[name] = view;
        _sceneTextureSources.set(view, texture);
        _textureMeta[name] = { width, height, mipLevelCount };
        if (previous && previous !== texture) {
            _invalidateComputeBindGroups();
            _queueComputeRetirement(previous);
        }
        return _getTextureResult(name, true);
    }

    // 2-6 clause 3: the level geometry is not transported across interop. Every backend derives it from the same
    // halving rule MipChain.Build uses - level n is max(1, w0 >> n) by max(1, h0 >> n), tightly packed in order with
    // no padding - so shipping a table alongside the bytes would only create a second source of truth able to
    // disagree with them. WebGPU needs no re-pitch either: bytesPerRow is stated per level.
    function _packedChainByteLength(width, height, mipLevelCount) {
        let total = 0, w = width, h = height;
        for (let level = 0; level < mipLevelCount; level++) {
            total += w * h * 4;
            w = Math.max(1, w >> 1);
            h = Math.max(1, h >> 1);
        }
        return total;
    }

    // One writeTexture per subresource. Degenerates to the pre-2-6 single call whenever mipLevelCount is 1.
    function _writeTextureLevels(texture, bytes, width, height, mipLevelCount) {
        let offset = 0, w = width, h = height;
        for (let level = 0; level < mipLevelCount; level++) {
            _device.queue.writeTexture(
                { texture, mipLevel: level },
                bytes,
                { offset, bytesPerRow: w * 4, rowsPerImage: h },
                { width: w, height: h, depthOrArrayLayers: 1 });
            offset += w * h * 4;
            w = Math.max(1, w >> 1);
            h = Math.max(1, h >> 1);
        }
    }

    function _createTextureFromExternalSource(name, source, width, height) {
        const texture = _device.createTexture({
            size: [width, height], format: 'rgba8unorm',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
        });
        _device.queue.copyExternalImageToTexture({ source }, { texture }, { width, height });
        return _storeTexture(name, texture, width, height);
    }

    async function loadTexture(name, imageUrl, deferDecodeToNextFrame = false) {
        if (_textures[name]) return _getTextureResult(name, true);
        try {
            const response = await fetch(imageUrl);
            if (!response.ok) throw new Error(`HTTP ${response.status}: ${imageUrl}`);
            const blob = await response.blob();
            if (deferDecodeToNextFrame) await _waitNextAnimationFrame();
            const bitmap = await createImageBitmap(blob, { premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
            const result = _createTextureFromExternalSource(name, bitmap, bitmap.width, bitmap.height);
            bitmap.close();
            return result;
        } catch (e) {
            console.error(`Failed to load texture '${name}': ${e}`);
            return { success: false, width: 0, height: 0 };
        }
    }

    function updateTexturePixels(name, rgbaPixels, width, height) {
        const tex = _textures[name];
        if (!tex) {
            console.error(`updateTexturePixels: texture '${name}' not found`);
            return false;
        }
        rgbaPixels = _interopToU8(rgbaPixels);
        // 2-6 clause 4: a texture that owns a chain is fed the whole packed block, so the expected size is the sum
        // over levels rather than level 0 alone. Writing only level 0 would leave the lower levels showing the
        // previous content at distance, which is much harder to diagnose than having no mipmaps at all.
        const mipLevelCount = (_textureMeta[name] || {}).mipLevelCount || 1;
        const expectedSize = _packedChainByteLength(width, height, mipLevelCount);
        if (!rgbaPixels || rgbaPixels.length !== expectedSize
            || width !== tex.width || height !== tex.height) {
            console.error(`updateTexturePixels: size mismatch, got ${rgbaPixels?.length}, expected ${expectedSize}`);
            return false;
        }

        if (_sceneTextureInUse(tex))
            return createTextureFromPixels(name, rgbaPixels, width, height, true, mipLevelCount).success;
        _writeTextureLevels(tex, rgbaPixels, width, height, mipLevelCount);
        return true;
    }

    // mipLevelCount > 1 means rgbaPixels carries the packed chain produced by MipChain.Build rather than level 0
    // alone. The parameter is trailing and defaults to 1, so every pre-2-6 five-argument call produces a
    // byte-identical descriptor and a single writeTexture.
    function createTextureFromPixels(name, rgbaPixels, width, height, forceNew = false, mipLevelCount = 1) {
        mipLevelCount = Math.max(1, mipLevelCount | 0);
        if (!forceNew && _textures[name]) {
            const meta = _textureMeta[name] || {};
            // The level count is part of the reuse test: a texture created single-level cannot absorb a chain,
            // because mipLevelCount is fixed at creation time.
            if (meta.width === width && meta.height === height && (meta.mipLevelCount || 1) === mipLevelCount)
                return updateTexturePixels(name, rgbaPixels, width, height) ? { success: true, width, height } : { success: false, width: 0, height: 0 };
        }
        rgbaPixels = _interopToU8(rgbaPixels);
        if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1
            || mipLevelCount > Math.floor(Math.log2(Math.max(width, height))) + 1
            || !rgbaPixels || rgbaPixels.length !== _packedChainByteLength(width, height, mipLevelCount))
            return { success: false, width: 0, height: 0 };
        const texture = _device.createTexture({
            size: [width, height], format: 'rgba8unorm', mipLevelCount,
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
        });
        try {
            _writeTextureLevels(texture, rgbaPixels, width, height, mipLevelCount);
            return _storeTexture(name, texture, width, height, mipLevelCount);
        } catch (error) { _queueComputeRetirement(texture); throw error; }
    }

    // 1-7 cubemap (contract clause 3):
    // six-layer texture (depthOrArrayLayers=6) + viewDimension:'cube'.
    // faceBytes is a tightly packed contiguous RGBA8 block assembled on the C# side
    // in CubeFace declaration order (+X,-X,+Y,-Y,+Z,-Z),
    // and its length must be exactly size*size*4*6.
    // Each face is uploaded by one writeTexture call,
    // using origin.z to select the layer and offset to locate the slice.
    // This naturally matches the 0..5 semantics of D3D12 subresources / Vulkan arrayLayer / Metal slice
    // with no reordering or flipping.
    // Single mip only
    // (1-7 does not perform GGX prefiltering and the specular term samples only LOD0).
    // If the same name already exists, reuse it, matching the other three backends.
    function createTextureCube(name, size, faceBytes) {
        if (!_device) return false;
        if (_textureCubes[name]) return true;
        faceBytes = _interopToU8(faceBytes);
        const bytesPerRow = size * 4;
        const faceBytesLength = bytesPerRow * size;
        if (!faceBytes || faceBytes.byteLength !== faceBytesLength * 6) {
            console.error(`createTextureCube: '${name}' byte size mismatch (expected ${faceBytesLength * 6}, got ${faceBytes ? faceBytes.byteLength : 0})`);
            return false;
        }
        const texture = _device.createTexture({
            size: [size, size, 6], format: 'rgba8unorm',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
        });
        for (let face = 0; face < 6; face++) {
            _device.queue.writeTexture(
                { texture, origin: { x: 0, y: 0, z: face } },
                faceBytes,
                { offset: face * faceBytesLength, bytesPerRow, rowsPerImage: size },
                { width: size, height: size, depthOrArrayLayers: 1 });
        }
        _textureCubes[name] = { texture, view: texture.createView({ dimension: 'cube' }), size };
        return true;
    }

    // Stub: texture-reference updates are resolved dynamically during draw
    function updateSpriteTexture() { return true; }

    function updateMeshTexture(cacheKey, slot, newTextureName) {
        const mesh = _staticMeshes[cacheKey];
        if (!mesh) { console.error(`updateMeshTexture: mesh '${cacheKey}' not found`); return false; }
        const texNames = ['textureName', 'normalTextureName', 'mrTextureName', 'aoTextureName', 'emissiveTextureName'];
        if (slot >= 0 && slot < texNames.length) mesh[texNames[slot]] = newTextureName || 'White';
        return true;
    }

    function updateMeshMaterialParams() { return true; }

    async function uploadEncodedTexture(name, encodedBytes, mimeType, deferDecodeToNextFrame = false) {
        if (_textures[name]) return _getTextureResult(name, true);
        try {
            if (!(encodedBytes instanceof Uint8Array)) encodedBytes = new Uint8Array(encodedBytes);
            const blob = mimeType ? new Blob([encodedBytes], { type: mimeType }) : new Blob([encodedBytes]);
            if (deferDecodeToNextFrame) await _waitNextAnimationFrame();
            const bitmap = await createImageBitmap(blob);
            const result = _createTextureFromExternalSource(name, bitmap, bitmap.width, bitmap.height);
            bitmap.close();
            return result;
        } catch (e) {
            console.error(`Failed to upload encoded texture '${name}': ${e}`);
            return { success: false, width: 0, height: 0 };
        }
    }

    function uploadGlyphTexture(name, rgbaData, width, height) {
        if (_textures[name]) return _getTextureResult(name, true);
        if (!(rgbaData instanceof Uint8Array)) rgbaData = new Uint8Array(rgbaData);
        return createTextureFromPixels(name, rgbaData, width, height, false);
    }

    function createAtlasTexture(name, width, height) {
        if (_textures[name]) return _getTextureResult(name, true);
        const texture = _device.createTexture({
            size: [width, height], format: 'rgba8unorm',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
        });
        const result = _storeTexture(name, texture, width, height);
        _atlasShadows.set(texture, new Uint8Array(width * height * 4));
        return result;
    }

    function uploadGlyphAtlasSubRects(atlasName, rgbaData, atlasWidth, rects) {
        _uploadAtlasRects(atlasName, _interopToU8(rgbaData), rects, atlasWidth);
    }

    // [JSImport] compact dirty-rect upload (Phase 4):
    // packedData concatenates row data of each rect in rects order (bytesPerRow=rw*4),
    // replacing full-image 16MB transfers.
    // The old uploadGlyphAtlasSubRects addressed data through full-image offsets
    // and was forced to upload the whole image.
    function uploadGlyphAtlasPackedRects(atlasName, packedData, rects) {
        _uploadAtlasRects(atlasName, _interopToU8(packedData), rects, 0);
    }

    function _uploadAtlasRects(name, bytes, rects, sourceWidth) {
        const texture = _textures[name], shadow = texture && _atlasShadows.get(texture);
        if (!texture || !shadow) throw new Error('字形更新必须使用已创建的图集');
        if (!bytes || !rects || rects.length % 4 !== 0) throw new Error('无效字形更新数据');
        let expected = 0;
        for (let i = 0; i < rects.length; i += 4) {
            const x = rects[i], y = rects[i+1], w = rects[i+2], h = rects[i+3];
            if (![x,y,w,h].every(Number.isInteger) || x < 0 || y < 0 || w < 1 || h < 1
                || x+w > texture.width || y+h > texture.height) throw new Error('字形矩形越界');
            expected += w*h*4;
        }
        if (sourceWidth ? sourceWidth !== texture.width || bytes.length !== shadow.length : bytes.length !== expected)
            throw new Error('字形更新字节数不匹配');
        if (rects.length === 0) return;
        const replace = _sceneTextureInUse(texture), pixels = replace ? shadow.slice() : shadow;
        let packedOffset = 0;
        for (let i = 0; i < rects.length; i += 4) {
            const x = rects[i], y = rects[i+1], w = rects[i+2], h = rects[i+3];
            const stride = (sourceWidth || w)*4, offset = sourceWidth ? (y*sourceWidth+x)*4 : packedOffset;
            if (!replace) _device.queue.writeTexture({ texture, origin:[x,y,0] }, bytes,
                { bytesPerRow:stride, offset }, { width:w, height:h, depthOrArrayLayers:1 });
            for (let row = 0; row < h; row++) pixels.set(bytes.subarray(offset+row*stride,offset+row*stride+w*4),
                ((y+row)*texture.width+x)*4);
            packedOffset += w*h*4;
        }
        if (replace) {
            // 图集保留 CPU 影子以复制未更新区域；不在已捕获视图上修改部分纹理。
            createTextureFromPixels(name, pixels, texture.width, texture.height, true);
            _atlasShadows.set(_textures[name], pixels);
        }
    }

    // Image encode / decode

    /** Decode image -> RGBA8 */
    async function decodeImageBytes(encodedBytes) {
        if (!(encodedBytes instanceof Uint8Array)) encodedBytes = new Uint8Array(encodedBytes);
        const bitmap = await createImageBitmap(new Blob([encodedBytes]));
        const width = bitmap.width, height = bitmap.height;
        const canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(width, height) : document.createElement('canvas');
        canvas.width = width; canvas.height = height;
        const ctx = canvas.getContext('2d');
        ctx.drawImage(bitmap, 0, 0);
        const imageData = ctx.getImageData(0, 0, width, height);
        bitmap.close();
        return { width, height, rgbaData: new Uint8Array(imageData.data.buffer, imageData.data.byteOffset, imageData.data.byteLength) };
    }

    /** Encode RGBA8 -> image */
    async function encodeImageBytes(rgbaData, width, height, format, quality) {
        if (!(rgbaData instanceof Uint8Array)) rgbaData = new Uint8Array(rgbaData);
        const canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(width, height) : document.createElement('canvas');
        canvas.width = width; canvas.height = height;
        const ctx = canvas.getContext('2d');
        ctx.putImageData(new ImageData(new Uint8ClampedArray(rgbaData.buffer, rgbaData.byteOffset, rgbaData.byteLength), width, height), 0, 0);
        const mimeMap = { jpeg: 'image/jpeg', png: 'image/png', bmp: 'image/bmp', gif: 'image/gif', tiff: 'image/tiff' };
        const mimeType = mimeMap[format] || 'image/png';
        const encodeQuality = Math.max(0, Math.min(1, (quality || 90) / 100));
        let blob;
        if (typeof canvas.convertToBlob === 'function') {
            blob = await canvas.convertToBlob({ type: mimeType, quality: encodeQuality });
        } else if (typeof canvas.toBlob === 'function') {
            blob = await new Promise((resolve, reject) => {
                canvas.toBlob(r => r ? resolve(r) : reject(new Error(`Can't encode as '${format}'`)), mimeType, encodeQuality);
            });
        } else throw new Error('Canvas blob encoding not supported.');
        const buffer = await blob.arrayBuffer();
        return new Uint8Array(buffer);
    }

    let _renderVideoModulePromise = null;
    async function _loadRenderVideoModule() {
        if (!_renderVideoModulePromise) {
            _renderVideoModulePromise = import('https://cdn.jsdelivr.net/npm/render-video@0.0.5/src/index.mjs');
        }
        return _renderVideoModulePromise;
    }

    /** Encode RGBA8 -> H.264 MP4 */
    async function encodeH264Video(rgbaFrames, width, height, fps, quality) {
        if (!Array.isArray(rgbaFrames) || rgbaFrames.length === 0) throw new Error('encodeH264Video requires frames.');
        if (!('VideoEncoder' in window)) throw new Error('encodeH264Video: no WebCodecs VideoEncoder.');
        const module = await _loadRenderVideoModule();
        const renderVideo = module?.default || module?.renderVideo;
        if (typeof renderVideo !== 'function') throw new Error('render-video module not loaded.');
        fps = Math.max(1, fps || 16);
        const RVFPS = 25, durationSec = rgbaFrames.length / fps, targetCount = Math.max(1, Math.round(durationSec * RVFPS));
        const canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(width, height) : document.createElement('canvas');
        canvas.width = width; canvas.height = height;
        const ctx = canvas.getContext('2d');
        if (!ctx) throw new Error('encodeH264Video: no 2D context.');
        const blob = await renderVideo(async fi => {
            if (fi >= targetCount) return null;
            const si = Math.min(rgbaFrames.length - 1, Math.floor(fi * fps / RVFPS));
            let rgba = rgbaFrames[si];
            if (!(rgba instanceof Uint8Array)) rgba = new Uint8Array(rgba);
            ctx.putImageData(new ImageData(new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, rgba.byteLength), width, height), 0, 0);
            return canvas;
        }, { quality });
        return new Uint8Array(await blob.arrayBuffer());
    }

    /** Decode H.264 MP4 -> RGBA8 frames */
    async function decodeH264Video(mp4Bytes, maxFrames, maxWidth, maxHeight, targetFps, startTimeSeconds) {
        if (!(mp4Bytes instanceof Uint8Array)) mp4Bytes = new Uint8Array(mp4Bytes);
        const limitFrames = (maxFrames && maxFrames > 0) ? maxFrames : Number.MAX_SAFE_INTEGER;
        const limitWidth = (maxWidth || 0), limitHeight = (maxHeight || 0);
        const effectiveFps = (targetFps || 0), startTime = (startTimeSeconds || 0);

        const blob = new Blob([mp4Bytes], { type: 'video/mp4' });
        const url = URL.createObjectURL(blob);
        const video = document.createElement('video');
        video.preload = 'auto'; video.muted = true; video.playsInline = true; video.src = url;

        try {
            await new Promise((resolve, reject) => {
                video.onloadedmetadata = () => { video.onloadedmetadata = null; video.onerror = null; resolve(); };
                video.onerror = () => { video.onloadedmetadata = null; video.onerror = null; reject(new Error('Failed to load video')); };
                video.load();
            });

            const srcWidth = video.videoWidth, srcHeight = video.videoHeight, duration = video.duration;
            if (srcWidth <= 0 || srcHeight <= 0 || !isFinite(duration) || duration <= 0)
                throw new Error(`Invalid video: ${srcWidth}x${srcHeight}, dur=${duration}`);

            let outWidth = srcWidth, outHeight = srcHeight;
            if (limitWidth > 0 && outWidth > limitWidth) { const r = outHeight / outWidth; outWidth = limitWidth; outHeight = Math.round(outWidth * r); }
            if (limitHeight > 0 && outHeight > limitHeight) { const r = outWidth / outHeight; outHeight = limitHeight; outWidth = Math.round(outHeight * r); }
            if (outWidth % 2) outWidth--; if (outHeight % 2) outHeight--;
            outWidth = Math.max(outWidth, 2); outHeight = Math.max(outHeight, 2);

            const fps = effectiveFps > 0 ? effectiveFps : 30, frameInterval = 1.0 / fps;
            const canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(outWidth, outHeight) : document.createElement('canvas');
            canvas.width = outWidth; canvas.height = outHeight;
            const ctx = canvas.getContext('2d', { willReadFrequently: true });
            if (!ctx) throw new Error('decodeH264Video: no 2D context.');

            const frames = [];
            let t = startTime;
            while (t < duration && frames.length < limitFrames) {
                video.currentTime = t;
                await new Promise(resolve => {
                    const cb = () => { video.removeEventListener('seeked', cb); resolve(); };
                    video.addEventListener('seeked', cb, { once: true });
                });
                ctx.drawImage(video, 0, 0, outWidth, outHeight);
                const img = ctx.getImageData(0, 0, outWidth, outHeight);
                frames.push({ width: outWidth, height: outHeight, rgbaData: new Uint8Array(img.data.buffer, img.data.byteOffset, img.data.byteLength) });
                t += frameInterval;
            }
            return frames;
        } finally {
            URL.revokeObjectURL(url);
            video.pause(); video.removeAttribute('src'); video.load(); video.remove();
        }
    }

    // Pipeline creation

    // colorFormat: defaults to _sceneFormat (Scene target).
    // The Overlay family passes _format (backbuffer).
    function _createColorTarget(modeKey, colorFormat) {
        const target = { format: colorFormat || _sceneFormat };
        // startsWith:
        // opaqueNd from 2-2 contract clause 7 is also always non-blended
        // (Nd only affects depth writes)
        if (!modeKey.startsWith('opaque')) {
            target.blend = {
                color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' },
            };
        }
        return target;
    }

    // 2-3 contract clause 7:
    // transparent objects must not contaminate velocity.
    // This is implemented by setting MRT slot 1 writeMask to 0
    // without introducing shader branches.
    // Opaque modes always write and never blend.
    // Semantically equivalent to DX IndependentBlendEnable + slot1 write mask
    // and Vulkan colorWriteMask.
    function _createVelocityTarget(modeKey) {
        return { format: 'rg16float', writeMask: modeKey.startsWith('opaque') ? GPUColorWrite.ALL : 0 };
    }

    // A0.2: slot 2 follows slot 1's rule - only opaque surfaces are what SSR / decals should see.
    function _createNormalTarget(modeKey) {
        return { format: 'rgba16float', writeMask: modeKey.startsWith('opaque') ? GPUColorWrite.ALL : 0 };
    }

    function _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, modeKey, cullModeKey = 'back', vertexEntryPoint = 'vs_main', vertexBuffers = null, velocity = false, overlay = false) {
        // 2-2 contract clause 7:
        // suffix Nd = GTAO-exempt mode, routed from the flags.w NoDepthWrite bit.
        // It is byte-for-byte identical to the base mode except for depthWriteEnabled
        // (blend/compare are judged from the suffix-stripped base mode).
        // Overlay:
        // after the first pass uses storeOp=discard, the backbuffer depth attachment is later loaded
        // with undefined contents.
        // Therefore both depth testing and depth writes must be fully disabled
        // (always + no writes), or random depth rejection can happen,
        // mirroring Vulkan overlay DepthTestEnable=false.
        const noDepthWrite = modeKey.endsWith('Nd') || overlay;
        const baseMode = noDepthWrite ? modeKey.slice(0, -2) : modeKey;
        const depthWriteEnabled = !overlay && baseMode !== 'transparent' && !noDepthWrite;
        const depthCompare = overlay ? 'always' : (baseMode === 'transparent' ? 'less-equal' : 'less');
        const defaultVertexBuffer = {
            arrayStride: 20 * 4,
            attributes: [
                { shaderLocation: 0, offset: 0, format: 'float32x3' },
                { shaderLocation: 1, offset: 12, format: 'float32x2' },
                { shaderLocation: 2, offset: 20, format: 'float32x3' },
                { shaderLocation: 3, offset: 32, format: 'float32x4' },
                { shaderLocation: 4, offset: 48, format: 'float32x4' },
                { shaderLocation: 5, offset: 64, format: 'float32x4' },
            ]
        };

        const buffers = vertexBuffers || [defaultVertexBuffer];
        // 复用现有透明 PSO 档位；不为雾再创建一套材质变体矩阵。
        const sampleFog = !!_fogSampling && !overlay && !modeKey.startsWith('opaque');
        const layouts = sampleFog ? [bindGroupLayout, _fogSampling.layout] : [bindGroupLayout];
        const normal = velocity && _normalOutput;
        const targets = velocity
            ? [_createColorTarget(modeKey), _createVelocityTarget(modeKey)]
            : [_createColorTarget(modeKey, overlay ? _format : null)];
        if (normal) targets.push(_createNormalTarget(modeKey));
        const pipeline = _device.createRenderPipeline({
            layout: _device.createPipelineLayout({ bindGroupLayouts: layouts }),
            vertex: {
                module: shaderModule,
                entryPoint: vertexEntryPoint,
                buffers,
            },
            fragment: {
                module: shaderModule,
                // 2-3 contract clause 2:
                // dual-fragment MRT entry points - fs_main (single attachment) / fs_main_mrt
                // (color + velocity).
                // Both share the same shade() body.
                // WGSL has no preprocessor, so entry-point dispatch is used instead of
                // #ifdef output-struct branching.
                // A0.2: fs_main_mrtn adds slot 2 (SceneNormal) when the process baked NORMAL_OUTPUT.
                entryPoint: (velocity ? (normal ? 'fs_main_mrtn' : 'fs_main_mrt') : 'fs_main') + (sampleFog ? '_fog' : ''),
                targets,
            },
            primitive: {
                topology: 'triangle-list',
                frontFace: 'cw',
                cullMode: cullModeKey,
            },
            depthStencil: {
                depthWriteEnabled,
                depthCompare,
                format: 'depth24plus',
            },
        });
        if (sampleFog) _fogMaterialPipelines.add(pipeline);
        return pipeline;
    }

    // 1-5 shadows
    // contract 8 is intentionally non-isomorphic here:
    // the pass state machine lives on the JS side.
    // beginPass sets _passDepthOnly,
    // and drawMesh3DBatch / drawInstancedMesh3D implicitly route to the shadow pipeline
    // when they see that flag, with zero new draw API on the C# side.
    let _passDepthOnly = false;
    // 2-3 contract clause 2:
    // velocity-pass flag
    // set by beginPass when a velocity attachment is present and reset by endPass.
    // Same pattern as 1-5 shadow:
    // draw sites implicitly route to the MRT variant table,
    // with zero new draw API on the C# side.
    let _passVelocity = false;
    // A0.2: set by beginPass when the SceneNormal attachment (slot 2) is present, reset by endPass. Draw sites do not
    // route on it - the velocity table already carries slot 2 when _normalOutput - it only lets them refuse a pass
    // whose attachment set disagrees with what was baked.
    let _passNormal = false;
    // Phase 4 Outline2D:
    // OutlineMask pass flag
    // set by beginPass when passId===3 and reset by endPass.
    // Same pattern as 1-5 shadow:
    // draw sites implicitly route to the mask pipeline,
    // with zero new draw API on the C# side.
    let _passOutlineMask = false;
    // Overlay pass flag
    // set by beginPass when passId===5 and reset by endPass.
    // Same pattern as shadow/mask:
    // draw sites implicitly route through _activeMeshPipelines to the backbuffer-format overlay family.
    let _passOverlay = false;

    // All draw sites obtain their variant table here:
    // velocity pass -> MRT table, otherwise the main table.
    // If the table is missing
    // (feature disabled but the attachment was still passed by mistake),
    // it falls back to the main table.
    // Attachment-set mismatch is then caught by WebGPU validation
    // and reported asynchronously to the console (rule 3).
    function _activeMeshPipelines() {
        // Overlay routing has priority:
        // it is mutually exclusive with velocity because only the Scene pass carries the velocity attachment.
        // The overlay family bakes _format + depth off,
        // avoiding HDR backbuffer attachment-state incompatibility (rule 3).
        if (_passOverlay && _mesh3DPipelineOverlay) return _mesh3DPipelineOverlay;
        // A0.2: a velocity pass without slot 2 under _normalOutput (or vice versa) is a caller bug; it falls through
        // to WebGPU validation exactly like the missing-table case above.
        return (_passVelocity && _mesh3DPipelineVelocity) ? _mesh3DPipelineVelocity : _mesh3DPipeline;
    }

    let _shadowAtlasName = null;
    let _shadowDepthBias = 0;
    let _shadowSlopeBias = 0;
    let _shadowBindGroupLayout = null;
    let _shadowPipeline = null;
    // Phase 4 Outline2D mask pipeline
    // created inside _createMesh3DPipeline: vs_main + fs_main_outline_mask
    let _maskPipeline = null;
    let _maskPipelineDoubleSided = null;
    let _defaultShadowTexture = null;
    let _defaultShadowView = null;

    // 2-5 Step E: 1x1x1 all-zero 3D fallback
    // used at binding 19 when apLut is not ready.
    // rgba16float matches the real LUT format.
    // All-zero is the identity element of the additive formula color*(1-a)+rgb
    // (a=0, rgb=0 -> pass through unchanged).
    let _defaultAerialLutTexture = null;
    let _defaultAerialLutView = null;

    // 1-7 environment radiance cube
    // Own registry
    // it is not merged into _textures because that registry carries Texture2D semantics
    // and is consumed by sprites/materials by name.
    // Mixing cubes into it would hand those paths dimension-mismatched views
    // and fail bind-group validation immediately.
    const _textureCubes = {};
    let _envCubeName = null;
    let _defaultEnvCubeTexture = null;
    let _defaultEnvCubeView = null;

    // 2-4 clause 10: DDGI irradiance atlas name for the current frame
    // name-as-handle, same pattern as _envCubeName
    let _ddgiAtlasName = null;

    // 2-4 Step 3: DDGI depth-moment atlas name for the current frame
    // same pattern as _ddgiAtlasName
    let _ddgiDepthName = null;

    // 2-5 Step C: cloud-noise 2D texture name for the current frame
    // name-as-handle, same pattern as _ddgiAtlasName
    let _cloudNoiseName = null;

    // 2-5 Step E: AP 3D LUT name for the current frame
    // same pattern as _cloudNoiseName; 3D textures are registered in _textures3d
    let _aerialLutName = null;

    function _createMesh3DPipeline() {
        const shaderModule = _device.createShaderModule({ code: _mesh3DShader });
        const bindGroupLayout = _device.createBindGroupLayout({
            entries: [
                { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
                { binding: 1, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
                { binding: 2, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
                { binding: 3, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
                { binding: 4, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
                { binding: 5, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
                { binding: 6, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
                { binding: 7, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                { binding: 8, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                { binding: 9, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                // 1-2 contract 8: shared scene-lighting UBO
                // SceneLightParams grew to 960B in 1-5 and then to 976B in 2-3;
                // updateSceneLights uploads the whole block every frame
                // and its length must match SCENE_LIGHT_BYTES exactly
                { binding: 10, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
                // 1-5: shadow atlas
                // depth-sampled + comparison sampler, referenced statically only by fs_main
                { binding: 11, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'depth' } },
                { binding: 12, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'comparison' } },
                // 2-3 Step C: prev bone palette / prev instance byte stream
                // referenced statically only by vs_main
                { binding: 13, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                { binding: 14, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                // 1-7: environment radiance cube
                // six-layer texture + viewDimension:'cube', referenced statically only by fs_main.
                // The sampler reuses binding 1, so only the texture entry is added here.
                // _shadowBindGroupLayout is intentionally not expanded
                // because the shadow pipeline has no fragment stage
                // and pure FS resources do not participate in its static-reference analysis.
                { binding: 15, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float', viewDimension: 'cube' } },
                // 2-4 clause 10: DDGI irradiance probe atlas
                // 2D float, referenced statically only by fs_main.
                // The sampler reuses binding 1;
                // _shadowBindGroupLayout is not expanded here either, same as uEnvCube.
                { binding: 16, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
                // 2-4 Step 3: DDGI depth-moment atlas
                // 2D float, referenced statically only by fs_main.
                // Reuses the sampler the same way as binding 16.
                { binding: 17, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
                // 2-5 Step C: cloud noise
                // 2D float, referenced statically only by fs_main.
                // Uses the wrap sampler at binding 20.
                // 2-5 Step E: AP 3D LUT
                // 3D float. Reuses the sampler at binding 1
                // Linear+Clamp on all three axes.
                // Both are referenced statically only by fs_main,
                // so _shadowBindGroupLayout is not expanded, same as uEnvCube.
                { binding: 18, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
                { binding: 19, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float', viewDimension: '3d' } },
                { binding: 20, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
                // 1-5 clause 15: the shadow atlas read as plain depth for the blocker search. Only a sampler slot is added -
                // the texture is the one already at binding 11. Referenced statically only by fs_main, so
                // _shadowBindGroupLayout is not expanded, same as binding 20 above.
                { binding: 21, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'non-filtering' } },
            ]
        });

        const vertexBuffers = [
            {
                arrayStride: 20 * 4,
                stepMode: 'instance',
                attributes: [
                    { shaderLocation: 6, offset: 0, format: 'float32x4' },
                    { shaderLocation: 7, offset: 16, format: 'float32x4' },
                    { shaderLocation: 8, offset: 32, format: 'float32x4' },
                    { shaderLocation: 9, offset: 48, format: 'float32x4' },
                    { shaderLocation: 10, offset: 64, format: 'float32x4' },
                ]
            },
            {
                arrayStride: 20 * 4,
                attributes: [
                    { shaderLocation: 0, offset: 0, format: 'float32x3' },
                    { shaderLocation: 1, offset: 12, format: 'float32x2' },
                    { shaderLocation: 2, offset: 20, format: 'float32x3' },
                    { shaderLocation: 3, offset: 32, format: 'float32x4' },
                    { shaderLocation: 4, offset: 48, format: 'float32x4' },
                    { shaderLocation: 5, offset: 64, format: 'float32x4' },
                ]
            }
        ];

        _mesh3DPipeline = {
            bindGroupLayout,
            opaque: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaque', 'back', 'vs_main', vertexBuffers),
            opaqueDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaque', 'none', 'vs_main', vertexBuffers),
            fade: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fade', 'back', 'vs_main', vertexBuffers),
            fadeDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fade', 'none', 'vs_main', vertexBuffers),
            // 2-2 contract clause 7: GTAO-exempt variants
            // depthWriteEnabled=false while everything else is byte-for-byte identical to the base mode.
            // flags.w NoDepthWrite routes here through _selectPipelineMode.
            // Transparent already does not write depth, so it has no Nd variant.
            opaqueNd: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaqueNd', 'back', 'vs_main', vertexBuffers),
            opaqueNdDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaqueNd', 'none', 'vs_main', vertexBuffers),
            fadeNd: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fadeNd', 'back', 'vs_main', vertexBuffers),
            fadeNdDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fadeNd', 'none', 'vs_main', vertexBuffers),
            transparent: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'back', 'vs_main', vertexBuffers),
            transparentDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'none', 'vs_main', vertexBuffers),
            transparentBackFace: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'front', 'vs_main', vertexBuffers),
        };

        // 2-3 contract clauses 2/3: velocity variant table
        // MRT dual attachments + fs_main_mrt.
        // Shares the same layout and vertex state as the main table,
        // differing only in fragment entry point and targets.
        // It is not created when the feature is disabled,
        // so the main shader variant matrix does not expand unless the feature is on
        // and only then are 7 extra PSOs baked.
        // The shadow pass never uses this table
        // because _passDepthOnly and _passVelocity are mutually exclusive,
        // exactly as required by contract clause 3.
        if (_velocityOutput) {
            _mesh3DPipelineVelocity = {
                bindGroupLayout,
                opaque: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaque', 'back', 'vs_main', vertexBuffers, true),
                opaqueDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaque', 'none', 'vs_main', vertexBuffers, true),
                fade: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fade', 'back', 'vs_main', vertexBuffers, true),
                fadeDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fade', 'none', 'vs_main', vertexBuffers, true),
                // 2-2 contract clause 7: GTAO-exempt variants in the velocity table
                // mirroring the four Nd variants in the main table
                opaqueNd: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaqueNd', 'back', 'vs_main', vertexBuffers, true),
                opaqueNdDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaqueNd', 'none', 'vs_main', vertexBuffers, true),
                fadeNd: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fadeNd', 'back', 'vs_main', vertexBuffers, true),
                fadeNdDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fadeNd', 'none', 'vs_main', vertexBuffers, true),
                transparent: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'back', 'vs_main', vertexBuffers, true),
                transparentDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'none', 'vs_main', vertexBuffers, true),
                transparentBackFace: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'front', 'vs_main', vertexBuffers, true),
            };
        }

        // Overlay PSO family:
        // 11 variants mirroring the main table, with color target baked as _format (backbuffer)
        // and depth set to always / no writes because attachment contents are undefined.
        // _activeMeshPipelines routes to this family when _passOverlay is active.
        // Sprite2D / Shape / Texts
        // (drawSprite2D / drawTextAtlasSprite / text instancing and similar paths)
        // render directly to the backbuffer through this family.
        _mesh3DPipelineOverlay = {
            bindGroupLayout,
            opaque: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaque', 'back', 'vs_main', vertexBuffers, false, true),
            opaqueDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaque', 'none', 'vs_main', vertexBuffers, false, true),
            fade: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fade', 'back', 'vs_main', vertexBuffers, false, true),
            fadeDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fade', 'none', 'vs_main', vertexBuffers, false, true),
            opaqueNd: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaqueNd', 'back', 'vs_main', vertexBuffers, false, true),
            opaqueNdDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'opaqueNd', 'none', 'vs_main', vertexBuffers, false, true),
            fadeNd: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fadeNd', 'back', 'vs_main', vertexBuffers, false, true),
            fadeNdDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'fadeNd', 'none', 'vs_main', vertexBuffers, false, true),
            transparent: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'back', 'vs_main', vertexBuffers, false, true),
            transparentDoubleSided: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'none', 'vs_main', vertexBuffers, false, true),
            transparentBackFace: _createUnifiedPipelineVariant(shaderModule, bindGroupLayout, 'transparent', 'front', 'vs_main', vertexBuffers, false, true),
        };

        // 1-5 shadow pipeline:
        // vertex-only (no fragment stage, which is legal in WebGPU),
        // with a dedicated layout containing only the resources statically referenced by vs_main
        // at 0/7/8/9.
        // The atlas must not be sampled while it is bound as an attachment,
        // avoiding validation errors.
        // CullNone + baked bias (contract 4).
        // A single variant covers static / instanced / skinned / morph paths
        // driven by flags bits (contract 3).
        // 2-3 Step C:
        // bindings 13/14 must also be declared because VELOCITY_OUTPUT is a const injected
        // globally by string replacement and shared across one shader module for the whole app.
        // WGSL/WebGPU static-reference analysis does not constant-fold:
        // if computePrevLocalPosition references uPrevBones/uPrevInstanceData in the call graph,
        // it still counts even when wrapped inside if (VELOCITY_OUTPUT=false).
        // Missing these declarations makes createRenderPipeline report a layout mismatch.
        // Under rule 3, validation errors arrive asynchronously in the console instead of throwing,
        // which makes this class of omission hard to diagnose.
        // The depth path does not need prev data, so binding the default fallback sentinels is sufficient.
        _shadowBindGroupLayout = _device.createBindGroupLayout({
            entries: [
                { binding: 0, visibility: GPUShaderStage.VERTEX, buffer: { type: 'uniform' } },
                { binding: 7, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                { binding: 8, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                { binding: 9, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                { binding: 13, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
                { binding: 14, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
            ]
        });
        _shadowPipeline = _device.createRenderPipeline({
            layout: _device.createPipelineLayout({ bindGroupLayouts: [_shadowBindGroupLayout] }),
            vertex: { module: shaderModule, entryPoint: 'vs_main', buffers: vertexBuffers },
            primitive: { topology: 'triangle-list', frontFace: 'cw', cullMode: 'none' },
            depthStencil: {
                depthWriteEnabled: true,
                depthCompare: 'less',
                format: 'depth32float',
                depthBias: _shadowDepthBias,
                depthBiasSlopeScale: _shadowSlopeBias,
            },
        });

        // Phase 4 Outline2D mask pipeline x2
        // (single-sided / double-sided, mirroring cull back / none in the main table):
        // VS reuses vs_main and therefore naturally supports static / instanced / skinned / morph paths.
        // FS uses fs_main_outline_mask
        // and forwards color straight through the per-draw uniform hdrParams slot.
        // Depth is less-equal, read-only, and not written
        // because the mask pass reuses SceneDepth,
        // mirroring the mask PSO depth configuration on DX / Vulkan / Metal.
        // The layout explicitly uses the main-table bindGroupLayout,
        // so bind groups remain identical to the Scene pass with zero new bindings.
        _maskPipeline = _device.createRenderPipeline({
            layout: _device.createPipelineLayout({ bindGroupLayouts: [bindGroupLayout] }),
            vertex: { module: shaderModule, entryPoint: 'vs_main', buffers: vertexBuffers },
            fragment: { module: shaderModule, entryPoint: 'fs_main_outline_mask', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list', frontFace: 'cw', cullMode: 'back' },
            depthStencil: { depthWriteEnabled: false, depthCompare: 'less-equal', format: 'depth24plus' },
        });
        _maskPipelineDoubleSided = _device.createRenderPipeline({
            layout: _device.createPipelineLayout({ bindGroupLayouts: [bindGroupLayout] }),
            vertex: { module: shaderModule, entryPoint: 'vs_main', buffers: vertexBuffers },
            fragment: { module: shaderModule, entryPoint: 'fs_main_outline_mask', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list', frontFace: 'cw', cullMode: 'none' },
            depthStencil: { depthWriteEnabled: false, depthCompare: 'less-equal', format: 'depth24plus' },
        });
    }

    // Frame rendering

    // Offscreen RenderTarget (1-1 Steps 2/3):
    // on the C# side, WGPURenderTarget uses name as the handle
    // to avoid cross-layer object lifetime issues.
    // Two shapes are supported
    // (generalized in Step 3, aligned with DX/Vulkan):
    // - color (formatKind 0/1): either the canvas preferred format
    //   (matching the color target baked into existing pipelines)
    //   or rgba16float for 1-4 HDR, which requires matching pipeline variants,
    //   and it owns a matching depth24plus attachment.
    //   Existing pipelines bake depth24plus in depthStencil,
    //   so a pass with no depth attachment would fail pipeline validation.
    // - depth-only (formatKind 2): depth32float shadow map (1-5),
    //   used both as attachment and sampled resource.
    //   depth24plus contents cannot be sampled,
    //   so shadow uses depth32float.
    //   Its sampling bind group is created by the dedicated 1-5 pipeline
    //   and directly references rt.depthView.
    // MatchBackbuffer RTs are lazily rebuilt when beginPass resolves them,
    // matching the _ensureDepthTexture pattern,
    // while the C# reference (name) remains valid.
    const _renderTargets = {};

    function _createRenderTargetResources(rt, width, height) {
        if (rt.depthOnly) {
            rt.depthTex = _device.createTexture({
                size: [width, height], format: rt.depthFormat || 'depth32float',
                usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
            });
            rt.depthView = rt.depthTex.createView();
            rt.width = width; rt.height = height;
            return;
        }
        // 2-3 contract clause 2:
        // velocity shape (rg16float) is used only as MRT slot 1 attachment + compute sampling source
        // (VelocityViewEffect references rt.colorView as SampledTexture).
        // It does not create companion depth
        // because the depth plane is provided by the Scene pass color target / SceneDepth,
        // and it does not create a blit bind group
        // because it is never presented directly and rg16float is unrelated to the sampling layouts
        // baked into the blit pipeline.
        if (rt.velocity) {
            rt.colorTex = _device.createTexture({
                size: [width, height], format: rt.colorFormat,
                usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
            });
            rt.colorView = rt.colorTex.createView();
            rt.width = width; rt.height = height;
            // 2-1: rebuilding colorView invalidates all variant bind-group caches
            // as a view-identity safety net
            rt.variantBindGroups = null;
            return;
        }
        rt.colorTex = _device.createTexture({
            size: [width, height], format: rt.colorFormat,
            usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING,
        });
        rt.colorView = rt.colorTex.createView();
        if (!rt.noImplicitDepth) {
            rt.depthTex = _device.createTexture({
                size: [width, height], format: 'depth24plus',
                usage: GPUTextureUsage.RENDER_ATTACHMENT,
            });
            rt.depthView = rt.depthTex.createView();
        }
        rt.width = width; rt.height = height;
        // 2-1: rebuilding colorView invalidates all variant bind-group caches
        // as a view-identity safety net.
        // Explicitly clear them here to prevent old views from leaking through.
        rt.variantBindGroups = null;
        // Blit bind groups:
        // point uses textureLoad and needs no sampler,
        // while linear adds the shared linear sampler.
        // They are rebuilt together with the view.
        // Bind groups created from layout:'auto' cannot be shared across pipelines.
        // An rgba16float RT (1-4) must therefore use the tonemap variant layout
        // and include the binding 2 exposure uniform entry
        // (Step B, sharing _blitExposureBuffer, written once per frame by blitToBackbuffer).
        const hdrSource = rt.colorFormat === 'rgba16float';
        const exposureEntries = hdrSource ? [{ binding: 2, resource: { buffer: _blitExposureBuffer } }] : [];
        rt.blitBindGroup = _device.createBindGroup({
            layout: (hdrSource ? _blitPipelineTonemap : _blitPipeline).getBindGroupLayout(0),
            entries: [{ binding: 0, resource: rt.colorView }, ...exposureEntries],
        });
        rt.blitBindGroupLinear = _device.createBindGroup({
            layout: (hdrSource ? _blitPipelineTonemapLinear : _blitPipelineLinear).getBindGroupLayout(0),
            entries: [
                { binding: 0, resource: rt.colorView },
                { binding: 1, resource: _blitSampler },
                ...exposureEntries,
            ],
        });
    }

    function _destroyRenderTargetResources(rt) {
        if (rt.colorTex) _queueComputeRetirement(rt.colorTex);
        if (rt.depthTex) _queueComputeRetirement(rt.depthTex);
        rt.colorTex = rt.depthTex = rt.colorView = rt.depthView = null;
        rt.blitBindGroup = rt.blitBindGroupLinear = rt.variantBindGroups = null;
        _invalidateComputeBindGroups();
    }

    function _resizeRenderTargetResources(rt, width, height) {
        // 完整分配成功才替换；旧命令引用在提交完成前继续有效。
        const replacement = { ...rt, colorTex: null, depthTex: null, colorView: null, depthView: null };
        try { _createRenderTargetResources(replacement, width, height); }
        catch (error) { _destroyRenderTargetResources(replacement); throw error; }
        _destroyRenderTargetResources(rt);
        Object.assign(rt, replacement);
    }

    function synchronizeRenderTargetSizes() {
        if (!_device || !_canvas || _immediateClosed || _instancedDiag.deviceLost)
            throw new Error('渲染目标尺寸同步要求有效 WebGPU 设备');
        if (_frameStarted || _commandEncoder || _passEncoder || (_sceneCollection?.stage ?? 0) !== 0)
            throw new Error('渲染目标尺寸同步必须在帧外执行');
        const width = _canvas.width, height = _canvas.height;
        if (!(width > 0 && height > 0)) throw new Error('渲染目标尺寸同步要求非零画布');
        // 收集之前固定资源身份；C# 效果 Resize 在此成功后才读取新尺寸。
        for (const rt of Object.values(_renderTargets)) {
            if (rt.matchBackbuffer && (rt.width !== width || rt.height !== height))
                _resizeRenderTargetResources(rt, width, height);
        }
        return [width, height];
    }

    // formatKind:
    // 0 = BackbufferCompatible color,
    // 1 = Rgba16Float color (1-4),
    // 2 = depth-only D32Float (1-5),
    // 3 = depth-only SceneDepth
    //   (2-2: depth24plus + TEXTURE_BINDING -
    //   the dual-target Scene pass depth attachment must match the existing depth24plus
    //   baked into pipelines, and WGSL texture_depth_2d is legal for depth24plus),
    // 4 = Rg16Float velocity color
    //   (2-3: dedicated to MRT slot 1 - no companion depth, no blit bind group,
    //   and matches SceneVelocity format on the other three backends)
    function createRenderTarget(name, width, height, matchBackbuffer, formatKind) {
        const rt = {
            matchBackbuffer: !!matchBackbuffer,
            depthOnly: formatKind === 2 || formatKind === 3,
            velocity: formatKind === 4,
            // 5/6 保留 HDR/LDR 颜色，但不创建兼容旧场景管线的隐式深度。
            noImplicitDepth: formatKind === 5 || formatKind === 6,
        };
        if (rt.depthOnly) {
            rt.depthFormat = formatKind === 3 ? 'depth24plus' : 'depth32float';
        } else if (rt.velocity) {
            rt.colorFormat = 'rg16float';
        } else {
            rt.colorFormat = formatKind === 1 || formatKind === 5 ? 'rgba16float' : _format;
            _ensureBlitPipeline();
        }
        const w = matchBackbuffer ? _canvas.width : width;
        const h = matchBackbuffer ? _canvas.height : height;
        try { _createRenderTargetResources(rt, w, h); }
        catch (error) { _destroyRenderTargetResources(rt); throw error; }
        if (_renderTargets[name]) _destroyRenderTargetResources(_renderTargets[name]);
        _renderTargets[name] = rt;
        // 把解析后的尺寸回传给 C#：matchBackbuffer 目标的描述符以 Width/Height = 0 申请，
        // 而后端之上的效果按 Desc 分配资源（雾的 froxel 网格在 0x0 时会直接放弃分配）。
        return [w, h];
    }

    function disposeRenderTarget(name) {
        const rt = _renderTargets[name];
        if (!rt) return;
        _destroyRenderTargetResources(rt);
        delete _renderTargets[name];
    }

    // FinalBlit (1-1 Steps 2/3 + 1-4 Step A + 2-1):
    // fullscreen-triangle multi-variant family, auto-selected by blitToBackbuffer:
    // - point/linear are chosen by source/target size:
    //   point = textureLoad(fragCoord), exact identity mapping with zero error
    //   when sizes match.
    //   Framebuffer coordinates are y-down on both sides, so no directional compensation is needed.
    //   linear = when sizes differ, VS outputs uv
    //   (NDC y-up -> uv y-down flip)
    //   and scales through the shared linear sampler.
    // - tonemap is chosen by source format:
    //   rgba16float sources (HDR-tier SceneColor) switch to fs_point/linear_tonemap
    //   (Step A uses pure pow(1/2.2) encoding, pixel-for-pixel equivalent to the LDR baseline),
    //   while targets still always bake _format for backbuffer rendering.
    // - 2-1 adds:
    //   tonemap+bloom (when no resolve is requested and bloom is available),
    //   uber (Post pass composition, consumed by renderPost),
    //   and FXAA (PostColor -> backbuffer, with luma read from alpha).
    // - 2-3 clause 17 adds RCAS, the second resolve variant, taken when the TAA tier owns the Post slot.
    // All pipelines carry depthStencil with write disabled and compare always:
    // FinalBlit/Post reuse the depth24plus attachment shape,
    // isomorphic to the Scene pass, so beginPass does not need per-pass attachment branching.
    // WGSL is supplied during initialize
    // (WebGPUPipeline.BlitShader, one module with multiple entry points).
    let _blitPipeline = null;
    let _blitPipelineLinear = null;
    let _blitPipelineTonemap = null;
    let _blitPipelineTonemapLinear = null;
    let _blitPipelineTonemapBloom = null;
    let _blitPipelineTonemapBloomLinear = null;
    let _blitPipelineUber = null;
    let _blitPipelineUberBloom = null;
    let _blitPipelineFxaa = null;
    // 2-3 clause 17: RCAS resolve, the TAA tier's counterpart to the FXAA variant
    let _blitPipelineRcas = null;
    // 2-2 Step C: 6 AO variants
    // tonemap +/- bloom x point/linear + uber +/- bloom, aligned with DX 2-2 Step B
    let _blitPipelineTonemapAo = null;
    let _blitPipelineTonemapAoLinear = null;
    let _blitPipelineTonemapBloomAo = null;
    let _blitPipelineTonemapBloomAoLinear = null;
    let _blitPipelineUberAo = null;
    let _blitPipelineUberBloomAo = null;
    // Phase 4 Outline2D: composite variant
    // alpha-blended on top of the backbuffer after FinalBlit
    let _blitPipelineOutlineComposite = null;
    let _blitSampler = null;
    let _blitPointSampler = null;
    let _blitShaderWGSL = null;
    // Tonemap parameter uniform
    // (1-4 Step B + 2-1 extended semantics):
    // 16B vec4f (x=exposure, y=bloomIntensity, zw=texelSize).
    // 2-3 clause 17: the RCAS variant reads x as sharpness instead, since an LDR resolve source has no exposure
    // to apply, which is why the FXAA path already writes a zero there.
    // FinalBlit variants share _blitExposureBuffer
    // and write it at most once per frame.
    // Post-pass uber uses a separate _postParamsBuffer:
    // all queue.writeBuffer calls take effect before the single endFrame submit,
    // so later writes overwrite earlier ones for the whole command buffer.
    // Sharing the same buffer would make uber and FinalBlit overwrite each other's parameters.
    // 2-2: aoIntensity uses a separate _aoParamsBuffer (vec4f.x),
    // and rewriting the same value for uber and FinalBlit in the same frame is harmless.
    let _blitExposureBuffer = null;
    let _postParamsBuffer = null;
    let _aoParamsBuffer = null;
    // Phase 4 Outline2D composite parameters
    // 16B vec4f: x/y = mask RT texelSize, z = outlineWidth.
    // Uses its own buffer for the same reason as _postParamsBuffer:
    // to avoid parameter overwrites with other FinalBlit variants.
    let _outlineCompositeParamsBuffer = null;
    const _BLIT_EXPOSURE_SCRATCH = new Float32Array(4);
    const _POST_PARAMS_SCRATCH = new Float32Array(4);
    const _AO_PARAMS_SCRATCH = new Float32Array(4);
    const _OUTLINE_COMPOSITE_PARAMS_SCRATCH = new Float32Array(4);

    function _ensureBlitPipeline() {
        if (_blitPipeline) return;
        const depthStencil = { depthWriteEnabled: false, depthCompare: 'always', format: 'depth24plus' };
        const module = _device.createShaderModule({ code: _blitShaderWGSL });
        _blitPipeline = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_point' },
            fragment: { module, entryPoint: 'fs_point', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineLinear = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_linear', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        // Tonemap variants (1-4):
        // reuse the same VS and only switch the fragment entry point to tonemap
        // (Step B: exposure x ACES + gamma,
        // with WGSL statically referencing the exposure uniform at binding 2,
        // which auto layout includes automatically).
        // Targets still use _format
        // because FinalBlit always renders the backbuffer.
        // The LDR tier never hits these
        // because it has no rgba16float source,
        // but they are always created to keep the path uniform.
        _blitPipelineTonemap = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_point' },
            fragment: { module, entryPoint: 'fs_point_tonemap', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineTonemapLinear = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_linear_tonemap', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        // 2-1 variants:
        // bloom / fxaa / uber_bloom fragments need uv
        // (for bloom / neighborhood sampling), so they use vs_linear,
        // while the source is still identity-read through textureLoad(fragCoord).
        // Uber without bloom uses vs_point.
        // Targets always use _format
        // (FinalBlit renders the backbuffer; the Post pass renders BackbufferCompatible PostColor
        // in the same format).
        _blitPipelineTonemapBloom = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_tonemap_bloom', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineTonemapBloomLinear = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_linear_tonemap_bloom', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineUber = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_point' },
            fragment: { module, entryPoint: 'fs_uber', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineUberBloom = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_uber_bloom', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineFxaa = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_fxaa', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        // 2-3 clause 17: RCAS resolve.
        // Same vs_linear because the cross neighborhood is addressed through uv,
        // but fs_rcas statically references only bindings 0/2/4,
        // so its auto layout is narrower than the FXAA one and its bind group must omit the linear sampler.
        _blitPipelineRcas = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_rcas', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        // 2-2 Step C: all AO variants use vs_linear
        // because AO upsampling needs uv; point paths still identity-read the source
        // through textureLoad.
        _blitPipelineTonemapAo = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_tonemap_ao', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineTonemapAoLinear = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_linear_tonemap_ao', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineTonemapBloomAo = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_tonemap_bloom_ao', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineTonemapBloomAoLinear = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_linear_tonemap_bloom_ao', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineUberAo = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_uber_ao', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitPipelineUberBloomAo = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: { module, entryPoint: 'fs_uber_bloom_ao', targets: [{ format: _format }] },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        // Phase 4 Outline2D composite:
        // fs_outline_composite expands the mask RT with an 8-neighborhood outline extraction
        // and alpha-blends it
        // (SrcAlpha / InvSrcAlpha, mirroring the DX alphaBlend PSO).
        // Depth is read-only
        // with compare always, same as the other blit variants.
        // Auto layout contains only the fs-static bindings 2/4/7,
        // naturally isolated from the other variants.
        _blitPipelineOutlineComposite = _device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs_linear' },
            fragment: {
                module, entryPoint: 'fs_outline_composite', targets: [{
                    format: _format,
                    blend: {
                        color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                        alpha: { srcFactor: 'one', dstFactor: 'zero', operation: 'add' },
                    },
                }],
            },
            primitive: { topology: 'triangle-list' },
            depthStencil,
        });
        _blitSampler = _device.createSampler({ magFilter: 'linear', minFilter: 'linear' });
        _blitPointSampler = _device.createSampler({ magFilter: 'nearest', minFilter: 'nearest' });
        _blitExposureBuffer = _device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        _postParamsBuffer = _device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        _aoParamsBuffer = _device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        _outlineCompositeParamsBuffer = _device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    }

    // 2-1 variant bind-group cache:
    // bind groups created from layout:'auto' cannot be shared across pipelines,
    // so they are cached on the RT object by variant key.
    // Invalidation uses view identity checks on (srcView, bloomView, aoView)
    // and automatically rebuilds when the RT or chain textures are lazily rebuilt.
    // 2-3 clauses 12/16:
    // srcView is the actual source bound at binding 0.
    // When the scene source is overridden by TAA,
    // it becomes the ping-pong texture instead of rt.colorView.
    // Resize in-place rebuild via createComputeTexture invalidates only compute-kernel bind groups,
    // not rt.variantBindGroups,
    // so srcView identity must be part of validation here.
    // The caller also appends the override name into the key,
    // so taa0/taa1 keep separate cache entries
    // and alternate every frame with steady-state zero rebuilds.
    // Without an override, it naturally falls back to rt.colorView.
    function _getVariantBindGroup(rt, key, pipeline, bloomView, entries, aoView, srcView) {
        const cache = rt.variantBindGroups || (rt.variantBindGroups = {});
        const c = cache[key];
        const av = aoView || null;
        const sv = srcView || rt.colorView;
        if (c && c.srcView === sv && c.bloomView === bloomView && c.aoView === av) return c.bindGroup;
        const bindGroup = _device.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries });
        cache[key] = { bindGroup, srcView: sv, bloomView, aoView: av };
        return bindGroup;
    }

    // Phase 4:
    // outlineMaskName / outlineWidth are new trailing parameters.
    // Passing null/0 means no mask, with behavior identical to the old version.
    // This is a wrapper layer:
    // after the main blit finishes, it optionally appends the outline composite,
    // mirroring DX where composite happens only inside BlitToBackbuffer,
    // covering both the resolve and non-resolve branches.
    // renderPost does not participate.
    // 2-3 clause 17: resolve replaced the old boolean fxaa flag and mirrors Season.Rendering.PostResolve
    // (0 = copy, 1 = FXAA, 2 = RCAS); sharpness is read only by the RCAS branch.
    function blitToBackbuffer(name, exposure, bloomName, bloomIntensity, resolve, sharpness, aoName, aoIntensity, sceneOverrideName, outlineMaskName, outlineWidth) {
        const rt = _renderTargets[name];
        if (!rt || !_passEncoder || rt.depthOnly) return;
        _blitToBackbufferMain(rt, exposure, bloomName, bloomIntensity, resolve, sharpness, aoName, aoIntensity, sceneOverrideName);
        if (outlineMaskName) _drawOutlineComposite(outlineMaskName, outlineWidth);
    }

    // Phase 4 Outline2D composite draw:
    // the mask RT (rgba8, rgb = group outline color / alpha == 1)
    // is expanded by fs_outline_composite through 8-neighborhood outline extraction
    // and composited onto the current pass target
    // (for FinalBlit this means the backbuffer).
    // texelSize is supplied from the mask RT's own size
    // which under MatchBackbuffer normally equals the canvas.
    function _drawOutlineComposite(maskName, outlineWidth) {
        if (!_passEncoder) return;
        const mrt = _renderTargets[maskName];
        if (!mrt || mrt.depthOnly || !mrt.colorView) return;
        _OUTLINE_COMPOSITE_PARAMS_SCRATCH[0] = 1 / Math.max(mrt.width, 1);
        _OUTLINE_COMPOSITE_PARAMS_SCRATCH[1] = 1 / Math.max(mrt.height, 1);
        _OUTLINE_COMPOSITE_PARAMS_SCRATCH[2] = outlineWidth > 0 ? outlineWidth : 1;
        _OUTLINE_COMPOSITE_PARAMS_SCRATCH[3] = 0;
        _device.queue.writeBuffer(_outlineCompositeParamsBuffer, 0, _OUTLINE_COMPOSITE_PARAMS_SCRATCH);
        // Variant bind-group caching follows the same pattern as the other blit variants
        // and is invalidated by mask RT view identity changes.
        // Auto layout contains only {2,4,7},
        // so binding 0 is intentionally omitted because this entry point does not read srcTex.
        const bindGroup = _getVariantBindGroup(mrt, 'outlineComposite', _blitPipelineOutlineComposite, null, [
            { binding: 2, resource: { buffer: _outlineCompositeParamsBuffer } },
            { binding: 4, resource: _blitPointSampler },
            { binding: 7, resource: mrt.colorView },
        ]);
        _passEncoder.setPipeline(_blitPipelineOutlineComposite);
        _passEncoder.setBindGroup(0, bindGroup);
        _passEncoder.draw(3);
    }

    function _blitToBackbufferMain(rt, exposure, bloomName, bloomIntensity, resolve, sharpness, aoName, aoIntensity, sceneOverrideName) {
        if (resolve === 1 || resolve === 2) {
            // 2-1 / 2-3 clause 17: PostColor (LDR, luma stored in alpha) -> the resolve variant belonging to the tier
            // that registered the Post slot.
            // texelSize is supplied through params.zw for both, and RCAS additionally reads params.x, the slot FXAA
            // leaves zeroed because an LDR source has no exposure.
            // While a resolve runs, uber parameters use _postParamsBuffer,
            // and this buffer is written only once in the frame, so no parameter overlap occurs.
            const rcas = resolve === 2;
            _BLIT_EXPOSURE_SCRATCH[0] = rcas ? sharpness : 0; _BLIT_EXPOSURE_SCRATCH[1] = 0;
            _BLIT_EXPOSURE_SCRATCH[2] = 1 / rt.width; _BLIT_EXPOSURE_SCRATCH[3] = 1 / rt.height;
            _device.queue.writeBuffer(_blitExposureBuffer, 0, _BLIT_EXPOSURE_SCRATCH);
            // The two auto layouts differ: fs_rcas never references the linear sampler at binding 1,
            // so including it would fail bind-group validation. Separate cache keys keep the entry lists apart.
            const pipeline = rcas ? _blitPipelineRcas : _blitPipelineFxaa;
            const entries = rcas
                ? [
                    { binding: 0, resource: rt.colorView },
                    { binding: 2, resource: { buffer: _blitExposureBuffer } },
                    { binding: 4, resource: _blitPointSampler },
                ]
                : [
                    { binding: 0, resource: rt.colorView },
                    { binding: 1, resource: _blitSampler },
                    { binding: 2, resource: { buffer: _blitExposureBuffer } },
                    { binding: 4, resource: _blitPointSampler },
                ];
            const bindGroup = _getVariantBindGroup(rt, rcas ? 'rcas' : 'fxaa', pipeline, null, entries);
            _passEncoder.setPipeline(pipeline);
            _passEncoder.setBindGroup(0, bindGroup);
            _passEncoder.draw(3);
            return;
        }
        // resolve === 0 (copy) falls through: PostColor is BackbufferCompatible and matches the backbuffer size,
        // so the selection below lands on the plain point variant, a straight texel copy.
        // 2-3 clause 12:
        // when the scene source is overridden by TAA resolve,
        // binding 0 switches to _textureViews[override].
        // That texture is always rgba16float linear HDR (clause 10),
        // so it always uses the tonemap family.
        // It is also always full-size (clause 15), so it is always point-sampled.
        // The name is appended to the variant key
        // so the two ping-pong sides keep separate cache entries.
        // The view is also validated by identity
        // and gets invalidated on in-place resize rebuild.
        // If the name is not registered, it falls back to rt.colorView,
        // leaving no residue, same as override = null.
        const ovView = sceneOverrideName ? (_textureViews[sceneOverrideName] || null) : null;
        const srcView = ovView || rt.colorView;
        const ovKey = ovView ? '|' + sceneOverrideName : '';
        const hdrSource = ovView ? true : rt.colorFormat === 'rgba16float';
        // Point/linear selection is lifted to the function level:
        // with an override, compare its own dimensions against the canvas
        // as a defensive check, though this is normally always true.
        const ovMeta = ovView ? _textureMeta[sceneOverrideName] : null;
        const point = ovView
            ? (!!ovMeta && ovMeta.width === _canvas.width && ovMeta.height === _canvas.height)
            : (rt.width === _canvas.width && rt.height === _canvas.height);
        if (hdrSource) {
            // Exposure is uploaded every frame
            // (RenderQuality.HdrExposure, contract 5).
            // The tonemap shader consumes it directly with no fallback,
            // because the C# blit chain always passes a valid value,
            // aligned with DX root-constant / Vulkan push-constant semantics.
            _BLIT_EXPOSURE_SCRATCH[0] = exposure;
            _BLIT_EXPOSURE_SCRATCH[1] = bloomIntensity;
            _BLIT_EXPOSURE_SCRATCH[2] = 0; _BLIT_EXPOSURE_SCRATCH[3] = 0;
            _device.queue.writeBuffer(_blitExposureBuffer, 0, _BLIT_EXPOSURE_SCRATCH);
        }
        // 2-1: switch to tonemap+bloom variants when the bloom texture is ready
        // on the direct path, i.e. when no resolve was requested.
        // If not ready, fall back to plain tonemap.
        // 2-2: when the AO texture is ready, switch further to AO variants
        // that multiply AO before ACES and then add bloom.
        // aoIntensity uses the dedicated _aoParamsBuffer (vec4f.x),
        // and rewriting the same value in the same frame as uber is harmless.
        const bloomView = (hdrSource && bloomName) ? _textureViews[bloomName] : null;
        const aoView = (hdrSource && aoName) ? _textureViews[aoName] : null;
        if (aoView) {
            _AO_PARAMS_SCRATCH[0] = aoIntensity;
            _device.queue.writeBuffer(_aoParamsBuffer, 0, _AO_PARAMS_SCRATCH);
            const pipeline = bloomView
                ? (point ? _blitPipelineTonemapBloomAo : _blitPipelineTonemapBloomAoLinear)
                : (point ? _blitPipelineTonemapAo : _blitPipelineTonemapAoLinear);
            const key = (bloomView
                ? (point ? 'tonemapBloomAo' : 'tonemapBloomAoLinear')
                : (point ? 'tonemapAo' : 'tonemapAoLinear')) + ovKey;
            const entries = [
                { binding: 0, resource: srcView },
                { binding: 1, resource: _blitSampler },
                { binding: 2, resource: { buffer: _blitExposureBuffer } },
                { binding: 5, resource: aoView },
                { binding: 6, resource: { buffer: _aoParamsBuffer } },
            ];
            if (bloomView) entries.push({ binding: 3, resource: bloomView });
            const bindGroup = _getVariantBindGroup(rt, key, pipeline, bloomView, entries, aoView, srcView);
            _passEncoder.setPipeline(pipeline);
            _passEncoder.setBindGroup(0, bindGroup);
            _passEncoder.draw(3);
            return;
        }
        if (bloomView) {
            const pipeline = point ? _blitPipelineTonemapBloom : _blitPipelineTonemapBloomLinear;
            const bindGroup = _getVariantBindGroup(rt, (point ? 'tonemapBloom' : 'tonemapBloomLinear') + ovKey, pipeline, bloomView, [
                { binding: 0, resource: srcView },
                { binding: 1, resource: _blitSampler },
                { binding: 2, resource: { buffer: _blitExposureBuffer } },
                { binding: 3, resource: bloomView },
            ], null, srcView);
            _passEncoder.setPipeline(pipeline);
            _passEncoder.setBindGroup(0, bindGroup);
            _passEncoder.draw(3);
            return;
        }
        // rt.blitBindGroup / Linear are baked against rt.colorView in _createRenderTargetResources.
        // The override path cannot reuse them and must go through variant caching instead.
        // Entries must match the statically referenced bindings of the entry point exactly
        // because extra bindings under auto layout trigger validation errors:
        // point = {0,2}, linear adds sampler binding 1.
        if (ovView) {
            const pipeline = point ? _blitPipelineTonemap : _blitPipelineTonemapLinear;
            const entries = [{ binding: 0, resource: srcView }];
            if (!point) entries.push({ binding: 1, resource: _blitSampler });
            entries.push({ binding: 2, resource: { buffer: _blitExposureBuffer } });
            const bindGroup = _getVariantBindGroup(rt, (point ? 'tonemap' : 'tonemapLinear') + ovKey, pipeline, null, entries, null, srcView);
            _passEncoder.setPipeline(pipeline);
            _passEncoder.setBindGroup(0, bindGroup);
            _passEncoder.draw(3);
            return;
        }
        if (point) {
            _passEncoder.setPipeline(hdrSource ? _blitPipelineTonemap : _blitPipeline);
            _passEncoder.setBindGroup(0, rt.blitBindGroup);
        } else {
            _passEncoder.setPipeline(hdrSource ? _blitPipelineTonemapLinear : _blitPipelineLinear);
            _passEncoder.setBindGroup(0, rt.blitBindGroupLinear);
        }
        _passEncoder.draw(3);
    }

    // 2-1 Post-pass uber composition:
    // SceneColor(HDR) -> exposure x bloom accumulation -> ACES + gamma -> LDR PostColor
    // with Rec.601 luma written into alpha for FXAA consumption.
    // This is valid only inside the Post pass
    // where it renders the current pass target.
    // Parameters use the dedicated _postParamsBuffer
    // separate from FinalBlit, see the buffer note above.
    // 2-2: when the AO texture is ready, switch to uber AO variants
    // with the same composition formula as blitToBackbuffer.
    // 2-3 clause 12:
    // when sceneOverrideName is non-null, binding 0 switches to TAA resolve output,
    // with the same semantics as override in blitToBackbuffer.
    // Uber entry points always include tonemap,
    // so there is no hdrSource branch here.
    function renderPost(sceneName, exposure, bloomName, bloomIntensity, aoName, aoIntensity, sceneOverrideName) {
        const rt = _renderTargets[sceneName];
        if (!rt || !_passEncoder || rt.depthOnly) return;
        _POST_PARAMS_SCRATCH[0] = exposure;
        _POST_PARAMS_SCRATCH[1] = bloomIntensity;
        _POST_PARAMS_SCRATCH[2] = 0; _POST_PARAMS_SCRATCH[3] = 0;
        _device.queue.writeBuffer(_postParamsBuffer, 0, _POST_PARAMS_SCRATCH);
        const ovView = sceneOverrideName ? (_textureViews[sceneOverrideName] || null) : null;
        const srcView = ovView || rt.colorView;
        const ovKey = ovView ? '|' + sceneOverrideName : '';
        const bloomView = bloomName ? _textureViews[bloomName] : null;
        const aoView = aoName ? _textureViews[aoName] : null;
        let pipeline, bindGroup;
        if (aoView) {
            _AO_PARAMS_SCRATCH[0] = aoIntensity;
            _device.queue.writeBuffer(_aoParamsBuffer, 0, _AO_PARAMS_SCRATCH);
            pipeline = bloomView ? _blitPipelineUberBloomAo : _blitPipelineUberAo;
            const entries = [
                { binding: 0, resource: srcView },
                { binding: 1, resource: _blitSampler },
                { binding: 2, resource: { buffer: _postParamsBuffer } },
                { binding: 5, resource: aoView },
                { binding: 6, resource: { buffer: _aoParamsBuffer } },
            ];
            if (bloomView) entries.push({ binding: 3, resource: bloomView });
            bindGroup = _getVariantBindGroup(rt, (bloomView ? 'uberBloomAo' : 'uberAo') + ovKey, pipeline, bloomView, entries, aoView, srcView);
        } else if (bloomView) {
            pipeline = _blitPipelineUberBloom;
            bindGroup = _getVariantBindGroup(rt, 'uberBloom' + ovKey, pipeline, bloomView, [
                { binding: 0, resource: srcView },
                { binding: 1, resource: _blitSampler },
                { binding: 2, resource: { buffer: _postParamsBuffer } },
                { binding: 3, resource: bloomView },
            ], null, srcView);
        } else {
            pipeline = _blitPipelineUber;
            bindGroup = _getVariantBindGroup(rt, 'uber' + ovKey, pipeline, null, [
                { binding: 0, resource: srcView },
                { binding: 2, resource: { buffer: _postParamsBuffer } },
            ], null, srcView);
        }
        _passEncoder.setPipeline(pipeline);
        _passEncoder.setBindGroup(0, bindGroup);
        _passEncoder.draw(3);
    }

    function _ensureDepthTexture(width, height) {
        if (_depthTexture && _depthTexture.width === width && _depthTexture.height === height) return;
        if (_depthTexture) _depthTexture.destroy();
        _depthTexture = _device.createTexture({
            size: [width, height],
            format: 'depth24plus',
            usage: GPUTextureUsage.RENDER_ATTACHMENT,
        });
    }

    // Frame-level (1-1 Step 1):
    // only create commandEncoder / ensure the depth texture / reset pool cursors.
    // Pass open/close is driven by C# FrameSchedule via beginPass/endPass.
    // Clear color is supplied by beginPass.
    // Parameters remain here only for compatibility with the C# IGraphics.BeginFrame signature.
    function beginFrame(clearR, clearG, clearB, clearA) {
        if (_immediateClosed || _instancedDiag.deviceLost) throw new Error('WebGPU host closed or device lost');
        if (!_context || !_device) return;
        synchronizeRenderTargetSizes();
        if (_debugLog) _fpsFrameStartMs = performance.now();

        const width = _canvas.width, height = _canvas.height;
        _ensureDepthTexture(width, height);

        _poolCursor.vertex = _poolCursor.index = _poolCursor.uniform = _poolCursor.storage = 0;
        _computeFrameId++;
        _fogSamplingFrame = null;
        _weatherSamplingFrame = null;
        // 2-3 Step C: frame serial
        // gate for the "roll prev bone shadow copy exactly once per frame" rule
        _frameSerial++;

        _commandEncoder = _device.createCommandEncoder();
        _clearActivePass();

        _frameStarted = true;
    }

    // Pass orchestration (1-1 Steps 0+1):
    // the pass state machine lives on the JS side
    // (_passEncoder switches and all draw functions route implicitly).
    // Step 1 only targets the backbuffer
    // with Scene rendered directly.
    // Offscreen targets are introduced in Step 2.
    // passId order matches the C# RenderPassId enum,
    // and the debug group is attached at the encoder level to wrap the whole pass (Step 0).
    const _passLabels = ['Shadow', 'Scene', 'Post', 'OutlineMask', 'FinalBlit', 'Overlay', 'SceneOpaque', 'FogComposite', 'SceneTransparent', 'WeatherComposite'];

    function beginPass(passId, targetName, clearColorEnable, clearR, clearG, clearB, clearA, clearDepthEnable, storeDepth, depthTargetName, velocityTargetName, normalTargetName) {
        if (!_frameStarted || !_commandEncoder) return;
        if (_passEncoder) throw new Error('不能嵌套 WebGPU 渲染 Pass');
        if (_isSceneCollecting()) throw new Error('场景收集封存之前不能开始渲染 Pass');
        const fogTarget = targetName ? _renderTargets[targetName] : null;
        if ((passId === 7 || passId === 9) && (!fogTarget || fogTarget.depthOnly || !fogTarget.noImplicitDepth
            || fogTarget.colorFormat !== 'rgba16float' || depthTargetName || velocityTargetName || normalTargetName))
            throw new Error('雾合成必须使用独立 HDR 颜色目标，不能挂载深度或速度');
        _clearActivePass();

        _commandEncoder.pushDebugGroup(_passLabels[passId] || `Pass${passId}`);

        // Target resolution:
        // non-null targetName means an offscreen RT, otherwise the backbuffer (Step 2).
        // Depth-only RTs (Step 3 shadow maps) have no color attachment.
        let colorView, depthView, depthOnly = false;
        const rt = targetName ? _renderTargets[targetName] : null;
        if (rt) {
            // MatchBackbuffer RTs are lazily rebuilt on first use after resize
            // while the C# name handle remains unchanged
            if (rt.matchBackbuffer && (rt.width !== _canvas.width || rt.height !== _canvas.height)) {
                _resizeRenderTargetResources(rt, _canvas.width, _canvas.height);
            }
            colorView = rt.colorView;
            depthView = rt.depthView;
            depthOnly = !!rt.depthOnly;
        } else {
            colorView = _context.getCurrentTexture().createView();
            depthView = _depthTexture.createView();
        }

        // 2-2 dual-target Scene pass:
        // rebind the depth plane to explicit SceneDepth
        // (depth24plus shape, formatKind 3, matching the format already baked into pipelines).
        // Lazy-rebuild rules are the same as for color targets.
        const drt = depthTargetName ? _renderTargets[depthTargetName] : null;
        if (drt) {
            if (drt.matchBackbuffer && (drt.width !== _canvas.width || drt.height !== _canvas.height)) {
                _resizeRenderTargetResources(drt, _canvas.width, _canvas.height);
            }
            depthView = drt.depthView;
        }

        // 2-3 contract clause 2:
        // velocity attachment (MRT slot 1, formatKind 4 = rg16float with no companion depth).
        // Depth-only passes never carry it
        // because VELOCITY_OUTPUT and SHADOW_PASS are mutually exclusive under clause 3.
        // Clear value is always (0,0,0,0), matching the other three backends:
        // any untouched pixel becomes zero velocity.
        // Lazy-rebuild rules are the same as for color targets.
        const vrt = (!depthOnly && velocityTargetName) ? _renderTargets[velocityTargetName] : null;
        if (vrt) {
            if (vrt.matchBackbuffer && (vrt.width !== _canvas.width || vrt.height !== _canvas.height)) {
                _resizeRenderTargetResources(vrt, _canvas.width, _canvas.height);
            }
        }
        const velocityView = vrt ? vrt.colorView : null;

        // A0.2: SceneNormal attachment (MRT slot 2, formatKind 5 = rgba16float without companion depth), only ever next to
        // slot 1. Cleared to (0,0,0,0) and loaded/stored exactly like velocity.
        const nrt = (velocityView && normalTargetName) ? _renderTargets[normalTargetName] : null;
        if (nrt) {
            if (nrt.depthOnly || nrt.colorFormat !== 'rgba16float')
                throw new Error('SceneNormal 必须是 rgba16float 颜色目标');
            if (nrt.matchBackbuffer && (nrt.width !== _canvas.width || nrt.height !== _canvas.height)) {
                _resizeRenderTargetResources(nrt, _canvas.width, _canvas.height);
            }
        }
        const normalView = nrt ? nrt.colorView : null;

        const colorAttachments = depthOnly ? [] : [{
            view: colorView,
            clearValue: { r: clearR, g: clearG, b: clearB, a: clearA },
            loadOp: clearColorEnable ? 'clear' : 'load',
            storeOp: 'store',
        }];
        if (velocityView) {
            colorAttachments.push({
                view: velocityView,
                clearValue: { r: 0, g: 0, b: 0, a: 0 },
                loadOp: clearColorEnable ? 'clear' : 'load',
                storeOp: 'store',
            });
        }
        if (normalView) {
            colorAttachments.push({
                view: normalView,
                clearValue: { r: 0, g: 0, b: 0, a: 0 },
                loadOp: clearColorEnable ? 'clear' : 'load',
                storeOp: 'store',
            });
        }

        _passEncoder = _commandEncoder.beginRenderPass({
            // Depth-only passes (shadow maps) use a depth-only attachment set,
            // and contents must be rendered through the dedicated depth32float pipeline (1-5)
            colorAttachments,
            // FogComposite=7 只写颜色，不挂载也不清除原场景深度。
            depthStencilAttachment: (passId === 7 || passId === 9) ? undefined : {
                view: depthView,
                depthClearValue: 1.0,
                depthLoadOp: clearDepthEnable ? 'clear' : 'load',
                // StoreDepth=false maps to discard
                // aligned with DontCare on the DX/Vulkan side.
                // Shadow passes store depth for later sampling.
                depthStoreOp: storeDepth ? 'store' : 'discard',
            }
        });
        // 1-5: the depth-only flag drives implicit routing in draw functions
        // to the shadow pipeline + shadow bind group
        _passDepthOnly = depthOnly;
        // 2-3: the velocity flag drives draw sites to switch to the MRT variant table
        // attachment set and pipeline must match exactly, per rule 3
        _passVelocity = !!velocityView;
        _passNormal = !!normalView;
        // Phase 4: OutlineMask pass (RenderPassId.OutlineMask=3)
        // routes draw sites to the mask pipeline
        _passOutlineMask = (passId === 3);
        // Overlay pass (RenderPassId.Overlay=5):
        // routes draw sites to the overlay family
        // backbuffer format + depth off
        _passOverlay = (passId === 5);
        _activePassId = passId;
        _activeColorTarget = rt;
        _activeColorView = colorView;
        _activeDepthView = (passId === 7 || passId === 9) ? null : depthView;
    }

    function endPass() {
        if (!_passEncoder) return;
        _passEncoder.end();
        _passEncoder = null;
        _clearActivePass();
        if (_commandEncoder) _commandEncoder.popDebugGroup();
    }

    // 收集器保存已经解析的 GPU 绑定，不保存 Control、纹理名称或 WASM MemoryView。
    // 命令、排序索引和优先级数组跨帧复用；关闭路径不创建收集器。
    let _sceneCollection = null;
    let _sceneFogAllowed = true;
    const _sceneImmediateDraw = {};
    function _isSceneCollecting() { return _sceneCollection?.stage === 1; }

    function beginSceneCollection() {
        if (!_frameStarted || !_commandEncoder || _passEncoder || _instancedDiag.deviceLost)
            throw new Error('场景收集必须在有效帧的 Pass 外开始');
        const c = _sceneCollection ??= { stage: 0, entries: [], opaque: [], transparent: [], priorities: [] };
        if (c.stage !== 0) throw new Error('上一批场景命令尚未释放');
        c.frame = _computeFrameId; c.command = _commandEncoder; c.stage = 1;
        c.count = 0; c.priorityOffset = c.priorityCount = 0;
    }

    function _requireSceneCollection(stage) {
        const c = _sceneCollection;
        if (!c || c.stage !== stage || !_frameStarted || c.frame !== _computeFrameId
            || c.command !== _commandEncoder || _instancedDiag.deviceLost)
            throw new Error('场景收集阶段或帧身份不匹配');
        return c;
    }

    function setSceneDrawPriority(values) {
        const c = _requireSceneCollection(1);
        if (!values || values.length % 2 !== 0) throw new Error('场景优先级必须是 Layer/Order 整数对');
        for (let i = 0; i < values.length; i++)
            if (!Number.isInteger(values[i]) || values[i] < -2147483648 || values[i] > 2147483647)
                throw new Error('场景优先级必须是 Layer/Order 整数对');
        c.priorityOffset = c.priorities.length; c.priorityCount = values.length;
        for (let i = 0; i < values.length; i++) c.priorities.push(values[i]);
    }

    function _compareSceneDraws(aIndex, bIndex, transparent) {
        const c = _sceneCollection, a = c.entries[aIndex], b = c.entries[bIndex];
        for (let i = 0; i < Math.max(a.priorityCount, b.priorityCount); i++) {
            const av = i < a.priorityCount ? c.priorities[a.priorityOffset + i] : 0;
            const bv = i < b.priorityCount ? c.priorities[b.priorityOffset + i] : 0;
            if (av !== bv) return av - bv;
        }
        return (transparent ? b.depth - a.depth : 0) || aIndex - bIndex;
    }

    function sealSceneCollection() {
        const c = _requireSceneCollection(1);
        if (_passEncoder) throw new Error('不能在 Pass 内封存场景');
        c.opaque.sort((a, b) => _compareSceneDraws(a, b, false));
        c.transparent.sort((a, b) => _compareSceneDraws(a, b, true));
        c.stage = 2;
    }

    function drawCollectedOpaque() {
        const c = _requireSceneCollection(2);
        if (!_passEncoder || _activePassId !== 6) throw new Error('不透明命令只能在 SceneOpaque 消费');
        c.stage = 3;
        for (const index of c.opaque) _executeSceneDraw(c.entries[index]);
    }

    function drawCollectedTransparent(sampleFog) {
        const c = _requireSceneCollection(3);
        if (!_passEncoder || _activePassId !== 8) throw new Error('透明命令只能在 SceneTransparent 消费');
        c.stage = 4;
        const previous = _sceneFogAllowed;
        _sceneFogAllowed = !!sampleFog;
        try { for (const index of c.transparent) _executeSceneDraw(c.entries[index]); }
        finally { _sceneFogAllowed = previous; }
    }

    function releaseSceneCollection() {
        const c = _sceneCollection;
        if (!c) return;
        for (let i = 0; i < c.count; i++) {
            const entry = c.entries[i];
            entry.group = entry.instances = entry.vertices = entry.indices = null;
        }
        c.opaque.length = c.transparent.length = c.priorities.length = 0;
        c.count = c.stage = 0; c.command = null;
    }

    function _executeSceneDraw(entry) {
        const pipelines = _activeMeshPipelines();
        const draw = pipeline => {
            _setMeshPipeline(pipeline);
            if (entry.instances) _passEncoder.setVertexBuffer(0, entry.instances);
            if (entry.vertices) _passEncoder.setVertexBuffer(1, entry.vertices);
            _passEncoder.setBindGroup(0, entry.group);
            if (entry.indices) {
                _passEncoder.setIndexBuffer(entry.indices, entry.indexFormat);
                _passEncoder.drawIndexed(entry.count, entry.instanceCount, 0, 0, entry.firstInstance);
            } else _passEncoder.draw(entry.count, entry.instanceCount, 0, entry.firstInstance);
        };
        if (entry.indices) _drawIndexedWithOptionalDoubleSidedTransparency(pipelines, entry.mode, entry.doubleSided, draw);
        else draw(pipelines[_getPipelineVariantKey(entry.mode, entry.doubleSided)]);
    }

    function _submitSceneDraw(mode, doubleSided, group, instances, vertices, indices, indexFormat, count,
        instanceCount = 1, firstInstance = 0, depth = 0) {
        if (!Number.isFinite(depth)) throw new Error('场景绘制深度必须有限');
        const c = _isSceneCollecting() ? _requireSceneCollection(1) : null;
        const entry = c ? (c.entries[c.count] ??= {}) : _sceneImmediateDraw;
        entry.mode = mode; entry.doubleSided = doubleSided; entry.group = group;
        entry.instances = _readSceneBuffer(instances); entry.vertices = _readSceneBuffer(vertices);
        entry.indices = _readSceneBuffer(indices); entry.indexFormat = indexFormat;
        entry.count = count; entry.instanceCount = instanceCount; entry.firstInstance = firstInstance; entry.depth = depth;
        if (c) {
            entry.priorityOffset = c.priorityOffset; entry.priorityCount = c.priorityCount;
            (mode.startsWith('opaque') ? c.opaque : c.transparent).push(c.count++);
        } else {
            try { _executeSceneDraw(entry); }
            finally { entry.group = entry.instances = entry.vertices = entry.indices = null; }
        }
    }

    function _sceneDrawDepth(uniform, center = null, instanceData = null, firstInstance = 0) {
        const m = instanceData || uniform, offset = instanceData ? firstInstance * 20 : 0;
        const x = center?.[0] ?? 0, y = center?.[1] ?? 0, z = center?.[2] ?? 0;
        const wx = x*m[offset]+y*m[offset+4]+z*m[offset+8]+m[offset+12];
        const wy = x*m[offset+1]+y*m[offset+5]+z*m[offset+9]+m[offset+13];
        const wz = x*m[offset+2]+y*m[offset+6]+z*m[offset+10]+m[offset+14];
        return wx*uniform[18]+wy*uniform[22]+wz*uniform[26]+uniform[30];
    }

    function _meshBoundsCenter(vertices, stride) {
        let minX = Infinity, minY = Infinity, minZ = Infinity, maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
        for (let i = 0; i + 2 < vertices.length; i += stride) {
            minX = Math.min(minX, vertices[i]); maxX = Math.max(maxX, vertices[i]);
            minY = Math.min(minY, vertices[i+1]); maxY = Math.max(maxY, vertices[i+1]);
            minZ = Math.min(minZ, vertices[i+2]); maxZ = Math.max(maxZ, vertices[i+2]);
        }
        return vertices.length >= 3 ? [(minX+maxX)*0.5, (minY+maxY)*0.5, (minZ+maxZ)*0.5] : [0,0,0];
    }

    function _getUniformI32View(uniformData) {
        return new Int32Array(uniformData.buffer, uniformData.byteOffset, uniformData.byteLength / 4);
    }

    // flags.w (offset 99) NoDepthWrite bit (128) =
    // 2-2 contract clause 7:
    // GTAO-exempt meshes route to Nd variants.
    // The Scene pass then does not write depth,
    // SceneDepth keeps its clear value,
    // and the GTAO sky/empty-space branch is exempted.
    function _selectPipelineMode(alpha, alphaMode, flagsW = 0) {
        if (alphaMode === 2) return 'transparent';
        const noDepthWrite = (flagsW & 128) !== 0;
        if (alpha < 0.999) return noDepthWrite ? 'fadeNd' : 'fade';
        return noDepthWrite ? 'opaqueNd' : 'opaque';
    }

    function _getPipelineVariantKey(modeKey, doubleSided) {
        if (!doubleSided) return modeKey;
        return modeKey === 'opaque'
            ? 'opaqueDoubleSided'
            : modeKey === 'fade'
                ? 'fadeDoubleSided'
                : modeKey === 'opaqueNd'
                    ? 'opaqueNdDoubleSided'
                    : modeKey === 'fadeNd'
                        ? 'fadeNdDoubleSided'
                        : 'transparentDoubleSided';
    }

    function _isTransparentMode(uniformData) {
        const uniformI32 = _getUniformI32View(uniformData);
        return _selectPipelineMode(uniformData[94], uniformI32[98], uniformI32[99]) === 'transparent';
    }

    function _drawIndexedWithOptionalDoubleSidedTransparency(pipelineSet, modeKey, doubleSided, setStateAndDraw) {
        if (modeKey === 'transparent' && doubleSided) {
            setStateAndDraw(pipelineSet.transparentBackFace);
            setStateAndDraw(pipelineSet.transparent);
            return;
        }

        setStateAndDraw(pipelineSet[_getPipelineVariantKey(modeKey, doubleSided)]);
    }

    function _selectPipelineByUniform(pipelineSet, uniformData, doubleSided = false) {
        const uniformI32 = _getUniformI32View(uniformData);
        const modeKey = _selectPipelineMode(uniformData[94], uniformI32[98], uniformI32[99]);
        return pipelineSet[_getPipelineVariantKey(modeKey, doubleSided)];
    }

    // 1-2 contract 8: shared scene-lighting UBO (binding 10)
    // C# UpdateCamera3D uploads SceneLightParams in one full-block update via updateSceneLights each frame.
    // After the unified-lighting refactor it became 1152B,
    // extended again to 1216B in 2-5 Step B (b11),
    // to 1232B after the zenith axis in Step C,
    // and to 1360B after procedural clouds in Step C.
    // Directional light has been merged into lights[8] (dirType.w=2),
    // the old sunDirection/sunColor fields were removed,
    // and subsequent fields shifted forward by 32B.
    // velocityParams @ offset 928, envParams @ offset 944, irradianceSH9[9] @ offset 960,
    // giParams0..2 @ offset 1104, skyParams0..4 @ offset 1152,
    // cloudLayerA[3] @ offset 1232, cloudLayerB[3] @ offset 1280,
    // cloudParams0/1 @ offset 1328.
    // WGSL on this backend does not declare those tail fields,
    // so not reading them is harmless, but the UBO length must still track them exactly
    // to stay byte-for-byte aligned with WGSL SceneLights.
    // This replaces the old per-draw inline 108-float lighting block
    // which is now a retired reserved region.
    // A single buffer stays resident across frames and is not pooled:
    // all draw calls share the same lighting state,
    // semantically identical to SetLighting on the other three backends.
    // Length must exactly match C# SceneLightParams.Bytes,
    // or updateSceneLights silently drops the upload.
    const SCENE_LIGHT_BYTES = 2432;
    let _sceneLightBuffer = null;

    function _ensureSceneLightBuffer() {
        if (!_sceneLightBuffer) {
            _sceneLightBuffer = _device.createBuffer({
                size: SCENE_LIGHT_BYTES,
                usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
            });
        }
        return _sceneLightBuffer;
    }

    function updateSceneLights(lightBytes) {
        if (!_device) return;
        lightBytes = _interopToU8(lightBytes);
        if (!lightBytes || lightBytes.byteLength !== SCENE_LIGHT_BYTES) return;
        _sceneLightBuffer = _writeSceneBuffer(_ensureSceneLightBuffer(), lightBytes, GPUBufferUsage.UNIFORM);
    }

    // prevBoneBuffer / prevInstanceBuffer (2-3 Step C):
    // when omitted, bind the default fallback sentinels.
    // This backend does not use layout:'auto',
    // so every entry must carry a resource.
    // When the corresponding hasPrev* bits are 0, VS does not read them,
    // so contents are irrelevant
    // which is exactly the case for text / sprite paths.
    function _createMeshBindGroup(layout, uniformBuffer, texView, normalView, mrView, aoView, emissiveView, boneBuffer, morphMetaBuffer, morphDataBuffer, prevBoneBuffer, prevInstanceBuffer) {
        return _device.createBindGroup({
            layout,
            entries: [
                { binding: 0, resource: { buffer: uniformBuffer } },
                { binding: 1, resource: _samplers['linear'] },
                { binding: 2, resource: _readSceneTextureView(texView) },
                { binding: 3, resource: _readSceneTextureView(normalView) },
                { binding: 4, resource: _readSceneTextureView(mrView) },
                { binding: 5, resource: _readSceneTextureView(aoView) },
                { binding: 6, resource: _readSceneTextureView(emissiveView) },
                { binding: 7, resource: { buffer: _readSceneBuffer(boneBuffer || _defaultBoneBuffer) } },
                { binding: 8, resource: { buffer: _readSceneBuffer(morphMetaBuffer || _defaultMorphMetaBuffer) } },
                { binding: 9, resource: { buffer: _readSceneBuffer(morphDataBuffer || _defaultMorphDataBuffer) } },
                { binding: 10, resource: { buffer: _readSceneBuffer(_ensureSceneLightBuffer()) } },
                // 1-5: lazily resolve the atlas view by name-as-handle,
                // falling back to a 1x1 dummy when the RT has not been created yet
                { binding: 11, resource: _getShadowAtlasView() },
                { binding: 12, resource: _samplers['shadow'] },
                { binding: 13, resource: { buffer: _readSceneBuffer(prevBoneBuffer || _defaultBoneBuffer) } },
                { binding: 14, resource: { buffer: _readSceneBuffer(prevInstanceBuffer || _defaultPrevInstanceBuffer) } },
                // 1-7: lazily resolve the environment cube view
                // with the same name-as-handle pattern,
                // falling back to a 1x1 all-black cube when not registered.
                // This function is the only bind-group creation point for the main layout
                // and all 8 draw sites converge here,
                // so expanding it once covers the entire PBR / text / sprite render path.
                { binding: 15, resource: _getEnvCubeView() },
                // 2-4 clause 10: lazily resolve the DDGI irradiance atlas view
                // by name-as-handle, same as bloom / AO.
                // Fall back to 1x1 White when unregistered / not ready.
                // Actual sampling is gated by WGSL DDGI_ENABLED + giParams.
                { binding: 16, resource: _getDdgiAtlasView() },
                // 2-4 Step 3: lazily resolve the DDGI depth-moment atlas view
                // same as binding 16.
                // Fall back to 1x1 White when not ready.
                // Actual Chebyshev sampling is runtime-gated by WGSL giParams2.y.
                { binding: 17, resource: _getDdgiDepthView() },
                // 2-5 Step C: lazily resolve the cloud-noise view
                // by name-as-handle, same as bindings 16/17.
                // Fall back to 1x1 White when not ready -
                // this is a dangerous fallback,
                // so actual sampling is runtime-gated by WGSL cloudParams0.w (layer count)
                // to guarantee zero sampling.
                { binding: 18, resource: _getCloudNoiseView() },
                // 2-5 Step E: lazily resolve the AP 3D LUT view from the 3D registry.
                // Fall back to 1x1x1 all-zero when not ready
                // as the additive identity element.
                // apParams0.x gating only saves the sampling cost.
                { binding: 19, resource: _getAerialLutView() },
                { binding: 20, resource: _samplers['repeat'] },
                // 1-5 clause 15: same atlas as binding 11, reached non-comparatively so the blocker search can read depth.
                { binding: 21, resource: _samplers['shadowPoint'] },
            ]
        });
    }

    function _getShadowAtlasView() {
        const rt = _shadowAtlasName ? _renderTargets[_shadowAtlasName] : null;
        return (rt && rt.depthView) || _defaultShadowView;
    }

    // 1-7: resolve the environment radiance cube view.
    // When the name is not registered or the cube was never created
    // for example after load failure,
    // fall back to a 1x1 all-black cube
    // so WGSL-side uEnvCube always has a valid binding.
    // Graceful degradation under contract clause 8 is carried by the envParams switch,
    // not by missing bindings.
    function _getEnvCubeView() {
        const cube = _envCubeName ? _textureCubes[_envCubeName] : null;
        return (cube && cube.view) || _defaultEnvCubeView;
    }

    // 1-7: register the environment cube name
    // name-as-handle, same pattern as setShadowAtlas.
    // Supplied every frame by C# UpdateCamera3D.
    // null / empty falls back to the black fallback cube.
    function setEnvCube(name) {
        _envCubeName = name || null;
    }

    // 2-4 clause 10: resolve the DDGI irradiance atlas view.
    // Fall back to 1x1 White when the name is not registered or the atlas is not built.
    // It is not a depth texture and can be sampled as float 2D,
    // ensuring WGSL-side uDdgiAtlas always has a valid binding.
    // Actual sampling enablement is carried by giParams.
    function _getDdgiAtlasView() {
        return (_ddgiAtlasName ? _textureViews[_ddgiAtlasName] : null) || _textureViews['White'];
    }

    // 2-4 clause 10: register the DDGI irradiance atlas name for the current frame
    // same pattern as setEnvCube.
    // Supplied every frame by C# UpdateCamera3D.
    // null / empty falls back to White.
    function setDdgiAtlas(name) {
        _ddgiAtlasName = name || null;
    }

    // 2-4 Step 3: resolve the DDGI depth-moment atlas view
    // same as _getDdgiAtlasView.
    // Fall back to 1x1 White when not ready.
    function _getDdgiDepthView() {
        return (_ddgiDepthName ? _textureViews[_ddgiDepthName] : null) || _textureViews['White'];
    }

    // 2-4 Step 3: register the DDGI depth-moment atlas name for the current frame
    // same as setDdgiAtlas.
    // null / empty falls back to White.
    function setDdgiDepth(name) {
        _ddgiDepthName = name || null;
    }

    // 2-5 Step C: resolve the cloud-noise view.
    // Fall back to 1x1 White when the name is not registered or the texture is not built.
    // This is a dangerous value and must be protected by layer-count gating.
    function _getCloudNoiseView() {
        return (_cloudNoiseName ? _textureViews[_cloudNoiseName] : null) || _textureViews['White'];
    }

    // 2-5 Step C: register the cloud-noise texture name for the current frame
    // same pattern as setDdgiAtlas.
    // null / empty falls back to White.
    function setCloudNoise(name) {
        _cloudNoiseName = name || null;
    }

    // 2-5 Step E: resolve the AP 3D LUT view from the 3D registry.
    // Fall back to 1x1x1 all-zero when unregistered / not built
    // as the additive identity element.
    function _getAerialLutView() {
        return (_aerialLutName ? _textureViews3d[_aerialLutName] : null) || _defaultAerialLutView;
    }

    // 2-5 Step E: register the AP 3D LUT name for the current frame
    // same as setCloudNoise.
    // null / empty falls back to the all-zero 3D sentinel.
    function setAerialLut(name) {
        _aerialLutName = name || null;
    }

    // Shadow-pass dedicated bind group
    // bindings 0/7/8/9 + the 2-3 Step C fallback sentinels at 13/14.
    // It does not include the atlas
    // because attachment binding and sampled binding are mutually exclusive under validation.
    // The depth path does not need prev data and always binds the default fallback sentinels.
    function _createShadowBindGroup(uniformBuffer, boneBuffer, morphMetaBuffer, morphDataBuffer) {
        return _device.createBindGroup({
            layout: _shadowBindGroupLayout,
            entries: [
                { binding: 0, resource: { buffer: uniformBuffer } },
                { binding: 7, resource: { buffer: _readSceneBuffer(boneBuffer || _defaultBoneBuffer) } },
                { binding: 8, resource: { buffer: _readSceneBuffer(morphMetaBuffer || _defaultMorphMetaBuffer) } },
                { binding: 9, resource: { buffer: _readSceneBuffer(morphDataBuffer || _defaultMorphDataBuffer) } },
                { binding: 13, resource: { buffer: _defaultBoneBuffer } },
                { binding: 14, resource: { buffer: _defaultPrevInstanceBuffer } },
            ]
        });
    }

    // 1-5: shadow-atlas quadrant viewport + scissor.
    // RenderShadowPass switches these per quadrant.
    // Batched draws are submitted lazily,
    // so the C# side must flush before switching.
    // See the ordering contract in Web/Graphics.cs RenderShadowPass.
    function setShadowViewport(x, y, size) {
        if (!_passEncoder) return;
        _passEncoder.setViewport(x, y, size, size, 0, 1);
        _passEncoder.setScissorRect(x, y, size, size);
    }

    function setShadowAtlas(name) {
        _shadowAtlasName = name || null;
    }

    function drawSprite2D(name, x, y, width, height, alpha, colorR, colorG, colorB, colorA, flipX, flipY, renderMode, pixelRange, clock, sourceX, sourceY, sourceWidth, sourceHeight) {
        if (!_frameStarted || (!_passEncoder && !_isSceneCollecting())) return;

        const texView = _textureViews[name] || _textureViews['White'];
        if (!texView) return;
        const whiteView = _textureViews['White'] || texView;

        const uni = _SPRITE2D_UNIFORM_SCRATCH;
        uni[84] = colorR; uni[85] = colorG; uni[86] = colorB; uni[87] = colorA;
        uni[94] = alpha;

        _SPRITE2D_UNIFORM_I32[1] = renderMode || 0;
        uni[92] = pixelRange || 0;

        const uniformBuffer = _acquireBuffer('uniform', uni.byteLength, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(uniformBuffer, 0, uni);

        // Four-corner UVs:
        // flip is applied in source space first,
        // then rotated clockwise by clock
        // matching DX / Vulkan / Metal TextCoords.GetTransforms semantics.
        // Base corners are (TL, TR, BL, BR).
        // Rotation affects only UVs and leaves corner positions unchanged,
        // aligning with native behavior for non-square image stretching.
        const rotateUv = (u, v) => {
            if (clock === 90) return [v, 1 - u];
            if (clock === 180) return [1 - u, 1 - v];
            if (clock === 270) return [1 - v, u];
            return [u, v];
        };
        let [tlU, tlV] = rotateUv(flipX ? 1 : 0, flipY ? 1 : 0);
        let [trU, trV] = rotateUv(flipX ? 0 : 1, flipY ? 1 : 0);
        let [blU, blV] = rotateUv(flipX ? 1 : 0, flipY ? 0 : 1);
        let [brU, brV] = rotateUv(flipX ? 0 : 1, flipY ? 0 : 1);

        // Source sub-rect drawing:
        // normalized mapping from the pixel source region
        // active when sourceWidth > 0, isomorphic to DX MapU/MapV.
        const meta = _textureMeta[name];
        const texW = meta ? meta.width : 0;
        const texH = meta ? meta.height : 0;
        if ((sourceWidth > 0 && texW > 0) || (sourceHeight > 0 && texH > 0)) {
            const mapU = u => (sourceWidth > 0 && texW > 0) ? (sourceX / texW) + u * (sourceWidth / texW) : u;
            const mapV = v => (sourceHeight > 0 && texH > 0) ? (sourceY / texH) + v * (sourceHeight / texH) : v;
            tlU = mapU(tlU); tlV = mapV(tlV);
            trU = mapU(trU); trV = mapV(trV);
            blU = mapU(blU); blV = mapV(blV);
            brU = mapU(brU); brV = mapV(brV);
        }

        const vd = _SPRITE2D_VERTEX_SCRATCH;
        const x1 = x + width, y1 = y + height;
        vd.fill(0);
        const writeSpriteVertex = (base, px, py, pu, pv) => {
            vd[base + 0] = px;
            vd[base + 1] = py;
            vd[base + 2] = 0;
            vd[base + 3] = pu;
            vd[base + 4] = pv;
            vd[base + 5] = 0;
            vd[base + 6] = 0;
            vd[base + 7] = -1;
            vd[base + 8] = 1;
            vd[base + 9] = 0;
            vd[base + 10] = 0;
            vd[base + 11] = 1;
        };
        writeSpriteVertex(0,   x,  y,  tlU, tlV);
        writeSpriteVertex(20, x1,  y,  trU, trV);
        writeSpriteVertex(40,  x, y1,  blU, blV);
        writeSpriteVertex(60, x1,  y,  trU, trV);
        writeSpriteVertex(80, x1, y1,  brU, brV);
        writeSpriteVertex(100, x, y1,  blU, blV);

        const vertexBuffer = _acquireBuffer('vertex', vd.byteLength, GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(vertexBuffer, 0, vd);

        const bindGroup = _createMeshBindGroup(
            _mesh3DPipeline.bindGroupLayout,
            uniformBuffer,
            texView,
            whiteView,
            whiteView,
            whiteView,
            whiteView);

        _submitSceneDraw(_selectPipelineMode(uni[94], _getUniformI32View(uni)[98], _getUniformI32View(uni)[99]),
            false, bindGroup, _identityInstanceBuffer, vertexBuffer, null, null, 6);
    }

    function drawTextAtlasSprite(x, y, width, height, u0, v0, u1, v1, alpha, colorR, colorG, colorB, colorA, renderMode, pixelRange) {
        if (!_frameStarted || (!_passEncoder && !_isSceneCollecting())) return;

        const texView = _textureViews['TextAtlas'] || _textureViews['White'];
        if (!texView) return;
        const whiteView = _textureViews['White'] || texView;

        const uni = _SPRITE2D_UNIFORM_SCRATCH;
        uni[84] = colorR; uni[85] = colorG; uni[86] = colorB; uni[87] = colorA;
        uni[94] = alpha;

        _SPRITE2D_UNIFORM_I32[1] = renderMode || 0;
        uni[92] = pixelRange || 0;

        const uniformBuffer = _acquireBuffer('uniform', uni.byteLength, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(uniformBuffer, 0, uni);

        const vd = _SPRITE2D_VERTEX_SCRATCH;
        const x1 = x + width, y1 = y + height;
        vd.fill(0);
        const writeSpriteVertex = (base, px, py, pu, pv) => {
            vd[base + 0] = px;
            vd[base + 1] = py;
            vd[base + 2] = 0;
            vd[base + 3] = pu;
            vd[base + 4] = pv;
            vd[base + 5] = 0;
            vd[base + 6] = 0;
            vd[base + 7] = -1;
            vd[base + 8] = 1;
            vd[base + 9] = 0;
            vd[base + 10] = 0;
            vd[base + 11] = 1;
        };
        writeSpriteVertex(0,   x,  y,  u0, v0);
        writeSpriteVertex(20, x1,  y,  u1, v0);
        writeSpriteVertex(40,  x, y1,  u0, v1);
        writeSpriteVertex(60, x1,  y,  u1, v0);
        writeSpriteVertex(80, x1, y1,  u1, v1);
        writeSpriteVertex(100, x, y1,  u0, v1);

        const vertexBuffer = _acquireBuffer('vertex', vd.byteLength, GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(vertexBuffer, 0, vd);

        const bindGroup = _createMeshBindGroup(
            _mesh3DPipeline.bindGroupLayout,
            uniformBuffer,
            texView,
            whiteView,
            whiteView,
            whiteView,
            whiteView);

        _submitSceneDraw(_selectPipelineMode(uni[94], _getUniformI32View(uni)[98], _getUniformI32View(uni)[99]),
            false, bindGroup, _identityInstanceBuffer, vertexBuffer, null, null, 6);
    }

    // Text GPU instancing
    // aligned with DX / Vulkan: one instanced draw renders the whole Texts control.
    // Per-Texts persistent resources:
    // instance buffer (world-matrix stream) + glyph storage buffer (bound at binding 9)
    const _textInstances = {};

    function updateTextInstance(key, instanceBytes, glyphBytes, instanceCount) {
        if (!_device) return;

        let entry = _textInstances[key];
        if (!entry) {
            entry = { instanceBuffer: null, glyphBuffer: null, instanceCount: 0 };
            _textInstances[key] = entry;
        }
        entry.instanceCount = instanceCount | 0;

        instanceBytes = _interopToU8(instanceBytes);
        if (instanceBytes && instanceBytes.byteLength > 0) {
            entry.instanceBuffer = _writeSceneBuffer(entry.instanceBuffer, instanceBytes, GPUBufferUsage.VERTEX, 16);
            // 下述跨帧复用仍成立；同帧已被读取的版本由上方 helper 独立保活。
            // queue.writeBuffer has no in-flight frame race here,
            // so one persistent buffer is enough
            // with no DX-style multi-frame synchronization needed
        }

        glyphBytes = _interopToU8(glyphBytes);
        if (glyphBytes && glyphBytes.byteLength > 0) {
            entry.glyphBuffer = _writeSceneBuffer(entry.glyphBuffer, glyphBytes, GPUBufferUsage.STORAGE, 16);
        }
    }

    function drawTextInstanced(key, alpha, colorR, colorG, colorB, colorA, pixelRange) {
        if (!_frameStarted || (!_passEncoder && !_isSceneCollecting())) return;

        const entry = _textInstances[key];
        if (!entry || !entry.instanceBuffer || !entry.glyphBuffer || entry.instanceCount <= 0) return;

        const texView = _textureViews['TextAtlas'];
        if (!texView) return;
        const whiteView = _textureViews['White'] || texView;

        _ensureSprite3DBuffer();

        const uni = _TEXT_UNIFORM_SCRATCH;
        uni[84] = colorR; uni[85] = colorG; uni[86] = colorB; uni[87] = colorA;
        uni[92] = pixelRange || 0;   // material.x = PxRange
        uni[94] = alpha;             // material.z = GlobalAlpha
        // [104] old hdrExposure slot is retired
        // (1-2 contract 8: inverse-ACES compensation for text now reads uLights.params0.y)

        const uniformBuffer = _acquireBuffer('uniform', uni.byteLength, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(uniformBuffer, 0, uni);

        // Glyph data binds at binding 9
        // reusing the morph-deltas slot and matching DX t5 / Vulkan binding 10
        const bindGroup = _createMeshBindGroup(
            _mesh3DPipeline.bindGroupLayout,
            uniformBuffer,
            texView,
            whiteView,
            whiteView,
            whiteView,
            whiteView,
            null,
            null,
            entry.glyphBuffer);

        // Transparent + DoubleSided
        // aligned with DX SetPipeline(Transparent, doubleSided: true)
        _submitSceneDraw('transparent', true, bindGroup, entry.instanceBuffer, _sprite3DVertexBuffer,
            null, null, 6, entry.instanceCount);
    }

    function disposeTextInstance(key) {
        const entry = _textInstances[key];
        if (!entry) return;
        if (entry.instanceBuffer) _queueComputeRetirement(entry.instanceBuffer);
        if (entry.glyphBuffer) _queueComputeRetirement(entry.glyphBuffer);
        delete _textInstances[key];
    }

    let _drawSprite3DDiagCount = 0;
    function drawSprite3D(name, uniformData, billboard) {
        if (!_frameStarted || (!_passEncoder && !_isSceneCollecting())) return;
        if (!ArrayBuffer.isView(uniformData)) uniformData = new Float32Array(uniformData);
        if (_debugLog && _drawSprite3DDiagCount < 3) {
            _log(`[drawSprite3D] name=${name} billboard=${billboard} alpha=${uniformData?.[94]?.toFixed(2)}`);
            _drawSprite3DDiagCount++;
        }

        const texView = _textureViews[name] || _textureViews['White'];
        if (!texView) return;

        const uniformBuffer = _acquireBuffer('uniform', uniformData.byteLength, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(uniformBuffer, 0, uniformData);

        _ensureSprite3DBuffer();

        const whiteView = _textureViews['White'] || texView;
        const bindGroup = _createMeshBindGroup(
            _mesh3DPipeline.bindGroupLayout,
            uniformBuffer,
            texView,
            whiteView,
            whiteView,
            whiteView,
            whiteView);

        _submitSceneDraw(_selectPipelineMode(uniformData[94], _getUniformI32View(uniformData)[98], _getUniformI32View(uniformData)[99]),
            false, bindGroup, _identityInstanceBuffer, _sprite3DVertexBuffer, null, null, 6, 1, 0,
            _isSceneCollecting() ? _sceneDrawDepth(uniformData) : 0);
    }

    let _drawMesh3DDiagCount = 0;
    function drawMesh3D(name, vertexData, indexData, uniformData, textureName, metallicRoughnessTextureName, indexFormat = 'uint16', doubleSided = false) {
        if (!_frameStarted || (!_passEncoder && !_isSceneCollecting())) return;

        if (_debugLog && _drawMesh3DDiagCount < 30) {
            _log(`[drawMesh3D] ${name} v${vertexData?.byteLength} i${indexData?.length} u${uniformData?.byteLength} ${textureName}`);
            _drawMesh3DDiagCount++;
        }

        if (!ArrayBuffer.isView(vertexData)) vertexData = new Float32Array(vertexData);
        const resolvedIndexFormat = (indexFormat === 'uint32' || indexData instanceof Uint32Array) ? 'uint32' : 'uint16';
        if (!ArrayBuffer.isView(indexData)) indexData = resolvedIndexFormat === 'uint32' ? new Uint32Array(indexData) : new Uint16Array(indexData);
        if (!ArrayBuffer.isView(uniformData)) uniformData = new Float32Array(uniformData);

        const texView = _textureViews[textureName] || _textureViews['White'];
        const whiteView = _textureViews['White'] || texView;
        const normalView = whiteView;
        const mrView = (metallicRoughnessTextureName && _textureViews[metallicRoughnessTextureName]) || whiteView;
        const aoView = whiteView;
        const emissiveView = whiteView;

        const vBuffer = _acquireBuffer('vertex', vertexData.byteLength, GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(vBuffer, 0, vertexData);

        const iBuffer = _acquireBuffer('index', indexData.byteLength, GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(iBuffer, 0, indexData);

        const uBuffer = _acquireBuffer('uniform', uniformData.byteLength, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(uBuffer, 0, uniformData);

        const bindGroup = _createMeshBindGroup(
            _mesh3DPipeline.bindGroupLayout,
            uBuffer,
            texView,
            normalView,
            mrView,
            aoView,
            emissiveView);

        const modeKey = _isTransparentMode(uniformData) ? 'transparent' : _selectPipelineMode(uniformData[94], _getUniformI32View(uniformData)[98], _getUniformI32View(uniformData)[99]);
        _submitSceneDraw(modeKey, doubleSided, bindGroup, _identityInstanceBuffer, vBuffer, iBuffer,
            resolvedIndexFormat, indexData.length, 1, 0,
            _isSceneCollecting() ? _sceneDrawDepth(uniformData, _meshBoundsCenter(vertexData, 20)) : 0);
    }

    // Draw static meshes that have already been uploaded into persistent GPU buffers
    function drawMesh3DCached(cacheKey, uniformData, textureName, metallicRoughnessTextureName) {
        if (!_frameStarted || (!_passEncoder && !_isSceneCollecting())) return;
        const mesh = _staticMeshes[cacheKey];
        if (!mesh) {
            if (_debugLog) console.warn(`[drawMesh3DCached] missing key=${cacheKey}, fallback ignored`);
            return;
        }

        if (!ArrayBuffer.isView(uniformData)) uniformData = new Float32Array(uniformData);

        const texView = _textureViews[textureName] || _textureViews['White'];
        const whiteView = _textureViews['White'] || texView;
        const mrView = (metallicRoughnessTextureName && _textureViews[metallicRoughnessTextureName]) || whiteView;

        const uBuffer = _acquireBuffer('uniform', uniformData.byteLength, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(uBuffer, 0, uniformData);

        const bindGroup = _createMeshBindGroup(
            _mesh3DPipeline.bindGroupLayout,
            uBuffer,
            texView,
            whiteView,
            mrView,
            whiteView,
            whiteView);

        const modeKey = _isTransparentMode(uniformData) ? 'transparent' : _selectPipelineMode(uniformData[94], _getUniformI32View(uniformData)[98], _getUniformI32View(uniformData)[99]);
        _submitSceneDraw(modeKey, mesh.doubleSided, bindGroup, _identityInstanceBuffer, mesh.vBuffer, mesh.iBuffer,
            mesh.indexFormat || 'uint16', mesh.indexCount, 1, 0,
            _isSceneCollecting() ? _sceneDrawDepth(uniformData, mesh.boundsCenter) : 0);
    }

    function drawMesh3DBatch(cacheKeys, uniformBytes, count, skinKey) {
        if (!_frameStarted || (!_passEncoder && !_isSceneCollecting()) || count <= 0) return;

        const boneBuffer = skinKey ? (_skinnedBoneBuffers[skinKey] || _defaultBoneBuffer) : _defaultBoneBuffer;
        // 2-3 Step C: the prev bone palette is populated automatically
        // from the shadow copy maintained by uploadSkinnedBones.
        // When that shadow copy is not yet formed
        // (first frame, or bone count just changed),
        // fall back to the current-frame palette.
        // Even if the C# side has already set hasPrevBones,
        // the deformation component of velocity then degrades to zero instead of becoming wrong,
        // which is the graceful degradation required by contract clause 8.
        // It must never fall back to _defaultBoneBuffer,
        // because identity matrices would snap vertices back to rest pose.
        const prevBoneBuffer = skinKey ? (_prevSkinnedBoneBuffers[skinKey] || boneBuffer) : _defaultBoneBuffer;

        const STRIDE = 108;  // Aligned with C# WebGPUUniformLayout.TotalFloats
                             // including the hdrParams vec4 added at the end in 1-4 Step B
        const BYTES_PER_UNIFORM = STRIDE * 4;

        uniformBytes = _interopToU8(uniformBytes);
        let floatView;
        if (uniformBytes instanceof Uint8Array) {
            floatView = new Float32Array(uniformBytes.buffer, uniformBytes.byteOffset, count * STRIDE);
        } else if (uniformBytes instanceof Float32Array) {
            floatView = uniformBytes;
        } else {
            floatView = new Float32Array(uniformBytes);
        }

        const intView = new Int32Array(floatView.buffer, floatView.byteOffset, count * STRIDE);
        const whiteView = _textureViews['White'];

        // Nd keys (2-2 contract clause 7):
        // GTAO-exempt modes keep the same bucket order as base modes
        // with opaque / fade first and transparent last
        // 收集时按原图元序列捕获，再做全场景排序，不能先按材质桶改变等深度顺序。
        for (const batchMode of (_isSceneCollecting() ? [null] : ['opaque', 'fade', 'opaqueNd', 'fadeNd', 'transparent'])) {
            let activePipeline = null;
            for (let i = 0; i < count; i++) {
                const cacheKey = cacheKeys[i];
                const mesh = _staticMeshes[cacheKey];
                if (!mesh) continue;

                const uniformBase = i * STRIDE;
                const alphaMode = intView[uniformBase + 98];
                const modeKey = _selectPipelineMode(floatView[uniformBase + 94], alphaMode, intView[uniformBase + 99]);
                if (batchMode !== null && modeKey !== batchMode) continue;

                const uniformView = floatView.subarray(uniformBase, uniformBase + STRIDE);

                // 1-5 shadow-pass implicit routing:
                // true-BLEND transparent objects are skipped per contract 7,
                // and everything else uses the depth-only pipeline
                if (_passDepthOnly) {
                    if (modeKey === 'transparent') continue;
                    const sBuffer = _acquireBuffer('uniform', BYTES_PER_UNIFORM, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
                    _device.queue.writeBuffer(sBuffer, 0, uniformView);
                    const sBindGroup = _createShadowBindGroup(sBuffer, boneBuffer, mesh.morphMetaBuffer, mesh.morphDataBuffer);
                    if (activePipeline !== _shadowPipeline) {
                        _passEncoder.setPipeline(_shadowPipeline);
                        activePipeline = _shadowPipeline;
                    }
                    _passEncoder.setVertexBuffer(0, _identityInstanceBuffer);
                    _setSceneVertexBuffer(1, mesh.vBuffer);
                    _passEncoder.setIndexBuffer(mesh.iBuffer, mesh.indexFormat || 'uint16');
                    _passEncoder.setBindGroup(0, sBindGroup);
                    _passEncoder.drawIndexed(mesh.indexCount);
                    continue;
                }

                // Phase 4 OutlineMask pass implicit routing:
                // true-BLEND transparent objects are skipped
                // for the same root reason as shadow contract 7,
                // and everything else uses the mask pipeline.
                // The bind group shares the same source as the Scene pass,
                // and the outline color is carried in the uniform hdrParams slot.
                if (_passOutlineMask) {
                    if (modeKey === 'transparent') continue;
                    const mTexView = _textureViews[mesh.textureName] || whiteView;
                    const mNormalView = _textureViews[mesh.normalTextureName] || whiteView || mTexView;
                    const mMrView = _textureViews[mesh.mrTextureName] || whiteView || mTexView;
                    const mAoView = _textureViews[mesh.aoTextureName] || whiteView || mTexView;
                    const mEmissiveView = _textureViews[mesh.emissiveTextureName] || whiteView || mTexView;
                    const mBuffer = _acquireBuffer('uniform', BYTES_PER_UNIFORM, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
                    _device.queue.writeBuffer(mBuffer, 0, uniformView);
                    const mBindGroup = _createMeshBindGroup(
                        _mesh3DPipeline.bindGroupLayout,
                        mBuffer,
                        mTexView,
                        mNormalView,
                        mMrView,
                        mAoView,
                        mEmissiveView,
                        boneBuffer,
                        mesh.morphMetaBuffer,
                        mesh.morphDataBuffer,
                        prevBoneBuffer);
                    const maskPipeline = mesh.doubleSided ? _maskPipelineDoubleSided : _maskPipeline;
                    if (activePipeline !== maskPipeline) {
                        _passEncoder.setPipeline(maskPipeline);
                        activePipeline = maskPipeline;
                    }
                    _passEncoder.setVertexBuffer(0, _identityInstanceBuffer);
                    _setSceneVertexBuffer(1, mesh.vBuffer);
                    _passEncoder.setIndexBuffer(mesh.iBuffer, mesh.indexFormat || 'uint16');
                    _passEncoder.setBindGroup(0, mBindGroup);
                    _passEncoder.drawIndexed(mesh.indexCount);
                    continue;
                }

                const texView = _textureViews[mesh.textureName] || whiteView;
                const normalView = _textureViews[mesh.normalTextureName] || whiteView || texView;
                const mrView = _textureViews[mesh.mrTextureName] || whiteView || texView;
                const aoView = _textureViews[mesh.aoTextureName] || whiteView || texView;
                const emissiveView = _textureViews[mesh.emissiveTextureName] || whiteView || texView;

                const uBuffer = _acquireBuffer('uniform', BYTES_PER_UNIFORM, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
                _device.queue.writeBuffer(uBuffer, 0, uniformView);

                const bindGroup = _createMeshBindGroup(
                    _mesh3DPipeline.bindGroupLayout,
                    uBuffer,
                    texView,
                    normalView,
                    mrView,
                    aoView,
                    emissiveView,
                    boneBuffer,
                    mesh.morphMetaBuffer,
                    mesh.morphDataBuffer,
                    prevBoneBuffer);

                _submitSceneDraw(modeKey, mesh.doubleSided, bindGroup, _identityInstanceBuffer, mesh.vBuffer, mesh.iBuffer,
                    mesh.indexFormat || 'uint16', mesh.indexCount, 1, 0,
                    _isSceneCollecting() ? _sceneDrawDepth(uniformView, mesh.boundsCenter) : 0);
            }
        }
    }

    // prevInstanceBytes (2-3 Step C):
    // previous-frame byte stream with the same layout as instanceBytes
    // (20 floats per instance = 4 world rows + 1 morph-weight vec4).
    // Produced by double-buffering on the C# side.
    // Null means no history,
    // and VS falls back according to hasPrev* bits.
    // firstInstance (Phase 4):
    // dedicated to per-instance Outline mask rendering -
    // full instance stream + count=1 + firstInstance=writeIndex.
    // Bones (instanceIndex*100) / morph / instance streams then line up naturally
    // through instance_index addressing,
    // mirroring Vulkan firstInstance.
    function drawInstancedMesh3D(cacheKey, uniformData, instanceBytes, instanceCount, skinKey, prevInstanceBytes = null, firstInstance = 0) {
        if (!_frameStarted || (!_passEncoder && !_isSceneCollecting()) || !instanceCount || instanceCount <= 0) return;

        const boneBuffer = skinKey ? (_skinnedBoneBuffers[skinKey] || _defaultBoneBuffer) : _defaultBoneBuffer;
        const prevBoneBuffer = skinKey ? (_prevSkinnedBoneBuffers[skinKey] || boneBuffer) : _defaultBoneBuffer;

        _instancedDiag.drawCalls++;
        _instancedDiag.lastCacheKey = cacheKey || '';
        _instancedDiag.lastInstanceCount = instanceCount || 0;
        _instancedDiag.lastInstanceBytes = instanceBytes?.byteLength || instanceBytes?.length || 0;

        const mesh = _staticMeshes[cacheKey];
        if (!mesh) {
            _instancedDiag.lastError = `missing-static-mesh:${cacheKey}`;
            return;
        }

        if (!ArrayBuffer.isView(uniformData)) uniformData = new Float32Array(uniformData);
        if (instanceBytes instanceof Uint8Array) {
            instanceBytes = new Float32Array(instanceBytes.buffer, instanceBytes.byteOffset, instanceBytes.byteLength / 4);
        } else if (!ArrayBuffer.isView(instanceBytes)) {
            instanceBytes = new Float32Array(instanceBytes);
        }

        const whiteView = _textureViews['White'];
        const texView = _textureViews[mesh.textureName] || whiteView;
        const normalView = _textureViews[mesh.normalTextureName] || whiteView || texView;
        const mrView = _textureViews[mesh.mrTextureName] || whiteView || texView;
        const aoView = _textureViews[mesh.aoTextureName] || whiteView || texView;
        const emissiveView = _textureViews[mesh.emissiveTextureName] || whiteView || texView;

        const uBuffer = _acquireBuffer('uniform', uniformData.byteLength, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(uBuffer, 0, uniformData);

        const instanceBuffer = _acquireBuffer('vertex', instanceBytes.byteLength, GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST);
        _device.queue.writeBuffer(instanceBuffer, 0, instanceBytes);

        // 2-3 Step C: prev-instance bytes use the storage pool and bind at binding 14.
        // One buffer cannot be both VERTEX and STORAGE,
        // so the vertex-pool slot above cannot be reused.
        // If the byte length differs from the current frame,
        // treat it as having no history and fall back to the sentinel path with cleared hasPrev bits.
        let prevInstanceBuffer = null;
        if (prevInstanceBytes && !_passDepthOnly && !_passOutlineMask) {
            let prevView = prevInstanceBytes;
            if (prevView instanceof Uint8Array) {
                prevView = new Float32Array(prevView.buffer, prevView.byteOffset, prevView.byteLength / 4);
            } else if (!ArrayBuffer.isView(prevView)) {
                prevView = new Float32Array(prevView);
            }
            if (prevView.byteLength === instanceBytes.byteLength) {
                prevInstanceBuffer = _acquireBuffer('storage', prevView.byteLength, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
                _device.queue.writeBuffer(prevInstanceBuffer, 0, prevView);
            }
        }

        // 1-5 shadow-pass implicit routing:
        // transparent objects are already skipped by C# dispatch,
        // and this is a second defensive check at the uniform level
        if (_passDepthOnly) {
            if (_isTransparentMode(uniformData)) return;
            const sBindGroup = _createShadowBindGroup(uBuffer, boneBuffer, mesh.morphMetaBuffer, mesh.morphDataBuffer);
            _passEncoder.setPipeline(_shadowPipeline);
            _passEncoder.setVertexBuffer(0, instanceBuffer);
            _setSceneVertexBuffer(1, mesh.vBuffer);
            _passEncoder.setIndexBuffer(mesh.iBuffer, mesh.indexFormat || 'uint16');
            _passEncoder.setBindGroup(0, sBindGroup);
            _passEncoder.drawIndexed(mesh.indexCount, instanceCount);
            return;
        }

        // Phase 4 OutlineMask pass implicit routing
        // mirroring the shadow branch:
        // use the mask pipeline + main-table bind group.
        // The instance stream still binds at slot 0
        // whether it is the host's full stream or a C#-filtered per-instance subset.
        // Transparent handling follows the same defensive rule as shadow.
        if (_passOutlineMask) {
            if (_isTransparentMode(uniformData)) return;
            const mBindGroup = _createMeshBindGroup(
                _mesh3DPipeline.bindGroupLayout,
                uBuffer,
                texView,
                normalView,
                mrView,
                aoView,
                emissiveView,
                boneBuffer,
                mesh.morphMetaBuffer,
                mesh.morphDataBuffer,
                prevBoneBuffer);
            _passEncoder.setPipeline(mesh.doubleSided ? _maskPipelineDoubleSided : _maskPipeline);
            _passEncoder.setVertexBuffer(0, instanceBuffer);
            _setSceneVertexBuffer(1, mesh.vBuffer);
            _passEncoder.setIndexBuffer(mesh.iBuffer, mesh.indexFormat || 'uint16');
            _passEncoder.setBindGroup(0, mBindGroup);
            _passEncoder.drawIndexed(mesh.indexCount, instanceCount, 0, 0, firstInstance);
            return;
        }

        const bindGroup = _createMeshBindGroup(
            _mesh3DPipeline.bindGroupLayout,
            uBuffer,
            texView,
            normalView,
            mrView,
            aoView,
            emissiveView,
            boneBuffer,
            mesh.morphMetaBuffer,
            mesh.morphDataBuffer,
            prevBoneBuffer,
            prevInstanceBuffer);

        const modeKey = _isTransparentMode(uniformData) ? 'transparent' : _selectPipelineMode(uniformData[94], _getUniformI32View(uniformData)[98], _getUniformI32View(uniformData)[99]);
        _instancedDiag.lastModeKey = modeKey;
        try {
            if (_isSceneCollecting()) {
                // Fade 与 Blend 均按实例深度参加全局排序；完整实例流保留骨骼/morph 索引。
                const splitInstances = !modeKey.startsWith('opaque');
                for (let i = 0; i < (splitInstances ? instanceCount : 1); i++) {
                    const index = firstInstance + i;
                    _submitSceneDraw(modeKey, mesh.doubleSided, bindGroup, instanceBuffer, mesh.vBuffer, mesh.iBuffer,
                        mesh.indexFormat || 'uint16', mesh.indexCount, splitInstances ? 1 : instanceCount, index,
                        _sceneDrawDepth(uniformData, mesh.boundsCenter, instanceBytes, index));
                }
            } else {
                // Per-slot drawing
                // for skinned shells / per-instance outline:
                // firstInstance offsets instance_index,
                // and the bone palette automatically stays aligned through instanceIndex*100 addressing.
                // Regular calls pass the default 0 and behavior stays unchanged.
                _submitSceneDraw(modeKey, mesh.doubleSided, bindGroup, instanceBuffer, mesh.vBuffer, mesh.iBuffer,
                    mesh.indexFormat || 'uint16', mesh.indexCount, instanceCount, firstInstance);
            }
        } catch (error) {
            _instancedDiag.lastError = error?.message || `${error}`;
            throw error;
        }
    }

    // 1-6 common compute infrastructure
    // kernel registration model:
    // this file contains no shader source,
    // and all WGSL / binding layouts / resource references are supplied from C#
    // via interop
    // see the shared Compute.cs contract.
    // Synchronization is naturally guaranteed by queue ordering
    // on this zero-barrier platform.
    // Dispatch is restricted to outside render passes
    // _passEncoder being non-null is a contract violation
    // and reports to console without throwing, aligned with rule 3.
    // Compile / validation errors also arrive asynchronously in the console,
    // following WebGPU validation behavior and the same rule 3.
    // Binding type numbering matches C# ComputeBindingType:
    // 0=Params 1=SampledTexture 2=DepthTexture (2-2)
    // 3=StorageTextureWrite 4=StorageBufferRead 5=StorageBufferReadWrite
    // 6=SampledTexture3D 7=StorageTexture3DWrite (1-8).
    // @binding(i) uses declaration order.
    // When SampledTexture / SampledTexture3D is present,
    // a linear-clamp sampler is automatically appended at @binding(15)
    // whose addressModeW defaults to clamp-to-edge,
    // so 3D sampling naturally gets trilinear filtering + end-face clamping.
    // Params and bind groups are cached by (kernel, dispatch index within the frame) slot:
    // repeated dispatches of the same kernel in one frame each get an independent uniform buffer,
    // avoiding parameter overwrites caused by writeBuffer executing before the merged submit.
    // This is zero-allocation in steady state.

    // group(1) 与共享光照 UBO 分离。启停参数创建后不再写入，避免同帧覆盖。
    let _fogSampling = null, _fogSamplingFrame = null, _weatherSamplingFrame = null;
    const _fogMaterialPipelines = new WeakSet();

    function _initializeFogSampling() {
        if (_fogSampling) return;
        const visibility = GPUShaderStage.FRAGMENT;
        const layout = _device.createBindGroupLayout({ label: 'fog_material_layout', entries: [
            { binding: 0, visibility, buffer: { type: 'read-only-storage', minBindingSize: _fogFrameBytes } },
            { binding: 1, visibility, texture: { sampleType: 'float', viewDimension: '3d' } },
            { binding: 2, visibility, texture: { sampleType: 'float', viewDimension: '3d' } },
            { binding: 3, visibility, buffer: { type: 'uniform', minBindingSize: 16 } },
        ] });
        const buffers = [];
        try {
            const frame = _device.createBuffer({ label: 'fog_disabled_frame', size: _fogFrameBytes,
                usage: GPUBufferUsage.STORAGE });
            buffers.push(frame);
            const params = [0, 1, 2, 3].map(value => {
                const buffer = _device.createBuffer({ label: `fog_sampling_${value}`, size: 16,
                    usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
                buffers.push(buffer);
                _device.queue.writeBuffer(buffer, 0, new Float32Array([value & 1, value >> 1, 0, 0]));
                return buffer;
            });
            // 关闭分支不会查询介质；复用已有 AP 哨兵，不分配额外雾体积。
            const disabled = _device.createBindGroup({ layout, entries: [
                { binding: 0, resource: { buffer: frame, size: _fogFrameBytes } },
                { binding: 1, resource: _defaultAerialLutView },
                { binding: 2, resource: _defaultAerialLutView },
                { binding: 3, resource: { buffer: params[0], size: 16 } },
            ] });
            const weatherOnly = _device.createBindGroup({ layout, entries: [
                { binding: 0, resource: { buffer: frame, size: _fogFrameBytes } },
                { binding: 1, resource: _defaultAerialLutView },
                { binding: 2, resource: _defaultAerialLutView },
                { binding: 3, resource: { buffer: params[2], size: 16 } },
            ] });
            _fogSampling = { layout, disabled, weatherOnly, enabledParams: params[1], combinedParams: params[3], buffers };
        } catch (error) {
            for (const buffer of buffers) _queueComputeRetirement(buffer);
            throw error;
        }
    }

    function _fogMaterialGroup() {
        if (!_frameStarted || !_passEncoder || _instancedDiag.deviceLost || _activePassId !== 8) return _fogSampling.disabled;
        const frame = _fogSamplingFrame, rain = _weatherSamplingFrame;
        const fog = _sceneFogAllowed && frame && frame.command === _commandEncoder && frame.frameId === _computeFrameId
            && _activeColorTarget === frame.refs[13] && _activeColorView === frame.refs[12]
            && _activeDepthView === frame.refs[10] && _sameFogResources(frame.refs, _resolveFogComposite(frame.input));
        const weather = rain && rain.command === _commandEncoder && rain.frameId === _computeFrameId
            && _activeDepthView === rain.refs[10] && _sameFogResources(rain.refs, _resolveFogComposite(rain.input))
            && (fog || (_activeColorTarget === rain.refs[13] && _activeColorView === rain.refs[12]));
        if (fog) return weather ? frame.weatherSamplingGroup : frame.samplingGroup;
        return weather ? _fogSampling.weatherOnly : _fogSampling.disabled;
    }

    function _setMeshPipeline(value) {
        _passEncoder.setPipeline(value);
        if (_fogMaterialPipelines.has(value)) _passEncoder.setBindGroup(1, _fogMaterialGroup());
    }

    // 独立雾合成：生产 WGSL 由 C# 提供；异步编译完成前 Prepare 明确回退。
    const _fogComposites = Object.create(null);
    const _fogFrameBytes = 448 * 16;
    const _weatherFrameBytes = 160 * 16;
    const _fogAoFormats = new Set(['r8unorm', 'r16float', 'rg16float', 'rgba16float',
        'rgba8unorm', 'bgra8unorm']);

    function createFogCompositePipeline(name, vertex, fragment, weather = false) {
        if (!_device || _instancedDiag.deviceLost || !name) return false;
        if (_fogComposites[name]) return !_fogComposites[name].failed;
        const device = _device;
        const state = { device, weather, frameBytes: weather ? _weatherFrameBytes : _fogFrameBytes,
            pipeline: null, slots: [], cursor: 0, frameId: -1, prepared: null, failed: false };
        let scopeOpen = false;
        try {
            device.pushErrorScope('validation'); scopeOpen = true;
            const visibility = GPUShaderStage.FRAGMENT;
            state.layout = device.createBindGroupLayout({ label: `${name}_layout`, entries: [
                { binding: 0, visibility, buffer: { type: 'read-only-storage', minBindingSize: state.frameBytes } },
                { binding: 1, visibility, texture: { sampleType: 'float', viewDimension: weather ? '2d' : '3d' } },
                { binding: 2, visibility, texture: { sampleType: 'float', viewDimension: weather ? '2d' : '3d' } },
                { binding: 3, visibility, texture: { sampleType: 'float', viewDimension: '2d' } },
                { binding: 4, visibility, texture: { sampleType: 'depth', viewDimension: '2d' } },
                { binding: 5, visibility, texture: { sampleType: 'float', viewDimension: '2d' } },
                { binding: 6, visibility, buffer: { type: 'uniform', minBindingSize: 16 } },
                { binding: 7, visibility, sampler: { type: 'filtering' } },
            ] });
            state.sampler = device.createSampler({ minFilter: 'linear', magFilter: 'linear',
                addressModeU: 'clamp-to-edge', addressModeV: 'clamp-to-edge', addressModeW: 'clamp-to-edge' });
            const pending = device.createRenderPipelineAsync({ label: name,
                layout: device.createPipelineLayout({ bindGroupLayouts: [state.layout] }),
                vertex: { module: device.createShaderModule({ label: `${name}_vs`, code: vertex }), entryPoint: 'VSMain' },
                fragment: { module: device.createShaderModule({ label: `${name}_ps`, code: fragment }),
                    entryPoint: 'PSMain', targets: [{ format: 'rgba16float' }] },
                primitive: { topology: 'triangle-list', cullMode: 'none' }, multisample: { count: 1 },
            });
            const validation = device.popErrorScope(); scopeOpen = false;
            _fogComposites[name] = state;
            Promise.all([pending, validation]).then(([pipeline, error]) => {
                if (error) throw new Error(error.message);
                if (_fogComposites[name] === state && _device === device && !_instancedDiag.deviceLost)
                    state.pipeline = pipeline;
            }).catch(error => {
                state.failed = true; state.prepared = null;
                console.error(`雾合成 '${name}' 编译失败：${error}`);
            });
            return true;
        } catch (error) {
            if (scopeOpen) device.popErrorScope().catch(() => {});
            console.error(`雾合成 '${name}' 创建失败：${error}`);
            return false;
        }
    }

    function _fogTexture(texture, dimension, usage) {
        return !!texture && texture.dimension === dimension && texture.sampleCount === 1
            && texture.width > 0 && texture.height > 0 && texture.depthOrArrayLayers > 0
            && (texture.usage & usage) === usage;
    }

    function _resolveFogComposite(input) {
        const color = _renderTargets[input.color], depth = _renderTargets[input.depth];
        const destination = _renderTargets[input.destination], frame = _storageBuffers[input.frame];
        if (!color || !depth || !destination || color.depthOnly || !depth.depthOnly || destination.depthOnly
            || !destination.noImplicitDepth || destination.depthTex || destination.depthView
            || !frame || frame.size < (input.weather ? _weatherFrameBytes : _fogFrameBytes) || !(frame.usage & GPUBufferUsage.STORAGE)
            || !Number.isFinite(input.aoIntensity) || input.aoIntensity < 0) return null;
        // 输入绝不在此重建；过期尺寸代表本帧数据不完整，必须回退。
        for (const rt of [color, depth, destination])
            if (rt.matchBackbuffer && (!_canvas || rt.width !== _canvas.width || rt.height !== _canvas.height)) return null;
        const c = color.colorTex, d = depth.depthTex, out = destination.colorTex;
        const sampled = GPUTextureUsage.TEXTURE_BINDING;
        if (!_fogTexture(c, '2d', sampled) || c.format !== 'rgba16float'
            || !_fogTexture(d, '2d', sampled) || !['depth24plus', 'depth32float'].includes(d.format)
            || !_fogTexture(out, '2d', GPUTextureUsage.RENDER_ATTACHMENT) || out.format !== 'rgba16float'
            || c.depthOrArrayLayers !== 1 || d.depthOrArrayLayers !== 1 || out.depthOrArrayLayers !== 1
            || c === out || c.width !== d.width || c.height !== d.height
            || c.width !== out.width || c.height !== out.height
            || c.width !== color.width || c.height !== color.height
            || d.width !== depth.width || d.height !== depth.height
            || out.width !== destination.width || out.height !== destination.height) return null;
        const integrated = input.weather ? c : _textures3d[input.integrated], local = input.weather ? c : _textures3d[input.local];
        if (!input.weather && (!_fogTexture(integrated, '3d', sampled) || !_fogTexture(local, '3d', sampled)
            || integrated.format !== 'rgba16float' || local.format !== 'rgba16float'
            || integrated.width !== local.width || integrated.height !== local.height
            || integrated.depthOrArrayLayers !== local.depthOrArrayLayers + 1)) return null;
        let ao = c, aoView = color.colorView;
        if (input.applyAo) {
            if (input.aoTarget) {
                const rt = _renderTargets[input.aoTarget];
                if (!rt || rt.depthOnly || (rt.matchBackbuffer
                    && (!_canvas || rt.width !== _canvas.width || rt.height !== _canvas.height))) return null;
                ao = rt.colorTex; aoView = rt.colorView;
            } else {
                ao = _textures[input.aoTexture]; aoView = _textureViews[input.aoTexture];
            }
            if (!_fogTexture(ao, '2d', sampled) || ao.depthOrArrayLayers !== 1
                || !_fogAoFormats.has(ao.format) || ao === out) return null;
        }
        const refs = [frame, integrated, local, c, d, ao, out,
            input.weather ? color.colorView : _textureViews3d[input.integrated],
            input.weather ? color.colorView : _textureViews3d[input.local],
            color.colorView, depth.depthView, aoView, destination.colorView, destination];
        return refs.every(Boolean) ? refs : null;
    }

    function _sameFogResources(a, b) {
        return !!a && !!b && a.length === b.length && a.every((value, index) => value === b[index]);
    }

    function prepareFogComposite(name, color, depth, frame, integrated, local, aoTexture, aoTarget,
        aoIntensity, applyAo, destination) {
        const state = _fogComposites[name];
        if (!state) return false;
        if (state.weather) _weatherSamplingFrame = null; else _fogSamplingFrame = null;
        state.prepared = null;
        if (!_frameStarted || !_commandEncoder || _passEncoder || _instancedDiag.deviceLost
            || state.device !== _device || !state.pipeline || state.failed) return false;
        const input = { color, depth, frame, integrated, local, aoTexture, aoTarget, aoIntensity, applyAo, destination, weather: state.weather };
        // 输出尚未用作本帧附件，可以在 Pass 外按当前画布重建。
        const target = _renderTargets[destination];
        try {
            if (target && target.matchBackbuffer && (target.width !== _canvas.width || target.height !== _canvas.height))
                _resizeRenderTargetResources(target, _canvas.width, _canvas.height);
            const refs = _resolveFogComposite(input);
            if (!refs) return false;
            if (state.frameId !== _computeFrameId) { state.frameId = _computeFrameId; state.cursor = 0; }
            const index = state.cursor++;
            let slot = state.slots[index];
            if (!slot) {
                slot = { buffer: _device.createBuffer({ label: `${name}_params_${index}`, size: 16,
                    usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST }), params: new Float32Array(4) };
                state.slots[index] = slot;
            }
            // 同一提交中的不同合成拥有独立槽；下一帧复用由 queue 顺序保证安全。
            slot.params[0] = aoIntensity; slot.params[1] = applyAo ? 1 : 0;
            _device.queue.writeBuffer(slot.buffer, 0, slot.params);
            if (!_sameFogResources(slot.refs, refs)) {
                slot.group = _device.createBindGroup({ layout: state.layout, entries: [
                    { binding: 0, resource: { buffer: refs[0], size: state.frameBytes } },
                    { binding: 1, resource: refs[7] }, { binding: 2, resource: refs[8] },
                    { binding: 3, resource: refs[9] }, { binding: 4, resource: refs[10] },
                    { binding: 5, resource: refs[11] }, { binding: 6, resource: { buffer: slot.buffer, size: 16 } },
                    { binding: 7, resource: state.sampler },
                ] });
                const sampleGroup = params => _device.createBindGroup({ layout: _fogSampling.layout, entries: [
                    { binding: 0, resource: { buffer: refs[0], size: _fogFrameBytes } },
                    { binding: 1, resource: refs[7] }, { binding: 2, resource: refs[8] },
                    { binding: 3, resource: { buffer: params, size: 16 } },
                ] });
                slot.samplingGroup = _fogSampling && !state.weather ? sampleGroup(_fogSampling.enabledParams) : null;
                slot.weatherSamplingGroup = _fogSampling && !state.weather ? sampleGroup(_fogSampling.combinedParams) : null;
                slot.refs = refs;
            }
            state.prepared = { input, refs, group: slot.group, samplingGroup: slot.samplingGroup,
                weatherSamplingGroup: slot.weatherSamplingGroup, owner: state, command: _commandEncoder, frameId: _computeFrameId };
            return true;
        } catch (error) {
            console.error(`雾合成 '${name}' 准备失败：${error}`);
            return false;
        }
    }

    function drawFogComposite(name) {
        const state = _fogComposites[name], prepared = state?.prepared;
        if (!prepared) return 0;
        state.prepared = null;
        if (!_frameStarted || !_passEncoder || _instancedDiag.deviceLost || state.device !== _device
            || prepared.command !== _commandEncoder || prepared.frameId !== _computeFrameId
            || _activePassId !== (state.weather ? 9 : 7) || _passDepthOnly || _passVelocity
            || _activeColorTarget !== prepared.refs[13] || _activeColorView !== prepared.refs[12]
            || !_sameFogResources(prepared.refs, _resolveFogComposite(prepared.input))) return 0;
        _passEncoder.setPipeline(state.pipeline);
        _passEncoder.setBindGroup(0, prepared.group);
        _passEncoder.draw(3);
        if (state.weather) _weatherSamplingFrame = prepared;
        else _fogSamplingFrame = prepared.samplingGroup ? prepared : null;
        return prepared.input.applyAo ? 3 : 1;
    }

    function disposeFogCompositePipeline(name) {
        const state = _fogComposites[name];
        if (!state) return;
        delete _fogComposites[name]; state.prepared = null;
        if (_fogSamplingFrame?.owner === state) _fogSamplingFrame = null;
        if (_weatherSamplingFrame?.owner === state) _weatherSamplingFrame = null;
        for (const slot of state.slots) _queueComputeRetirement(slot.buffer);
        state.slots.length = 0;
    }

    // A0.1 粒子间接绘制：WGSL 全部由 C# 下发，JS 只负责 PSO、绑定与那一条 drawIndexedIndirect。
    // PSO 按本帧真实附件形状异步烘焙；未就绪时 Prepare 明确回退，和雾合成同一回退语义。
    const _particleDraws = Object.create(null);
    // 必须与 ParticleDraw.FrameBytes 一致：绑定组声明的 size 若小于着色器读到的下标，
    // WebGPU 校验器会直接判非法，而报错点只出现在首次 draw 那一帧。
    const _particleFrameBytes = 192;
    const _particleInstanceStride = 32;
    const _particleArgsBytes = 20;
    let _particleQuadIndex = null;

    // 四个角按 0,1,2,1,3,2 展开成两个三角形，与其它三端共用的单位四边形索引一致；
    // reset kernel 写进 indexCount 的 6 就是这里的长度。粒子关闭剔除，绕序只决定顶点序。
    // 索引内容与任何效果实例无关，所以按设备共享一只，随设备一起消亡。
    function _particleQuadIndexBuffer() {
        if (_particleQuadIndex && _particleQuadIndex.device === _device) return _particleQuadIndex.buffer;
        const data = new Uint16Array([0, 1, 2, 1, 3, 2]);
        const buffer = _device.createBuffer({ label: 'particle_quad_index', size: data.byteLength,
            usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST });
        _device.queue.writeBuffer(buffer, 0, data);
        _particleQuadIndex = { device: _device, buffer };
        return buffer;
    }

    function createParticleDrawPipeline(name, vertex, fragment, fragmentVelocity) {
        if (!_device || _instancedDiag.deviceLost || !name) return false;
        if (_particleDraws[name]) return !_particleDraws[name].failed;
        const device = _device;
        const state = { device, variants: Object.create(null), prepared: null, failed: false,
            refs: null, group: null };
        try {
            // 实例与帧数据都是顶点阶段只读。可写 storage 在顶点阶段不可见，而绘制侧也不需要写回：
            // 计数由 compute 写进间接参数，顶点阶段只按 instance_index 取第 slot 条记录。
            const visibility = GPUShaderStage.VERTEX;
            state.layout = device.createBindGroupLayout({ label: `${name}_layout`, entries: [
                { binding: 0, visibility, buffer: { type: 'read-only-storage', minBindingSize: _particleInstanceStride } },
                { binding: 1, visibility, buffer: { type: 'read-only-storage', minBindingSize: _particleFrameBytes } },
            ] });
            state.pipelineLayout = device.createPipelineLayout({ bindGroupLayouts: [state.layout] });
            state.vertex = device.createShaderModule({ label: `${name}_vs`, code: vertex });
            state.fragment = device.createShaderModule({ label: `${name}_ps`, code: fragment });
            state.fragmentVelocity = device.createShaderModule({ label: `${name}_ps_mrt`, code: fragmentVelocity });
            _particleDraws[name] = state;
            return true;
        } catch (error) {
            console.error(`粒子管线 '${name}' 创建失败：${error}`);
            return false;
        }
    }

    // 颜色与深度格式由本帧真实附件决定，但速度槽的有无只有进了 Pass 才知道（_passVelocity）。
    // 所以同一附件形状下两个变体一起烘焙，Draw 时按标志取用，绝不在 Pass 内创建管线。
    function _particleVariant(state, colorFormat, depthFormat) {
        const key = `${colorFormat}|${depthFormat}`;
        let variant = state.variants[key];
        if (variant) return variant;
        variant = { color: null, velocity: null };
        state.variants[key] = variant;
        const device = state.device;
        // 预乘 alpha：片元已经把 rgb 乘进 alpha，所以源因子是 one，而不是主管线透明档的 src-alpha。
        const blend = {
            color: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' },
            alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' },
        };
        const common = {
            layout: state.pipelineLayout,
            // 没有顶点缓冲也没有顶点属性：角点来自 vertex_index，每实例数据来自 storage buffer。
            vertex: { module: state.vertex, entryPoint: 'VSMain' },
            primitive: { topology: 'triangle-list', cullMode: 'none' },
            // 粒子是最后一层透明：测试但不写深度，与主管线透明档的 less-equal 一致。
            depthStencil: { depthWriteEnabled: false, depthCompare: 'less-equal', format: depthFormat },
            multisample: { count: 1 },
        };
        let scopeOpen = false;
        try {
            device.pushErrorScope('validation'); scopeOpen = true;
            const pendingColor = device.createRenderPipelineAsync({ ...common, label: `particle_${key}`,
                fragment: { module: state.fragment, entryPoint: 'PSMain',
                    targets: [{ format: colorFormat, blend }] } });
            // 速度槽 writeMask 置零，和主管线让透明体不污染速度的做法一致（2-3 条款 7）。
            // A0.2：法线槽同理；它是否存在由进程级 _normalOutput 决定，所以仍只烘这两个变体。
            // writeMask 为零的槽不要求片元有对应输出，所以沿用速度版片元模块。
            const velocityTargets = [{ format: colorFormat, blend }, { format: 'rg16float', writeMask: 0 }];
            if (_normalOutput) velocityTargets.push({ format: 'rgba16float', writeMask: 0 });
            const pendingVelocity = device.createRenderPipelineAsync({ ...common, label: `particle_${key}_mrt`,
                fragment: { module: state.fragmentVelocity, entryPoint: 'PSMain',
                    targets: velocityTargets } });
            const validation = device.popErrorScope(); scopeOpen = false;
            Promise.all([pendingColor, pendingVelocity, validation]).then(([color, velocity, error]) => {
                if (error) throw new Error(error.message);
                if (state.variants[key] !== variant || _device !== device || _instancedDiag.deviceLost) return;
                variant.color = color; variant.velocity = velocity;
            }).catch(error => {
                state.failed = true; state.prepared = null;
                console.error(`粒子管线变体 '${key}' 编译失败：${error}`);
            });
        } catch (error) {
            if (scopeOpen) device.popErrorScope().catch(() => {});
            state.failed = true;
            console.error(`粒子管线变体 '${key}' 创建失败：${error}`);
        }
        return variant;
    }

    function _resolveParticleDraw(input) {
        const color = _renderTargets[input.color];
        const depth = input.depth ? _renderTargets[input.depth] : null;
        const instances = _storageBuffers[input.instances];
        const args = _storageBuffers[input.args];
        const frame = _storageBuffers[input.frame];
        if (!color || color.depthOnly || !instances || !args || !frame) return null;
        // depth 为空代表场景 Pass 跑在默认深度缓冲上，这是环境光遮蔽档之外的常态，不是失败。
        if (input.depth && (!depth || !depth.depthOnly)) return null;
        if (instances.size < _particleInstanceStride || frame.size < _particleFrameBytes
            || args.size < _particleArgsBytes || !(args.usage & GPUBufferUsage.INDIRECT)
            || !(instances.usage & GPUBufferUsage.STORAGE) || !(frame.usage & GPUBufferUsage.STORAGE)
            || instances === args || instances === frame || args === frame) return null;
        // 过期尺寸代表本帧附件不完整，只能回退而不能在此重建：颜色与深度已经是本帧 Pass 的附件。
        for (const rt of depth ? [color, depth] : [color])
            if (rt.matchBackbuffer && (!_canvas || rt.width !== _canvas.width || rt.height !== _canvas.height)) return null;
        const colorFormat = color.colorFormat || _sceneFormat;
        const depthFormat = depth ? depth.depthFormat : 'depth24plus';
        if (!colorFormat || !depthFormat || !color.colorView) return null;
        return { color, depth, instances, args, frame, colorFormat, depthFormat,
            colorView: color.colorView, depthView: depth ? depth.depthView : null };
    }

    function _sameParticleRefs(a, b) {
        return !!a && !!b && a.color === b.color && a.depth === b.depth
            && a.instances === b.instances && a.args === b.args && a.frame === b.frame
            && a.colorView === b.colorView && a.depthView === b.depthView
            && a.colorFormat === b.colorFormat && a.depthFormat === b.depthFormat;
    }

    function prepareParticleDraw(name, color, depth, instances, args, frame) {
        const state = _particleDraws[name];
        if (!state) return false;
        state.prepared = null;
        if (!_frameStarted || !_commandEncoder || _passEncoder || _instancedDiag.deviceLost
            || state.device !== _device || state.failed) return false;
        const input = { color, depth: depth || null, instances, args, frame };
        try {
            const refs = _resolveParticleDraw(input);
            if (!refs) return false;
            const variant = _particleVariant(state, refs.colorFormat, refs.depthFormat);
            // 首次见到这组附件形状时管线仍在异步编译：本帧明确不绘制，下一帧再试。
            if (state.failed || !variant.color || !variant.velocity) return false;
            if (!_sameParticleRefs(state.refs, refs)) {
                state.group = _device.createBindGroup({ layout: state.layout, entries: [
                    { binding: 0, resource: { buffer: refs.instances } },
                    { binding: 1, resource: { buffer: refs.frame, size: _particleFrameBytes } },
                ] });
                state.refs = refs;
            }
            state.prepared = { input, refs, variant, group: state.group, args: refs.args,
                indices: _particleQuadIndexBuffer(), command: _commandEncoder, frameId: _computeFrameId };
            return true;
        } catch (error) {
            console.error(`粒子绘制 '${name}' 准备失败：${error}`);
            return false;
        }
    }

    function drawParticles(name) {
        const state = _particleDraws[name], prepared = state?.prepared;
        if (!prepared) return false;
        // 一次 Prepare 只兑付一次提交，与其它三端同一约定。
        state.prepared = null;
        if (!_frameStarted || !_passEncoder || _instancedDiag.deviceLost || state.device !== _device
            || prepared.command !== _commandEncoder || prepared.frameId !== _computeFrameId
            // Scene=1 与 SceneTransparent=8 是粒子唯一的落点；两者都必然挂载深度附件。
            || (_activePassId !== 1 && _activePassId !== 8)
            || _passDepthOnly || _passOutlineMask || _passOverlay
            // A0.2：速度变体是否带法线槽是按 _normalOutput 烘的，本 Pass 的附件集必须与之一致。
            || (_passVelocity && _passNormal !== _normalOutput)
            || _activeColorTarget !== prepared.refs.color || _activeColorView !== prepared.refs.colorView
            // depth 为空时无法比对身份：默认深度视图每个 Pass 现建，只能要求它确实存在。
            || !_activeDepthView || (prepared.refs.depthView && _activeDepthView !== prepared.refs.depthView)
            || !_sameParticleRefs(prepared.refs, _resolveParticleDraw(prepared.input))) return false;
        const pipeline = _passVelocity ? prepared.variant.velocity : prepared.variant.color;
        if (!pipeline) return false;
        _passEncoder.setPipeline(pipeline);
        _passEncoder.setBindGroup(0, prepared.group);
        _passEncoder.setIndexBuffer(prepared.indices, 'uint16');
        // 实例数由 GPU 在参数块里决定；CPU 全程不回读，这正是间接绘制的意义。
        _passEncoder.drawIndexedIndirect(prepared.args, 0);
        return true;
    }

    function disposeParticleDrawPipeline(name) {
        const state = _particleDraws[name];
        if (!state) return;
        delete _particleDraws[name];
        state.prepared = null; state.refs = null; state.group = null;
    }

    const _computeKernels = {};
    const _storageBuffers = {};
    let _computeFrameId = 0;

    // 1-8: dedicated registry for 3D textures.
    // It must never be merged into _textures / _textureViews.
    // Those registries are consumed by drawSprite2D and materials by name
    // and always carry 2D semantics.
    // Mixing 3D resources in would hand those paths an unsampleable dimension.
    // The 1-7 _textureCubes path already established the same precedent.
    const _textures3d = {}, _textureViews3d = {}, _textureMeta3d = {};

    // 1-8: map format intent to the concrete WebGPU format.
    // This is the single source of truth:
    // storageTexture.format in bind-group layout
    // and the texture formats used by createComputeTexture / createComputeTexture3D
    // must both come from this function,
    // or validation errors will occur.
    // formatKind matches C# ComputeStorageFormat:
    // 0=Rgba8Unorm 1=Rgba16Float 2=R16Float 3=R8Unorm 4=Rg16Float.
    //
    // Using r16float/r8unorm/rg16float as STORAGE_BINDING belongs to the optional
    // texture-formats-tier1 feature
    // which is already probed and enabled on demand in Step 0,
    // with results stored in _gpuFeatures.
    // But in WGSL, the FMT in texture_storage_*<FMT, write> is a literal written by the effect author
    // and cannot float with device capability.
    // If this function returned r16float under tier1 while the effect source declares rgba16float,
    // "layout format != shader format" would still be a validation error.
    // Therefore this function always returns core-guaranteed wide-channel formats:
    // rgba16float is the only core format that is both compute-writable and trilinear-filterable
    // in half precision,
    // and rgba8unorm serves the same fallback role for R8Unorm.
    // Effect code only uses the .x / .xy channels,
    // so numeric semantics remain fully aligned with the other three backends
    // see the WebGPU column in the C# ComputeStorageFormat summary.
    // If tier1 narrow formats are to be truly enabled later,
    // the FMT literals in effect source must be updated in lockstep.
    // _gpuFeatures is already prepared to drive that decision.
    function _mapStorageFormat(formatKind) {
        switch (formatKind >>> 0) {
            case 1: return 'rgba16float';  // Rgba16Float
            case 2: return 'rgba16float';  // R16Float   -> core fallback
            case 3: return 'rgba8unorm';   // R8Unorm    -> core fallback
            case 4: return 'rgba16float';  // Rg16Float  -> core fallback
            default: return 'rgba8unorm';  // Rgba8Unorm
        }
    }

    function createComputeKernel(name, wgslCode, entryPoint, bindingsJson) {
        if (!_device) return false;
        if (_computeKernels[name]) return true;
        try {
            const bindings = JSON.parse(bindingsJson);
            const layoutEntries = [];
            let paramsSize = 0, hasSampled = false;
            for (let i = 0; i < bindings.length; i++) {
                const b = bindings[i];
                switch (b.type) {
                    case 0:
                        paramsSize = b.size >>> 0;
                        layoutEntries.push({ binding: i, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } });
                        break;
                    case 1:
                        hasSampled = true;
                        // viewDimension is written explicitly
                        // even though the default is already '2d',
                        // to document the contrast against the 1-8 3D branch
                        layoutEntries.push({ binding: i, visibility: GPUShaderStage.COMPUTE, texture: { sampleType: 'float', viewDimension: '2d' } });
                        break;
                    case 2:
                        // 2-2: depth input
                        // WGSL texture_depth_2d, textureLoad with no sampler, contract clause 3
                        layoutEntries.push({ binding: i, visibility: GPUShaderStage.COMPUTE, texture: { sampleType: 'depth' } });
                        break;
                    case 3:
                        // 2-1: format is declared by the format field in bindingsJson.
                        // Since 1-8 it is uniformly mapped through _mapStorageFormat,
                        // sharing the same source of truth as createComputeTexture
                        // to prevent validation errors from layout format != texture format.
                        layoutEntries.push({ binding: i, visibility: GPUShaderStage.COMPUTE, storageTexture: { access: 'write-only', format: _mapStorageFormat(b.format), viewDimension: '2d' } });
                        break;
                    case 4:
                        layoutEntries.push({ binding: i, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage' } });
                        break;
                    case 5:
                        layoutEntries.push({ binding: i, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' } });
                        break;
                    case 6:
                        // 1-8: 3D sampled slot.
                        // Setting hasSampled appends the linear sampler at @binding(15),
                        // whose addressModeU/V/W are all clamp-to-edge,
                        // so WGSL textureSampleLevel naturally gets trilinear filtering
                        // plus end-face clamping.
                        hasSampled = true;
                        layoutEntries.push({ binding: i, visibility: GPUShaderStage.COMPUTE, texture: { sampleType: 'float', viewDimension: '3d' } });
                        break;
                    case 7:
                        // 1-8: 3D storage write slot.
                        // Its format must match the literal inside the effect WGSL source
                        // texture_storage_3d<FMT, write>,
                        // so it also uses the core-guaranteed format from _mapStorageFormat.
                        layoutEntries.push({ binding: i, visibility: GPUShaderStage.COMPUTE, storageTexture: { access: 'write-only', format: _mapStorageFormat(b.format), viewDimension: '3d' } });
                        break;
                    default:
                        console.error(`createComputeKernel '${name}': unknown binding type ${b.type}`);
                        return false;
                }
            }
            if (hasSampled)
                layoutEntries.push({ binding: 15, visibility: GPUShaderStage.COMPUTE, sampler: { type: 'filtering' } });

            const module = _device.createShaderModule({ label: `compute_${name}`, code: wgslCode });
            const bindGroupLayout = _device.createBindGroupLayout({ label: `compute_${name}_bgl`, entries: layoutEntries });
            const pipeline = _device.createComputePipeline({
                label: `compute_${name}`,
                layout: _device.createPipelineLayout({ bindGroupLayouts: [bindGroupLayout] }),
                compute: { module, entryPoint: entryPoint || 'CSMain' },
            });
            _computeKernels[name] = { pipeline, bindGroupLayout, bindings, paramsSize, hasSampled, slots: [], cursor: 0, frameId: -1 };
            return true;
        } catch (e) {
            console.error(`createComputeKernel '${name}' failed: ${e}`);
            return false;
        }
    }

    function disposeComputeKernel(name) {
        const k = _computeKernels[name];
        if (!k) return;
        for (const slot of k.slots) if (slot && slot.paramsBuffer) _queueComputeRetirement(slot.paramsBuffer);
        delete _computeKernels[name];
    }

    // Register storage textures
    // used both as write-only storage and sampled resources
    // into the _textures registry under the same name,
    // so existing consumer paths such as drawSprite2D can resolve them by name unchanged.
    // formatKind matches C# ComputeStorageFormat,
    // and the concrete format always comes from _mapStorageFormat
    // as the single source of truth.
    function createComputeTexture(name, width, height, formatKind) {
        const existing = _textures[name];
        if (existing) {
            const meta = _textureMeta[name] || {};
            if (meta.width === width && meta.height === height && existing.format === _mapStorageFormat(formatKind))
                return _getTextureResult(name, true);
            // On size changes, destroy the old GPUTexture and rebuild in place
            // while keeping the same name so consumer paths need no changes.
            // Compute bind-group caches holding the old textureView are now invalid
            // and are cleared entirely.
            // Resize is infrequent, so the cost is negligible.
            _queueComputeRetirement(existing);
            delete _textures[name]; delete _textureViews[name]; delete _textureMeta[name];
            _invalidateComputeBindGroups();
        }
        const texture = _device.createTexture({
            size: [width, height], format: _mapStorageFormat(formatKind),
            usage: GPUTextureUsage.STORAGE_BINDING | GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
        });
        return _storeTexture(name, texture, width, height);
    }

    // 1-8: 3D storage textures.
    // They do not enter _textures;
    // see the registry-isolation note at the _textures3d declaration.
    // The size limit follows the contract
    // from the CreateComputeTexture3D summary in Compute.cs:
    // any dimension > 256 requires device-capability checks.
    // This backend defaults maxTextureDimension3D to 2048
    // and stores the probed value in _gpuLimits during Step 0.
    // createView() on a dimension:'3d' texture naturally creates a '3d' view,
    // matching layout viewDimension:'3d' by default.
    function createComputeTexture3D(name, width, height, depth, formatKind) {
        if (!_device || _instancedDiag.deviceLost || !(width > 0 && height > 0 && depth > 0)
            || Math.max(width, height, depth) > _device.limits.maxTextureDimension3D) return false;
        const format = _mapStorageFormat(formatKind);
        const meta = _textureMeta3d[name];
        if (meta) {
            if (meta.width === width && meta.height === height && meta.depth === depth && meta.format === format)
                return true;
            // On size / format changes, rebuild in place.
            // Cached bind groups still hold destroyed views and must be fully cleared,
            // same as the 2D path.
            releaseComputeTexture3D(name);
        }
        try {
            const texture = _device.createTexture({
                label: `compute3d_${name}`,
                size: [width, height, depth], dimension: '3d', format,
                usage: GPUTextureUsage.STORAGE_BINDING | GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
            });
            _textures3d[name] = texture;
            _textureViews3d[name] = texture.createView();
            _textureMeta3d[name] = { width, height, depth, format };
            return true;
        } catch (e) {
            console.error(`createComputeTexture3D '${name}' ${width}x${height}x${depth} ${format} failed: ${e}`);
            delete _textures3d[name]; delete _textureViews3d[name]; delete _textureMeta3d[name];
            return false;
        }
    }

    // Clear all compute-kernel bind-group caches:
    // after in-place rebuild of storage textures, textureView identity changes,
    // and an old cache hit would bind a destroyed view.
    // Resize is infrequent, so clearing everything has no practical performance impact.
    function _invalidateComputeBindGroups() {
        _fogSamplingFrame = null;
        _weatherSamplingFrame = null;
        for (const state of Object.values(_fogComposites)) {
            state.prepared = null;
            for (const slot of state.slots) { slot.refs = null; slot.group = null; slot.samplingGroup = null; slot.weatherSamplingGroup = null; }
        }
        for (const kn in _computeKernels) {
            const k = _computeKernels[kn];
            if (k.slots) for (const slot of k.slots) {
                if (slot) { slot.bindGroup = null; slot.resKey = null; slot.rtRefs = null; slot.bufferRefs = null; }
            }
        }
    }

    let _retiredComputeResources = [];
    function releaseComputeTexture3D(name) {
        const texture = _textures3d[name];
        delete _textures3d[name]; delete _textureViews3d[name]; delete _textureMeta3d[name];
        _invalidateComputeBindGroups();
        if (texture) _queueComputeRetirement(texture);
    }
    function _queueComputeRetirement(resource) {
        // 帧内资源必须等当前编码提交或丢弃后才能进入 GPU 退休队列。
        _retiredComputeResources.push(resource);
        if (!_frameStarted) _retireComputeResources();
    }
    function _retireComputeResources() {
        if (!_retiredComputeResources.length) return;
        const resources = _retiredComputeResources; _retiredComputeResources = [];
        const dispose = () => { for (const resource of resources) resource.destroy(); };
        _device.queue.onSubmittedWorkDone().then(dispose, dispose);
    }

    // A0.1: indirect 为真时追加 GPUBufferUsage.INDIRECT。用途位只能在 createBuffer 时声明，
    // 之后无法加宽，所以这个标志由 C# 侧的 StorageBufferUsage 一路传到这里。
    function createStorageBuffer(id, sizeInBytes, indirect) {
        if (_storageBuffers[id]) return true;
        let usage = GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST | GPUBufferUsage.COPY_SRC;
        if (indirect) usage |= GPUBufferUsage.INDIRECT;
        _storageBuffers[id] = _device.createBuffer({
            size: Math.max(4, Math.ceil(sizeInBytes / 4) * 4),
            usage,
        });
        return true;
    }

    // 1-8: CPU -> storage-buffer constant-block path
    // filling the previous gap where StorageBufferRead had no upload entry point.
    // Must be called from the frame-loop thread and outside render passes
    // see the IGraphics.UpdateStorageBuffer contract.
    // This backend relies on queue ordering for writeBuffer,
    // and ordering relative to later dispatches is guaranteed by submission order,
    // so no barrier is needed.
    function updateStorageBuffer(id, bytes) {
        if (!_device || _instancedDiag.deviceLost || _passEncoder) return false;
        const buf = _storageBuffers[id];
        if (!buf) { console.error(`updateStorageBuffer: storage buffer '${id}' not found`); return false; }
        const data = _interopToU8(bytes);
        if (!data || data.byteLength === 0) return false;
        // 必须完整上传；截断会留下上一帧参数，不能作为成功结果发布。
        if ((data.byteLength & 3) !== 0 || data.byteLength > buf.size) {
            console.error(`updateStorageBuffer '${id}': invalid ${data.byteLength}B for buffer ${buf.size}B`);
            return false;
        }
        try {
            _device.queue.writeBuffer(buf, 0, data, 0, data.byteLength);
            return true;
        } catch (e) {
            console.error(`updateStorageBuffer '${id}' failed: ${e}`);
            return false;
        }
    }

    function disposeStorageBuffer(id) {
        const buf = _storageBuffers[id];
        if (!buf) return;
        delete _storageBuffers[id];
        _invalidateComputeBindGroups();
        _queueComputeRetirement(buf);
    }

    // resourcesJson:
    // prefix-encoded array in binding declaration order
    // skipping the Params slot:
    // "t:textureName" / "b:bufferId" / "r:RTName".
    // 2-1: RenderTarget color can serve as compute input.
    // 2-2: when binding DepthTexture, resolve depthView.
    // The string itself is the bind-group cache key,
    // so unchanged resource groups rebuild nothing.
    // "r:" references add view-identity validation through slot.rtRefs:
    // MatchBackbuffer RTs lazily rebuild colorView/depthView in beginPass while their names stay unchanged,
    // so checking only the key would incorrectly hit old views.
    function dispatchCompute(name, paramsBytes, resourcesJson, gx, gy, gz) {
        const k = _computeKernels[name];
        if (!k) { console.error(`dispatchCompute: kernel '${name}' not found`); return false; }
        if (!_frameStarted || !_commandEncoder) { console.error(`dispatchCompute '${name}': outside frame`); return false; }
        if (_passEncoder) { console.error(`dispatchCompute '${name}': dispatch is forbidden inside a render pass (contract violation)`); return false; }

        if (k.frameId !== _computeFrameId) { k.frameId = _computeFrameId; k.cursor = 0; }
        const slotIdx = k.cursor++;
        let slot = k.slots[slotIdx];
        if (!slot) {
            slot = {
                paramsBuffer: k.paramsSize > 0
                    ? _device.createBuffer({ size: Math.max(16, k.paramsSize), usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST })
                    : null,
                bindGroup: null,
                resKey: null,
                rtRefs: null,
                bufferRefs: null,
            };
            k.slots[slotIdx] = slot;
        }

        if (slot.paramsBuffer) {
            paramsBytes = _interopToU8(paramsBytes);
            if (paramsBytes && paramsBytes.byteLength > 0)
                _device.queue.writeBuffer(slot.paramsBuffer, 0, paramsBytes);
        }

        let rtStale = false;
        if (slot.rtRefs) {
            for (const ref of slot.rtRefs) {
                const rt = _renderTargets[ref.name];
                if (!rt || (ref.depth ? rt.depthView : rt.colorView) !== ref.view) { rtStale = true; break; }
            }
        }
        // 同名缓冲可能因 copy-on-write 切换版本；不能继续引用已退休的旧对象。
        const bufferStale = slot.bufferRefs?.some(ref => _storageBuffers[ref.name] !== ref.buffer) ?? false;
        if (slot.resKey !== resourcesJson || rtStale || bufferStale) {
            const resources = resourcesJson ? JSON.parse(resourcesJson) : [];
            const entries = [];
            const rtRefs = [], bufferRefs = [];
            let resIndex = 0;
            for (let i = 0; i < k.bindings.length; i++) {
                const type = k.bindings[i].type;
                if (type === 0) {
                    entries.push({ binding: i, resource: { buffer: slot.paramsBuffer } });
                    continue;
                }
                const ref = resources[resIndex++];
                if (ref === undefined) { console.error(`dispatchCompute '${name}': resource count does not match the binding declaration`); return false; }
                if (ref.startsWith('r:')) {
                    const rtName = ref.slice(2);
                    const rt = _renderTargets[rtName];
                    // 2-2: DepthTexture binding (type 2) resolves depthView for depth-only RTs,
                    // otherwise colorView
                    const wantDepth = type === 2;
                    const view = rt ? (wantDepth ? rt.depthView : rt.colorView) : null;
                    if (!view) { console.error(`dispatchCompute '${name}': render target '${rtName}' not found`); return false; }
                    entries.push({ binding: i, resource: view });
                    rtRefs.push({ name: rtName, view, depth: wantDepth });
                } else if (type === 6 || type === 7) {
                    // 1-8: 3D bindings resolve from the dedicated 3D registry.
                    // The 2D registry may contain a same-name 2D texture,
                    // and mixing them would silently bind the wrong dimension.
                    const texName = ref.slice(2);
                    const view = _textureViews3d[texName];
                    if (!view) { console.error(`dispatchCompute '${name}': 3D texture '${texName}' not found`); return false; }
                    entries.push({ binding: i, resource: view });
                } else if (type === 1 || type === 3) {
                    const texName = ref.slice(2);
                    const view = _textureViews[texName];
                    if (!view) { console.error(`dispatchCompute '${name}': texture '${texName}' not found`); return false; }
                    entries.push({ binding: i, resource: view });
                } else {
                    const buf = _storageBuffers[ref.slice(2)];
                    if (!buf) { console.error(`dispatchCompute '${name}': storage buffer '${ref.slice(2)}' not found`); return false; }
                    entries.push({ binding: i, resource: { buffer: buf } });
                    bufferRefs.push({ name: ref.slice(2), buffer: buf });
                }
            }
            if (k.hasSampled)
                entries.push({ binding: 15, resource: _samplers['linear'] });
            slot.bindGroup = _device.createBindGroup({ label: `compute_${name}_bg`, layout: k.bindGroupLayout, entries });
            slot.resKey = resourcesJson;
            slot.rtRefs = rtRefs.length > 0 ? rtRefs : null;
            slot.bufferRefs = bufferRefs.length > 0 ? bufferRefs : null;
        }

        const pass = _commandEncoder.beginComputePass({ label: `compute_${name}` });
        pass.setPipeline(k.pipeline);
        pass.setBindGroup(0, slot.bindGroup);
        pass.dispatchWorkgroups(gx, gy, gz);
        pass.end();
        return true;
    }

    function endFrame() {
        if (!_frameStarted || !_commandEncoder) return;
        // Defensive handling for an unmatched EndPass
        // normal paths should already have closed the pass via endPass
        if (_passEncoder) endPass();
        _clearActivePass();
        releaseSceneCollection();
        let readback = null;
        const capture = _nextCapture;
        _nextCapture = null;
        if (capture) {
            const width = _canvas.width, height = _canvas.height;
            const stride = Math.ceil(width * 4 / 256) * 256;
            readback = _device.createBuffer({ size: stride * height, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
            _commandEncoder.copyTextureToBuffer({ texture: _context.getCurrentTexture() },
                { buffer: readback, bytesPerRow: stride, rowsPerImage: height }, [width, height]);
            capture.width = width; capture.height = height; capture.stride = stride;
        }
        _device.queue.submit([_commandEncoder.finish()]);
        if (readback) {
            readback.mapAsync(GPUMapMode.READ).then(() => {
                const raw = new Uint8Array(readback.getMappedRange());
                const pixels = new Uint8Array(capture.width * capture.height * 4);
                for (let y = 0; y < capture.height; y++)
                    pixels.set(raw.subarray(y * capture.stride, y * capture.stride + capture.width * 4), y * capture.width * 4);
                if (_format.startsWith('bgra')) {
                    for (let i = 0; i < pixels.length; i += 4) [pixels[i], pixels[i + 2]] = [pixels[i + 2], pixels[i]];
                }
                readback.unmap(); readback.destroy();
                // Explicit base64 avoids JSON arrays with millions of numeric elements.
                let binary = '';
                for (let i = 0; i < pixels.length; i += 8192) binary += String.fromCharCode(...pixels.subarray(i, i + 8192));
                capture.resolve({ width: capture.width, height: capture.height, base64: btoa(binary) });
            }).catch(error => { readback.destroy(); capture.reject(error); });
        }
        _commandEncoder = null; _frameStarted = false;
        _retireImmediateResources();
        _retireComputeResources();

        if (_debugLog) {
            const now = performance.now(), frameMs = now - _fpsFrameStartMs;
            if (frameMs > _fpsMaxFrameMs) _fpsMaxFrameMs = frameMs;
            _fpsFrameCount++;
            if (now - _fpsLastSec >= 1000) {
                console.log(`[FPS] ${(_fpsFrameCount * 1000 / (now - _fpsLastSec)).toFixed(1)} fps, max ${_fpsMaxFrameMs.toFixed(2)} ms, pool v/i/u=${_bufferPool.vertex.length}/${_bufferPool.index.length}/${_bufferPool.uniform.length}`);
                _fpsLastSec = now; _fpsFrameCount = 0; _fpsMaxFrameMs = 0;
            }
        }
    }

    function resizeCanvas(width, height) {
        if (_canvas) { _canvas.width = width; _canvas.height = height; }
    }

    function requestCanvasResize(width, height) {
        if (!(width > 0 && height > 0)) throw new Error('Invalid canvas size');
        _resizeWidth = width; _resizeHeight = height; _needsResize = true;
    }

    function getCanvasSize() {
        return _canvas ? { width: _canvas.width || _canvas.clientWidth || 0, height: _canvas.height || _canvas.clientHeight || 0 } : { width: 0, height: 0 };
    }

    // Apply pending resize
    // called by the C# render loop at frame start
    // so beginFrame sees the latest size
    function applyPendingResize() {
        if (!_needsResize) return { width: 0, height: 0 };
        _canvas.width = _resizeWidth;
        _canvas.height = _resizeHeight;
        _needsResize = false;
        return { width: _resizeWidth, height: _resizeHeight };
    }

    // [JSImport] variant (Phase 2):
    // returns [width, height], or [0, 0] when no resize is pending.
    function applyPendingResizePacked() {
        if (!_needsResize) return [0, 0];
        _canvas.width = _resizeWidth;
        _canvas.height = _resizeHeight;
        _needsResize = false;
        return [_resizeWidth, _resizeHeight];
    }

    function setDebugLog(enabled) { _debugLog = !!enabled; }
    function getInstancedDiagState() { return { ..._instancedDiag }; }

    let _immediatePipeline, _immediateLayout, _immediateLinear, _immediatePoint;
    let _nextCapture;
    function captureNextFrame() {
        if (_nextCapture) throw new Error('A capture is already pending');
        return new Promise((resolve, reject) => { _nextCapture = { resolve, reject }; });
    }
    let _immediateDraws = [], _immediateGarbage = [];
    let _immediateClosed = false;
    let _immediateLiveBuffers = 0, _immediatePendingRetire = 0;
    function initializeImmediate(source) {
        if (_immediateClosed || _instancedDiag.deviceLost) throw new Error('Immediate device is closed/lost');
        if (_immediatePipeline) return;
        // D01-A writes target alpha independently; it is not a premultiplied page-compositing contract.
        _context.configure({ device: _device, format: _format, alphaMode: 'opaque',
            usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC });
        const module = _device.createShaderModule({ code: source, label: 'Season immediate 2D' });
        _immediateLayout = _device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { type: 'uniform', hasDynamicOffset: true, minBindingSize: 112 } },
            { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: {} },
            { binding: 2, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
            { binding: 3, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
        ] });
        _immediateLinear = _device.createSampler({ minFilter: 'linear', magFilter: 'linear' });
        _immediatePoint = _device.createSampler({ minFilter: 'nearest', magFilter: 'nearest' });
        _immediatePipeline = _device.createRenderPipeline({
            layout: _device.createPipelineLayout({ bindGroupLayouts: [_immediateLayout] }),
            vertex: { module, entryPoint: 'vs' },
            fragment: { module, entryPoint: 'fs', targets: [{ format: _format, blend: {
                color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha', operation: 'add' },
                alpha: { srcFactor: 'one', dstFactor: 'zero', operation: 'add' }
            } }] },
            primitive: { topology: 'triangle-strip', cullMode: 'none' },
            depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'always' }
        });
    }

    function prepareImmediate(names, parameters) {
        if (_passEncoder || !_frameStarted || _immediateClosed || _instancedDiag.deviceLost)
            throw new Error('Immediate Prepare requires an active frame outside passes');
        if (parameters.length !== names.length * 28) throw new Error('Invalid immediate parameter payload');
        _immediateDraws = [];
        if (!names.length) return;
        const alignment = _device.limits.minUniformBufferOffsetAlignment;
        const stride = Math.ceil(112 / alignment) * alignment;
        const data = new Float32Array(names.length * stride / 4);
        for (let i = 0; i < names.length; i++) data.set(parameters.slice(i * 28, i * 28 + 28), i * stride / 4);
        const buffer = _device.createBuffer({ size: data.byteLength, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        _immediateLiveBuffers++;
        _immediateGarbage.push(() => { buffer.destroy(); _immediateLiveBuffers--; });
        _device.queue.writeBuffer(buffer, 0, data);
        const groups = new Map();
        for (let i = 0; i < names.length; i++) {
            const view = _textureViews[names[i]];
            if (!view) throw new Error(`Immediate texture missing: ${names[i]}`);
            let group = groups.get(view);
            if (!group) {
                group = _device.createBindGroup({ layout: _immediateLayout, entries: [
                    { binding: 0, resource: { buffer, offset: 0, size: 112 } },
                    { binding: 1, resource: _readSceneTextureView(view) },
                    { binding: 2, resource: _immediateLinear },
                    { binding: 3, resource: _immediatePoint }
                ] });
                groups.set(view, group);
            }
            _immediateDraws.push({ group, offset: i * stride });
        }
    }

    function submitImmediate() {
        if (!_passEncoder || !_passOverlay || _immediateClosed) throw new Error('Immediate Submit requires Overlay');
        _passEncoder.setPipeline(_immediatePipeline);
        for (const draw of _immediateDraws) {
            _passEncoder.setBindGroup(0, draw.group, [draw.offset]);
            _passEncoder.draw(4);
        }
        _immediateDraws = [];
    }

    function retireTexture(name) {
        const texture = _textures[name];
        if (!texture) return;
        // Remove the name now, but keep the captured object alive through encoding and submission.
        delete _textures[name]; delete _textureViews[name]; delete _textureMeta[name];
        _invalidateComputeBindGroups();
        _immediateGarbage.push(() => texture.destroy());
        if (!_frameStarted) _retireImmediateResources();
    }

    function _retireImmediateResources() {
        if (!_immediateGarbage.length) return;
        const resources = _immediateGarbage.splice(0);
        _immediatePendingRetire++;
        const release = () => {
            for (const dispose of resources) dispose();
            _immediatePendingRetire--;
        };
        _device.queue.onSubmittedWorkDone().then(release, release);
    }

    function abortFrame() {
        if (_nextCapture) { _nextCapture.reject(new Error('Frame aborted')); _nextCapture = null; }
        if (_passEncoder) endPass();
        _clearActivePass();
        releaseSceneCollection();
        _commandEncoder = null; _frameStarted = false; _immediateDraws = [];
        _retireImmediateResources();
        _retireComputeResources();
    }

    async function closeImmediate() {
        _immediateClosed = true;
        abortFrame();
        await _device.queue.onSubmittedWorkDone().catch(() => {});
        _immediatePipeline = _immediateLayout = _immediateLinear = _immediatePoint = null;
        // Legacy named atlas is host-owned; stable pages were already retired by C#.
        retireTexture('TextAtlas');
        await _device.queue.onSubmittedWorkDone().catch(() => {});
    }

    function getImmediateDiagnostics() {
        return { closed: _immediateClosed, liveBuffers: _immediateLiveBuffers,
            pendingRetire: _immediatePendingRetire, textures: Object.keys(_textures),
            deviceLost: _instancedDiag.deviceLost, error: _instancedDiag.uncapturedError };
    }

    return {
        requestCanvasResize,
        captureNextFrame,
        initializeImmediate, prepareImmediate, submitImmediate, retireTexture, abortFrame, closeImmediate, getImmediateDiagnostics,
        initialize,
        loadTexture,
        uploadGlyphTexture,
        uploadEncodedTexture,
        decodeImageBytes,
        encodeImageBytes,
        encodeH264Video,
        decodeH264Video,
        beginFrame,
        beginSceneCollection, setSceneDrawPriority, sealSceneCollection,
        drawCollectedOpaque, drawCollectedTransparent, releaseSceneCollection,
        beginPass,
        endPass,
        createRenderTarget,
        disposeRenderTarget,
        synchronizeRenderTargetSizes,
        blitToBackbuffer,
        renderPost,
        drawSprite2D,
        drawTextAtlasSprite,
        updateTextInstance,
        drawTextInstanced,
        disposeTextInstance,
        drawSprite3D,
        drawMesh3D,
        uploadStaticMesh,
        uploadStaticSkinnedMesh,
        uploadStaticMeshInterop,
        uploadSkinnedBones,
        updateSceneLights,
        setShadowViewport,
        setShadowAtlas,
        // 1-7 Cubemap + environment IBL
        createTextureCube,
        setEnvCube,
        // 2-4 DDGI irradiance atlas
        setDdgiAtlas,
        setDdgiDepth,
        // 2-5 Step C/E cloud noise + AP 3D LUT
        setCloudNoise,
        setAerialLut,
        drawInstancedMesh3D,
        endFrame,
        resizeCanvas,
        getCanvasSize,
        applyPendingResize,
        applyPendingResizePacked,
        setDebugLog,
        requestFrame,
        pollInput,
        pollInputPacked,
        keyboardSnapshot,
        drawMesh3DBatch,
        updateStaticMeshVertices,
        updateTexturePixels,
        createTextureFromPixels,
        createAtlasTexture,
        uploadGlyphAtlasSubRects,
        uploadGlyphAtlasPackedRects,
        updateSpriteTexture,
        updateMeshTexture,
        updateMeshMaterialParams,
        getInstancedDiagState,
        createComputeKernel,
        disposeComputeKernel,
        createFogCompositePipeline, prepareFogComposite, drawFogComposite, disposeFogCompositePipeline,
        // A0.1 粒子间接绘制
        createParticleDrawPipeline, prepareParticleDraw, drawParticles, disposeParticleDrawPipeline,
        createComputeTexture,
        createStorageBuffer,
        disposeStorageBuffer,
        dispatchCompute,
        // 1-8 Compute 3D resource expansion
        createComputeTexture3D, releaseComputeTexture3D,
        updateStorageBuffer,
    };
})();
