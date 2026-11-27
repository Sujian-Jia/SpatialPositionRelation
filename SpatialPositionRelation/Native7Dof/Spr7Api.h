#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif
#ifdef _WIN32
# define SPR7_API __declspec(dllexport)
#else
# define SPR7_API
#endif

// ABI v1. All scalars are IEEE binary64, quaternions WXYZ, units SI.
// Pose is XYZ followed by WXYZ. Struct is contiguous doubles, no padding.
typedef struct Spr7Frame {
    double time, mode;
    double q[39], u[37];
    double stationPort[7], visitorPort[7];
    double stationPortVelocity[3], visitorPortVelocity[3];
    double jointFrames[7][7], tool[7], grapple[3];
    double portDistance, centerDistance, axialGap, toolDistance;
    // First tool contact, capture, port contact, first controller lock request, lock, release.
    // -1 until observed. Timestamps are measured by the native scenario.
    double events[6];
    double lockRequestSetting;
} Spr7Frame;

SPR7_API int32_t Spr7_Abi(void);
SPR7_API void* Spr7_Create(void);
SPR7_API void Spr7_Destroy(void* session);
// Advance up to maxSteps times at the native fixed step. Stops at the next
// 20-step sampling boundary. Returns actual steps, 0 at EOF, or -1 on failure.
SPR7_API int32_t Spr7_Advance(void* session, int32_t maxSteps);
// Read only at step 0 or a sample boundary. Returns 0 on success.
SPR7_API int32_t Spr7_Read(void* session, Spr7Frame* frame, int32_t frameBytes);
#ifdef __cplusplus
}
#endif

