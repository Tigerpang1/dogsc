cbuffer SceneConstants : register(b0)
{
    float4 OutputSourceSize; // output width, output height, source width, source height
    float4 Crop;             // source crop origin xy, source crop size xy
    float4 ScreenRect;       // output pixel left, top, width, height
    float4 Style;            // corner radius px, shadow radius px, shadow strength, click strength
    float4 Cursor;           // normalized source x/y, visible, cursor scale
    float4 CursorDynamics;   // hotspot rotation radians, click progress (-1 uses legacy strength), click scale, click opacity
    float4 Background;       // rgba
    float4 Style2;           // gradient angle, cursor style, click effect style, frame outer radius px
    float4 BackgroundEnd;    // gradient end rgba
    float4 FrameStyle;       // header height pixels, frame kind, light mode, border width
    float4 DeviceFrame;      // bezel px, outer radius px, rim width px, kind (1 phone, 2 tablet)
    float4 FrameColor;
    float4 Motion;            // rotation degrees, opacity, reserved
    float4 Transform0, Transform1, Transform2;
    float4 Inverse0, Inverse1, Inverse2;
    float4 ClickColor;       // click ring rgba
    float4 Focus;            // source normalized center, size, blur
    float4 FocusStyle;       // shape, angle, softness, dimming
    float4 FocusProgress;
    float4 BlurRect0;
    float4 BlurRect1;
    float4 BlurRect2;
    float4 BlurRect3;
    float4 BlurStyle0;        // strength, kind (0 blur, 1 pixelate), corner radius, opacity
    float4 BlurStyle1;
    float4 BlurStyle2;
    float4 BlurStyle3;
    float4 LayerCount;
    float4 LayerRect0, LayerRect1, LayerRect2, LayerRect3, LayerRect4, LayerRect5, LayerRect6, LayerRect7;
    float4 LayerStyle0, LayerStyle1, LayerStyle2, LayerStyle3, LayerStyle4, LayerStyle5, LayerStyle6, LayerStyle7;
    float4 UnderlayStyle;
    float4 BackgroundPattern; // kind, spacing, strength, reserved
    float4 PatternColor;
    float4 BackgroundAnimation; // enabled, preset, time, reserved
    float4 LayerCrop0, LayerCrop1, LayerCrop2, LayerCrop3, LayerCrop4, LayerCrop5, LayerCrop6, LayerCrop7;
    float4 LayerBorder0, LayerBorder1, LayerBorder2, LayerBorder3, LayerBorder4, LayerBorder5, LayerBorder6, LayerBorder7;
    float4 CursorAssetInfo; // width/height and hotspot in 44px arrow-height units
    float4 LayerShadow0, LayerShadow1, LayerShadow2, LayerShadow3, LayerShadow4, LayerShadow5, LayerShadow6, LayerShadow7; // opacity, blur radius px, offset x/y px
    float4 BackdropTreatment; // screen suppression
    float4 BackdropPass; // 0 normal, 1 horizontal blur, 2 final composite, 3 camera only; screen/camera blur, has camera
    float4 SpotlightControl; // visible spotlight, strongest active dimming
};

Texture2D SourceTexture : register(t0);
Texture2D BackdropTexture : register(t11);
Texture2D CameraCompositeTexture : register(t12);
Texture2D LayerTexture0 : register(t1);
Texture2D LayerTexture1 : register(t2);
Texture2D LayerTexture2 : register(t3);
Texture2D LayerTexture3 : register(t4);
Texture2D LayerTexture4 : register(t5);
Texture2D LayerTexture5 : register(t6);
Texture2D LayerTexture6 : register(t7);
Texture2D LayerTexture7 : register(t8);
Texture2D UnderlayTexture : register(t9);
Texture2D CursorTexture : register(t10);
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

float layerShapeDistance(float2 pixel, float2 center, float2 halfSize, float radius, bool circle)
{
    return circle ? length(pixel - center) - min(halfSize.x, halfSize.y) : roundedBox(pixel - center, halfSize, radius);
}

float2 inverseLayerRotation(float2 pixel, float2 center, float angle)
{
    float2 p = pixel - center;
    return center + float2(cos(angle) * p.x + sin(angle) * p.y, -sin(angle) * p.x + cos(angle) * p.y);
}

