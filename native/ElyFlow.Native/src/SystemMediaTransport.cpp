#include "ElyFlowNative.h"

#include <windows.h>
#include <roapi.h>
#include <wrl.h>
#include <wrl/event.h>
#include <windows.media.h>
#include <windows.foundation.h>
#include <windows.storage.h>
#include <windows.storage.streams.h>
#include <SystemMediaTransportControlsInterop.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <knownfolders.h>
#include <propkey.h>
#include <propvarutil.h>
#include <memory>
#include <string>

#define RETURN_IF_FAILED(expression) do { const HRESULT returnHr = (expression); if (FAILED(returnHr)) return returnHr; } while (false)

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Wrappers::HString;
using Microsoft::WRL::Wrappers::HStringReference;
using namespace ABI::Windows::Media;
using ButtonHandler = __FITypedEventHandler_2_Windows__CMedia__CSystemMediaTransportControls_Windows__CMedia__CSystemMediaTransportControlsButtonPressedEventArgs;

namespace
{
void EnsureAppIdentity(HWND hwnd)
{
    constexpr wchar_t appId[] = L"ElyCast";
    SetCurrentProcessExplicitAppUserModelID(appId);

    ComPtr<IPropertyStore> windowProperties;
    if (SUCCEEDED(SHGetPropertyStoreForWindow(hwnd, IID_PPV_ARGS(&windowProperties))))
    {
        PROPVARIANT value{};
        if (SUCCEEDED(InitPropVariantFromString(appId, &value)))
        {
            windowProperties->SetValue(PKEY_AppUserModel_ID, value);
            windowProperties->Commit();
            PropVariantClear(&value);
        }
    }

    PWSTR programs = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_Programs, KF_FLAG_CREATE, nullptr, &programs))) return;
    wchar_t executable[MAX_PATH]{};
    GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
    const std::wstring shortcut = std::wstring(programs) + L"\\ElyCast.lnk";
    CoTaskMemFree(programs);

    ComPtr<IShellLinkW> link;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link)))) return;
    link->SetPath(executable);
    link->SetDescription(L"ElyCast");
    link->SetIconLocation(executable, 0);
    ComPtr<IPropertyStore> properties;
    if (SUCCEEDED(link.As(&properties)))
    {
        PROPVARIANT value{};
        if (SUCCEEDED(InitPropVariantFromString(appId, &value)))
        {
            properties->SetValue(PKEY_AppUserModel_ID, value);
            properties->Commit();
            PropVariantClear(&value);
        }
    }
    ComPtr<IPersistFile> file;
    if (SUCCEEDED(link.As(&file))) file->Save(shortcut.c_str(), TRUE);
}

class MediaTransport final
{
public:
    MediaTransport() = default;
    // Owns COM registrations + the RoInitialize apartment: non-copyable and
    // non-movable so the destructor's teardown can never run twice.
    MediaTransport(const MediaTransport&) = delete;
    MediaTransport& operator=(const MediaTransport&) = delete;
    MediaTransport(MediaTransport&&) = delete;
    MediaTransport& operator=(MediaTransport&&) = delete;

    HRESULT Initialize(HWND hwnd, ElyMediaTransportCommand callback, void* context)
    {
        callback_ = callback;
        context_ = context;
        EnsureAppIdentity(hwnd);
        const HRESULT init = RoInitialize(RO_INIT_MULTITHREADED);
        ownsRo_ = SUCCEEDED(init);

        ComPtr<IActivationFactory> factory;
        RETURN_IF_FAILED(RoGetActivationFactory(
            HStringReference(RuntimeClass_Windows_Media_SystemMediaTransportControls).Get(),
            IID_PPV_ARGS(&factory)));

        ComPtr<ISystemMediaTransportControlsInterop> interop;
        RETURN_IF_FAILED(factory.As(&interop));
        RETURN_IF_FAILED(interop->GetForWindow(hwnd, IID_PPV_ARGS(&controls_)));

        handler_ = Callback<ButtonHandler>([this](ISystemMediaTransportControls*, ISystemMediaTransportControlsButtonPressedEventArgs* args)
        {
            SystemMediaTransportControlsButton button{};
            if (args && SUCCEEDED(args->get_Button(&button)) && callback_)
                callback_(static_cast<int32_t>(button), context_);
            return S_OK;
        });
        RETURN_IF_FAILED(controls_->add_ButtonPressed(handler_.Get(), &buttonToken_));
        // Stay absent from the Windows media UI until ElyCast actually starts
        // a local audio file. Video/live playback must never publish a session.
        RETURN_IF_FAILED(controls_->put_IsEnabled(false));
        return S_OK;
    }

