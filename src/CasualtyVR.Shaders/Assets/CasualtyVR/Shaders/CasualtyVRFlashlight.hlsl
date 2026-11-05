#ifndef CASUALTYVR_FLASHLIGHT_INCLUDED
#define CASUALTYVR_FLASHLIGHT_INCLUDED

float4 _CvrFlashPosition;
float4 _CvrFlashDirection;
float4 _CvrFlashColor;
float4 _CvrFlashParams;

half3 CvrApplyFlashlight(half3 baseColor, float3 positionWS)
{
    float range = _CvrFlashParams.x;
    if (range <= 0.001 || _CvrFlashParams.w <= 0.001)
        return baseColor;

    float3 fromLight = positionWS - _CvrFlashPosition.xyz;
    float distance = length(fromLight);
    if (distance <= 0.001 || distance >= range)
        return baseColor;

    float cone = dot(fromLight / distance, normalize(_CvrFlashDirection.xyz));
    float angular = smoothstep(_CvrFlashParams.z, _CvrFlashParams.y, cone);
    float rangeFade = 1.0 - smoothstep(range * 0.72, range, distance);
    float attenuation = rangeFade / (1.0 + distance * distance * 0.015);
    float strength = angular * attenuation * _CvrFlashParams.w;
    return baseColor * (1.0 + _CvrFlashColor.rgb * strength);
}

#endif
