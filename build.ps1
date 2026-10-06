$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskWpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$taskRefs = @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll','UIAutomationClient.dll','UIAutomationTypes.dll') | ForEach-Object { '/r:' + (Join-Path $taskWpf $_) }
$taskSources = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')) ('/out:' + (Join-Path $taskRoot 'CodexQuotaTaskbar.exe')) /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll @taskRefs @taskSources
if ($LASTEXITCODE -ne 0) { throw '控件编译失败' }
Write-Output (Join-Path $taskRoot 'CodexQuotaTaskbar.exe')
