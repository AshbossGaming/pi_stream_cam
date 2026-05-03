@if "%DEBUG%" == "" @echo off
@rem ###### START OF GradleWrapper #######
@rem ###### END OF GradleWrapper #######
set DIR=%~dp0
set EXEC_DIR=%DIR%gradle\wrapper\gradle-wrapper.jar
set JAVA_EXE=java.exe
%JAVA_EXE% -jar "%EXEC_DIR%" %*
