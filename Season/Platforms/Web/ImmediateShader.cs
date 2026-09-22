// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

internal static class ImmediateShader
{
    internal const string Source = """
        struct Params {
            originXAxis: vec4f, yAxis: vec4f, uvRect: vec4f, tint: vec4f,
            clipRect: vec4f, parameters: vec4f, uvClamp: vec4f
        };
        @group(0) @binding(0) var<uniform> p: Params;
        @group(0) @binding(1) var image: texture_2d<f32>;
        @group(0) @binding(2) var linearSampler: sampler;
        @group(0) @binding(3) var pointSampler: sampler;
        struct Vertex { @builtin(position) position: vec4f, @location(0) uv: vec2f };
        @vertex fn vs(@builtin(vertex_index) index: u32) -> Vertex {
            let corner = vec2f(f32(index & 1u), f32(index >> 1u));
            var output: Vertex;
            output.position = vec4f(p.originXAxis.xy + corner.x * p.originXAxis.zw + corner.y * p.yAxis.xy, 0, 1);
            output.uv = p.uvRect.xy + corner * p.uvRect.zw;
            return output;
        }
        @fragment fn fs(v: Vertex) -> @location(0) vec4f {
            let screenSize = 1.0 / max(fwidth(v.uv), vec2f(0.00001));
            let uv = clamp(v.uv, p.uvClamp.xy, p.uvClamp.zw);
            let linear = textureSampleLevel(image, linearSampler, uv, 0);
            let point = textureSampleLevel(image, pointSampler, uv, 0);
            let sampled = select(linear, point, p.parameters.x > 0.5);
            if (any(v.position.xy < p.clipRect.xy) || any(v.position.xy >= p.clipRect.zw)) { discard; }
            if (p.parameters.y > 0) {
                let median = max(min(sampled.r, sampled.g), min(max(sampled.r, sampled.g), sampled.b));
                let msdfDistance = median - 0.5;
                let trueDistance = sampled.a - 0.5;
                let distance = select(trueDistance, msdfDistance, msdfDistance * trueDistance > 0);
                let range = max(0.5 * dot(p.parameters.y * p.parameters.zw, screenSize), 1.0);
                return vec4f(p.tint.rgb, p.tint.a * clamp(range * distance + 0.5, 0, 1));
            }
            return sampled * p.tint;
        }
        """;
}
