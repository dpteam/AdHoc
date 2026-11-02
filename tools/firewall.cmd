@echo off
rem ============================================================
rem  DPT.PSP AdHoc Server - firewall rules (asks for UAC "Yes")
rem  Ports are read from bin\config.ini.
rem
rem  extra options:
rem    firewall.cmd -PublicAny     allow the public board from anywhere
rem    firewall.cmd -AdhocLanOnly  allow the adhoc port from LAN only
rem    firewall.cmd -Remove        delete the DPT.PSP rules
rem ============================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0firewall.ps1" %*
endlocal