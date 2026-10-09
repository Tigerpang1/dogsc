cbuffer SceneConstants : register(b0)
{
    float4 OutputSourceSize; // output width, output height, source width, source height
    float4 Crop;             // source crop origin xy, source crop size xy
    float4 ScreenRect;       // output pixel left, top, width, height
    float4 Style;            // corner radius px, shadow radius px, shadow strength, click strength
    float4 Cursor;           // normalized source x/y, visible, cursor scale
    float4 Background;       // rgba
};

Texture2D SourceTexture : register(t0);
SamplerState LinearClamp : register(s0);

struct VertexOutput { float4 Position : SV_Position; float2 UV : TEXCOORD0; };

VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    VertexOutput o;
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    o.Position = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.UV = p;
    return o;
}

float roundedBox(float2 p, float2 halfSize, float radius)
{
    float2 q = abs(p) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float segmentDistance(float2 p, float2 a, float2 b)
{
    float2 ab = b - a;
    float h = saturate(dot(p - a, ab) / max(dot(ab, ab), 0.0001));
    return length(p - (a + h * ab));
}

float cursorShape(float2 p)
{
    const float2 v[7] = {
        float2(0,0), float2(0,18), float2(4.7,13.7), float2(8.5,22),
        float2(11.5,20.7), float2(8.2,12.5), float2(15,12.2)
    };
    bool inside = false;
    float edge = 10000.0;
    [unroll] for (int i = 0; i < 7; i++)
    {
        int j = (i + 6) % 7;
        float2 a = v[i], b = v[j];
        edge = min(edge, segmentDistance(p, a, b));
        bool crosses = ((a.y > p.y) != (b.y > p.y)) &&
            (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y + 0.00001) + a.x);
        if (crosses) inside = !inside;
    }
    return inside ? -edge : edge;
}

float4 PSMain(VertexOutput i) : SV_Target
{
    float2 pixel = i.UV * OutputSourceSize.xy;
    float4 color = Background;
    float2 center = ScreenRect.xy + ScreenRect.zw * 0.5;
    float2 local = (pixel - ScreenRect.xy) / max(ScreenRect.zw, 1.0);
    float2 halfSize = ScreenRect.zw * 0.5;
    float d = roundedBox(pixel - center, halfSize, Style.x);

    float shadowD = roundedBox(pixel - (center + float2(0, Style.y * 0.24)), halfSize, Style.x + Style.y * 0.4);
    float shadow = (1.0 - smoothstep(-Style.y, Style.y, shadowD)) * Style.z;
    color.rgb = lerp(color.rgb, float3(0,0,0), shadow);

    if (d <= 0.0)
    {
        float2 sourceUv = Crop.xy + local * Crop.zw;
        color = SourceTexture.Sample(LinearClamp, sourceUv);
    }

    if (d <= 0.0 && Style.w > 0.001 && Cursor.z > 0.5)
    {
        float2 clickPoint = ScreenRect.xy + ((Cursor.xy - Crop.xy) / max(Crop.zw, 0.00001)) * ScreenRect.zw;
        float clickRadius = 5.0 + Style.w * 24.0;
        float ring = 1.0 - smoothstep(1.2, 3.3, abs(length(pixel - clickPoint) / max(Cursor.w, 0.0001) - clickRadius));
        color.rgb = lerp(color.rgb, float3(1.0, 0.92, 0.35), ring * Style.w * 0.82);
    }

    if (d <= 0.0 && Cursor.z > 0.5)
    {
        float2 cursorPoint = ScreenRect.xy + ((Cursor.xy - Crop.xy) / max(Crop.zw, 0.00001)) * ScreenRect.zw;
        float sd = cursorShape((pixel - cursorPoint) / max(Cursor.w, 0.0001));
        float outline = 1.0 - smoothstep(1.0, 2.0, abs(sd));
        float fill = 1.0 - smoothstep(-0.4, 0.6, sd);
        color.rgb = lerp(color.rgb, float3(1,1,1), fill);
        color.rgb = lerp(color.rgb, float3(0.02, 0.025, 0.035), outline);
    }
    color.a = 1.0;
    return color;
}
