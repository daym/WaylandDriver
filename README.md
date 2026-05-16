# Managed Wayland driver for Mono 6 WinForms

Mono 6 does not ship a native Wayland `System.Windows.Forms` backend. On Unix it selects the X11 driver unless a loadable driver is supplied through `MONO_MWF_DRIVER`.

This directory is a C# Wayland driver scaffold using the same loadable-driver shape as [https://github.com/migueldeicaza/CocoaDriver](https://github.com/migueldeicaza/CocoaDriver):

Text/edit controls in stock Mono 6 still call `Graphics.FromHwnd` directly through
`Control.CreateGraphics()`. On Linux that enters the X11/libgdiplus path even when
`MONO_MWF_DRIVER` is set. Apply `mono-6-wayland-creategraphics.patch` to Mono's
`System.Windows.Forms` source before using the full control gallery with text
controls; otherwise `TextBox`, `MaskedTextBox`, and `RichTextBox` will fail on a
pure Wayland session with an X-server/display error.

The Wayland driver overrides the `XplatUIDriver.CreateGraphics(IntPtr)` hook
introduced by that patch, so build the driver against the rebuilt patched Mono:

```sh
xbuild WaylandDriver.sln
```

```sh
MONO_MWF_DRIVER=/path/to/WaylandDriver/WaylandDriver/bin/Debug/CocoaDriver.dll mono app.exe
```

The output assembly is intentionally named `CocoaDriver.dll` for now. Mono 6.12 grants internal WinForms access to an assembly named `CocoaDriver` signed by `../CocoaDriver/CocoaKey.snk`; it does not grant that access to `WaylandDriver`. This lets the driver build against stock Mono 6 installations that include the Cocoa friend-assembly patch.

The project compiles against Mono's runtime `System.Windows.Forms.dll`, not the `*-api` reference assembly, because the driver needs internal WinForms types such as `XplatUIDriver`, `Hwnd`, `Msg`, and `MSG`.

For a clean assembly name, patch Mono's `System.Windows.Forms` assembly to add an `InternalsVisibleTo` entry for `WaylandDriver` with the same public key, then change `<AssemblyName>CocoaDriver</AssemblyName>` to `<AssemblyName>WaylandDriver</AssemblyName>` in `WaylandDriver/WaylandDriver.csproj`.

The separate `mono-6-wayland-creategraphics.patch` keeps the default behavior for
existing drivers by adding a virtual `XplatUIDriver.CreateGraphics(IntPtr)` hook
whose default implementation calls `Graphics.FromHwnd`.  This driver overrides
that hook and returns a `Graphics` backed by the managed window backbuffer,
avoiding the X11-only `Graphics.FromHwnd` path.

## Current state

Implemented:

- Pure C# Wayland socket connection using `System.Net.Sockets` and `Mono.Unix.UnixEndPoint`.
- Wayland request/message encoding and decoding for normal non-fd messages.
- `wl_display.get_registry`, registry global collection, and `wl_registry.bind`.
- Basic binds for `wl_compositor`, `wl_shm`, `wl_output`, and `xdg_wm_base`.
- Basic `xdg_wm_base.ping` handling, `xdg_surface.configure` acking, and toplevel close to `WM_CLOSE`.
- `wl_shm` buffer allocation, `SCM_RIGHTS` fd passing, mmap-backed ARGB buffers, attach, damage, and commit.
- Native integer HiDPI scale tracking from `wl_output.scale` plus `wl_surface.enter/leave`; scale defaults to 1 until the compositor reports otherwise.
- A mostly non-crashing `XplatUIDriver` scaffold with synthetic WinForms handles, timer/message queue support, window text/position/visibility state, and offscreen `System.Drawing` paint targets.

Not implemented yet:

- pointer grabs, drag and drop, tray, real monitor geometry, and full window state negotiation.
- Fractional scaling/viewporter support; current HiDPI support is Wayland's integer `wl_surface.set_buffer_scale` path.
- Buffer reuse; current painting creates a fresh shm buffer per commit and destroys it after compositor release.

There are no custom C libraries and no dependency on `libwayland-client`. The low-level Wayland fd path is handled with Mono.Posix `sendmsg`, `CMSG_*`, `mmap`, and POSIX file APIs.
libxkcommon CAN be optionally provided--and will be used if provided.
