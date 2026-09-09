// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine
//
// seasonMedia.js — unified media interop for SeasonEngine on the Web platform.
// Replaces seasonAudioPlayer.js, seasonAudioRecorder.js, seasonAudioWorklet.js
// and seasonVideoPlayer.js in a single file. It registers three surfaces:
//
//   globalThis.seasonAudioPlayer   music/sound playback (HTMLAudioElement x2)
//   globalThis.seasonAudioRecorder microphone PCM16 capture (AudioWorklet)
//   window.SeasonVideoPlayer       hidden <video> playback with frame callback
//
// The PCM capture worklet must run inside the AudioWorkletGlobalScope, which a
// plain <script> cannot reach, so its source is embedded below as a string and
// loaded through a Blob URL (same origin as the document, so addModule needs no
// CORS handling and no separate file deployment).

// ---------------------------------------------------------------------------
// Embedded AudioWorklet module (was seasonAudioWorklet.js).
// The AudioContext pins the sample rate to 16 kHz and the getUserMedia stream is
// mono, so every block here is already 16 kHz / 1 channel / Float32. Each block
// is forwarded to the main thread through port.postMessage (copied, because the
// graph reuses the input buffer), so a stop never loses the final block.
// ---------------------------------------------------------------------------
const SEASON_PCM_WORKLET_SOURCE = `
class SeasonPcmWorklet extends AudioWorkletProcessor {
    constructor() {
        super();
    }

    process(inputs) {
        const input = inputs[0];
        if (input && input.length > 0 && input[0].length > 0) {
            this.port.postMessage(new Float32Array(input[0]));
        }
        return true;
    }
}
registerProcessor('season-pcm-worklet', SeasonPcmWorklet);
`;

