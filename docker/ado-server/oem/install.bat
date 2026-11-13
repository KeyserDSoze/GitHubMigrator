@echo off
rem dockur/windows runs this once, as the last step of the unattended Windows install.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\OEM\setup.ps1 >> C:\OEM\setup.out.log 2>&1
