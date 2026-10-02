# Virtual-desktop COM backend audit (test release)

## Runtime policy

`VirtualDesktops.cs` reads `Environment.OSVersion.Version` and (only for supported Windows 11 build families) the read-only `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\UBR` value. It enables the private COM backend only for:

- Windows 10 builds 19041–19045, using the Build10240 interface layout and IIDs `F31574D6-B682-4CDC-BD56-1827860ABEC6` (`IVirtualDesktopManagerInternal`) and `FF72FFDD-BE7E-43FC-9C03-AD81681E88E4` (`IVirtualDesktop`).
- Windows 11 24H2 build 26100, UBR 2605 or later; and build 26200, UBR 8117 or later. These use the current static interface declarations and IIDs `53F5CA0B-158F-4124-900C-057158060B27` and `3F07F4BE-B107-441A-AF0F-39D82529072C`.

All other Windows versions/builds, missing UBR data, non-Windows hosts, COM activation failures, and per-window HRESULT failures fail closed: the UI retains a visible warning and the launcher keeps ordinary desktop behavior. No private COM call attempts to remove a desktop. The declarations stop at `CreateDesktop`; window movement obtains the build-specific `IApplicationViewCollection.GetViewForHwnd` application view and invokes `IVirtualDesktopManagerInternal.MoveViewToDesktop`. Before moving, the backend checks `IVirtualDesktopPinnedApps.IsViewPinned`; pinned, missing, or unqueryable views are never moved. The public `IVirtualDesktopManager.MoveWindowToDesktop` method is not used (it cannot move the external game/companion windows targeted here).

This is an explicit allowlist, not a claim that Microsoft guarantees the undocumented private COM ABI. The guard prevents guessed layouts from being called on Windows 11 21H2/22H2/23H2, Windows 10 19046+, or future unreviewed builds. Users can still use the launcher and leave the desktop option disabled. Version checks and fakes are regression-tested, but actual COM behavior has not been tested on Windows in this release environment.

## Audited source references

The exact ABI evidence is copied within this repository at `references/virtual-desktop-com/` with the required upstream interface sources, MIT licenses, commit IDs, and SHA-256 hashes in its README:

1. Grabacr07 `VirtualDesktop` v5.0.5, commit `a6c69e420307e0717f296501c1e7595977e27b6b` (MIT). Audit `src/VirtualDesktop/Interop/ComInterfaceAssemblyBuilder.cs`, `IID.cs`, `VirtualDesktopProvider.cs`, `Build10240/.interfaces/IVirtualDesktopManagerInternal.cs`, `Build10240/.interfaces/IApplicationViewCollection.cs`, `Build10240/.interfaces/IVirtualDesktopPinnedApps.cs`, `Build22000/.interfaces/IVirtualDesktopManagerInternal.cs`, and `Properties/Settings.settings`.
   - Its `ComInterfaceAssemblyBuilder` emits C# interfaces using Roslyn at runtime, writes/loads generated assemblies from a local app-data cache, and the `IID` resolver uses build-specific settings then scans the registry. It carries a Build22000 layout but its selected build mapping and runtime generation are unnecessary and not used here.
2. Ciantic `VirtualDesktopAccessor` Rust branch, commit `7ff9ef827bab9a081421ebb204339dd96475ec1a` (MIT). Audit `README.md` (minimum Windows 11 24H2 build 26100.2605; tested 25H2 build 26200.8117), `src/interfaces.rs` (`IVirtualDesktop`, `IVirtualDesktopManagerInternal`), `src/comobjects.rs`, `Cargo.lock`, and `LICENSE.txt`.
   - This is a statically declared interface implementation, unlike the runtime code-generator above. Its latest source identifies the 24H2+ ABI used for the Windows 11 mapping. The launcher does not ship its DLL, crate, Cargo dependencies, or runtime.

These snapshots are for audit/reference only. The COM declarations in `VirtualDesktopCom.cs` are the narrow methods used by this launcher, with reserved slots represented only to preserve vtable order. `FindDesktopPointer` asks `IObjectArray.GetAt` for the exact build-specific `IVirtualDesktop` IID and forwards that acquired interface pointer directly to `MoveViewToDesktop`; it does not canonicalize the argument to `IUnknown`. The pointer remains owned through the synchronous COM call and is then released. Ciantic source shows `GetViewForHwnd` after the first three collection methods and `MoveViewToDesktop` after `GetDesktopCount`; Windows 10 IID/layout is cross-checked against retained Grabacr07 Build10240 interfaces. MIT license notices are included in the release payload. The source audit does not establish Windows runtime compatibility; Windows 10/11 PC verification remains required.