// ---------------------------------------------------------------------------
// seasonAudioRecorder — microphone recording.
// AudioWorklet-based PCM16 capture: the worklet forwards 16 kHz mono Float32
// blocks while recording, and stop() concatenates them into one Int16 array.
// This avoids MediaRecorder's webm/opus container entirely, so the C# side
// receives raw 16 kHz / mono PCM16 samples and writes its own RIFF WAV header —
// identical layout to the Android/Windows output.
//
// Surface:
//   seasonAudioRecorder.start()          -> Promise<boolean>   (stream live?)
//   seasonAudioRecorder.stop()           -> Promise<int>        (pending PCM byte length)
//   seasonAudioRecorder.copyPcm(buffer)  -> int                 (copies pending PCM into the MemoryView)
//   seasonAudioRecorder.queryPermission()-> Promise<int>        (0 granted / 1 prompt / 2 denied / 3 unsupported)
//
// getUserMedia requires a secure context (https or localhost).
// ---------------------------------------------------------------------------
(function () {
    'use strict';

    let mediaStream = null;
    let audioContext = null;
    let sourceNode = null;
    let workletNode = null;
    let chunks = [];
    let pendingPcm = null;
    let workletModuleUrl = null;

    function ensureWorkletModule(ctx) {
        // The worklet source is embedded above; a Blob URL makes it loadable by
        // addModule without an extra deployed file. The URL is created once and
        // reused across AudioContext instances (each context has its own worklet
        // scope, so the module must be added per context).
        if (!workletModuleUrl) {
            workletModuleUrl = URL.createObjectURL(
                new Blob([SEASON_PCM_WORKLET_SOURCE], { type: 'text/javascript' }));
        }
        return ctx.audioWorklet.addModule(workletModuleUrl);
    }

    function cleanup(closeContext) {
        if (sourceNode) { try { sourceNode.disconnect(); } catch (e) { } sourceNode = null; }
        if (workletNode) { try { workletNode.port.onmessage = null; workletNode.disconnect(); } catch (e) { } workletNode = null; }
        if (audioContext) {
            const ctx = audioContext;
            audioContext = null;
            if (closeContext) { ctx.close().catch(function () { }); }
        }
        if (mediaStream) {
            for (const track of mediaStream.getTracks()) track.stop();
            mediaStream = null;
        }
        chunks = [];
        pendingPcm = null;
    }

    globalThis.seasonAudioRecorder = {
        async start() {
            try {
                cleanup(false);

                mediaStream = await navigator.mediaDevices.getUserMedia({
                    audio: {
                        channelCount: 1,
                        sampleRate: 16000,
                        echoCancellation: true,
                        noiseSuppression: true
                    }
                });

                audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });
                await ensureWorkletModule(audioContext);

                chunks = [];

                sourceNode = audioContext.createMediaStreamSource(mediaStream);
                workletNode = new AudioWorkletNode(audioContext, 'season-pcm-worklet', {
                    numberOfInputs: 1,
                    numberOfOutputs: 0,
                    channelCount: 1
                });
                workletNode.port.onmessage = function (event) {
                    if (event.data instanceof Float32Array) {
                        chunks.push(event.data);
                    }
                };

                sourceNode.connect(workletNode);
                return true;
            } catch (error) {
                console.error('[SeasonAudioRecorder] start failed:', error);
                cleanup(true);
                return false;
            }
        },

        // Stops capture, aggregates the worklet blocks into one Int16 PCM buffer
        // and keeps it in pendingPcm. Returns the pending byte length (0 when
        // there is nothing), because JSImport cannot marshal a Task<byte[]>; the
        // C# side then allocates exactly that many bytes and calls copyPcm.
        async stop() {
            try {
                if (sourceNode) { try { sourceNode.disconnect(); } catch (e) { } sourceNode = null; }
                if (workletNode) { try { workletNode.port.onmessage = null; workletNode.disconnect(); } catch (e) { } workletNode = null; }

                const collected = chunks;
                chunks = [];

                let total = 0;
                for (const chunk of collected) total += chunk.length;
                const pcm16 = new Int16Array(total);
                let offset = 0;
                for (const chunk of collected) {
                    for (let i = 0; i < chunk.length; i++) {
                        const s = Math.max(-1, Math.min(1, chunk[i]));
                        pcm16[offset + i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
                    }
                    offset += chunk.length;
                }

                if (audioContext) {
                    // Await close so the worklet thread finishes before the context dies.
                    const ctx = audioContext;
                    audioContext = null;
                    await ctx.close();
                }
                if (mediaStream) {
                    for (const track of mediaStream.getTracks()) track.stop();
                    mediaStream = null;
                }

                pendingPcm = new Uint8Array(pcm16.buffer);
                return pendingPcm.length;
            } catch (error) {
                console.error('[SeasonAudioRecorder] stop failed:', error);
                cleanup(true);
                pendingPcm = null;
                return 0;
            }
        },

        // Copies the pending PCM into the caller-provided MemoryView and returns
        // the number of bytes copied. buffer is an Uint8Array on the JS side.
        copyPcm(buffer) {
            if (!pendingPcm || pendingPcm.length === 0) return 0;
            const n = Math.min(pendingPcm.length, buffer.length);
            buffer.set(pendingPcm.subarray(0, n));
            pendingPcm = null;
            return n;
        },

        async queryPermission() {
            try {
                if (!navigator.permissions || !navigator.permissions.query) return 3;
                const status = await navigator.permissions.query({ name: 'microphone' });
                switch (status.state) {
                    case 'granted': return 0;
                    case 'prompt': return 1;
                    case 'denied': return 2;
                    default: return 3;
                }
            } catch (error) {
                // permissions.query('microphone') is unavailable in some browsers
                // (e.g. older Safari); treat it as prompt so getUserMedia can still
                // surface the consent UI.
                return 1;
            }
        }
    };
})();