float4 blendLayer(float4 dst, Texture2D tex, float2 canvasUv, float2 contentPixel, float4 rect, float4 style, float4 crop, float4 border, float4 shadow)
{
    if (rect.z <= 0.0 || rect.w <= 0.0 || (style.x <= 0.001 && shadow.x <= 0.001)) return dst;
    float2 canvasPixel = canvasUv * OutputSourceSize.xy;
    float2 center = (rect.xy + rect.zw * 0.5) * OutputSourceSize.xy;
    float2 halfSize = rect.zw * OutputSourceSize.xy * 0.5;
    float radius = style.y * min(OutputSourceSize.x, OutputSourceSize.y);
    bool circle = style.y < -0.5;
    float angle = style.z * 0.01745329252;
    float2 layerPixel = inverseLayerRotation(canvasPixel, center, angle);
    if (shadow.x > 0.001 && style.x > 0.001)
    {
        // Layer shadows are composited first. Positive Y follows the canvas top-left origin.
        float shadowDistance = layerShapeDistance(inverseLayerRotation(canvasPixel - shadow.zw, center, angle), center, halfSize, radius, circle);
        float shadowCoverage = shadow.y <= 0.001
            ? (shadowDistance <= 0.0 ? 1.0 : 0.0)
            : 1.0 - smoothstep(-shadow.y, shadow.y, shadowDistance);
        float shadowAlpha = saturate(shadow.x * style.x * (style.w > 1.5 ? Motion.y : 1.0) * shadowCoverage);
        dst.rgb = lerp(dst.rgb, float3(0.0, 0.0, 0.0), shadowAlpha);
        dst.a = shadowAlpha + dst.a * (1.0 - shadowAlpha);
    }
    if (style.x <= 0.001) return dst;
    float shapeDistance = layerShapeDistance(layerPixel, center, halfSize, radius, circle);
    if (style.w < 1.5 && shapeDistance > 0.0)
    {
        // Sticker borders surround the card; camera borders retain their inset semantics.
        if (border.x < 0.0 && shapeDistance <= -border.x)
            return float4(lerp(dst.rgb, border.yzw, style.x), dst.a + style.x * (1.0 - dst.a));
        return dst;
    }
    float2 uv;
    if (style.w > 1.5)
    {
        float2 screenCenter = ScreenRect.xy + ScreenRect.zw * 0.5;
        float2 cardTarget = (contentPixel - screenCenter) / max(ScreenRect.zw * 0.5, 1.0);
        float3 cardProjection = mul(float3(cardTarget, 1.0), float3x3(Inverse0.xyz, Inverse1.xyz, Inverse2.xyz));
        float2 cardLocal = cardProjection.xy / max(abs(cardProjection.z), 0.00001) * sign(cardProjection.z) * 0.5 + 0.5;
        float2 screenOrigin = (screenCenter - ScreenRect.zw * 0.5) / OutputSourceSize.xy;
        float2 layerOrigin = (rect.xy - screenOrigin) * OutputSourceSize.xy / ScreenRect.zw;
        float2 layerSize = rect.zw * OutputSourceSize.xy / ScreenRect.zw;
        if (any(cardLocal < layerOrigin) || any(cardLocal > layerOrigin + layerSize)) return dst;
        uv = (cardLocal - layerOrigin) / max(layerSize, 0.00001);
    }
    else uv = (layerPixel / OutputSourceSize.xy - rect.xy) / rect.zw;
    if (fmod(style.w, 2.0) > 0.5) uv.x = 1.0 - uv.x;
    if (any(uv < 0.0) || any(uv > 1.0)) return dst;
    uv = crop.xy + uv * crop.zw;
    float blur = saturate(frac(style.w) * 4.0);
    float4 src;
    if (blur > 0.001)
    {
        uint texWidth, texHeight;
        tex.GetDimensions(texWidth, texHeight);
        float2 stepUv = (blur * 8.0) / float2(max(texWidth, 1), max(texHeight, 1));
        src = tex.Sample(LinearClamp, uv) * 4.0;
        src += tex.Sample(LinearClamp, uv + float2(stepUv.x, 0)) * 2.0;
        src += tex.Sample(LinearClamp, uv - float2(stepUv.x, 0)) * 2.0;
        src += tex.Sample(LinearClamp, uv + float2(0, stepUv.y)) * 2.0;
        src += tex.Sample(LinearClamp, uv - float2(0, stepUv.y)) * 2.0;
        src += tex.Sample(LinearClamp, uv + stepUv) * 1.0;
        src += tex.Sample(LinearClamp, uv - stepUv) * 1.0;
        src += tex.Sample(LinearClamp, uv + float2(stepUv.x, -stepUv.y)) * 1.0;
        src += tex.Sample(LinearClamp, uv + float2(-stepUv.x, stepUv.y)) * 1.0;
        src *= 1.0 / 16.0;
    }
    else src = tex.Sample(LinearClamp, uv);
    src.a *= style.x * (style.w > 1.5 ? Motion.y : 1.0);
    if (style.w > 1.5 && SpotlightControl.x > 0.5) src.rgb *= 1 - SpotlightControl.y;
    if (border.x > 0.0 && -shapeDistance < border.x)
        src.rgb = lerp(border.yzw, src.rgb, saturate(-shapeDistance / border.x));
    return float4(lerp(dst.rgb, src.rgb, src.a), dst.a + src.a * (1.0 - dst.a));
}

float segmentDistance(float2 p, float2 a, float2 b)
{
    float2 ab = b - a;
    float h = saturate(dot(p - a, ab) / max(dot(ab, ab), 0.0001));
    return length(p - (a + h * ab));
}

