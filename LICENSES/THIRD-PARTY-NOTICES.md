# Third-party notices

GridShift is a Windows x64 self-contained .NET 8 WinForms application. The installer contains the files listed below; no NuGet library is loaded by the app at runtime and no package is downloaded when it runs.

## Virtual desktop COM interface references

The application calls Windows' internal virtual-desktop COM service only on explicitly recognized builds. External game/app window placement uses `IApplicationViewCollection.GetViewForHwnd` and the internal `IVirtualDesktopManagerInternal.MoveViewToDesktop`; the public manager is not used for those windows. The build-specific declarations were independently authored from these MIT-licensed sources; neither upstream library/package is shipped or loaded:

- Grabacr07, `VirtualDesktop` v5.0.5, source commit `a6c69e420307e0717f296501c1e7595977e27b6b`, MIT. The relevant Build10240 view/manager/pinned-app interface source files and original license are preserved in `references/virtual-desktop-com/`; the release notice is `LICENSES/VirtualDesktop.MIT.txt`.
- Ciantic, `VirtualDesktopAccessor` Rust branch, source commit `7ff9ef827bab9a081421ebb204339dd96475ec1a`, MIT. Exact `src/interfaces.rs` and original license are preserved in `references/virtual-desktop-com/`; the release notice is `LICENSES/Ciantic.MIT.txt`.

The package-based Grabacr assembly's runtime Roslyn code generation and dynamic interface IID resolution are not used. The supported ABI mappings and fallback policy are documented in `README.md`.

## Microsoft .NET runtime and Windows Forms

The installer bundles the .NET 8 `win-x64` self-contained runtime and Windows Desktop runtime matching this build. Exact upstream package versions and payload file names are recorded in `artifacts/publish/runtime-inventory.txt`. Original Microsoft license and third-party notice files from those runtime packages are installed under `LICENSES/` alongside the application. Runtime components are MIT-licensed; see those included originals and https://github.com/dotnet/runtime and https://github.com/dotnet/winforms.

## Installer toolchain

NSIS 3.13 is used only on the build machine to produce the setup executable; it is not required to install or run the app. Its original COPYING notice, including applicable component/license terms, is included as `NSIS-COPYING.txt`.