// ---------------------------------------------------------------------------
// seasonAudioPlayer — music / sound playback.
// Two lazy HTMLAudioElement players (music and sound) mirror the two-channel
// structure used by Windows (MediaPlayer x2), Apple (AVPlayer x2) and Android
// (MediaPlayer x2): PlayMedia's "Music" type goes to the music channel and
// everything else goes to the sound channel. The browser decodes WAV/MP3/AAC
// natively, so no WebAudio decode pipeline is required.
//
// Browsers block playback that does not start from a user gesture (autoplay
// policy). App startup calls PlayMedia before any gesture exists, so play()
// failures with NotAllowedError are parked in a pending list and retried once
// on the first pointerdown/keydown/touchstart anywhere in the document.
//
// Surface (all synchronous; bound through [JSImport] on the C# side):
//   seasonAudioPlayer.play(kind, url, volume) -> boolean   kind: 'music' | 'sound'
//   seasonAudioPlayer.setVolume(music, sound) -> void      values are 0..1 floats
//   seasonAudioPlayer.pause()                  -> void
//   seasonAudioPlayer.resume()                 -> void
//   seasonAudioPlayer.isPlaying()              -> boolean
// ---------------------------------------------------------------------------
(function () {
    'use strict';

    let musicAudio = null;
    let soundAudio = null;
    let pending = [];        // channel names awaiting a user gesture
    let gestureArmed = false;

    function clamp01(v) {
        return v < 0 ? 0 : (v > 1 ? 1 : v);
    }

    function channelAudio(kind) {
        if (kind === 'music') {
            if (!musicAudio) {
                musicAudio = new Audio();
                musicAudio.loop = false;
                musicAudio.preload = 'auto';
            }
            return musicAudio;
        }
        if (!soundAudio) {
            soundAudio = new Audio();
            soundAudio.loop = false;
            soundAudio.preload = 'auto';
        }
        return soundAudio;
    }

    function armGestureRetry() {
        if (gestureArmed) return;
        gestureArmed = true;
        const retry = function () {
            gestureArmed = false;
            document.removeEventListener('pointerdown', retry, true);
            document.removeEventListener('keydown', retry, true);
            document.removeEventListener('touchstart', retry, true);
            const queued = pending;
            pending = [];
            for (const kind of queued) {
                const audio = channelAudio(kind);
                audio.play().catch(function (e) {
                    console.error('[SeasonAudioPlayer] gesture retry failed for', kind, ':', e);
                });
            }
        };
        document.addEventListener('pointerdown', retry, { once: true, capture: true });
        document.addEventListener('keydown', retry, { once: true, capture: true });
        document.addEventListener('touchstart', retry, { once: true, capture: true });
    }

    // Synchronous on purpose: the C# side binds play/resume through synchronous
    // [JSImport] signatures, which cannot unwrap a Promise. The rejection path
    // is handled here instead of being returned to the caller.
    function tryPlay(audio, kind) {
        audio.play().then(function () {
            // Started.
        }).catch(function (error) {
            if (error && error.name === 'NotAllowedError') {
                // Autoplay policy: park the channel and retry on the first gesture.
                if (pending.indexOf(kind) < 0) pending.push(kind);
                armGestureRetry();
            } else {
                console.error('[SeasonAudioPlayer] play failed for', kind, ':', error);
            }
        });
        return true;
    }

    globalThis.seasonAudioPlayer = {
        play(kind, url, volume) {
            try {
                const audio = channelAudio(kind);
                audio.pause();
                audio.src = url;
                audio.volume = clamp01(volume);
                return tryPlay(audio, kind);
            } catch (error) {
                console.error('[SeasonAudioPlayer] play error:', error);
                return false;
            }
        },

        setVolume(music, sound) {
            if (musicAudio) musicAudio.volume = clamp01(music);
            if (soundAudio) soundAudio.volume = clamp01(sound);
        },

        pause() {
            if (musicAudio) musicAudio.pause();
            if (soundAudio) soundAudio.pause();
        },

        resume() {
            if (musicAudio) tryPlay(musicAudio, 'music');
            if (soundAudio) tryPlay(soundAudio, 'sound');
        },

        isPlaying() {
            const active = function (audio) {
                return audio && !audio.paused && !audio.ended;
            };
            return active(musicAudio) || active(soundAudio);
        }
    };
})();

