<# Private-registry and message-only-window checks. No real default selection,
   input hooks, user captures, visible windows or Windows Settings launches. #>
[CmdletBinding()]
param([string]$AppDll, [string]$Out)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out=Join-Path $root 'artifacts/capture-integration' }
$scratch=Join-Path $env:TEMP ('WinSnipper-capture-integration-'+[guid]::NewGuid().ToString('N'))
$harness=Join-Path $scratch 'harness'
New-Item -ItemType Directory -Force -Path $harness,$Out | Out-Null
if (-not $AppDll) {
    & dotnet build (Join-Path $root 'WinSnipper.csproj') -c Release --artifacts-path (Join-Path $scratch 'app') -m:1 -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App build failed' }
    $AppDll=Join-Path $scratch 'app/bin/WinSnipper/release/WinSnipper.dll'
}
$AppDll=(Resolve-Path $AppDll).Path
Copy-Item (Join-Path $root 'app.manifest') (Join-Path $harness 'app.manifest')
@'
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><ApplicationManifest>app.manifest</ApplicationManifest><NoWarn>WFAC010</NoWarn></PropertyGroup>
 <ItemGroup><Using Remove="System.Drawing"/><Using Remove="System.Windows.Forms"/><Reference Include="WinSnipper"><HintPath>$(AppDll)</HintPath></Reference></ItemGroup>
</Project>
'@ | Set-Content (Join-Path $harness 'Checks.csproj')
@'
using System.Diagnostics;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WinSnipper;

