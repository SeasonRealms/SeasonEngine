// Microphone recording for SeasonEngine on the Web platform.
// AudioWorklet-based PCM16 capture: the worklet (seasonAudioWorklet.js) forwards
// 16 kHz mono Float32 blocks while recording, and stop() concatenates them into
// one Int16 array. This avoids MediaRecorder's webm/opus container entirely, so
// the C# side receives raw 16 kHz / mono PCM16 samples and writes its own RIFF
// WAV header — identical layout to the Android/Windows output.
//
// Surface:
//   seasonAudioRecorder.start()          -> Promise<boolean>   (stream live?)
//   seasonAudioRecorder.stop()           -> Promise<int>        (pending PCM byte length)
//   seasonAudioRecorder.copyPcm(buffer)  -> int                 (copies pending PCM into the MemoryView)
//   seasonAudioRecorder.queryPermission()-> Promise<int>        (0 granted / 1 prompt / 2 denied / 3 unsupported)
//
// getUserMedia requires a secure context (https or localhost).

(function () {
    'use strict';

    let mediaStream = null;
    let audioContext = null;
    let sourceNode = null;
    let workletNode = null;
    let chunks = [];
    let pendingPcm = null;

    async function ensureWorkletModule(ctx) {
        // Blazor serves the worklet from wwwroot/js; resolve it against the page base.
        const url = new URL('seasonAudioWorklet.js', document.baseURI).href;
        await ctx.audioWorklet.addModule(url);
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
