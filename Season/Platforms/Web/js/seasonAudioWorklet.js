// PCM16 capture worklet for SeasonEngine microphone recording.
// The AudioContext pins the sample rate to 16 kHz and the getUserMedia stream is
// mono, so every block here is already 16 kHz / 1 channel / Float32. Each block
// is forwarded to the main thread through port.postMessage (copied, because the
// graph reuses the input buffer), so a stop never loses the final block.

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
