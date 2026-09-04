// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

window.sampleWebHome = {
    requestFullscreenById: async function (elementId) {
        const element = document.getElementById(elementId);
        if (!element) {
            throw new Error(`Element '${elementId}' was not found.`);
        }

        if (document.fullscreenElement === element) {
            return;
        }

        await element.requestFullscreen();
    }
};

window.requestSampleWasmFullscreenById = function (elementId) {
    return window.sampleWebHome.requestFullscreenById(elementId);
};
