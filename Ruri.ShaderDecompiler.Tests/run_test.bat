@echo off
REM Compile Mandelbrot shader to SPIR-V (stripped symbols)
set DXC="C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe"

echo Compiling test.hlsl to SPIR-V...
%DXC% -T ps_6_0 -E main -spirv -Fo test.spv test.hlsl
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

echo Building and running test...
dotnet run --project ..\Ruri.ShaderDecompiler.Tests test.spv

echo Done.
