#define NOMINMAX
#include "../../native/SoundboardAudio.h"
#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <propsys.h>
#include <propvarutil.h>
#include <ks.h>
#include <ksmedia.h>
#include <iostream>
#include <vector>
#include <thread>
#include <atomic>
#include <cmath>
#include <stdexcept>
#include <algorithm>
#include <string>

using GetFactory = HRESULT(WINAPI*)(REFCLSID,REFIID,void**);
using CanUnload = HRESULT(WINAPI*)();
HMODULE dll;
GetFactory factory;
int checks=0;
void Check(bool pass,const char* message) { ++checks; if(!pass)throw std::runtime_error(message); }
void HR(HRESULT hr,const char* message) { if(FAILED(hr)){std::cerr<<message<<" HRESULT 0x"<<std::hex<<hr<<std::dec<<"\n";} Check(SUCCEEDED(hr),message); }
void Near(float actual,float expected,const char* message) { Check(std::abs(actual-expected)<0.0001f,message); }
std::wstring Endpoint(const wchar_t* name) { return std::wstring(L"SoundboardLab.NativeTests.")+std::to_wstring(GetCurrentProcessId())+L"."+name; }
void AggregationTests() {
    struct Outer final : IUnknown {
        LONG refs=1; IUnknown* inner=nullptr;
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {
            if(!out)return E_POINTER;*out=nullptr;
            if(id==__uuidof(IUnknown)){*out=static_cast<IUnknown*>(this);AddRef();return S_OK;}
            return inner?inner->QueryInterface(id,out):E_NOINTERFACE;
        }
        ULONG STDMETHODCALLTYPE AddRef() override {return InterlockedIncrement(&refs);}
        ULONG STDMETHODCALLTYPE Release() override {return InterlockedDecrement(&refs);}
    } outer;
    IClassFactory* f=nullptr;HR(factory(CLSID_SoundboardCapture,__uuidof(IClassFactory),reinterpret_cast<void**>(&f)),"Aggregation factory");
    void* invalid=reinterpret_cast<void*>(1);
    Check(f->CreateInstance(&outer,__uuidof(IAudioProcessingObject),&invalid)==CLASS_E_NOAGGREGATION&&invalid==nullptr,"Aggregated creation requires private IUnknown");
    HR(f->CreateInstance(&outer,__uuidof(IUnknown),reinterpret_cast<void**>(&outer.inner)),"Create aggregated inner unknown");
    Check(outer.refs==1,"Private unknown has separate lifetime");
    IAudioProcessingObject* apo=nullptr;HR(outer.QueryInterface(__uuidof(IAudioProcessingObject),reinterpret_cast<void**>(&apo)),"Outer discovers inner APO");
    Check(outer.refs==2,"APO interface increments controlling unknown");
    IUnknown* identity=nullptr;HR(apo->QueryInterface(__uuidof(IUnknown),reinterpret_cast<void**>(&identity)),"Aggregated identity");
    Check(identity==static_cast<IUnknown*>(&outer)&&outer.refs==3,"Public identity belongs to the controlling outer");
    identity->Release();apo->Release();Check(outer.refs==1,"Interface releases delegate to the outer");
    Check(outer.inner->Release()==0,"Outer releases private inner lifetime");outer.inner=nullptr;f->Release();
    std::cout<<"PASS: aggregated COM creation, controlling identity, delegated references and inner teardown\n";
}
struct APO {
    IAudioProcessingObject* object=nullptr;
    IAudioProcessingObjectRT* rt=nullptr;
    IAudioProcessingObjectConfiguration* config=nullptr;
    IAudioMediaType* format=nullptr;
    std::vector<float> input,output;
    uint32_t channels=2,rate=48000,maxFrames=512;
    bool locked=false;
    APO(const std::wstring& endpoint,uint32_t sampleRate=48000,uint32_t channelCount=2,const wchar_t* original=nullptr):channels(channelCount),rate(sampleRate) {
        IClassFactory* f=nullptr;HR(factory(CLSID_SoundboardCapture,__uuidof(IClassFactory),reinterpret_cast<void**>(&f)),"GetClassObject");
        HR(f->CreateInstance(nullptr,__uuidof(IAudioProcessingObject),reinterpret_cast<void**>(&object)),"CreateInstance");f->Release();
        HR(object->QueryInterface(__uuidof(IAudioProcessingObjectRT),reinterpret_cast<void**>(&rt)),"QI RT");
        HR(object->QueryInterface(__uuidof(IAudioProcessingObjectConfiguration),reinterpret_cast<void**>(&config)),"QI Configuration");
        IPropertyStore* props=nullptr;HR(PSCreateMemoryPropertyStore(__uuidof(IPropertyStore),reinterpret_cast<void**>(&props)),"Memory property store");
        PROPVARIANT v={};HR(InitPropVariantFromString(endpoint.c_str(),&v),"Endpoint property");HR(props->SetValue(PKEY_SB_Endpoint,v),"Set endpoint");PropVariantClear(&v);
        if(original){HR(InitPropVariantFromString(original,&v),"Original property");HR(props->SetValue(PKEY_SB_OriginalAPO,v),"Set original");PropVariantClear(&v);}
        APOInitSystemEffects init={};init.APOInit.cbSize=sizeof(init);init.APOInit.clsid=CLSID_SoundboardCapture;init.pAPOSystemEffectsProperties=props;
        HRESULT initialized=object->Initialize(sizeof(init),reinterpret_cast<BYTE*>(&init));props->Release();HR(initialized,"Initialize");
        UNCOMPRESSEDAUDIOFORMAT fmt={};fmt.guidFormatType=KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;fmt.dwSamplesPerFrame=channels;fmt.dwBytesPerSampleContainer=4;fmt.dwValidBitsPerSample=32;fmt.fFramesPerSecond=static_cast<float>(rate);fmt.dwChannelMask=channels==1?SPEAKER_FRONT_CENTER:(channels==2?SPEAKER_FRONT_LEFT|SPEAKER_FRONT_RIGHT:0);
        HR(CreateAudioMediaTypeFromUncompressedAudioFormat(&fmt,&format),"Create float media type");
        IAudioMediaType* supported=nullptr;Check(object->IsInputFormatSupported(format,format,&supported)==S_OK,"Float format negotiation");if(supported)supported->Release();
        input.resize(maxFrames*channels);output.resize(maxFrames*channels);Lock();
    }
    ~APO(){ if(locked)config->UnlockForProcess();if(format)format->Release();if(config)config->Release();if(rt)rt->Release();if(object)object->Release(); }
    void Lock() {
        APO_CONNECTION_DESCRIPTOR in={APO_CONNECTION_BUFFER_TYPE_EXTERNAL,reinterpret_cast<UINT_PTR>(input.data()),maxFrames,format,APO_CONNECTION_DESCRIPTOR_SIGNATURE};
        APO_CONNECTION_DESCRIPTOR out={APO_CONNECTION_BUFFER_TYPE_EXTERNAL,reinterpret_cast<UINT_PTR>(output.data()),maxFrames,format,APO_CONNECTION_DESCRIPTOR_SIGNATURE};
        auto* i=&in;auto* o=&out;HR(config->LockForProcess(1,&i,1,&o),"LockForProcess");locked=true;
    }
    APO_BUFFER_FLAGS Process(uint32_t count,APO_BUFFER_FLAGS flag=BUFFER_VALID,float voice=0.1f,bool inplace=false) {
        std::fill(input.begin(),input.end(),voice);std::fill(output.begin(),output.end(),-1234.f);
        APO_CONNECTION_PROPERTY in={reinterpret_cast<UINT_PTR>(input.data()),count,flag,APO_CONNECTION_PROPERTY_SIGNATURE};
        APO_CONNECTION_PROPERTY out={reinterpret_cast<UINT_PTR>(inplace?input.data():output.data()),0,BUFFER_INVALID,APO_CONNECTION_PROPERTY_SIGNATURE};
        auto* i=&in;auto* o=&out;rt->APOProcess(1,&i,1,&o);
        Check(out.u32ValidFrameCount==(flag==BUFFER_INVALID?0:count),"Output frame count");
        if(inplace)output=input;return out.u32BufferFlags;
    }
};
// A process-local COM class factory exercises preserved original APOs without
// registering any DLL or touching the endpoint registry.
inline constexpr GUID CLSID_TestOriginal={0x6dfbbf12,0x9361,0x48ee,{0xac,0xa7,0x26,0xd8,0xa2,0x59,0xb3,0x05}};
int originalInitialized=0,originalProcessed=0,originalLocked=0;
class Original final : public IAudioProcessingObject,public IAudioProcessingObjectRT,public IAudioProcessingObjectConfiguration {
    LONG refs=1;uint32_t channels=2;
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override { if(!out)return E_POINTER;*out=nullptr;if(id==__uuidof(IUnknown)||id==__uuidof(IAudioProcessingObject))*out=static_cast<IAudioProcessingObject*>(this);else if(id==__uuidof(IAudioProcessingObjectRT))*out=static_cast<IAudioProcessingObjectRT*>(this);else if(id==__uuidof(IAudioProcessingObjectConfiguration))*out=static_cast<IAudioProcessingObjectConfiguration*>(this);else return E_NOINTERFACE;AddRef();return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override {return InterlockedIncrement(&refs);} ULONG STDMETHODCALLTYPE Release() override {ULONG n=InterlockedDecrement(&refs);if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE Reset() override {return S_OK;}
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* value) override {*value=123;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES**) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 count,BYTE* bytes) override {auto* init=reinterpret_cast<APOInitBaseStruct*>(bytes);if(count<sizeof(APOInitSystemEffects)||init->clsid!=CLSID_TestOriginal)return E_INVALIDARG;++originalInitialized;return S_OK;}
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType*,IAudioMediaType* requested,IAudioMediaType** supported) override {if(supported){*supported=requested;requested->AddRef();}return S_OK;}
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite,IAudioMediaType* requested,IAudioMediaType** supported) override {return IsInputFormatSupported(opposite,requested,supported);}
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* count) override {*count=channels;return S_OK;}
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32,APO_CONNECTION_DESCRIPTOR** in,UINT32,APO_CONNECTION_DESCRIPTOR**) override {UNCOMPRESSEDAUDIOFORMAT fmt={};in[0]->pFormat->GetUncompressedAudioFormat(&fmt);channels=fmt.dwSamplesPerFrame;++originalLocked;return S_OK;}
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {--originalLocked;return S_OK;}
    void STDMETHODCALLTYPE APOProcess(UINT32,APO_CONNECTION_PROPERTY** in,UINT32,APO_CONNECTION_PROPERTY** out) override {
        ++originalProcessed;out[0]->u32ValidFrameCount=in[0]->u32ValidFrameCount;out[0]->u32BufferFlags=in[0]->u32BufferFlags;
        if(in[0]->u32BufferFlags==BUFFER_VALID){auto* src=reinterpret_cast<float*>(in[0]->pBuffer);auto* dst=reinterpret_cast<float*>(out[0]->pBuffer);for(uint32_t n=0;n<in[0]->u32ValidFrameCount*channels;++n)dst[n]=src[n]*.5f;}
    }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 n) override {return n;} UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 n) override {return n;}
};
class OriginalFactory : public IClassFactory {
    LONG refs=1;
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {if(id!=__uuidof(IClassFactory)&&id!=__uuidof(IUnknown))return E_NOINTERFACE;*out=this;AddRef();return S_OK;}
    ULONG STDMETHODCALLTYPE AddRef() override {return InterlockedIncrement(&refs);} ULONG STDMETHODCALLTYPE Release() override {ULONG n=InterlockedDecrement(&refs);if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown*,REFIID id,void** out) override {auto* original=new Original();HRESULT hr=original->QueryInterface(id,out);original->Release();return hr;}
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override {return S_OK;}
};
void BasicTests() {
    auto endpoint=Endpoint(L"basic");APO apo(endpoint);Check(SB_Open(endpoint.c_str())==0,"SB_Open");SB_Status status={};Check(SB_GetStatus(&status)==0,"Status");Check(status.connected && status.activeConsumers==1 && status.sampleRate==48000 && status.channels==2,"Attached status");
    APO_REG_PROPERTIES* registration=nullptr;HR(apo.object->GetRegistrationProperties(&registration),"Registration properties");Check(registration->u32NumAPOInterfaces==4,"Four registration interfaces");Check(registration->iidAPOInterfaceList[1]==__uuidof(IAudioProcessingObjectRT),"Registration RT IID");CoTaskMemFree(registration);
    Check(apo.Process(8)==BUFFER_VALID,"Mic valid");Near(apo.output[0],.1f,"Mic passthrough");
    Check(apo.Process(8,BUFFER_SILENT)==BUFFER_SILENT,"Silent passthrough");Near(apo.output[0],0,"Silence writes zeros");
    SB_SetControls(.5f,.25f,0,0);float clip[]={.2f,.4f,.4f,.8f,.6f,.2f,.8f,.4f};Check(SB_Push(clip,4)==4,"Push clip");
    Check(apo.Process(4,BUFFER_VALID,.2f)==BUFFER_VALID,"Mix valid");Near(apo.output[0],.15f,"Mic plus clip L");Near(apo.output[1],.2f,"Mic plus clip R");Near(apo.output[4],.25f,"Sequential frame order");
    SB_SetControls(1,1,1,0);Check(SB_Push(clip,4)==4,"Push replacement");apo.Process(4,BUFFER_VALID,.9f,true);Near(apo.output[0],.2f,"Block voice inplace");Near(apo.output[1],.4f,"Stereo preservation inplace");
    apo.Process(4,BUFFER_VALID,.3f);Near(apo.output[0],.3f,"Block voice does not mute an idle microphone");
    Check(SB_Push(clip,4)==4,"Push blocked paused clip");SB_SetControls(1,1,1,1);apo.Process(4,BUFFER_VALID,.3f);Near(apo.output[0],.3f,"Paused clip does not block voice");SB_Stop();
    SB_SetControls(1,1,0,0);Check(SB_Push(clip,4)==4,"Push silent mic");Check(apo.Process(4,BUFFER_SILENT)==BUFFER_VALID,"Silent mic becomes valid when injected");Near(apo.output[1],.4f,"Silent mic samples excluded");
    Check(SB_Push(clip,4)==4,"Push paused");SB_SetControls(1,1,0,1);apo.Process(4);Near(apo.output[0],.1f,"Pause preserves mic");SB_GetStatus(&status);Check(status.queuedFrames==4,"Pause retains cursor");SB_SetControls(1,1,0,0);apo.Process(4);Near(apo.output[0],.3f,"Resume consumes clip");
    Check(SB_Push(clip,4)==4,"Push stopped");SB_Stop();apo.Process(4);Near(apo.output[0],.1f,"Stop drops queued samples");SB_GetStatus(&status);Check(status.queuedFrames==0,"Stop status drained");
    float invalid[]={NAN,INFINITY,8,8};Check(SB_Push(invalid,2)==2,"Finite input sanitation");apo.Process(2,BUFFER_SILENT);Near(apo.output[0],0,"NaN sanitized");Near(apo.output[2],1,"Output limiter");
    Check(apo.Process(4,BUFFER_INVALID)==BUFFER_INVALID,"Invalid input preserved");
    Check(SB_Push(clip,4)==4,"Push before reset");HR(apo.object->Reset(),"Reset");apo.Process(4);Near(apo.output[0],.1f,"Reset does not replay stale frames");
    HR(apo.config->UnlockForProcess(),"Unlock");apo.locked=false;SB_GetStatus(&status);Check(!status.connected && status.activeConsumers==0,"Unlock disconnects");Check(SB_Push(clip,4)<0,"Push rejects no active consumer");apo.Lock();SB_GetStatus(&status);Check(status.connected,"Relock reconnects");
    SB_Close();Check(SB_Open(endpoint.c_str())==0,"Reopen");SB_Close();
    std::cout<<"PASS: COM activation, registration, mic mix, gains, stereo, block voice, silent flags, pause/resume, stop/reset, sanitation/limiter, unlock/reopen\n";
}
void FormatTests() {
    for(auto rate:{44100u,96000u,16000u}) {
        auto endpoint=Endpoint((L"rate"+std::to_wstring(rate)).c_str());APO apo(endpoint,rate,1);Check(SB_Open(endpoint.c_str())==0,"Mono resample open");
        std::vector<float> clip(480*2);for(int n=0;n<480;++n){clip[n*2]=n*.001f;clip[n*2+1]=n*.001f+.2f;}
        SB_SetControls(0,1,0,0);Check(SB_Push(clip.data(),480)==480,"Mono resample push");apo.Process(64,BUFFER_SILENT);
        for(int n=0;n<64;++n)Near(apo.output[n],.1f+static_cast<float>(n*48000.0/rate*.001),"Interpolation and mono average");
        SB_Close();
    }
    std::cout<<"PASS: 44.1 kHz, 96 kHz and 16 kHz realtime interpolation with stereo-to-mono downmix\n";
}
void BroadcastTests() {
    auto endpoint=Endpoint(L"broadcast");APO first(endpoint),second(endpoint);Check(SB_Open(endpoint.c_str())==0,"Broadcast open");
    SB_Status status={};SB_GetStatus(&status);Check(status.activeConsumers==2,"Multiple consumers");
    std::vector<float> clip(192000*2,.2f);Check(SB_Push(clip.data(),192000)==192000,"Four-second ring fills");Check(SB_Push(clip.data(),1)==0,"Backpressure on full queue");
    first.Process(128,BUFFER_SILENT);Near(first.output[0],.2f,"First consumer audio");Check(SB_Push(clip.data(),1)==0,"Slower consumer prevents overwrite");
    second.Process(64,BUFFER_SILENT);Near(second.output[0],.2f,"Second independent cursor audio");Check(SB_Push(clip.data(),128)==64,"Minimum cursor determines free space");
    SB_Stop();first.Process(1,BUFFER_SILENT);second.Process(1,BUFFER_SILENT);Check(SB_Push(clip.data(),192000)==192000,"Stop frees queue without resetting shared write counters");
    SB_Stop();first.Process(1);second.Process(1);SB_Close();
    std::cout<<"PASS: independent APO cursors, broadcast, ring wraparound, backpressure, stop frees full queue\n";
}
void OriginalTests() {
    auto* f=new OriginalFactory();DWORD cookie=0;HR(CoRegisterClassObject(CLSID_TestOriginal,f,CLSCTX_INPROC_SERVER,REGCLS_MULTIPLEUSE,&cookie),"Register process-local original factory");f->Release();
    {
        auto endpoint=Endpoint(L"original");APO apo(endpoint,48000,2,L"{6DFBBF12-9361-48EE-ACA7-26D8A259B305}");Check(originalInitialized==1&&originalLocked==1,"Original initialize and lock delegated");Check(SB_Open(endpoint.c_str())==0,"Original chain open");
        float clip[]={.2f,.4f};Check(SB_Push(clip,1)==1,"Original chain push");apo.Process(1,BUFFER_VALID,.4f);Near(apo.output[0],.4f,"Original processes mic before clip injection");Near(apo.output[1],.6f,"Original chain stereo mix");Check(originalProcessed==1,"Original processing delegated");
        HNSTIME latency=0;HR(apo.object->GetLatency(&latency),"Original latency");Check(latency==123,"Original latency preserved");SB_Close();
    }
    Check(originalLocked==0,"Original unlocked on teardown");HR(CoRevokeClassObject(cookie),"Revoke original factory");
    std::cout<<"PASS: preserved original APO COM initialization, format negotiation, lock, processing, latency, teardown\n";
}
void ConcurrentTests() {
    auto endpoint=Endpoint(L"concurrent");APO apo(endpoint);Check(SB_Open(endpoint.c_str())==0,"Concurrent open");std::atomic<bool> finished=false;std::atomic<int> accepted=0;std::atomic<int> failures=0;
    std::thread producer([&]{std::vector<float> block(256*2,.2f);for(int n=0;n<2000;++n){int frames=SB_Push(block.data(),256);if(frames<0)++failures;else accepted+=frames;if(n%31==0)SB_Stop();if(n%11==0)SB_SetControls(.5f,1,0,n%22==0);}finished=true;});
    int callbacks=0;while(!finished || callbacks<300){apo.Process(128,BUFFER_VALID,.2f);for(int n=0;n<256;++n)if(!std::isfinite(apo.output[n])||std::abs(apo.output[n])>1)++failures;++callbacks;}
    producer.join();Check(failures==0&&accepted>0,"Concurrent producer/consumer values valid");SB_Close();std::cout<<"PASS: concurrent IPC producer, controls and stop during "<<callbacks<<" real APO callbacks\n";
}
int Child(const wchar_t* endpoint,const wchar_t* readyName,const wchar_t* goName) {
    APO apo(endpoint);HANDLE ready=OpenEventW(EVENT_MODIFY_STATE,FALSE,readyName);HANDLE go=OpenEventW(SYNCHRONIZE,FALSE,goName);
    if(!ready||!go)return 20;SetEvent(ready);if(WaitForSingleObject(go,10000)!=WAIT_OBJECT_0)return 21;
    Check(apo.Process(16,BUFFER_SILENT)==BUFFER_VALID,"Child valid injection");for(int n=0;n<32;++n)Near(apo.output[n],.375f,"Child shared memory samples");CloseHandle(ready);CloseHandle(go);return 0;
}
void CrossProcessTests() {
    auto endpoint=Endpoint(L"crossprocess"),readyName=Endpoint(L"ready"),goName=Endpoint(L"go");
    HANDLE ready=CreateEventW(nullptr,TRUE,FALSE,readyName.c_str()),go=CreateEventW(nullptr,TRUE,FALSE,goName.c_str());wchar_t executable[MAX_PATH];GetModuleFileNameW(nullptr,executable,MAX_PATH);
    std::wstring command=L"\""+std::wstring(executable)+L"\" --child \""+endpoint+L"\" \""+readyName+L"\" \""+goName+L"\"";
    STARTUPINFOW si={sizeof(si)};PROCESS_INFORMATION pi={};HR(CreateProcessW(nullptr,command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&si,&pi)?S_OK:HRESULT_FROM_WIN32(GetLastError()),"Start consumer child");
    Check(WaitForSingleObject(ready,10000)==WAIT_OBJECT_0,"Child APO ready");Check(SB_Open(endpoint.c_str())==0,"Open child mapping");std::vector<float> clip(32,.375f);Check(SB_Push(clip.data(),16)==16,"Push crossprocess");SetEvent(go);Check(WaitForSingleObject(pi.hProcess,10000)==WAIT_OBJECT_0,"Child finished");DWORD code=99;GetExitCodeProcess(pi.hProcess,&code);Check(code==0,"Crossprocess consumer output verified");SB_Close();CloseHandle(ready);CloseHandle(go);CloseHandle(pi.hThread);CloseHandle(pi.hProcess);
    std::cout<<"PASS: bridge and APO in separate processes exchange actual float samples through named shared memory\n";
}
void IndependentClientTests(){
    auto leftId=Endpoint(L"client-left"),rightId=Endpoint(L"client-right");APO left(leftId),right(rightId);
    left.Process(8,BUFFER_SILENT);right.Process(8,BUFFER_SILENT);
    void* a=SB_CreateClient(leftId.c_str());void* b=SB_CreateClient(rightId.c_str());Check(a&&b,"Create independent capture producers");
    SB_ClientSetControls(a,1,1,0,0);SB_ClientSetControls(b,2,.5f,0,0);
    std::vector<float> first(32,.2f),second(32,.6f);Check(SB_ClientPush(a,first.data(),16)==16&&SB_ClientPush(b,second.data(),16)==16,"Push different clips to two endpoints");
    left.Process(16,BUFFER_VALID,.1f);right.Process(16,BUFFER_VALID,.1f);
    Near(left.output[0],.3f,"First endpoint owns its own clip and gain");Near(right.output[0],.5f,"Second endpoint owns its own clip and gain");
    SB_DestroyClient(a);Check(SB_ClientPush(b,second.data(),16)==16,"Closing one client leaves the other endpoint available");
    SB_ClientStop(b);right.Process(16,BUFFER_VALID,.1f);Near(right.output[0],.2f,"Per-client stop restores voice without queued clip");SB_DestroyClient(b);
    std::cout<<"PASS: independent multi-endpoint producer handles, gains, stop and teardown\n";
}
int wmain(int argc,wchar_t** argv) {
    HRESULT init=CoInitializeEx(nullptr,COINIT_MULTITHREADED);if(FAILED(init))return 2;
    dll=LoadLibraryW(L"SoundboardLab.Audio.dll");if(!dll){std::cerr<<"DLL load failed "<<GetLastError()<<"\n";return 3;}
    factory=reinterpret_cast<GetFactory>(GetProcAddress(dll,"DllGetClassObject"));
    try {
        Check(factory!=nullptr,"Factory export exists");if(argc==5 && std::wstring(argv[1])==L"--child")return Child(argv[2],argv[3],argv[4]);
        Check(SB_Open(L"missing-native-test-endpoint")<0,"Missing endpoint error");Check(SB_GetLastError()<0,"Last error available");
        AggregationTests();BasicTests();FormatTests();BroadcastTests();OriginalTests();ConcurrentTests();CrossProcessTests();IndependentClientTests();
        auto unload=reinterpret_cast<CanUnload>(GetProcAddress(dll,"DllCanUnloadNow"));Check(unload&&unload()==S_OK,"All COM instances released");
        std::cout<<"ALL NATIVE TESTS PASSED: "<<checks<<" assertions; DLL not installed, no audio endpoint settings changed.\n";
        FreeLibrary(dll);CoUninitialize();return 0;
    } catch(const std::exception& e){SB_Close();std::cerr<<"FAILED: "<<e.what()<<"\n";return 1;}
}
