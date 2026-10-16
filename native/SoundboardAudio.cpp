#define SB_BUILD
#define NOMINMAX
#include "SoundboardAudio.h"
#include <baseaudioprocessingobject.h>
#include <mmdeviceapi.h>
#include <sddl.h>
#include <cmath>
#include <cstring>
#include <algorithm>
#include <mutex>
#include <string>
#include <vector>
#include <new>
#include <cstddef>

namespace {
constexpr LONG Magic = 0x53424c31;
constexpr uint64_t Capacity = 192000; // Four seconds of stereo 48 kHz.
constexpr int Slots = 32;
constexpr uint64_t StaleMs = 2000;
struct alignas(64) Consumer {
    volatile LONG owner;
    LONG rate, channels;
    LONG reservedCursorAlignment;
    alignas(8) volatile LONG64 cursor;
    volatile LONG64 heartbeat;
    BYTE reservedCacheLine[32];
};
struct Frame {
    volatile LONG64 sequence;
    volatile LONG left, right;
};
struct Shared {
    volatile LONG magic, version;
    alignas(8) volatile LONG64 write, stopFloor;
    volatile LONG micGain, clipGain, blockVoice, paused;
    volatile LONG producer;
    BYTE reservedConsumerAlignment[20];
    Consumer consumers[Slots];
    Frame ring[Capacity];
};
// Keep the v1 shared-memory ABI identical to the installed audio-service host.
static_assert(offsetof(Consumer,cursor)==16 && offsetof(Consumer,heartbeat)==24 && sizeof(Consumer)==64);
static_assert(offsetof(Shared,consumers)==64 && offsetof(Shared,ring)==2112 && sizeof(Shared)==3074112);
LONG Load(volatile LONG* p) { return InterlockedCompareExchange(p,0,0); }
uint64_t Load(volatile LONG64* p) { return static_cast<uint64_t>(InterlockedCompareExchange64(p,0,0)); }
void Store(volatile LONG* p, LONG v) { InterlockedExchange(p,v); }
void Store(volatile LONG64* p, uint64_t v) { InterlockedExchange64(p,static_cast<LONG64>(v)); }
LONG Bits(float v) { LONG x; memcpy(&x,&v,4); return x; }
float Float(LONG v) { float x; memcpy(&x,&v,4); return x; }
uint64_t Hash(const wchar_t* id) {
    uint64_t h=14695981039346656037ull;
    for (;*id;++id) { h ^= (*id & 255); h *= 1099511628211ull; h ^= ((*id>>8)&255); h *= 1099511628211ull; }
    return h;
}
std::wstring Name(const wchar_t* id) {
    wchar_t text[96]; swprintf_s(text,L"Global\\SoundboardLab.Audio.v1.%016llx",Hash(id)); return text;
}
struct Mapping {
    HANDLE handle=nullptr;
    Shared* data=nullptr;
    ~Mapping(){ Close(); }
    void Close(){ if(data) UnmapViewOfFile(data); if(handle) CloseHandle(handle); data=nullptr; handle=nullptr; }
    HRESULT Open(const wchar_t* id, bool create) {
        Close();
        if (create) {
            handle=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,Name(id).c_str());
            if(!handle){auto local=Name(id);local.replace(0,6,L"Local");handle=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,local.c_str());}
            if(!handle) {
            PSECURITY_DESCRIPTOR sd=nullptr;
            if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;SY)(A;;GRGW;;;AU)S:(ML;;NW;;;LW)",SDDL_REVISION_1,&sd,nullptr))
                return HRESULT_FROM_WIN32(GetLastError());
            SECURITY_ATTRIBUTES sa={sizeof(sa),sd,FALSE};
            handle=CreateFileMappingW(INVALID_HANDLE_VALUE,&sa,PAGE_READWRITE,0,sizeof(Shared),Name(id).c_str());
            DWORD error=GetLastError();
            // The real engine runs as SYSTEM and creates the Global transport.
            // An unprivileged in-process test host uses a session-local mapping.
            if(!handle && error==ERROR_ACCESS_DENIED) {
                auto local=Name(id);local.replace(0,6,L"Local");
                handle=CreateFileMappingW(INVALID_HANDLE_VALUE,&sa,PAGE_READWRITE,0,sizeof(Shared),local.c_str());error=GetLastError();
            }
            LocalFree(sd);
            if(!handle) return HRESULT_FROM_WIN32(error);
            }
        } else {
            handle=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,Name(id).c_str());
            if(!handle){auto local=Name(id);local.replace(0,6,L"Local");handle=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,local.c_str());}
        }
        if(!handle) return HRESULT_FROM_WIN32(GetLastError());
        data=static_cast<Shared*>(MapViewOfFile(handle,FILE_MAP_READ|FILE_MAP_WRITE,0,0,sizeof(Shared)));
        if(!data) { HRESULT hr=HRESULT_FROM_WIN32(GetLastError()); Close(); return hr; }
        if(create && InterlockedCompareExchange(&data->magic,-1,0)==0) {
            data->version=1; data->micGain=Bits(1); data->clipGain=Bits(1);
            Store(&data->magic,Magic);
        }
        // Initialization is outside the real time callback. A competing APO can
        // arrive while the creator is publishing the version and controls.
        for(int n=0;n<100 && Load(&data->magic)==-1;++n) Sleep(1);
        if(Load(&data->magic)!=Magic || data->version!=1) { Close(); return E_UNEXPECTED; }
        return S_OK;
    }
};
std::mutex bridgeMutex;
Mapping bridge;
LONG lastError=0;
int Error(HRESULT hr){ Store(&lastError,hr); return static_cast<int>(hr); }
int Active(Shared* s, uint64_t now, uint64_t* minCursor=nullptr, Consumer** first=nullptr) {
    int count=0; uint64_t end=Load(&s->write), floor=Load(&s->stopFloor), minimum=end;
    for(auto& c:s->consumers) if(Load(&c.owner)>0 && now-Load(&c.heartbeat)<=StaleMs) {
        ++count; minimum=std::min(minimum,std::max(floor,Load(&c.cursor)));
        if(first && !*first) *first=&c;
    }
    if(minCursor) *minCursor=minimum;
    return count;
}
LONG objectCount=0;
const CRegAPOProperties<4> Registration(CLSID_SoundboardCapture,L"Soundboard Lab Capture Mixer",L"Soundboard Lab",1,0,
    __uuidof(IAudioProcessingObject),static_cast<APO_FLAG>(APO_FLAG_DEFAULT|APO_FLAG_INPLACE),1,1,1,1,32,
    __uuidof(IAudioProcessingObjectRT),__uuidof(IAudioProcessingObjectConfiguration),__uuidof(IAudioSystemEffects2));

