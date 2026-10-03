param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts\capture-ui'))
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path $PSScriptRoot -Parent
$taskBuild = Join-Path $env:TEMP ('winsnipper-capture-ui-' + [guid]::NewGuid().ToString('N'))
dotnet build "$taskRepo\WinSnipper.csproj" -c Release --artifacts-path $taskBuild | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Capture UI build failed' }
$taskAssembly = Join-Path $taskBuild 'bin\WinSnipper\release\WinSnipper.dll'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$taskLoaded = [Reflection.Assembly]::LoadFrom($taskAssembly)
$taskApplication = [Windows.Application]::new()
$taskApplication.Resources.MergedDictionaries.Add([Windows.ResourceDictionary]::new())
$taskApplication.Resources.MergedDictionaries[0].Source = [Uri]::new("$taskRepo\src\Theme.xaml", [UriKind]::Absolute)
$taskXml = [xml](Get-Content -LiteralPath "$taskRepo\src\CaptureHubWindow.xaml" -Raw)
$taskGrid = $taskXml.Window.Grid
$taskGrid.SetAttribute('xmlns', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$taskGrid.SetAttribute('xmlns:x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$taskResources = $taskXml.CreateElement('Grid.Resources', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$taskDictionary = $taskXml.CreateElement('ResourceDictionary', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$taskDictionary.SetAttribute('Source', [Uri]::new("$taskRepo\src\Theme.xaml", [UriKind]::Absolute).AbsoluteUri)
$taskWrapper = $taskXml.CreateElement('ResourceDictionary', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$taskMerged = $taskXml.CreateElement('ResourceDictionary.MergedDictionaries', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
[void]$taskMerged.AppendChild($taskDictionary)
[void]$taskWrapper.AppendChild($taskMerged)
foreach ($taskStyle in $taskXml.Window.'Window.Resources'.Style) { [void]$taskWrapper.AppendChild($taskStyle.CloneNode($true)) }
[void]$taskResources.AppendChild($taskWrapper)
[void]$taskGrid.PrependChild($taskResources)
foreach ($taskNode in $taskGrid.SelectNodes('descendant-or-self::*')) {
    $taskNode.RemoveAttribute('Click')
}
$taskReader = [Xml.XmlNodeReader]::new($taskGrid)
try { $taskSurface = [Windows.Markup.XamlReader]::Load($taskReader) }
catch { Write-Output $_.Exception.ToString(); throw }
$taskSurface.Background = $taskSurface.Resources['Ws.Chrome']
$taskSurface.SetValue([Windows.Documents.TextElement]::ForegroundProperty, $taskSurface.Resources['Ws.Text'])
$taskSurface.FindName('CaptureName').Text = 'Final edited screenshot.png'
$taskSurface.FindName('Status').Text = 'The final screenshot joins this draft. Nothing is sent to the agent.'
$taskTarget = $taskLoaded.GetType('Version2.Capture.CaptureDestination')
$taskDestination = [Activator]::CreateInstance($taskTarget, @('workspace','Fixture workspace','agent','Agent one','session','main',$null))
$taskSurface.FindName('Destinations').ItemsSource = @($taskDestination)
$taskSurface.FindName('Destinations').SelectedIndex = 0
$taskSurface.Measure([Windows.Size]::new(510,350))
$taskSurface.Arrange([Windows.Rect]::new(0,0,510,350))
$taskSurface.UpdateLayout()
$taskBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(510,350,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$taskBitmap.Render($taskSurface)
$taskEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$taskOutputFile = Join-Path ([IO.Path]::GetFullPath($Output)) 'winsnipper-capture-hub.png'
$taskStream = [IO.File]::Create($taskOutputFile)
try { $taskEncoder.Save($taskStream) } finally { $taskStream.Dispose() }
Write-Output $taskOutputFile
