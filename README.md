# Managed Wayland driver for Mono WinForms

Mono 6 does not ship a native Wayland `System.Windows.Forms` backend. On Unix it selects the X11 driver unless a loadable driver is supplied through `MONO_MWF_DRIVER`.

This directory is a C# Wayland driver scaffold using the same loadable-driver shape as `../CocoaDriver`:

```sh
cd WaylandDriver
guix shell -m ../manifest.scm -- xbuild WaylandDriver.sln
```

```sh
MONO_MWF_DRIVER=/path/to/WaylandDriver/WaylandDriver/bin/Debug/CocoaDriver.dll mono app.exe
```

The output assembly is intentionally named `CocoaDriver.dll` for now. Mono 6.12 grants internal WinForms access to an assembly named `CocoaDriver` signed by `../CocoaDriver/CocoaKey.snk`; it does not grant that access to `WaylandDriver`. This lets the driver build against stock Mono 6 installations that include the Cocoa friend-assembly patch.

The project compiles against Mono's runtime `System.Windows.Forms.dll`, not the `*-api` reference assembly, because the driver needs internal WinForms types such as `XplatUIDriver`, `Hwnd`, `Msg`, and `MSG`.

For a clean assembly name, patch Mono's `System.Windows.Forms` assembly to add an `InternalsVisibleTo` entry for `WaylandDriver` with the same public key, then change `<AssemblyName>CocoaDriver</AssemblyName>` to `<AssemblyName>WaylandDriver</AssemblyName>` in `WaylandDriver/WaylandDriver.csproj`.

## Current state

Implemented:

- Pure C# Wayland socket connection using `System.Net.Sockets` and `Mono.Unix.UnixEndPoint`.
- Wayland request/message encoding and decoding for normal non-fd messages.
- `wl_display.get_registry`, registry global collection, and `wl_registry.bind`.
- Basic binds for `wl_compositor` and `xdg_wm_base`.
- Basic `xdg_wm_base.ping` handling, `xdg_surface.configure` acking, and toplevel close to `WM_CLOSE`.
- A mostly non-crashing `XplatUIDriver` scaffold with synthetic WinForms handles, timer/message queue support, window text/position/visibility state, and offscreen `System.Drawing` paint targets.

Not implemented yet:

- `wl_shm` buffer allocation and fd passing for real pixels on screen.
- Input from `wl_seat`, keyboard maps, pointer grabs, cursors, clipboard/data-device, drag and drop, tray, real monitor geometry, and full window state negotiation.
- Damage/commit from WinForms paint buffers to Wayland surfaces.

There are no custom C libraries and no dependency on `libwayland-client`. The first unavoidable low-level gap for a complete backend is passing shared-memory file descriptors for `wl_shm`; that can still be done from C# using Mono/POSIX APIs, but it is not part of this first scaffold.
