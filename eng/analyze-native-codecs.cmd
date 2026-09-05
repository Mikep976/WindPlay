@echo off
call "%~1\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cl /nologo /std:c++20 /EHsc /W4 /WX /sdl /analyze /analyze:external- /external:W0 /external:I"%~2\include" /c "%~3\native\WindPlay.Codecs\WindPlay.Codecs.cpp" /Fo"%~3\artifacts\security\native-analysis-x64.obj"
exit /b %errorlevel%
