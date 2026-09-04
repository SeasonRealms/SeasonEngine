// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

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
