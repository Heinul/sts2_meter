@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

set "BASE=C:\Users\HeNull\AppData\Roaming\SlayTheSpire2\steam\76561198137802403"
set "SETTINGS=%BASE%\settings.save"
set "MODDED=%BASE%\modded"

echo ========================================
echo   STS2 모드 토글
echo ========================================
echo.

:: settings.save 확인
if not exist "%SETTINGS%" (
    echo [오류] settings.save 를 찾을 수 없습니다.
    echo 경로: %SETTINGS%
    pause
    exit /b 1
)

:: 현재 mods_enabled 값 확인
findstr /C:"\"mods_enabled\": true" "%SETTINGS%" > nul 2>&1
if %errorlevel% == 0 (
    set "CURRENT=true"
    set "TARGET=false"
) else (
    set "CURRENT=false"
    set "TARGET=true"
)

echo [현재] mods_enabled: %CURRENT%
echo [전환] %CURRENT% -^> %TARGET%
echo.

if "%TARGET%"=="true" (
    :: false → true (모드 활성화)
    :: 현재 프로필(바닐라)을 modded/에 백업
    echo 프로필 백업: base -^> modded/
    if not exist "%MODDED%" mkdir "%MODDED%"
    for %%P in (profile1 profile2 profile3) do (
        if exist "%BASE%\%%P" (
            robocopy "%BASE%\%%P" "%MODDED%\%%P" /MIR /NJH /NJS /NDL > nul
            echo   %%P -^> modded\%%P [OK]
        )
    )
) else (
    :: true → false (모드 비활성화)
    :: modded/에서 바닐라 프로필 복원
    if not exist "%MODDED%" (
        echo [경고] modded 폴더가 없습니다. 프로필 복원 건너뜀.
    ) else (
        echo 프로필 복원: modded/ -^> base
        for %%P in (profile1 profile2 profile3) do (
            if exist "%MODDED%\%%P" (
                robocopy "%MODDED%\%%P" "%BASE%\%%P" /MIR /NJH /NJS /NDL > nul
                echo   modded\%%P -^> %%P [OK]
            )
        )
    )
)

:: settings.save에서 mods_enabled 토글
powershell -Command "$f='%SETTINGS%'; $c=[System.IO.File]::ReadAllText($f); $c=$c.Replace('\"mods_enabled\": %CURRENT%','\"mods_enabled\": %TARGET%'); [System.IO.File]::WriteAllText($f,$c,[System.Text.UTF8Encoding]::new($false))"

echo.
echo ========================================
echo   [완료] mods_enabled: %TARGET%
echo ========================================
echo.
pause
