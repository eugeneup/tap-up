param([switch]$Test, [string]$BongoAssembly, [switch]$BuildWindowProbe, [switch]$BuildTools, [string]$BongoManaged = 'C:\Program Files (x86)\Steam\steamapps\common\BongoCat\BongoCat_Data\Managed')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler not found. Requires 64-bit Windows with .NET Framework 4.8.' }
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path dist,build -Force | Out-Null
    & $compiler /nologo /target:library /platform:anycpu /optimize+ /warnaserror+ /out:build\TapUp.BongoBridge.dll bridge\BongoBridge.cs
    if ($LASTEXITCODE -ne 0) { throw 'Game bridge build failed.' }
    if (-not (Test-Path -LiteralPath vendor\Mono.Cecil.dll)) { throw 'Missing vendor/Mono.Cecil.dll (official Mono.Cecil 0.11.6 net40 build).' }
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /out:build\PrepareBridge.exe /reference:vendor\Mono.Cecil.dll /resource:vendor\Mono.Cecil.dll,Cecil tools\PrepareBridge.cs
    if ($LASTEXITCODE -ne 0) { throw 'Bridge preparation tool build failed.' }
    & .\build\PrepareBridge.exe build\TapUp.BongoBridge.dll $BongoManaged
    if ($LASTEXITCODE -ne 0) { throw 'Bridge is incompatible with the game Mono libraries.' }
    $gameReferences = @('/reference:vendor\Mono.Cecil.dll', '/resource:vendor\Mono.Cecil.dll,TapUp.Mono.Cecil', '/resource:build\TapUp.BongoBridge.dll,TapUp.GameBridge', '/resource:vendor\Mono.Cecil.LICENSE.txt,TapUp.CecilLicense')
    $appSources = @(Get-ChildItem -LiteralPath src -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ /win32manifest:app.manifest /win32icon:assets\Tap-Up.ico /out:dist\Tap-Up.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @gameReferences @appSources
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    Copy-Item -LiteralPath README.md -Destination dist\README.md -Force
    Copy-Item -LiteralPath LICENSE -Destination dist\LICENSE -Force
    @('Mono.Cecil 0.11.6 (MIT)', 'https://github.com/jbevain/cecil', '', (Get-Content -LiteralPath vendor\Mono.Cecil.LICENSE.txt -Raw)) | Set-Content -LiteralPath dist\THIRD-PARTY-NOTICES.txt -Encoding utf8
    if ($BuildTools) {
        & $compiler /nologo /target:exe /main:GamePatchCli /platform:x64 /optimize+ /warnaserror+ /out:build\GamePatch.exe @gameReferences src\GamePatch.cs tools\GamePatchCli.cs
        if ($LASTEXITCODE -ne 0) { throw 'Game setup CLI build failed.' }
        & $compiler /nologo /target:exe /main:ChestProbe /platform:x64 /optimize+ /warnaserror+ /out:build\ChestProbe.exe src\ChestConnection.cs tools\ChestProbe.cs
        if ($LASTEXITCODE -ne 0) { throw 'Chest probe build failed.' }
    }
    Write-Output "Built: $PSScriptRoot\dist\Tap-Up.exe"
    if ($Test) {
        & $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /out:build\TapUp.Tests.exe src\NativeInput.cs src\TapEngine.cs tests\EngineTests.cs
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
        & .\build\TapUp.Tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
        & $compiler /nologo /target:exe /main:ButtonTests /platform:x64 /optimize+ /warnaserror+ /out:build\Button.Tests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @gameReferences @appSources tests\ButtonTests.cs
        if ($LASTEXITCODE -ne 0) { throw 'Button test build failed.' }
        & .\build\Button.Tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Button tests failed.' }
        & $compiler /nologo /target:exe /main:CounterTests /platform:x64 /optimize+ /warnaserror+ /out:build\Counter.Tests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @gameReferences @appSources tests\CounterTests.cs tests\FakeCollector.cs
        if ($LASTEXITCODE -ne 0) { throw 'Counter test build failed.' }
        & .\build\Counter.Tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Counter tests failed.' }
        & $compiler /nologo /target:exe /main:ChestUiTests /platform:x64 /optimize+ /warnaserror+ /out:build\ChestUI.Tests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @gameReferences @appSources tests\ChestUiTests.cs tests\FakeCollector.cs
        if ($LASTEXITCODE -ne 0) { throw 'Chest test build failed.' }
        & .\build\ChestUI.Tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Chest tests failed.' }
        & $compiler /nologo /target:exe /main:GameBridgeTests /platform:x64 /optimize+ /warnaserror+ /out:build\GameBridge.Tests.exe @gameReferences /reference:build\TapUp.BongoBridge.dll src\GamePatch.cs src\ChestConnection.cs tests\GameBridgeTests.cs
        if ($LASTEXITCODE -ne 0) { throw 'Game bridge test build failed.' }
        & .\build\GameBridge.Tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Game bridge tests failed.' }
    }
    if ($BongoAssembly) {
        & $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /out:build\VerifyBongo.exe src\NativeInput.cs src\TapEngine.cs tools\VerifyBongo.cs
        if ($LASTEXITCODE -ne 0) { throw 'Compatibility probe build failed.' }
        & .\build\VerifyBongo.exe $BongoAssembly
        if ($LASTEXITCODE -ne 0) { throw 'Bongo input compatibility probe failed.' }
    }
    if ($BuildWindowProbe) {
        & $compiler /nologo /target:winexe /main:VerifyBongoWindow /platform:x64 /optimize+ /warnaserror+ /out:build\VerifyBongoWindow.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\NativeInput.cs src\TapEngine.cs tools\VerifyBongo.cs tools\VerifyBongoWindow.cs
        if ($LASTEXITCODE -ne 0) { throw 'Windowed compatibility probe build failed.' }
    }
} finally { Pop-Location }