float2 rotateAround(float2 coordinate, float2 center, float angle)
{
    float2 p = coordinate - center;
    return center + float2(cos(angle) * p.x - sin(angle) * p.y, sin(angle) * p.x + cos(angle) * p.y);
}

float3 transformPoint(float2 coordinate, float4 row0, float4 row1, float4 row2)
{
    return mul(float3(coordinate, 1.0), float3x3(row0.xyz, row1.xyz, row2.xyz));
}

float regionCoverage(float2 uv, float2 sourceUv, float4 rect, float4 style)
{
    if (rect.z <= 0.0 || rect.w <= 0.0 || style.w <= 0.001) return 0;
    bool sourceSpace = style.y >= 3.5;
    float2 size = sourceSpace ? OutputSourceSize.zw : OutputSourceSize.xy;
    float2 pixel = (sourceSpace ? sourceUv : uv) * size;
    float2 center = (rect.xy + rect.zw * 0.5) * size;
    float2 halfSize = rect.zw * size * 0.5;
    float radius = sourceSpace ? style.z * min(rect.z * size.x, rect.w * size.y) : style.z * min(size.x, size.y);
    float distance = roundedBox(pixel - center, halfSize, radius);
    return 1.0 - smoothstep(-1.0, 1.0, distance);
}

float4 applyRegionEffect(float4 inputColor, float2 uv, float2 sourceUv, float4 rect, float4 style)
{
    float kind = fmod(style.y, 4.0);
    if (kind > 1.5) return inputColor;
    float mask = regionCoverage(uv, sourceUv, rect, style) * saturate(style.w);
    if (mask <= 0.001) return inputColor;
    float4 affected;
    if (kind > 0.5)
    {
        float blockSize = 2.0 + style.x * 50.0;
        float2 pixel = uv * OutputSourceSize.xy;
        float2 snappedPixel = (floor(pixel / blockSize) + 0.5) * blockSize;
        float2 snappedLocal = (snappedPixel - ScreenRect.xy) / max(ScreenRect.zw, 1.0);
        affected = SourceTexture.Sample(LinearClamp, Crop.xy + snappedLocal * Crop.zw);
    }
    else
    {
        float radius = (4.0 + style.x * 44.0) * style.w;
        affected = 0; float total = 0;
        [unroll] for (int y=-2;y<=2;y++) [unroll] for (int x=-2;x<=2;x++)
        {
            float weight = exp(-0.5 * (x*x + y*y));
            affected += SourceTexture.Sample(LinearClamp, sourceUv + float2(x,y) * radius / OutputSourceSize.zw) * weight;
            total += weight;
        }
        affected /= total;
    }
    return lerp(inputColor, affected, mask * saturate(style.x));
}