    HRESULT SetMedia(const wchar_t* title, const wchar_t* artist, const wchar_t* album, const wchar_t* artworkPath)
    {
        if (!controls_) return E_HANDLE;
        controls_->put_IsEnabled(true);
        controls_->put_IsPlayEnabled(true);
        controls_->put_IsPauseEnabled(true);
        controls_->put_IsNextEnabled(true);
        controls_->put_IsPreviousEnabled(true);
        ComPtr<ISystemMediaTransportControlsDisplayUpdater> updater;
        RETURN_IF_FAILED(controls_->get_DisplayUpdater(&updater));
        RETURN_IF_FAILED(updater->ClearAll());
        RETURN_IF_FAILED(updater->put_Type(MediaPlaybackType_Music));

        HString titleString;
        HString artistString;
        HString albumString;
        RETURN_IF_FAILED(titleString.Set(title ? title : L""));
        RETURN_IF_FAILED(artistString.Set(artist ? artist : L""));
        RETURN_IF_FAILED(albumString.Set(album ? album : L""));
        ComPtr<IMusicDisplayProperties> music;
        RETURN_IF_FAILED(updater->get_MusicProperties(&music));
        RETURN_IF_FAILED(music->put_Title(titleString.Get()));
        RETURN_IF_FAILED(music->put_Artist(artistString.Get()));
        RETURN_IF_FAILED(music->put_AlbumArtist(artistString.Get()));
        ComPtr<IMusicDisplayProperties2> music2;
        if (SUCCEEDED(music.As(&music2))) music2->put_AlbumTitle(albumString.Get());

        if (artworkPath && *artworkPath)
        {
            ComPtr<ABI::Windows::Storage::IStorageFileStatics> fileFactory;
            RETURN_IF_FAILED(RoGetActivationFactory(HStringReference(RuntimeClass_Windows_Storage_StorageFile).Get(), IID_PPV_ARGS(&fileFactory)));
            HString pathString;
            RETURN_IF_FAILED(pathString.Set(artworkPath));
            ComPtr<__FIAsyncOperation_1_Windows__CStorage__CStorageFile> operation;
            RETURN_IF_FAILED(fileFactory->GetFileFromPathAsync(pathString.Get(), &operation));
            ComPtr<ABI::Windows::Foundation::IAsyncInfo> asyncInfo;
            RETURN_IF_FAILED(operation.As(&asyncInfo));
            ABI::Windows::Foundation::AsyncStatus status = ABI::Windows::Foundation::AsyncStatus::Started;
            for (int attempt = 0; attempt < 200 && status == ABI::Windows::Foundation::AsyncStatus::Started; ++attempt)
            {
                Sleep(5);
                RETURN_IF_FAILED(asyncInfo->get_Status(&status));
            }
            if (status != ABI::Windows::Foundation::AsyncStatus::Completed) return E_FAIL;
            ComPtr<ABI::Windows::Storage::IStorageFile> file;
            RETURN_IF_FAILED(operation->GetResults(&file));
            ComPtr<ABI::Windows::Storage::Streams::IRandomAccessStreamReferenceStatics> streamFactory;
            RETURN_IF_FAILED(RoGetActivationFactory(HStringReference(RuntimeClass_Windows_Storage_Streams_RandomAccessStreamReference).Get(), IID_PPV_ARGS(&streamFactory)));
            ComPtr<ABI::Windows::Storage::Streams::IRandomAccessStreamReference> thumbnail;
            RETURN_IF_FAILED(streamFactory->CreateFromFile(file.Get(), &thumbnail));
            RETURN_IF_FAILED(updater->put_Thumbnail(thumbnail.Get()));
        }
        RETURN_IF_FAILED(updater->Update());
        return controls_->put_PlaybackStatus(MediaPlaybackStatus_Changing);
    }

    void SetState(bool hasMedia, bool playing)
    {
        if (!controls_) return;
        controls_->put_PlaybackStatus(!hasMedia ? MediaPlaybackStatus_Closed :
            (playing ? MediaPlaybackStatus_Playing : MediaPlaybackStatus_Paused));
        controls_->put_IsEnabled(hasMedia);
    }

    ~MediaTransport()
    {
        if (controls_)
        {
            controls_->remove_ButtonPressed(buttonToken_);
            controls_->put_PlaybackStatus(MediaPlaybackStatus_Closed);
            controls_->put_IsEnabled(false);
        }
        handler_.Reset();
        controls_.Reset();
        if (ownsRo_) RoUninitialize();
    }

private:
    ComPtr<ISystemMediaTransportControls> controls_;
    ComPtr<ButtonHandler> handler_;
    EventRegistrationToken buttonToken_{};
    ElyMediaTransportCommand callback_{};
    void* context_{};
    bool ownsRo_{};
};
}

void* ElyMediaTransport_Create(void* hwnd, ElyMediaTransportCommand callback, void* context)
{
    if (!hwnd || !callback) return nullptr;
    auto instance = std::make_unique<MediaTransport>();
    if (FAILED(instance->Initialize(static_cast<HWND>(hwnd), callback, context)))
        return nullptr; // unique_ptr tears the partial instance down for us
    return instance.release(); // ownership crosses the C ABI as an opaque handle
}

void ElyMediaTransport_Destroy(void* instance)
{
    // Re-adopt the handle so its lifetime ends through RAII, not a bare delete.
    std::unique_ptr<MediaTransport>(static_cast<MediaTransport*>(instance));
}

int32_t ElyMediaTransport_SetMedia(void* instance, const wchar_t* title, const wchar_t* artist,
                                  const wchar_t* album, const wchar_t* artworkPath)
{
    if (!instance) return E_POINTER;
    return static_cast<int32_t>(static_cast<MediaTransport*>(instance)->SetMedia(title, artist, album, artworkPath));
}

void ElyMediaTransport_SetState(void* instance, int32_t hasMedia, int32_t playing)
{
    if (instance) static_cast<MediaTransport*>(instance)->SetState(hasMedia != 0, playing != 0);
}
