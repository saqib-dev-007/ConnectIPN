# Vendored WinFsp assemblies

`winfsp-msil.dll` (the .NET file-system API) and `winfsp-x64.dll` (native shim) are
taken unmodified from the WinFsp 2.1 installer (winfsp-2.1.25156.msi, github.com/winfsp/winfsp).

They are checked in so the desktop project compiles without WinFsp being installed.
The WinFsp driver itself is a kernel service and must be installed separately with
administrator rights: https://winfsp.dev/rel/

WinFsp is licensed under GPLv3 with a FLOSS exception; see the upstream LICENSE.txt.