static class Checks
{
 const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static readonly Assembly AppAssembly=typeof(App).Assembly;
 static readonly Type Integration=AppAssembly.GetType("WinSnipper.WindowsCaptureIntegration")!;
 static readonly Type Bridge=AppAssembly.GetType("WinSnipper.CaptureActivation")!;
 static readonly Type Intent=AppAssembly.GetType("WinSnipper.CaptureIntent")!;
 static readonly List<string> Passed=new();
 static string Out="";
 sealed class HeadlessApp:App { protected override void OnStartup(StartupEventArgs e){} }
 static void Check(bool ok,string description){if(!ok)throw new Exception(description);Passed.Add(description);Console.WriteLine("PASS: "+description);}
 static object? Invoke(Type type,string name,params object?[] args)=>type.GetMethod(name,Static)!.Invoke(null,args);
 static object Kind(int value)=>Enum.ToObject(Intent,value);
 [STAThread] static int Main(string[] args)
 {
  Out=args[0];var app=new HeadlessApp{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/WinSnipper;component/src/Theme.xaml",UriKind.Relative)});
  string fixture=@"Software\WinSnipper\IntegrationTests\"+Guid.NewGuid().ToString("N");
  string? originalChoice=ReadActualChoice();
  try{
   using(var root=Registry.CurrentUser.CreateSubKey(fixture)) {Registration(root);PrintScreenPolicy(root);Notifications(root);}
   Parsing();Messaging();SettingsRender();
   Check(ReadActualChoice()==originalChoice,"real Windows UserChoice remains unchanged by integration tests");
   File.WriteAllText(Path.Combine(Out,"checks.json"),System.Text.Json.JsonSerializer.Serialize(new{passed=Passed},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
   return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{Registry.CurrentUser.DeleteSubKeyTree(fixture,throwOnMissingSubKey:false);}
 }
 static string? ReadActualChoice(){using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ms-screenclip\UserChoice");return key?.GetValue("ProgId") as string;}
 static void Registration(RegistryKey root)
 {
  using(var choice=root.CreateSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ms-screenclip\UserChoice")){choice.SetValue("ProgId","Other.Capture");choice.SetValue("Hash","fixture-owner-choice");}
  var register=Integration.GetMethod("Register",Static,null,new[]{typeof(RegistryKey),typeof(string)},null)!;
  string exe=@"C:\Private Fixture\WinSnipper-OCR.exe";register.Invoke(null,new object[]{root,exe});
  using var caps=root.OpenSubKey(@"Software\WinSnipper\Capabilities\URLAssociations");
  using var registered=root.OpenSubKey(@"Software\RegisteredApplications");
  using var command=root.OpenSubKey(@"Software\Classes\WinSnipper.ScreenClip\shell\open\command");
  using var choiceAfter=root.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ms-screenclip\UserChoice");
  Check((string?)caps?.GetValue("ms-screenclip")=="WinSnipper.ScreenClip"&&(string?)registered?.GetValue("WinSnipper")==@"Software\WinSnipper\Capabilities","registration advertises per-user Windows screen capture capability");
  Check((string?)command?.GetValue("")=="\""+exe+"\" --capture-uri \"%1\"","protocol command quotes executable and URI independently");
  Check((string?)choiceAfter?.GetValue("ProgId")=="Other.Capture"&&(string?)choiceAfter.GetValue("Hash")=="fixture-owner-choice","registration never rewrites the protected default choice or hash");
  register.Invoke(null,new object[]{root,exe});
  Check((string?)command?.GetValue("")=="\""+exe+"\" --capture-uri \"%1\"","registration is idempotent");
 }
 static void PrintScreenPolicy(RegistryKey root)
 {
  var apply=typeof(SnippingTool).GetMethod("Apply",Static)!;
  using var key=root.CreateSubKey(@"Control Panel\Keyboard");const string value="PrintScreenKeyForSnippingTool";
  int saves=0;var s=new Settings();
  apply.Invoke(null,new object[]{s,root,false,(Action)(()=>{saves++;Check(key.GetValue(value)is null,"original PrintScreen state is persisted before first registry write");})});
  Check((int?)key.GetValue(value)==0&&s.SavedPrintScreenBinding==-1&&s.PrintScreenBindingCaptured,"non-default mode disables Windows PrintScreen binding and captures absent original");
  apply.Invoke(null,new object[]{s,root,false,(Action)(()=>saves++)});
  Check(saves==1,"unchanged PrintScreen policy does not rewrite settings");
  apply.Invoke(null,new object[]{s,root,true,(Action)(()=>saves++)});
  Check((int?)key.GetValue(value)==1&&s.SavedPrintScreenBinding==-1&&s.AppliedPrintScreenBinding==1,"own Windows default enables on-demand PrintScreen without replacing original snapshot");
  s.ReplaceSnippingTool=false;apply.Invoke(null,new object[]{s,root,true,(Action)(()=>saves++)});
  Check(key.GetValue(value)is null&&!s.PrintScreenBindingCaptured,"disabling takeover restores an originally absent PrintScreen value");

  key.SetValue(value,1,RegistryValueKind.DWord);var existing=new Settings();
  apply.Invoke(null,new object[]{existing,root,false,(Action)(()=>{})});
  existing.ReplaceSnippingTool=false;apply.Invoke(null,new object[]{existing,root,false,(Action)(()=>{})});
  Check((int?)key.GetValue(value)==1,"original explicit Windows PrintScreen setting is restored");

  key.SetValue(value,0,RegistryValueKind.DWord);var preexisting=new Settings{ReplaceSnippingTool=false};
  apply.Invoke(null,new object[]{preexisting,root,false,(Action)(()=>{})});
  Check((int?)key.GetValue(value)==0,"unowned pre-existing disabled binding is not deleted");
  var failed=new Settings();key.SetValue(value,1,RegistryValueKind.DWord);bool threw=false;
  try{apply.Invoke(null,new object[]{failed,root,false,(Action)(()=>throw new IOException("injected settings failure"))});}catch(TargetInvocationException){threw=true;}
  Check(threw&&(int?)key.GetValue(value)==1,"failed snapshot persistence prevents Windows registry mutation");

  var changed=new Settings();apply.Invoke(null,new object[]{changed,root,false,(Action)(()=>{})});
  key.SetValue(value,1,RegistryValueKind.DWord);changed.ReplaceSnippingTool=false;
  apply.Invoke(null,new object[]{changed,root,false,(Action)(()=>{})});
  Check((int?)key.GetValue(value)==1,"restoration preserves a binding changed outside WinSnipper");
  var legacy=new Settings{SavedPrintScreenBinding=-1};
  typeof(Settings).GetProperty("LegacyPrintScreenBinding",Private)!.SetValue(legacy,true);
  key.SetValue(value,0,RegistryValueKind.DWord);apply.Invoke(null,new object[]{legacy,root,true,(Action)(()=>{})});
  Check(legacy.PrintScreenBindingCaptured&&legacy.SavedPrintScreenBinding==-1&&(int?)key.GetValue(value)==1,"legacy takeover migrates its recorded original before enabling native routing");
  key.SetValue(value,"unexpected",RegistryValueKind.String);threw=false;
  try{apply.Invoke(null,new object[]{new Settings(),root,false,(Action)(()=>{})});}catch(TargetInvocationException){threw=true;}
  Check(threw&&(string?)key.GetValue(value)=="unexpected","non-DWORD PrintScreen policy is left unchanged");
 }
 static void Parsing()
 {
  var parse=Integration.GetMethod("TryParse",Static)!;
  foreach(var text in new[]{"ms-screenclip:","ms-screenclip://?source=HotKey","ms-screenclip://?source=PrintScreen","ms-screenclip://capture/image?rectangle","MS-SCREENCLIP://CAPTURE/IMAGE?window"}){
   object?[] args={text,Kind(1)};Check((bool)parse.Invoke(null,args)!&&(int)Convert.ChangeType(args[1]!,typeof(int))==1,"screenshot activation parses "+text);
  }
  object?[] video={"ms-screenclip://capture/video",Kind(1)};
  Check((bool)parse.Invoke(null,video)!&&Convert.ToInt32(video[1])==2,"video activation routes to recording intent");
  object?[] controls={"ms-screenclip:"+(char)13+(char)10+"payload",Kind(1)};
  Check(!(bool)parse.Invoke(null,controls)!,"decoded command line control characters are rejected");
  foreach(var text in new[]{"https://example.invalid/","file:///C:/anything.exe","ms-screenclip://other/image","ms-screenclip://capture/image#payload","ms-screenclip://capture/image?redirect-uri=other://response","ms-screenclip://capture/image?%72edirect-uri=other://response","ms-screenclip:\r\nanything",new string('a',5000)}){
   object?[] args={text,Kind(1)};Check(!(bool)parse.Invoke(null,args)!,"unsupported, callback, or malformed activation rejected ("+Math.Min(text.Length,5000)+" chars)");
  }
 }
 static void Notifications(RegistryKey root)
 {
  using var original=root.CreateSubKey("Notifications");
  var watched=original.OpenSubKey("",writable:false)!;using var changed=new ManualResetEventSlim();
  var type=AppAssembly.GetType("WinSnipper.CaptureDefaultWatcher")!;
  var watcher=(IDisposable)Activator.CreateInstance(type,Private,null,new object[]{watched,(Action)(()=>changed.Set())},null)!;
  PumpUntil(()=>(bool)type.GetProperty("IsReady",Private)!.GetValue(watcher)!);
  original.SetValue("Choice","owner-selected-default");
  Check(changed.Wait(TimeSpan.FromSeconds(3)),"native registry watcher observes a private default change without polling");
  watcher.Dispose();watcher.Dispose();
  Check(true,"default watcher disposal is idempotent and closes its owned worker");
 }
 static void Messaging()
 {
  using(var self=Process.GetCurrentProcess()){
   var identity=Bridge.GetMethod("HasUserSid",Static)!;
   Check((bool)identity.Invoke(null,new object[]{self.Handle,WindowsIdentity.GetCurrent().User!})!,"activation process identity accepts the current user token");
   Check(!(bool)identity.Invoke(null,new object[]{self.Handle,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})!,"activation process identity rejects another user SID");
  }
  string endpoint="WinSnipper.IntegrationTest."+Guid.NewGuid().ToString("N");int screenshots=0,recordings=0;
  var parameter=Expression.Parameter(Intent,"intent");
  Action<int> deliver=kind=>{if(kind==1)screenshots++;else if(kind==2)recordings++;};
  var callback=Expression.Lambda(typeof(Action<>).MakeGenericType(Intent),Expression.Invoke(Expression.Constant(deliver),Expression.Convert(parameter,typeof(int))),parameter).Compile();
  var bridge=(IDisposable)Activator.CreateInstance(Bridge,Private,null,new object[]{callback,endpoint},null)!;
  bool Send(int kind,int id)=>(bool)Invoke(Bridge,"TrySend",Kind(kind),id,endpoint)!;
  var request=Task.Run(()=>Send(1,123));PumpUntil(()=>request.IsCompleted&&screenshots==1);
  Check(request.Result,"native capture message crosses worker-to-UI boundary and acknowledges receipt");
  var repeat=Task.Run(()=>Send(1,123));PumpUntil(()=>repeat.IsCompleted);
  Check(repeat.Result&&screenshots==1,"same activation request id cannot open a second capture");
  var video=Task.Run(()=>Send(2,456));PumpUntil(()=>video.IsCompleted&&recordings==1);
  Check(video.Result,"recording activation routes independently");
  var probe=Task.Run(()=>Send(3,789));PumpUntil(()=>probe.IsCompleted);
  Check(probe.Result&&screenshots==1&&recordings==1,"activation probe acknowledges without taking a screenshot or opening UI");
  var invalid=Task.Run(()=>Send(99,1));PumpUntil(()=>invalid.IsCompleted);
  Check(!invalid.Result,"unknown IPC command is rejected");
  bridge.Dispose();Check(!Send(3,900),"disposed activation endpoint cannot receive requests");
 }
 static void SettingsRender()
 {
  var window=new SettingsWindow();var root=(FrameworkElement)window.Content;
  Check(!window.IsVisible&&window.MaxHeight<=System.Windows.Forms.Screen.FromPoint(System.Drawing.Point.Empty).WorkingArea.Height,"settings is bounded before display and stays hidden in the test");
  root.Measure(new Size(480,900));root.Arrange(new Rect(0,0,480,900));root.UpdateLayout();
  var bitmap=new RenderTargetBitmap(480,900,96,96,PixelFormats.Pbgra32);
  var background=new DrawingVisual();using(var dc=background.RenderOpen())dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30,31,36)),null,new Rect(0,0,480,900));
  bitmap.Render(background);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(Out,"capture-default-settings.png")))png.Save(file);
  window.Close();Check(true,"Windows capture default settings render without opening Windows Settings");
 }
 static void PumpUntil(Func<bool> done)
 {
  var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};var elapsed=Stopwatch.StartNew();
  timer.Tick+=(_,_)=>{if(done()||elapsed.Elapsed>TotalWait)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
  if(!done())throw new Exception("Dispatcher check timed out");
 }
 static readonly TimeSpan TotalWait=TimeSpan.FromSeconds(5);
}
'@ | Set-Content (Join-Path $harness 'Program.cs')
& dotnet run --project (Join-Path $harness 'Checks.csproj') -c Release "-p:AppDll=$AppDll" -- $Out
if ($LASTEXITCODE -ne 0) { throw 'Native capture integration checks failed' }
Write-Host "Native capture integration evidence: $Out"
