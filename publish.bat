@ECHO OFF
powershell -ExecutionPolicy ByPass -NoProfile "%~dp0publish.ps1" %*