class Capture final : public CBaseAudioProcessingObject, public IAudioSystemEffects2 {
    LONG refs=1;
    // The audio engine can aggregate an APO. Keep a private IUnknown for the
    // inner lifetime, while every exposed interface delegates to its owner.
    class InnerUnknown final : public IUnknown {
        Capture* owner;
    public:
        explicit InnerUnknown(Capture* value):owner(value){}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override { return owner->NonDelegatingQueryInterface(id,out); }
        ULONG STDMETHODCALLTYPE AddRef() override { return owner->NonDelegatingAddRef(); }
        ULONG STDMETHODCALLTYPE Release() override { return owner->NonDelegatingRelease(); }
    } inner{this};
    IUnknown* controller;
    Mapping mapping;
    int slot=-1;
    uint32_t rate=48000, channels=2, maxFrames=0;
    double position=0;
    uint64_t stopSeen=0;
    IAudioProcessingObject* original=nullptr;
    IAudioProcessingObjectRT* originalRT=nullptr;
    IAudioProcessingObjectConfiguration* originalConfig=nullptr;
    IAudioSystemEffects2* originalEffects=nullptr;
    bool originalLocked=false;
    void Detach() {
        if(slot>=0 && mapping.data) Store(&mapping.data->consumers[slot].owner,0);
        slot=-1;
    }
    HRESULT Attach() {
        auto* s=mapping.data;
        if(!s) return S_OK; // Discovery does not attach a transport.
        for(int n=0;n<Slots;++n) if(InterlockedCompareExchange(&s->consumers[n].owner,-1,0)==0) {
            slot=n; auto& c=s->consumers[n]; c.rate=rate; c.channels=channels;
            position=static_cast<double>(Load(&s->write)); stopSeen=Load(&s->stopFloor);
            Store(&c.cursor,static_cast<uint64_t>(position)); Store(&c.heartbeat,GetTickCount64());
            Store(&c.owner,GetCurrentProcessId()); return S_OK;
        }
        return HRESULT_FROM_WIN32(ERROR_TOO_MANY_SESS);
    }
    HRESULT ReadString(IPropertyStore* props,const PROPERTYKEY& key,std::wstring& value) {
        if(!props) return S_FALSE; PROPVARIANT v={}; HRESULT hr=props->GetValue(key,&v);
        if(SUCCEEDED(hr) && v.vt==VT_LPWSTR && v.pwszVal) value=v.pwszVal;
        PropVariantClear(&v); return hr;
    }
    bool Sample(uint64_t index,float& l,float& r) {
        auto& f=mapping.data->ring[index%Capacity];
        uint64_t seq=Load(&f.sequence);
        if(seq!=index+1) return false;
        l=Float(Load(&f.left)); r=Float(Load(&f.right));
        return Load(&f.sequence)==seq;
    }
public:
    explicit Capture(IUnknown* outer=nullptr): CBaseAudioProcessingObject(Registration),controller(outer?outer:&inner) { InterlockedIncrement(&objectCount); }
    ~Capture() {
        if(m_bIsLocked) UnlockForProcess(); Detach();
        if(originalEffects)originalEffects->Release();
        if(originalConfig)originalConfig->Release();
        if(originalRT)originalRT->Release();
        if(original)original->Release(); InterlockedDecrement(&objectCount);
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {
        return controller->QueryInterface(id,out);
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return controller->AddRef(); }
    ULONG STDMETHODCALLTYPE Release() override { return controller->Release(); }
    HRESULT NonDelegatingQueryInterface(REFIID id,void** out) {
        if(!out) return E_POINTER; *out=nullptr;
        if(id==__uuidof(IUnknown)) { *out=static_cast<IUnknown*>(&inner); NonDelegatingAddRef(); return S_OK; }
        if(id==__uuidof(IAudioProcessingObject)) *out=static_cast<IAudioProcessingObject*>(this);
        else if(id==__uuidof(IAudioProcessingObjectRT)) *out=static_cast<IAudioProcessingObjectRT*>(this);
        else if(id==__uuidof(IAudioProcessingObjectConfiguration)) *out=static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if(id==__uuidof(IAudioSystemEffects)||id==__uuidof(IAudioSystemEffects2)) *out=static_cast<IAudioSystemEffects2*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG NonDelegatingAddRef() { return InterlockedIncrement(&refs); }
    ULONG NonDelegatingRelease() { ULONG n=InterlockedDecrement(&refs); if(!n)delete this; return n; }
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 bytes,BYTE* data) override {
        if(m_bIsInitialized) return APOERR_ALREADY_INITIALIZED;
        if(!data || bytes<sizeof(APOInitSystemEffects))return E_INVALIDARG;
        auto* init=reinterpret_cast<APOInitSystemEffects*>(data);
        if(init->APOInit.cbSize!=bytes || init->APOInit.clsid!=CLSID_SoundboardCapture)return E_INVALIDARG;
        if(bytes==sizeof(APOInitSystemEffects2) && reinterpret_cast<APOInitSystemEffects2*>(data)->InitializeForDiscoveryOnly) {
            m_bIsInitialized=true; return S_OK;
        }
        std::wstring endpoint, prior;
        ReadString(init->pAPOSystemEffectsProperties,PKEY_SB_Endpoint,endpoint);
        ReadString(init->pAPOSystemEffectsProperties,PKEY_SB_OriginalAPO,prior);
        if(endpoint.empty() && init->pDeviceCollection) {
            UINT count=0; init->pDeviceCollection->GetCount(&count);
            IMMDevice* device=nullptr;
            if(count && SUCCEEDED(init->pDeviceCollection->Item(count-1,&device))) {
                wchar_t* id=nullptr; if(SUCCEEDED(device->GetId(&id))){ endpoint=id; CoTaskMemFree(id); } device->Release();
            }
        }
        if(endpoint.empty())return E_INVALIDARG;
        if(!prior.empty()) {
            CLSID clsid; HRESULT hr=CLSIDFromString(prior.c_str(),&clsid);
            if(FAILED(hr) || clsid==CLSID_SoundboardCapture)return E_INVALIDARG;
            hr=CoCreateInstance(clsid,nullptr,CLSCTX_INPROC_SERVER,__uuidof(IAudioProcessingObject),reinterpret_cast<void**>(&original));
            if(FAILED(hr))return hr;
            hr=original->QueryInterface(__uuidof(IAudioProcessingObjectRT),reinterpret_cast<void**>(&originalRT));
            if(FAILED(hr))return hr;
            hr=original->QueryInterface(__uuidof(IAudioProcessingObjectConfiguration),reinterpret_cast<void**>(&originalConfig));
            if(FAILED(hr))return hr;
            original->QueryInterface(__uuidof(IAudioSystemEffects2),reinterpret_cast<void**>(&originalEffects));
            std::vector<BYTE> originalInit(data,data+bytes);
            reinterpret_cast<APOInitBaseStruct*>(originalInit.data())->clsid=clsid;
            hr=original->Initialize(bytes,originalInit.data()); if(FAILED(hr))return hr;
        }
        HRESULT hr=mapping.Open(endpoint.c_str(),true);
        if(FAILED(hr))return hr;
        m_bIsInitialized=true; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite,IAudioMediaType* requested,IAudioMediaType** supported) override {
        HRESULT hr=CBaseAudioProcessingObject::IsInputFormatSupported(opposite,requested,supported);
        if(hr==S_OK && original) { if(supported && *supported){(*supported)->Release(); *supported=nullptr;} return original->IsInputFormatSupported(opposite,requested,supported); }
        return hr;
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite,IAudioMediaType* requested,IAudioMediaType** supported) override {
        HRESULT hr=CBaseAudioProcessingObject::IsOutputFormatSupported(opposite,requested,supported);
        if(hr==S_OK && original) { if(supported && *supported){(*supported)->Release(); *supported=nullptr;} return original->IsOutputFormatSupported(opposite,requested,supported); }
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 ni,APO_CONNECTION_DESCRIPTOR** in,UINT32 no,APO_CONNECTION_DESCRIPTOR** out) override {
        if(!m_bIsInitialized)return APOERR_NOT_INITIALIZED;
        if(ni!=1||no!=1||!in||!out||!in[0]||!out[0]||!in[0]->pFormat)return E_INVALIDARG;
        UNCOMPRESSEDAUDIOFORMAT fmt={}; HRESULT hr=in[0]->pFormat->GetUncompressedAudioFormat(&fmt);
        if(FAILED(hr))return hr;
        if(fmt.guidFormatType!=KSDATAFORMAT_SUBTYPE_IEEE_FLOAT || fmt.dwBytesPerSampleContainer!=4 || fmt.dwValidBitsPerSample!=32 || fmt.dwSamplesPerFrame==0 || fmt.dwSamplesPerFrame>32 || !std::isfinite(fmt.fFramesPerSecond) || fmt.fFramesPerSecond<8000 || fmt.fFramesPerSecond>192000) return APOERR_FORMAT_NOT_SUPPORTED;
        hr=CBaseAudioProcessingObject::LockForProcess(ni,in,no,out); if(FAILED(hr))return hr;
        channels=fmt.dwSamplesPerFrame; rate=static_cast<uint32_t>(fmt.fFramesPerSecond); maxFrames=out[0]->u32MaxFrameCount;
        if(originalConfig) {
            hr=originalConfig->LockForProcess(ni,in,no,out);
            if(FAILED(hr)){ CBaseAudioProcessingObject::UnlockForProcess(); return hr; } originalLocked=true;
        }
        hr=Attach();
        if(FAILED(hr))UnlockForProcess();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        Detach(); if(originalLocked){ originalConfig->UnlockForProcess(); originalLocked=false; }
        return CBaseAudioProcessingObject::UnlockForProcess();
    }
    HRESULT STDMETHODCALLTYPE Reset() override {
        if(mapping.data){position=static_cast<double>(Load(&mapping.data->write));stopSeen=Load(&mapping.data->stopFloor);}
        return original ? original->Reset() : S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* time) override { if(!time)return E_POINTER; *time=0; return original?original->GetLatency(time):S_OK; }
    HRESULT STDMETHODCALLTYPE GetEffectsList(LPGUID* ids,UINT* count,HANDLE event) override {
        if(!ids||!count)return E_POINTER; *ids=nullptr; *count=0;
        return originalEffects?originalEffects->GetEffectsList(ids,count,event):S_OK;
    }
    void STDMETHODCALLTYPE APOProcess(UINT32 ni,APO_CONNECTION_PROPERTY** input,UINT32 no,APO_CONNECTION_PROPERTY** output) override {
        // Only fixed memory, bounded loops, and interlocked reads/writes here.
        // No locks, waits, COM, heap allocation, or device calls.
        if(ni!=1||no!=1||!input||!output||!input[0]||!output[0])return;
        auto* src=input[0]; auto* dst=output[0];
        if(!m_bIsLocked || src->u32BufferFlags==BUFFER_INVALID || src->u32ValidFrameCount>maxFrames) {
            dst->u32ValidFrameCount=0; dst->u32BufferFlags=BUFFER_INVALID; return;
        }
        if(originalRT){ originalRT->APOProcess(ni,input,no,output); src=dst; }
        UINT32 count=src->u32ValidFrameCount;
        if(src->u32BufferFlags==BUFFER_INVALID || count>maxFrames || !dst->pBuffer){ dst->u32ValidFrameCount=0;dst->u32BufferFlags=BUFFER_INVALID;return; }
        float* samples=reinterpret_cast<float*>(src->pBuffer); float* target=reinterpret_cast<float*>(dst->pBuffer);
        Shared* s=mapping.data;
        float mic=s?Float(Load(&s->micGain)):1, clip=s?Float(Load(&s->clipGain)):1;
        bool block=s && Load(&s->blockVoice)!=0, paused=s && Load(&s->paused)!=0;
        uint64_t end=s?Load(&s->write):0, floor=s?Load(&s->stopFloor):0;
        if(s && (floor!=stopSeen || position<static_cast<double>(floor))){position=static_cast<double>(floor);stopSeen=floor;}
        if(s && end>Capacity && position<static_cast<double>(end-Capacity)) position=static_cast<double>(end-Capacity);
        bool nonzero=false; double step=48000.0/rate;
        for(UINT32 n=0;n<count;++n) {
            float l=0,r=0; bool clipFrame=false;
            if(s && slot>=0 && !paused && position<static_cast<double>(end)) {
                uint64_t index=static_cast<uint64_t>(position); float l1=0,r1=0;
                if(Sample(index,l,r)) {
                    clipFrame=true;
                    if(index+1<end && Sample(index+1,l1,r1)) { float f=static_cast<float>(position-index);l+=(l1-l)*f;r+=(r1-r)*f; }
                }
                position=std::min(static_cast<double>(end),position+step);
            }
            for(UINT32 c=0;c<channels;++c) {
                float voice=(!(block && clipFrame) && src->u32BufferFlags==BUFFER_VALID)?samples[n*channels+c]*mic:0;
                float sound=channels==1?(l+r)*0.5f:(c==0?l:(c==1?r:0));
                float value=voice+sound*clip;
                if(!std::isfinite(value))value=0;
                value=std::clamp(value,-1.f,1.f);
                target[n*channels+c]=value; nonzero |= value!=0;
            }
        }
        if(s && slot>=0){Store(&s->consumers[slot].cursor,static_cast<uint64_t>(position));Store(&s->consumers[slot].heartbeat,GetTickCount64());}
        dst->u32ValidFrameCount=count; dst->u32BufferFlags=nonzero?BUFFER_VALID:BUFFER_SILENT;
    }
};
class Factory final : public IClassFactory {
    LONG refs=1;
public:
    Factory(){InterlockedIncrement(&objectCount);} ~Factory(){InterlockedDecrement(&objectCount);}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {
        if(!out)return E_POINTER;*out=nullptr;
        if(id!=__uuidof(IUnknown)&&id!=__uuidof(IClassFactory))return E_NOINTERFACE;
        *out=this;AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return InterlockedIncrement(&refs);}
    ULONG STDMETHODCALLTYPE Release() override {ULONG n=InterlockedDecrement(&refs);if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID id,void** out) override {
        if(!out)return E_POINTER;*out=nullptr;
        if(outer && id!=__uuidof(IUnknown))return CLASS_E_NOAGGREGATION;
        auto* apo=new(std::nothrow) Capture(outer);if(!apo)return E_OUTOFMEMORY;
        HRESULT hr=apo->NonDelegatingQueryInterface(id,out);apo->NonDelegatingRelease();return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {if(lock)InterlockedIncrement(&objectCount);else InterlockedDecrement(&objectCount);return S_OK;}
};
}
STDAPI DllGetClassObject(REFCLSID clsid,REFIID iid,void** out) {
    if(clsid!=CLSID_SoundboardCapture)return CLASS_E_CLASSNOTAVAILABLE;
    auto* f=new(std::nothrow) Factory();if(!f)return E_OUTOFMEMORY;HRESULT hr=f->QueryInterface(iid,out);f->Release();return hr;
}
STDAPI DllCanUnloadNow(){return Load(&objectCount)==0?S_OK:S_FALSE;}
extern "C" __declspec(dllexport) HRESULT WINAPI DllRegisterServer() {
    HMODULE module=nullptr;wchar_t path[MAX_PATH],clsid[40];
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&DllRegisterServer),&module))return HRESULT_FROM_WIN32(GetLastError());
    if(!GetModuleFileNameW(module,path,MAX_PATH))return HRESULT_FROM_WIN32(GetLastError());
    StringFromGUID2(CLSID_SoundboardCapture,clsid,40);
    std::wstring key=std::wstring(L"SOFTWARE\\Classes\\CLSID\\")+clsid+L"\\InprocServer32";
    HKEY registry=nullptr;LONG result=RegCreateKeyExW(HKEY_LOCAL_MACHINE,key.c_str(),0,nullptr,0,KEY_WRITE|KEY_WOW64_64KEY,nullptr,&registry,nullptr);
    if(result!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(result);
    result=RegSetValueExW(registry,nullptr,0,REG_SZ,reinterpret_cast<const BYTE*>(path),static_cast<DWORD>((wcslen(path)+1)*sizeof(wchar_t)));
    if(result==ERROR_SUCCESS)result=RegSetValueExW(registry,L"ThreadingModel",0,REG_SZ,reinterpret_cast<const BYTE*>(L"Both"),sizeof(L"Both"));
    RegCloseKey(registry);if(result!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(result);
    return RegisterAPO(Registration);
}
extern "C" __declspec(dllexport) HRESULT WINAPI DllUnregisterServer() {
    HRESULT hr=UnregisterAPO(CLSID_SoundboardCapture);wchar_t clsid[40];StringFromGUID2(CLSID_SoundboardCapture,clsid,40);
    std::wstring key=std::wstring(L"SOFTWARE\\Classes\\CLSID\\")+clsid;
    LONG result=RegDeleteTreeW(HKEY_LOCAL_MACHINE,key.c_str());
    if(result!=ERROR_SUCCESS && result!=ERROR_FILE_NOT_FOUND)return HRESULT_FROM_WIN32(result);
    return hr;
}
SB_API int SB_Open(const wchar_t* id) {
    std::lock_guard<std::mutex> lock(bridgeMutex);
    if(bridge.data){if(Load(&bridge.data->producer)==static_cast<LONG>(GetCurrentProcessId()))Store(&bridge.data->producer,0);bridge.Close();}
    if(!id || !*id)return Error(E_INVALIDARG);
    HRESULT hr=bridge.Open(id,false);if(FAILED(hr))return Error(hr);
    LONG existing=InterlockedCompareExchange(&bridge.data->producer,GetCurrentProcessId(),0);
    if(existing!=0 && existing!=static_cast<LONG>(GetCurrentProcessId())) {
        HANDLE process=OpenProcess(SYNCHRONIZE,FALSE,existing);
        bool dead=(process && WaitForSingleObject(process,0)==WAIT_OBJECT_0) || (!process && GetLastError()==ERROR_INVALID_PARAMETER);
        if(process)CloseHandle(process);
        if(!dead || InterlockedCompareExchange(&bridge.data->producer,GetCurrentProcessId(),existing)!=existing) {bridge.Close();return Error(HRESULT_FROM_WIN32(ERROR_BUSY));}
    }
    return Error(S_OK);
}
SB_API void SB_Close(){std::lock_guard<std::mutex> lock(bridgeMutex);if(bridge.data){Store(&bridge.data->stopFloor,Load(&bridge.data->write));Store(&bridge.data->producer,0);}bridge.Close();}
SB_API int SB_GetStatus(SB_Status* status) {
    std::lock_guard<std::mutex> lock(bridgeMutex);if(!status)return Error(E_POINTER);*status={};status->lastError=Load(&lastError);
    if(!bridge.data)return 0;
    uint64_t minimum=0; Consumer* first=nullptr;status->activeConsumers=Active(bridge.data,GetTickCount64(),&minimum,&first);
    status->connected=status->activeConsumers>0;status->sampleRate=first?first->rate:48000;status->channels=first?first->channels:2;
    status->queuedFrames=static_cast<int>(Load(&bridge.data->write)-minimum);return 0;
}
SB_API int SB_Push(const float* frames,int count) {
    std::lock_guard<std::mutex> lock(bridgeMutex);if(count<0 || (!frames&&count))return Error(E_INVALIDARG);
    if(!bridge.data)return Error(HRESULT_FROM_WIN32(ERROR_NOT_CONNECTED));
    auto* s=bridge.data;uint64_t minimum=0;
    if(!Active(s,GetTickCount64(),&minimum))return Error(HRESULT_FROM_WIN32(ERROR_NOT_CONNECTED));
    uint64_t end=Load(&s->write),used=end-minimum;
    int accepted=static_cast<int>(std::min<uint64_t>(count,used>=Capacity?0:Capacity-used));
    for(int n=0;n<accepted;++n){auto& f=s->ring[(end+n)%Capacity];Store(&f.sequence,0);float l=frames[2*n],r=frames[2*n+1];Store(&f.left,Bits(std::isfinite(l)?l:0));Store(&f.right,Bits(std::isfinite(r)?r:0));Store(&f.sequence,end+n+1);}
    Store(&s->write,end+accepted);Store(&lastError,0);return accepted;
}
SB_API void SB_Stop(){std::lock_guard<std::mutex> lock(bridgeMutex);if(bridge.data)Store(&bridge.data->stopFloor,Load(&bridge.data->write));}
SB_API void SB_SetControls(float mic,float clip,int block,int paused) {
    std::lock_guard<std::mutex> lock(bridgeMutex);if(!bridge.data)return;
    auto* s=bridge.data;Store(&s->micGain,Bits(std::isfinite(mic)?std::clamp(mic,0.f,8.f):1));Store(&s->clipGain,Bits(std::isfinite(clip)?std::clamp(clip,0.f,8.f):1));Store(&s->blockVoice,block?1:0);Store(&s->paused,paused?1:0);
}
SB_API int SB_GetLastError(){return Load(&lastError);}

SB_API void* SB_CreateClient(const wchar_t* id) {
    std::lock_guard<std::mutex> lock(bridgeMutex);
    if(!id||!*id){Error(E_INVALIDARG);return nullptr;}
    auto* client=new(std::nothrow) Mapping();if(!client){Error(E_OUTOFMEMORY);return nullptr;}
    HRESULT hr=client->Open(id,false);if(FAILED(hr)){delete client;Error(hr);return nullptr;}
    LONG existing=InterlockedCompareExchange(&client->data->producer,GetCurrentProcessId(),0);
    if(existing!=0&&existing!=static_cast<LONG>(GetCurrentProcessId())){
        HANDLE process=OpenProcess(SYNCHRONIZE,FALSE,existing);
        bool dead=(process&&WaitForSingleObject(process,0)==WAIT_OBJECT_0)||(!process&&GetLastError()==ERROR_INVALID_PARAMETER);
        if(process)CloseHandle(process);
        if(!dead||InterlockedCompareExchange(&client->data->producer,GetCurrentProcessId(),existing)!=existing){delete client;Error(HRESULT_FROM_WIN32(ERROR_BUSY));return nullptr;}
    }
    Error(S_OK);return client;
}
SB_API void SB_DestroyClient(void* pointer){
    if(!pointer)return;std::lock_guard<std::mutex> lock(bridgeMutex);auto* client=static_cast<Mapping*>(pointer);
    if(client->data){Store(&client->data->stopFloor,Load(&client->data->write));Store(&client->data->producer,0);}delete client;
}
SB_API int SB_ClientGetStatus(void* client,SB_Status* status) {
    std::lock_guard<std::mutex> lock(bridgeMutex);if(!client)return Error(E_POINTER);auto& mapping=*static_cast<Mapping*>(client);if(!status)return Error(E_POINTER);*status={};status->lastError=Load(&lastError);
    if(!mapping.data)return 0;
    uint64_t minimum=0; Consumer* first=nullptr;status->activeConsumers=Active(mapping.data,GetTickCount64(),&minimum,&first);
    status->connected=status->activeConsumers>0;status->sampleRate=first?first->rate:48000;status->channels=first?first->channels:2;
    status->queuedFrames=static_cast<int>(Load(&mapping.data->write)-minimum);return 0;
}
SB_API int SB_ClientPush(void* client,const float* frames,int count) {
    std::lock_guard<std::mutex> lock(bridgeMutex);if(!client)return Error(E_POINTER);auto& mapping=*static_cast<Mapping*>(client);if(count<0 || (!frames&&count))return Error(E_INVALIDARG);
    if(!mapping.data)return Error(HRESULT_FROM_WIN32(ERROR_NOT_CONNECTED));
    auto* s=mapping.data;uint64_t minimum=0;
    if(!Active(s,GetTickCount64(),&minimum))return Error(HRESULT_FROM_WIN32(ERROR_NOT_CONNECTED));
    uint64_t end=Load(&s->write),used=end-minimum;
    int accepted=static_cast<int>(std::min<uint64_t>(count,used>=Capacity?0:Capacity-used));
    for(int n=0;n<accepted;++n){auto& f=s->ring[(end+n)%Capacity];Store(&f.sequence,0);float l=frames[2*n],r=frames[2*n+1];Store(&f.left,Bits(std::isfinite(l)?l:0));Store(&f.right,Bits(std::isfinite(r)?r:0));Store(&f.sequence,end+n+1);}
    Store(&s->write,end+accepted);Store(&lastError,0);return accepted;
}
SB_API void SB_ClientStop(void* client){std::lock_guard<std::mutex> lock(bridgeMutex);if(!client)return;auto& mapping=*static_cast<Mapping*>(client);if(mapping.data)Store(&mapping.data->stopFloor,Load(&mapping.data->write));}
SB_API void SB_ClientSetControls(void* client,float mic,float clip,int block,int paused) {
    std::lock_guard<std::mutex> lock(bridgeMutex);if(!client)return;auto& mapping=*static_cast<Mapping*>(client);if(!mapping.data)return;
    auto* s=mapping.data;Store(&s->micGain,Bits(std::isfinite(mic)?std::clamp(mic,0.f,128.f):1));Store(&s->clipGain,Bits(std::isfinite(clip)?std::clamp(clip,0.f,8.f):1));Store(&s->blockVoice,block?1:0);Store(&s->paused,paused?1:0);
}