// ---------------------------------------------------------------------------
// SeasonVideoPlayer — video playback (was seasonVideoPlayer.js).
// Creates a hidden <video> element, Canvas, and requestVideoFrameCallback.
// Video decoding and audio playback are handled natively by the browser, with
// no extra dependencies required. Frame data is transferred to the C# side
// through Base64 encoding. Called through IJSRuntime
// (SeasonVideoPlayer.init / SeasonVideoPlayer.stop).
// ---------------------------------------------------------------------------
window.SeasonVideoPlayer = (function () {
    var _video = null;
    var _canvas = null;
    var _ctx = null;
    var _dotnet = null;
    var _rafId = 0;
    var _usingVfc = false;

    function init(url, dotnetRef) {
        stop();

        _dotnet = dotnetRef;

        // Create the hidden video element
        _video = document.createElement('video');
        _video.src = url;
        _video.crossOrigin = 'anonymous';
        _video.playsInline = true;
        _video.loop = false;
        _video.style.display = 'none';
        document.body.appendChild(_video);

        // Create the offscreen canvas
        _canvas = document.createElement('canvas');
        _ctx = _canvas.getContext('2d', { willReadFrequently: true });

        var self = this;
        _video.addEventListener('loadedmetadata', function () {
            var w = _video.videoWidth;
            var h = _video.videoHeight;
            if (!w || !h) {
                w = 640; h = 480;
            }
            _canvas.width = w;
            _canvas.height = h;
            _dotnet.invokeMethodAsync('OnReady', w, h);
            _video.play().catch(function (e) {
                console.error('[SeasonVideoPlayer] play failed:', e);
            });
            requestFrame();
        });

        _video.addEventListener('ended', function () {
            _dotnet.invokeMethodAsync('OnEnded');
        });

        _video.addEventListener('error', function (e) {
            _dotnet.invokeMethodAsync('OnError',
                _video.error ? _video.error.message : 'unknown');
        });
    }

    function requestFrame() {
        if (!_video || _video.paused || _video.ended) return;

        if (_video.requestVideoFrameCallback) {
            _usingVfc = true;
            _video.requestVideoFrameCallback(function () {
                captureFrame();
                requestFrame();
            });
        } else {
            // Fallback: use requestAnimationFrame
            _rafId = requestAnimationFrame(function () {
                captureFrame();
                requestFrame();
            });
        }
    }

    function captureFrame() {
        if (!_ctx || !_video || _video.readyState < 2) return;

        var w = _canvas.width;
        var h = _canvas.height;
        if (!w || !h) return;

        _ctx.drawImage(_video, 0, 0, w, h);
        var imageData = _ctx.getImageData(0, 0, w, h);
        var pixels = imageData.data; // Uint8ClampedArray, RGBA

        // Convert to Base64
        // compatible with C# Convert.FromBase64String
        var binary = '';
        for (var i = 0; i < pixels.length; i++) {
            binary += String.fromCharCode(pixels[i]);
        }
        var base64 = btoa(binary);

        _dotnet.invokeMethodAsync('OnFrame', base64, w, h);
    }

    function stop() {
        if (_video) {
            _video.pause();
            _video.removeAttribute('src');
            _video.load();
            if (_video.parentNode) {
                _video.parentNode.removeChild(_video);
            }
            _video = null;
        }
        if (_rafId) {
            if (_usingVfc) {
                // requestVideoFrameCallback has no cancel method,
                // but the video is already paused so the callback will not fire
            } else {
                cancelAnimationFrame(_rafId);
            }
            _rafId = 0;
        }
        _canvas = null;
        _ctx = null;
        _usingVfc = false;
    }

    return {
        init: init,
        stop: stop
    };
})();
