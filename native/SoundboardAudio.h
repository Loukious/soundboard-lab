#pragma once
#include <stdint.h>
#ifdef _WIN32
#include <windows.h>
#include <propkeydef.h>
inline constexpr GUID CLSID_SoundboardCapture = {0xae6b213a,0x2854,0x47d6,{0x93,0xd6,0xca,0x9f,0x22,0x88,0x0d,0x3e}};
inline constexpr PROPERTYKEY PKEY_SB_Endpoint = {{0x5f294d10,0xe843,0x43ba,{0x88,0x60,0x32,0x6e,0x9f,0x17,0x31,0x09}},1};
inline constexpr PROPERTYKEY PKEY_SB_OriginalAPO = {PKEY_SB_Endpoint.fmtid,2};
#endif
struct SB_Status { int32_t connected, sampleRate, channels, queuedFrames, activeConsumers, lastError; };
#ifdef SB_BUILD
#define SB_API extern "C" __declspec(dllexport)
#else
#define SB_API extern "C" __declspec(dllimport)
#endif
SB_API int SB_Open(const wchar_t* endpointId);
SB_API void SB_Close();
SB_API int SB_GetStatus(SB_Status* status);
SB_API int SB_Push(const float* stereo48k, int frames);
SB_API void SB_Stop();
SB_API void SB_SetControls(float micGain, float clipGain, int blockVoice, int paused);
SB_API int SB_GetLastError();

// Independent producer handles allow one app to route to several capture endpoints.
SB_API void* SB_CreateClient(const wchar_t* endpointId);
SB_API void SB_DestroyClient(void* client);
SB_API int SB_ClientGetStatus(void* client,SB_Status* status);
SB_API int SB_ClientPush(void* client,const float* stereo48k,int frames);
SB_API void SB_ClientStop(void* client);
SB_API void SB_ClientSetControls(void* client,float micGain,float clipGain,int blockVoice,int paused);