float cursorShape(float2 p)
{
    const float2 v[7] = {
        float2(0,0), float2(0,36), float2(9.4,27.4), float2(17,44),
        float2(23,41.4), float2(16.4,25), float2(30,24.4)
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

float circleCoverage(float distance, float radius)
{
    return 1.0 - smoothstep(max(radius - 0.8, 0.0), radius + 0.8, distance);
}

float ringCoverage(float distance, float radius, float lineWidth)
{
    float outer = circleCoverage(distance, radius);
    float inner = circleCoverage(distance, max(radius - lineWidth, 0.0));
    return saturate(outer - inner);
}

float4 gaussianComposite(Texture2D tex, float2 uv, float radius, float2 direction)
{
    if (radius <= 0.01) return tex.Sample(LinearClamp, uv);
    float4 sum = 0; float weightSum = 0;
    // A symmetric separable Gaussian spans three standard deviations. Intermediates
    // store premultiplied camera colour so transparent edges blur without black halos.
    [unroll] for (int tap = -12; tap <= 12; tap++)
    {
        float distance = tap * 0.25;
        float weight = exp(-0.5 * distance * distance);
        sum += tex.Sample(LinearClamp, uv + direction * (distance * radius) / OutputSourceSize.xy) * weight;
        weightSum += weight;
    }
    return sum / weightSum;
}

float4 compositeLayers(float4 color, float2 uv, float2 contentPixel)
{
    if (LayerCount.x > 0.0 && LayerCount.y < 1.0) color = blendLayer(color, LayerTexture0, uv, contentPixel, LayerRect0, LayerStyle0, LayerCrop0, LayerBorder0, LayerShadow0);
    if (LayerCount.x > 1.0 && LayerCount.y < 2.0) color = blendLayer(color, LayerTexture1, uv, contentPixel, LayerRect1, LayerStyle1, LayerCrop1, LayerBorder1, LayerShadow1);
    if (LayerCount.x > 2.0 && LayerCount.y < 3.0) color = blendLayer(color, LayerTexture2, uv, contentPixel, LayerRect2, LayerStyle2, LayerCrop2, LayerBorder2, LayerShadow2);
    if (LayerCount.x > 3.0 && LayerCount.y < 4.0) color = blendLayer(color, LayerTexture3, uv, contentPixel, LayerRect3, LayerStyle3, LayerCrop3, LayerBorder3, LayerShadow3);
    if (LayerCount.x > 4.0 && LayerCount.y < 5.0) color = blendLayer(color, LayerTexture4, uv, contentPixel, LayerRect4, LayerStyle4, LayerCrop4, LayerBorder4, LayerShadow4);
    if (LayerCount.x > 5.0 && LayerCount.y < 6.0) color = blendLayer(color, LayerTexture5, uv, contentPixel, LayerRect5, LayerStyle5, LayerCrop5, LayerBorder5, LayerShadow5);
    if (LayerCount.x > 6.0 && LayerCount.y < 7.0) color = blendLayer(color, LayerTexture6, uv, contentPixel, LayerRect6, LayerStyle6, LayerCrop6, LayerBorder6, LayerShadow6);
    if (LayerCount.x > 7.0 && LayerCount.y < 8.0) color = blendLayer(color, LayerTexture7, uv, contentPixel, LayerRect7, LayerStyle7, LayerCrop7, LayerBorder7, LayerShadow7);
    return color;
}

float4 PSMain(VertexOutput i) : SV_Target
{
    if (BackdropPass.x > 0.5 && BackdropPass.x < 1.5)
        return gaussianComposite(BackdropTexture, i.UV, BackdropPass.y, float2(1, 0));
    if (BackdropPass.x > 2.5)
        return compositeLayers(float4(0,0,0,0), i.UV, i.UV * OutputSourceSize.xy);
    if (BackdropPass.x > 1.5)
    {
        float4 base = gaussianComposite(BackdropTexture, i.UV, BackdropPass.y, float2(0, 1));
        if (BackdropPass.w > 0.5)
        {
            float4 camera = gaussianComposite(CameraCompositeTexture, i.UV, BackdropPass.z, float2(0, 1));
            base.rgb = camera.rgb + base.rgb * (1 - camera.a);
        }
        return float4(compositeLayers(base, i.UV, i.UV * OutputSourceSize.xy).rgb, 1);
    }
    float2 pixel = i.UV * OutputSourceSize.xy;
    float2 center = ScreenRect.xy + ScreenRect.zw * 0.5;
    float rotation = Motion.x * 0.01745329252;
    float2 contentPixel = rotateAround(pixel, center, -rotation);
    float2 gradientDirection = float2(cos(Style2.x), sin(Style2.x));
    float gradientPosition = saturate(dot(i.UV - 0.5, gradientDirection) + 0.5);
    float4 color;
    if (BackgroundAnimation.x > 0.5)
    {
        float phase = BackgroundAnimation.z;
        float wave = saturate(i.UV.y + sin(i.UV.x * 5.0 + phase * 0.7) * 0.16);
        float4 a, b, c;
        if (BackgroundAnimation.y < 0.5)
        { a = float4(0.035,0.055,0.17,1); b = float4(0.08,0.53,0.43,1); c = float4(0.47,0.13,0.57,1); }
        else if (BackgroundAnimation.y < 1.5)
        { a = float4(0.025,0.10,0.25,1); b = float4(0.02,0.48,0.56,1); c = float4(0.18,0.39,0.78,1); }
        else
        { a = float4(0.17,0.055,0.32,1); b = float4(0.78,0.20,0.36,1); c = float4(0.98,0.55,0.12,1); }
        float t = saturate(wave + sin(phase * 0.35) * 0.08);
        color = t < 0.5 ? lerp(a, b, smoothstep(0, 0.5, t)) : lerp(b, c, smoothstep(0.5, 1, t));
        color.rgb += 0.025 * sin(float3(phase, phase + 2.1, phase + 4.2));
    }
    else color = lerp(Background, BackgroundEnd, gradientPosition);
    if (BackgroundPattern.x > 0.5 && BackgroundPattern.z > 0.001)
    {
        float spacing = max(BackgroundPattern.y, 2.0);
        float2 gridDistance = abs(frac(pixel / spacing + 0.5) - 0.5) * spacing;
        float patternMask;
        if (BackgroundPattern.x < 1.5) patternMask = 1.0 - smoothstep(0.6, 1.6, min(gridDistance.x, gridDistance.y));
        else patternMask = 1.0 - smoothstep(1.0, 2.2, length(gridDistance - spacing * 0.5));
        color.rgb = lerp(color.rgb, PatternColor.rgb, patternMask * BackgroundPattern.z);
    }
    if (UnderlayStyle.y > 0.5)
    {
        uint imageWidth, imageHeight;
        UnderlayTexture.GetDimensions(imageWidth, imageHeight);
        float imageAspect = (float)imageWidth / max((float)imageHeight, 1.0);
        float canvasAspect = OutputSourceSize.x / max(OutputSourceSize.y, 1.0);
        float2 imageUv = i.UV;
        bool imageVisible = true;
        if (UnderlayStyle.z < 0.5)
        {
            if (imageAspect > canvasAspect) imageUv.x = (imageUv.x - 0.5) * (canvasAspect / imageAspect) + 0.5;
            else imageUv.y = (imageUv.y - 0.5) * (imageAspect / canvasAspect) + 0.5;
        }
        else if (UnderlayStyle.z < 1.5)
        {
            if (imageAspect > canvasAspect)
            {
                float displayedHeight = canvasAspect / imageAspect;
                imageVisible = imageUv.y >= (1.0 - displayedHeight) * 0.5 && imageUv.y <= (1.0 + displayedHeight) * 0.5;
                imageUv.y = (imageUv.y - (1.0 - displayedHeight) * 0.5) / displayedHeight;
            }
            else
            {
                float displayedWidth = imageAspect / canvasAspect;
                imageVisible = imageUv.x >= (1.0 - displayedWidth) * 0.5 && imageUv.x <= (1.0 + displayedWidth) * 0.5;
                imageUv.x = (imageUv.x - (1.0 - displayedWidth) * 0.5) / displayedWidth;
            }
        }
        float4 under = imageVisible ? UnderlayTexture.Sample(LinearClamp, imageUv) : float4(0,0,0,0);
        if (UnderlayStyle.w > 0.001 && imageVisible)
        {
            float2 texel = 1.0 / float2(imageWidth, imageHeight) * UnderlayStyle.w * 0.015;
            float4 blur = under * 0.2;
            blur += UnderlayTexture.Sample(LinearClamp, imageUv + float2(texel.x, 0)) * 0.1;
            blur += UnderlayTexture.Sample(LinearClamp, imageUv - float2(texel.x, 0)) * 0.1;
            blur += UnderlayTexture.Sample(LinearClamp, imageUv + float2(0, texel.y)) * 0.1;
            blur += UnderlayTexture.Sample(LinearClamp, imageUv - float2(0, texel.y)) * 0.1;
            blur += UnderlayTexture.Sample(LinearClamp, imageUv + texel) * 0.1;
            blur += UnderlayTexture.Sample(LinearClamp, imageUv - texel) * 0.1;
            blur += UnderlayTexture.Sample(LinearClamp, imageUv + float2(texel.x, -texel.y)) * 0.1;
            blur += UnderlayTexture.Sample(LinearClamp, imageUv + float2(-texel.x, texel.y)) * 0.1;
            under = blur;
        }
        under.a *= UnderlayStyle.x;
        color = float4(lerp(color.rgb, under.rgb, under.a), 1.0);
    }
    float4 authoredBackground = color;
    float2 target = (contentPixel - center) / max(ScreenRect.zw * 0.5, 1.0);
    float3 sourceProjection = transformPoint(target, Inverse0, Inverse1, Inverse2);
    float2 sourcePosition = sourceProjection.xy / max(abs(sourceProjection.z), 0.00001) * sign(sourceProjection.z);
    float2 local = sourcePosition * 0.5 + 0.5;
    float2 halfSize = ScreenRect.zw * 0.5;
    float d = roundedBox(sourcePosition * halfSize, halfSize, Style.x);

    float header = FrameStyle.x;
    float2 cardTarget = (contentPixel - center) / max(ScreenRect.zw * 0.5, 1.0);
    float3 cardProjection = transformPoint(cardTarget, Inverse0, Inverse1, Inverse2);
    float2 cardPosition = cardProjection.xy / max(abs(cardProjection.z), 0.00001) * sign(cardProjection.z);
    float2 cardHalf = ScreenRect.zw * 0.5;
    float2 cardBounds = cardHalf + float2(0, header * 0.5);
    float2 cardDistancePoint = cardPosition * cardHalf + float2(0, header * 0.5);
    bool desktopChrome = FrameStyle.y > 0.5 && FrameStyle.y < 4.5;
    float cardRadius = desktopChrome ? Style2.w : Style.x + FrameStyle.w;
    float outerRadius = desktopChrome ? Style2.w : Style.x;
    float cardDistance = roundedBox(cardDistancePoint, cardBounds, cardRadius);

    if (DeviceFrame.w > 0.5)
    {
        float2 devicePoint = cardPosition * cardHalf;
        float2 deviceBounds = cardHalf + DeviceFrame.xx;
        float deviceDistance = roundedBox(devicePoint, deviceBounds, DeviceFrame.y);
        if (deviceDistance <= 0.0)
        {
            float vertical = saturate((devicePoint.y + deviceBounds.y) / max(deviceBounds.y * 2.0, 1.0));
            float3 metal = lerp(float3(0.16, 0.17, 0.19), float3(0.39, 0.41, 0.44), 1.0 - abs(vertical * 2.0 - 1.0) * 0.45);
            float innerDistance = roundedBox(devicePoint, cardHalf, Style.x);
            float3 bezel = lerp(float3(0.025, 0.028, 0.034), float3(0.075, 0.082, 0.092), saturate(vertical * 0.8 + 0.1));
            float rimDistance = roundedBox(devicePoint, deviceBounds - DeviceFrame.zz, max(1.0, DeviceFrame.y - DeviceFrame.z));
            color.rgb = metal;
            color.rgb = lerp(color.rgb, bezel, 1.0 - smoothstep(-0.8, 0.8, innerDistance));
            color.rgb = lerp(color.rgb, float3(0.52, 0.54, 0.57), (1.0 - smoothstep(-0.8, 0.8, rimDistance)) * 0.5);
            color.a = 1.0;
        }
    }

    float2 shadowTarget = cardTarget;
    float3 shadowProjection = transformPoint(shadowTarget, Inverse0, Inverse1, Inverse2);
    float2 shadowPosition = shadowProjection.xy / max(abs(shadowProjection.z), 0.00001) * sign(shadowProjection.z);
    float2 shadowBounds = cardBounds + (DeviceFrame.w > 0.5 ? DeviceFrame.xx : float2(0, 0));
    float shadowRadius = DeviceFrame.w > 0.5 ? DeviceFrame.y : outerRadius;
    float2 shadowPoint = shadowPosition * cardHalf + float2(0, header * 0.5 - Style.y * 0.48);
    float shadowD = roundedBox(shadowPoint, shadowBounds, shadowRadius + Style.y * 0.4);
    float shadow = (1.0 - smoothstep(-Style.y, Style.y, shadowD)) * Style.z;
    color.rgb = lerp(color.rgb, float3(0,0,0), shadow);

    if (FrameStyle.y > 0.5 && cardDistance <= 0.0)
    {
        color = lerp(color, FrameColor, Motion.y);
        if (FrameStyle.y > 0.5 && FrameStyle.y < 4.5)
        {
            float2 chromeCenter = float2(ScreenRect.x + header * 0.42, ScreenRect.y - header * 0.5);
            float radius = header * 0.14;
            float3 colors[3] = { float3(0.95,0.32,0.28), float3(0.96,0.74,0.25), float3(0.24,0.78,0.39) };
            [unroll] for (int dot = 0; dot < 3; dot++)
            {
                float2 dotCanvas = chromeCenter + float2(header * 0.45 * dot, 0);
                float2 dotTarget = (dotCanvas - center) / max(halfSize, 1.0);
                float3 dotProjection = transformPoint(dotTarget, Transform0, Transform1, Transform2);
                float dotScale = 1.0 / max(abs(dotProjection.z), 0.1);
                float2 dotPoint = rotateAround(center + dotProjection.xy * dotScale * halfSize, center, rotation);
                // dotPoint is already projected and rotated into output pixels;
                // comparing it to contentPixel (which is inverse-rotated) applies
                // the Z rotation twice and can clip the toolbar dots at the edge.
                float distance = length(pixel - dotPoint);
                float coverage = 1.0 - smoothstep(radius * dotScale - 0.7, radius * dotScale + 0.7, distance);
                color.rgb = lerp(color.rgb, colors[dot], coverage);
            }
        }
    }

    bool contentVisible = d <= 0.0 && cardDistance <= 0.0;
    if (contentVisible)
    {
        float2 sourceUv = Crop.xy + local * Crop.zw;
        color = lerp(color, SourceTexture.Sample(LinearClamp, sourceUv), Motion.y);
        if (FocusProgress.x > 0.001 && Focus.z > 0.001)
        {
            float2 delta = sourceUv - Focus.xy;
            float distanceToFocus;
            if (FocusStyle.x < 0.5)
                distanceToFocus = length(delta * float2(OutputSourceSize.z / OutputSourceSize.w, 1));
            else
            {
                float angle = FocusStyle.y * 0.01745329252;
                distanceToFocus = abs(dot(delta, float2(cos(angle), sin(angle))));
            }
            float edge = Focus.z * 0.5;
            float blurWeight = smoothstep(edge - max(FocusStyle.z, 0.001), edge + max(FocusStyle.z, 0.001), distanceToFocus) * FocusProgress.x;
            float2 texel = 1.0 / OutputSourceSize.zw * Focus.w * 0.018;
            float4 blurred = color * 0.2;
            blurred += SourceTexture.Sample(LinearClamp, sourceUv + float2(texel.x, 0)) * 0.1;
            blurred += SourceTexture.Sample(LinearClamp, sourceUv - float2(texel.x, 0)) * 0.1;
            blurred += SourceTexture.Sample(LinearClamp, sourceUv + float2(0, texel.y)) * 0.1;
            blurred += SourceTexture.Sample(LinearClamp, sourceUv - float2(0, texel.y)) * 0.1;
            blurred += SourceTexture.Sample(LinearClamp, sourceUv + texel) * 0.1;
            blurred += SourceTexture.Sample(LinearClamp, sourceUv - texel) * 0.1;
            blurred += SourceTexture.Sample(LinearClamp, sourceUv + float2(texel.x, -texel.y)) * 0.1;
            blurred += SourceTexture.Sample(LinearClamp, sourceUv + float2(-texel.x, texel.y)) * 0.1;
            color = lerp(color, blurred, blurWeight);
            color.rgb *= 1.0 - FocusStyle.w * blurWeight;
        }
        color = applyRegionEffect(color, i.UV, sourceUv, BlurRect0, BlurStyle0);
        color = applyRegionEffect(color, i.UV, sourceUv, BlurRect1, BlurStyle1);
        color = applyRegionEffect(color, i.UV, sourceUv, BlurRect2, BlurStyle2);
        color = applyRegionEffect(color, i.UV, sourceUv, BlurRect3, BlurStyle3);
    }

    if (contentVisible && Cursor.z > 0.5 && (Style2.z < 1.5 || Style2.z > 2.5))
    {
        float2 cursorLocal = (Cursor.xy - Crop.xy) / max(Crop.zw, 0.00001);
        float3 clickProjection = transformPoint(cursorLocal * 2.0 - 1.0, Transform0, Transform1, Transform2);
        float2 clickPoint = rotateAround(center + (clickProjection.xy / max(abs(clickProjection.z), 0.00001) * sign(clickProjection.z)) * halfSize, center, rotation);
        float distance = length(pixel - clickPoint) / max(Cursor.w, 0.0001);
        if (CursorDynamics.y < 0.0)
        {
            // Legacy FrameScene callers may only supply the original half-sine strength.
            if (Style.w > 0.001)
            {
                float clickRadius = 5.0 + Style.w * 24.0;
                float legacyRing = 1.0 - smoothstep(1.2, 3.3, abs(distance - clickRadius));
                if (Style2.z > 0.5) legacyRing = 1.0 - smoothstep(1.0, 2.5, abs(distance - 18.0));
                color.rgb = lerp(color.rgb, ClickColor.rgb, legacyRing * Style.w * 0.9);
            }
        }
        else if (Style2.z > 1.5 && Style2.z < 2.5)
        {
            // Appended enum value 2 is None.
        }
        else if (CursorDynamics.y < 1.0)
        {
            float p = saturate(CursorDynamics.y);
            float baseHeight = 44.0 * Cursor.w * CursorDynamics.z;
            float opacity = CursorDynamics.w * ClickColor.a;
            float effect = 0.0;
            if (Style2.z < 0.5) // Pulse: elastic double ring.
            {
                float ease = 1.0 - pow(1.0 - p, 2.5);
                float outerRadius = baseHeight * (0.50 + 1.25 * ease) * 0.5;
                float lineWidth = max(baseHeight * 0.065, 1.2) / max(Cursor.w, 0.0001);
                effect += ringCoverage(distance, outerRadius, lineWidth) * 0.90 * (1.0 - p);
                if (p <= 0.55)
                {
                    float innerP = p / 0.55;
                    float innerRadius = baseHeight * (0.25 + 0.45 * sin(innerP * 3.14159265)) * 0.5;
                    effect += circleCoverage(distance, innerRadius) * 0.55 * (1.0 - innerP);
                }
                effect += pow(saturate(1.0 - distance / max(outerRadius, 0.001)), 3.0) * 0.10 * (1.0 - p);
            }
            else if (Style2.z < 1.5) // Ring keeps its legacy name and follows upstream soft glow geometry.
            {
                float radius, alpha;
                if (p < 0.18)
                {
                    float bloom = p / 0.18;
                    radius = baseHeight * (0.75 + 0.50 * (1.0 - pow(1.0 - bloom, 2.0))) * 0.5;
                    alpha = 0.78 * bloom;
                }
                else
                {
                    float fade = (p - 0.18) / 0.82;
                    radius = baseHeight * (1.25 + 0.55 * fade) * 0.5;
                    alpha = 0.78 * (1.0 - pow(fade, 1.4));
                }
                float outer = pow(saturate(1.0 - distance / max(radius, 0.001)), 1.7);
                float inner = pow(saturate(1.0 - distance / max(radius * 0.5, 0.001)), 1.4);
                effect = alpha * (outer * 0.65 + inner * 0.35);
            }
            else if (Style2.z < 3.5) // Ripple: expanding ring and shrinking center dot.
            {
                float ease = 1.0 - pow(1.0 - p, 3.0);
                float radius = baseHeight * (0.45 + 1.45 * ease) * 0.5;
                float lineWidth = max(baseHeight * (0.075 - 0.04 * p), 1.2) / max(Cursor.w, 0.0001);
                effect = ringCoverage(distance, radius, lineWidth) * 0.95 * (1.0 - pow(p, 1.3));
                if (p < 0.35)
                {
                    float dotProgress = p / 0.35;
                    float dotRadius = baseHeight * 0.28 * (1.0 - dotProgress) * 0.5;
                    effect += circleCoverage(distance, dotRadius) * 0.85 * (1.0 - dotProgress);
                }
                effect += pow(saturate(1.0 - distance / max(radius, 0.001)), 3.0) * 0.10 * (1.0 - p);
            }
            else // Burst: shrinking filled center, growing outline and four short accent rays.
            {
                float centerRadius = max(baseHeight * (0.35 - 0.18 * p), 1.0) * 0.5;
                effect += circleCoverage(distance, centerRadius) * 0.95 * (1.0 - p * p);
                float outerRadius = baseHeight * (0.45 + 0.55 * p) * 0.5;
                float lineWidth = max(baseHeight * 0.035, 1.0) / max(Cursor.w, 0.0001);
                effect += ringCoverage(distance, outerRadius, lineWidth) * 0.55 * (1.0 - p);
                float burstEase = 1.0 - pow(1.0 - p, 2.0);
                float accentOffset = baseHeight * (0.28 + 0.38 * burstEase) / max(Cursor.w, 0.0001);
                float accentRadius = max(baseHeight * 0.075 * (1.0 - p), 1.2) / max(Cursor.w, 0.0001) * 0.5;
                float2 radial = (pixel - clickPoint) / max(Cursor.w, 0.0001);
                float rayDistance = min(min(length(radial - float2(accentOffset, 0)), length(radial + float2(accentOffset, 0))),
                    min(length(radial - float2(0, accentOffset)), length(radial + float2(0, accentOffset))));
                effect += circleCoverage(rayDistance, accentRadius) * 0.85 * (1.0 - p);
            }
            color.rgb = lerp(color.rgb, ClickColor.rgb, saturate(effect * opacity));
        }
    }

    if (contentVisible && Cursor.z > 0.5)
    {
        float2 cursorLocal = (Cursor.xy - Crop.xy) / max(Crop.zw, 0.00001);
        float3 cursorProjection = transformPoint(cursorLocal * 2.0 - 1.0, Transform0, Transform1, Transform2);
        float2 cursorPoint = rotateAround(center + (cursorProjection.xy / max(abs(cursorProjection.z), 0.00001) * sign(cursorProjection.z)) * halfSize, center, rotation);
        float2 cursorLocalPixel = rotateAround(pixel, cursorPoint, -CursorDynamics.x) - cursorPoint;
        float2 cp = cursorLocalPixel / max(Cursor.w, 0.0001);
        if (Style2.y > 2.5)
        {
            float2 assetSize = CursorAssetInfo.xy;
            float2 assetTopLeft = -CursorAssetInfo.zw;
            float2 assetUv = (cp - assetTopLeft) / max(assetSize, float2(0.0001, 0.0001));
            if (all(assetUv >= 0.0) && all(assetUv <= 1.0))
            {
                float4 cursorColor = CursorTexture.Sample(LinearClamp, assetUv);
                color.rgb = lerp(color.rgb, cursorColor.rgb, cursorColor.a);
            }
        }
        else if (Style2.y > 1.5)
        {
            float ring = 1.0 - smoothstep(1.0, 2.3, abs(length(cp) - 8.0));
            color.rgb = lerp(color.rgb, float3(1,1,1), ring);
            color.rgb = lerp(color.rgb, float3(0.02, 0.025, 0.035), ring * 0.22);
        }
        else if (Style2.y > 0.5)
        {
            float dot = 1.0 - smoothstep(7.0, 9.0, length(cp));
            color.rgb = lerp(color.rgb, float3(1,1,1), dot);
            color.rgb = lerp(color.rgb, float3(0.02, 0.025, 0.035), dot * 0.25);
        }
        else
        {
            float sd = cursorShape(cp);
            float outline = 1.0 - smoothstep(1.0, 2.0, abs(sd));
            float fill = 1.0 - smoothstep(-0.4, 0.6, sd);
            color.rgb = lerp(color.rgb, float3(1,1,1), fill);
            color.rgb = lerp(color.rgb, float3(0.02, 0.025, 0.035), outline);
        }
    }
    if (SpotlightControl.x > 0.5)
    {
        float2 regionSourceUv = Crop.xy + local * Crop.zw;
        float mask = 0;
        if (contentVisible)
        {
            if (fmod(BlurStyle0.y,4.0) > 1.5) mask = max(mask,regionCoverage(i.UV,regionSourceUv,BlurRect0,BlurStyle0));
            if (fmod(BlurStyle1.y,4.0) > 1.5) mask = max(mask,regionCoverage(i.UV,regionSourceUv,BlurRect1,BlurStyle1));
            if (fmod(BlurStyle2.y,4.0) > 1.5) mask = max(mask,regionCoverage(i.UV,regionSourceUv,BlurRect2,BlurStyle2));
            if (fmod(BlurStyle3.y,4.0) > 1.5) mask = max(mask,regionCoverage(i.UV,regionSourceUv,BlurRect3,BlurStyle3));
        }
        color.rgb *= 1.0 - SpotlightControl.y * (1.0 - mask);
    }
    color = lerp(color, authoredBackground, BackdropTreatment.x);
    color = compositeLayers(color, i.UV, contentPixel);
    color.a = 1.0;
    return color;
}
