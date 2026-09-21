using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RiotAccounts.App;

public interface IRiotLoginAutomation
{
    Task LoginAsync(Credentials credentials,IProgress<string> progress,CancellationToken ct);
}
public sealed record InputBounds(int X,int Y,int Width,int Height);
public sealed record CalibrationProgress(int Step,string Field);
// The current Riot client (Electron) exposes no UI Automation tree, so each field is registered as a point plus a
// pixel strip of the blank, focused form. Login clicks the point, requires the strip to match, and confirms typing by pixels.
public sealed record Calibration(int Width,int Height,uint Dpi,string ClientVersion,int UserX,int UserY,int PasswordX,int PasswordY,byte[] Template,byte[] PasswordTemplate,InputBounds? UserBounds,InputBounds? PasswordBounds,int SchemaVersion=3);
public sealed record ClientCheck(bool Found,bool Registered,string Detail);
public sealed class NativeLogin(Store store,DiagnosticLog? log=null):IRiotLoginAutomation
{
    private const string CalibrationKey="login-calibration-v3";
    private static readonly string[] ClientProcesses=["Riot Client","RiotClientUx"];
    public string? LogPath=>log?.FilePath;
    private void Log(string stage,string message)=>log?.Write(stage,message);
    public string? ClientPath()
    {
        var custom=store.Read<string>("setting","clientPath");
        if(!string.IsNullOrWhiteSpace(custom))return ValidateClientPath(custom);
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Riot Games","RiotClientInstalls.json");
        if(!File.Exists(path))return null;
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        foreach(var key in new[]{"rc_default","rc_live"})
            if(doc.RootElement.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is string exe&&ValidateClientPath(exe) is{} validated)return validated;
        return null;
    }
    private static string? ValidateClientPath(string path)=>File.Exists(path)&&string.Equals(Path.GetFileName(path),"RiotClientServices.exe",StringComparison.OrdinalIgnoreCase)?Path.GetFullPath(path):null;
    // The client UI process is "Riot Client" (Electron, under RiotClientElectron) today and was "RiotClientUx" before.
    // Only the process that owns the visible top-level window counts; helper processes have none.
    private Process? FindClient()
    {
        var root=Path.GetDirectoryName(ClientPath()??throw new InvalidOperationException("設定でRiotClientServices.exeを選択してください。"))!;
        Process? found=null;
        foreach(var name in ClientProcesses)
            foreach(var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var hwnd=process.MainWindowHandle;
                    if(process.HasExited||hwnd==IntPtr.Zero||!Win.IsWindowVisible(hwnd))continue;
                    var executable=process.MainModule?.FileName;
                    if(executable!=null&&Path.GetFullPath(executable).StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    {
                        if(found!=null)
                        {
                            found.Dispose();found=null;
                            throw new InvalidOperationException("Riot画面が複数あります。対象以外のRiotクライアントを閉じて再試行してください。");
                        }
                        found=process;
                    }
                }
                catch(System.ComponentModel.Win32Exception){ }
                catch(InvalidOperationException) when(process.HasExited){ }
                finally{if(!ReferenceEquals(found,process))process.Dispose();}
            }
        return found;
    }
    private async Task<Process> OpenClient(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var found=await Task.Run(FindClient,ct);if(found!=null)return found;
        using var started=Process.Start(new ProcessStartInfo(ClientPath()??throw new InvalidOperationException("Riotクライアントが見つかりません。設定で実行ファイルを選択してください。")){UseShellExecute=true});
        for(var i=0;i<60;i++){await Task.Delay(500,ct);found=await Task.Run(FindClient,ct);if(found!=null)return found;}
        Log("クライアント検出","表示中のRiot Client（またはRiotClientUx）のウィンドウが30秒以内に見つかりませんでした");
        throw new InvalidOperationException("Riotクライアントの画面が見つかりません。Riotクライアントを開き、ログイン画面を表示してから再試行してください。");
    }
    // Setup inspection never reads credentials, types text, or submits the form.
    public async Task OpenForSetupAsync(CancellationToken ct)
    {
        using var process=await OpenClient(ct);
        Win.ShowWindow(process.MainWindowHandle,9);
        Win.SetForegroundWindow(process.MainWindowHandle);
    }
    public async Task<ClientCheck> CheckClientAsync(CancellationToken ct)
    {
        try
        {
            using var process=await OpenClient(ct);
            var hwnd=process.MainWindowHandle;
            Win.ShowWindow(hwnd,9);Win.SetForegroundWindow(hwnd);await Task.Delay(600,ct);
            using var target=new Target(process,hwnd);
            target.Check(ct);
            var registered=false;
            if(store.GetSecret(CalibrationKey) is{Length:>0} saved)
                try{registered=JsonSerializer.Deserialize<Calibration>(saved) is{} c&&CalibrationGeometryMatches(c,target.Width,target.Height,target.Dpi,target.Version);}
                catch(JsonException){ }
            var detail=$"Riot画面 {target.Width}x{target.Height}・{target.Dpi}dpi・v{target.Version}";
            Log("画面確認",detail+(registered?"（登録済みの位置が有効）":"（位置は未登録、または画面構成が変わっています）"));
            return new(true,registered,detail);
        }
        catch(OperationCanceledException){Log("画面確認","中止");throw;}
        catch(Exception ex){Log("画面確認",$"{ex.GetType().Name}: {ex.Message}");throw;}
    }
    private Calibration LoadCalibration()
    {
        var saved=store.GetSecret(CalibrationKey);
        if(string.IsNullOrEmpty(saved))throw new InvalidOperationException("自動入力の位置が登録されていません。設定の「自動入力を設定する」で位置を登録してください。");
        try{return JsonSerializer.Deserialize<Calibration>(saved)??throw new JsonException();}
        catch(JsonException){throw new InvalidOperationException("登録済みの位置を読み取れません。設定の「自動入力を設定する」で登録し直してください。");}
    }
    public Task LoginAsync(Credentials credentials,IProgress<string> progress,CancellationToken ct)=>EnterAsync(credentials,true,progress,ct);
    // Types dummy text with the same code path as login, checks it appeared, then clears it. Never submits the form.
    public Task TestInputAsync(IProgress<string> progress,CancellationToken ct)=>EnterAsync(new Credentials("test-user","Dummy-Pass-1234"),false,progress,ct);
    private async Task EnterAsync(Credentials credentials,bool submit,IProgress<string> progress,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(credentials.Username)||string.IsNullOrEmpty(credentials.Password)||credentials.Username.Any(char.IsControl)||credentials.Password.Any(char.IsControl))
            throw new InvalidOperationException("ログインIDとパスワードを確認してください。空欄・制御文字は入力できません。");
        var stage="クライアント検出";
        try
        {
            progress.Report("Riotのログイン画面を確認中… Escで中止できます。入力中はキーボード・マウスを操作しないでください。");
            using var process=await OpenClient(ct);
            var hwnd=process.MainWindowHandle;
            Win.ShowWindow(hwnd,9);Win.SetForegroundWindow(hwnd);await Task.Delay(600,ct);
            using var target=new Target(process,hwnd);
            target.Check(ct);
            stage="登録位置の確認";
            var calibration=LoadCalibration();
            ValidateCalibrationGeometry(target,calibration,ct);
            Log(stage,$"{(submit?"ログイン":"試験入力")}: 画面{target.Width}x{target.Height}・{target.Dpi}dpiの登録位置を使用");
            var user=calibration.UserBounds!;var password=calibration.PasswordBounds!;
            stage="ID欄の確認";
            var userBefore=await FocusField(target,calibration.UserX,calibration.UserY,user,calibration.Template,"ID欄",ct);
            stage="ID入力";
            progress.Report("IDを入力中…");
            await TypeText(target,user,userBefore,credentials.Username,ct);
            Log(stage,"入力を確認しました");
            stage="パスワード欄の確認";
            var passwordBefore=await FocusField(target,calibration.PasswordX,calibration.PasswordY,password,calibration.PasswordTemplate,"パスワード欄",ct);
            stage="パスワード入力";
            progress.Report("パスワードを入力中…");
            await TypeText(target,password,passwordBefore,credentials.Password,ct);
            Log(stage,"入力を確認しました");
            if(!submit)
            {
                stage="試験入力の消去";
                await ClearField(target,password,calibration.PasswordTemplate,ct);
                await ClickField(target,calibration.UserX,calibration.UserY,ct);
                await ClearField(target,user,calibration.Template,ct);
                Log(stage,"消去しました（送信なし）");
                progress.Report("試験入力を確認できました。入力した文字は消去し、ログインは送信していません。");
                return;
            }
            stage="送信";
            CheckInputFocus(target,ct);
            Win.Press(0x0D);
            Log(stage,"送信操作を行いました");
            progress.Report("ID・パスワードの入力を確認し、ログインを送信しました。認証成功は未確認です。認証結果・追加認証はRiotクライアントで確認してください。");
        }
        catch(OperationCanceledException){Log(stage,"中止しました");throw;}
        catch(InvalidOperationException ex){Log(stage,ex.Message);throw new InvalidOperationException($"［{stage}］{ex.Message}",ex);}
        catch(Exception ex){Log(stage,$"{ex.GetType().Name} 0x{ex.HResult:X8}");throw;}
    }
    private static async Task ClickField(Target target,int x,int y,CancellationToken ct)
    {
        Click(target,x,y,ct);
        await Task.Delay(200,ct);
        target.Check(ct);
        target.RememberCursor();
    }
    // Click the registered point, then require the field strip to look like the registered blank, focused field.
    private static async Task<byte[]> FocusField(Target target,int x,int y,InputBounds region,byte[] template,string name,CancellationToken ct)
    {
        await ClickField(target,x,y,ct);
        CheckInputFocus(target,ct);
        var current=CaptureRegion(target.Hwnd,region);
        target.Check(ct);
        if(!LooksBlank(template,current,target.Dpi))
            throw new InvalidOperationException($"{name}が登録時の空の状態と一致しません。入力を空にし、同じ表示状態で再試行してください。配置が変わった場合は位置を登録し直してください。");
        return current;
    }
    private static async Task ClearField(Target target,InputBounds region,byte[] template,CancellationToken ct)
    {
        CheckInputFocus(target,ct);Win.SelectAll();Win.Press(0x08);
        await Task.Delay(150,ct);
        CheckInputFocus(target,ct);
        if(!LooksBlank(template,CaptureRegion(target.Hwnd,region),target.Dpi))
            throw new InvalidOperationException("試験入力の文字を消去できたか確認できません。入力欄の文字を手動で消してください。");
    }
    // Native keyboard focus must sit inside the Riot window, which must still be the foreground, unmoved window,
    // and the mouse must not have moved since the click.
    private static void CheckInputFocus(Target target,CancellationToken ct)
    {
        target.Check(ct);CheckModifiers();
        var nativeFocus=new Win.GuiThreadInfo{Size=(uint)Marshal.SizeOf<Win.GuiThreadInfo>()};
        if(!Win.GetGUIThreadInfo(0,ref nativeFocus)||nativeFocus.Focus==IntPtr.Zero||Win.GetAncestor(nativeFocus.Focus,2)!=target.Hwnd)throw UnsafeFocus();
        target.CheckCursor();
    }
    private static async Task TypeText(Target target,InputBounds region,byte[] before,string value,CancellationToken ct)
    {
        CheckInputFocus(target,ct);
        Win.SelectAll();
        foreach(var c in value)
        {
            await Task.Delay(8,ct); // Pump foreground events and cancellation before every character.
            CheckInputFocus(target,ct);
            Win.Unicode(c);
        }
        await Task.Delay(150,ct);
        CheckInputFocus(target,ct);
        // Protected fields never expose their content; confirm by the strip's pixels changing instead.
        if(!TextAppeared(before,CaptureRegion(target.Hwnd,region),value.Length,target.Dpi))
            throw new InvalidOperationException("入力欄に文字が入ったことを確認できなかったため、ログインは送信していません。入力欄の状態を確認し、必要なら手動で操作するか個別コピーを使ってください。");
    }
    private static InvalidOperationException UnsafeFocus()=>new("安全な入力先を確認できないため中止しました。Riotのログイン欄を確認してください。認識できない場合は個別コピーで入力してください。");
    public static void Guard(IntPtr hwnd,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if((Win.GetAsyncKeyState(0x1B)&0x8000)!=0)throw new OperationCanceledException("Escで中止しました。",ct);
        if(!Win.IsWindow(hwnd)||Win.GetForegroundWindow()!=hwnd)throw new InvalidOperationException("画面が切り替わったため入力を中止しました。");
    }
    private static void CheckModifiers()
    {
        foreach(var key in new[]{0x10,0x11,0x12,0x5B,0x5C,0x09,0x0D,0x01,0x02})
            if((Win.GetAsyncKeyState(key)&0x8000)!=0)throw new InvalidOperationException("キー・マウスボタンを離して再試行してください。");
    }
    private static Win.Point ScreenPoint(Target target,int x,int y)
    {
        if(x<0||y<0||x>=target.Width||y>=target.Height)throw new InvalidOperationException("登録位置がRiot画面の外側です。位置を再登録してください。");
        var point=new Win.Point{X=x,Y=y};
        if(!Win.ClientToScreen(target.Hwnd,ref point)||Win.GetAncestor(Win.WindowFromPoint(point),2)!=target.Hwnd)
            throw new InvalidOperationException("入力位置が別の画面に隠れているため中止しました。");
        return point;
    }
    private static void Click(Target target,int x,int y,CancellationToken ct)
    {
        target.Check(ct);CheckModifiers();
        var point=ScreenPoint(target,x,y);
        if(!Win.SetCursorPos(point.X,point.Y))throw new InvalidOperationException("入力位置へ移動できませんでした。");
        target.Check(ct);
        var verified=ScreenPoint(target,x,y);
        if(!Win.GetCursorPos(out var current)||current.X!=verified.X||current.Y!=verified.Y)throw UnsafeFocus();
        Win.Click();
    }
    public async Task<Calibration> CalibrateAsync(IProgress<CalibrationProgress> progress,CancellationToken ct)
    {
        using var process=await OpenClient(ct);var hwnd=process.MainWindowHandle;
        Win.ShowWindow(hwnd,9);Win.SetForegroundWindow(hwnd);await Task.Delay(400,ct);
        using var target=new Target(process,hwnd);target.Check(ct);
        var points=new List<Win.Point>();
        foreach(var label in new[]{"ID欄","パスワード欄"})
        {
            progress.Report(new(points.Count+1,label));
            while((Win.GetAsyncKeyState(0x77)&0x8000)!=0){target.Check(ct);await Task.Delay(50,ct);}
            while((Win.GetAsyncKeyState(0x77)&0x8000)==0){target.Check(ct);await Task.Delay(50,ct);}
            target.Check(ct);
            if(!Win.GetCursorPos(out var point)||!Win.ScreenToClient(hwnd,ref point))throw UnsafeFocus();
            ScreenPoint(target,point.X,point.Y);
            points.Add(point);await Task.Delay(150,ct);
        }
        var user=Strip(target.Width,target.Height,points[0].X,points[0].Y,target.Dpi);
        var password=Strip(target.Width,target.Height,points[1].X,points[1].Y,target.Dpi);
        if(!ValidBounds(user,target.Width,target.Height)||!ValidBounds(password,target.Width,target.Height)||Intersects(user,password)||points[0].Y>=points[1].Y)
            throw new InvalidOperationException("ID欄とパスワード欄の位置を確認できません。ID欄を上、パスワード欄を下にし、それぞれ入力欄の中央にマウスを置いて登録し直してください。");
        // The user confirmed both fields are empty. Store each field's strip as it looks when blank and focused.
        await ClickField(target,points[0].X,points[0].Y,ct);CheckInputFocus(target,ct);
        var template=CaptureRegion(hwnd,user);
        await ClickField(target,points[1].X,points[1].Y,ct);CheckInputFocus(target,ct);
        var passwordTemplate=CaptureRegion(hwnd,password);
        var result=new Calibration(target.Width,target.Height,target.Dpi,target.Version,points[0].X,points[0].Y,points[1].X,points[1].Y,template,passwordTemplate,user,password);
        target.Check(ct);store.SetSecret(CalibrationKey,JsonSerializer.Serialize(result));
        Log("位置登録",$"登録しました（画面{target.Width}x{target.Height}・{target.Dpi}dpi）");
        return result;
    }
    internal static bool CalibrationGeometryMatches(Calibration c,int width,int height,uint dpi,string version)=>
        c.SchemaVersion==3&&c.PasswordTemplate is{Length:>0}&&c.Template is{Length:>0}&&c.Width==width&&c.Height==height&&c.Dpi==dpi&&c.ClientVersion==version&&!string.IsNullOrWhiteSpace(version)&&width>0&&height>0&&dpi>0&&
        ValidBounds(c.UserBounds,width,height)&&ValidBounds(c.PasswordBounds,width,height)&&!Intersects(c.UserBounds!,c.PasswordBounds!)&&
        Contains(c.UserBounds!,c.UserX,c.UserY)&&Contains(c.PasswordBounds!,c.PasswordX,c.PasswordY)&&c.UserY<c.PasswordY;
    private static bool ValidBounds(InputBounds? b,int width,int height)=>b!=null&&b.Width>=15&&b.Height>=10&&b.X>=0&&b.Y>=0&&(long)b.X+b.Width<=width&&(long)b.Y+b.Height<=height;
    private static bool Contains(InputBounds b,int x,int y)=>x>=b.X&&y>=b.Y&&x<(long)b.X+b.Width&&y<(long)b.Y+b.Height;
    private static bool Intersects(InputBounds a,InputBounds b)=>a.X<(long)b.X+b.Width&&b.X<(long)a.X+a.Width&&a.Y<(long)b.Y+b.Height&&b.Y<(long)a.Y+a.Height;
    private static void ValidateCalibrationGeometry(Target target,Calibration calibration,CancellationToken ct)
    {
        target.Check(ct);
        if(!CalibrationGeometryMatches(calibration,target.Width,target.Height,target.Dpi,target.Version))
            throw new InvalidOperationException("Riot画面の構成・サイズ・表示倍率・バージョンが変わりました。ログイン位置を再登録してください。");
        ScreenPoint(target,calibration.UserX,calibration.UserY);ScreenPoint(target,calibration.PasswordX,calibration.PasswordY);
    }
    // A strip about one field wide and half a field tall around the registered point (scaled by DPI): it covers where typed text
    // and password dots appear, and stays inside the field so focus rings and neighbouring animation do not matter.
    private static InputBounds Strip(int width,int height,int x,int y,uint dpi)
    {
        var scale=Math.Clamp(dpi/96.0,1.0,4.0);
        var halfWidth=(int)(170*scale);var halfHeight=(int)(14*scale);
        var left=Math.Max(0,x-halfWidth);var top=Math.Max(0,y-halfHeight);
        return new(left,top,Math.Min(width,x+halfWidth)-left,Math.Min(height,y+halfHeight)-top);
    }
    internal static byte[] CaptureRegion(IntPtr hwnd,InputBounds region)
    {
        if(!Win.GetClientRect(hwnd,out var rect)||!ValidBounds(region,rect.Right,rect.Bottom))throw UnsafeFocus();
        var origin=new Win.Point();if(!Win.ClientToScreen(hwnd,ref origin))throw UnsafeFocus();
        using var bitmap=new Bitmap(region.Width,region.Height,PixelFormat.Format24bppRgb);
        using(var graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(origin.X+region.X,origin.Y+region.Y,0,0,bitmap.Size);
        using var memory=new MemoryStream();bitmap.Save(memory,ImageFormat.Png);return memory.ToArray();
    }
    // Number of pixel columns that differ noticeably between two same-size strips; -1 if they cannot be compared.
    internal static int ChangedColumns(byte[] first,byte[] second)
    {
        if(first==null||second==null)return -1;
        try
        {
            using var aStream=new MemoryStream(first);using var bStream=new MemoryStream(second);
            using var a=new Bitmap(aStream);using var b=new Bitmap(bStream);
            if(a.Width!=b.Width||a.Height!=b.Height)return -1;
            var columns=0;
            for(var x=0;x<a.Width;x++)
                for(var y=0;y<a.Height;y++)
                {
                    var c=a.GetPixel(x,y);var d=b.GetPixel(x,y);
                    if(Math.Abs(c.R-d.R)>24||Math.Abs(c.G-d.G)>24||Math.Abs(c.B-d.B)>24){columns++;break;}
                }
            return columns;
        }
        catch(ArgumentException){return -1;}
        catch(ExternalException){return -1;}
    }
    // Blank = at most a blinking caret's worth of columns changed. One typed character changes more, so leftover text is refused.
    internal static bool LooksBlank(byte[] template,byte[] current,uint dpi)
    {
        var changed=ChangedColumns(template,current);
        return changed>=0&&changed<=(int)Math.Ceiling(3*Math.Clamp(dpi/96.0,1.0,4.0));
    }
    internal static bool TextAppeared(byte[] before,byte[] after,int length,uint dpi)
    {
        if(length<=0)return false;
        var changed=ChangedColumns(before,after);
        return changed>=(int)Math.Ceiling((4+Math.Min(length,8))*Math.Clamp(dpi/96.0,1.0,4.0));
    }
    private sealed class Target:IDisposable
    {
        private readonly Process process;
        private readonly DateTime started;
        private readonly Win.WinEventProc callback;
        private readonly IntPtr hook;
        private bool focusLost;
        private Win.Point? cursor;
        public IntPtr Hwnd{get;}
        public int X{get;} public int Y{get;} public int Width{get;} public int Height{get;}
        public uint Dpi{get;}
        public string Version{get;}
        public Target(Process process,IntPtr hwnd)
        {
            this.process=process;Hwnd=hwnd;started=process.StartTime;
            if(!Win.GetClientRect(hwnd,out var rect)||rect.Right<=0||rect.Bottom<=0)throw UnsafeFocus();
            var origin=new Win.Point();if(!Win.ClientToScreen(hwnd,ref origin))throw UnsafeFocus();
            X=origin.X;Y=origin.Y;Width=rect.Right;Height=rect.Bottom;Dpi=Win.GetDpiForWindow(hwnd);
            Version=FileVersionInfo.GetVersionInfo(process.MainModule!.FileName!).FileVersion??"";
            if(Dpi==0||string.IsNullOrWhiteSpace(Version))throw new InvalidOperationException("Riot画面の表示倍率・バージョンを確認できません。");
            callback=(_,_,window,_,_,_,_)=>{if(window!=Hwnd)focusLost=true;};
            hook=Win.SetWinEventHook(3,3,IntPtr.Zero,callback,0,0,0); // EVENT_SYSTEM_FOREGROUND, out of context.
            if(hook==IntPtr.Zero)throw new InvalidOperationException("画面切替の監視を開始できないため中止しました。");
        }
        public void Check(CancellationToken ct)
        {
            Guard(Hwnd,ct);
            Win.GetWindowThreadProcessId(Hwnd,out var owner);
            if(focusLost||process.HasExited||process.StartTime!=started||owner!=process.Id||!Win.IsWindowVisible(Hwnd)||!Win.IsWindowEnabled(Hwnd))throw UnsafeFocus();
            var origin=new Win.Point();
            if(!Win.GetClientRect(Hwnd,out var rect)||!Win.ClientToScreen(Hwnd,ref origin)||rect.Right!=Width||rect.Bottom!=Height||origin.X!=X||origin.Y!=Y||Win.GetDpiForWindow(Hwnd)!=Dpi)
                throw new InvalidOperationException("入力中にRiot画面の位置・サイズ・表示倍率が変わったため中止しました。");
        }
        public void RememberCursor(){if(!Win.GetCursorPos(out var point))throw UnsafeFocus();cursor=point;}
        public void CheckCursor(){if(cursor is{} expected&&(!Win.GetCursorPos(out var current)||current.X!=expected.X||current.Y!=expected.Y))throw new InvalidOperationException("入力中にマウスが動いたため中止しました。");}
        public void Dispose(){if(hook!=IntPtr.Zero)Win.UnhookWinEvent(hook);GC.KeepAlive(callback);}
    }
}
internal static class Win
{
    [StructLayout(LayoutKind.Sequential)]public struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]public struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]public struct GuiThreadInfo{public uint Size,Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public Rect CaretRect;}
    [StructLayout(LayoutKind.Sequential)]private struct Input{public uint Type;public InputUnion Data;}
    [StructLayout(LayoutKind.Explicit)]private struct InputUnion{[FieldOffset(0)]public MouseInput Mouse;[FieldOffset(0)]public KeyboardInput Keyboard;}
    [StructLayout(LayoutKind.Sequential)]private struct MouseInput{public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct KeyboardInput{public ushort Key,Scan;public uint Flags,Time;public UIntPtr Extra;}
    internal delegate void WinEventProc(IntPtr hook,uint eventType,IntPtr hwnd,int objectId,int childId,uint eventThread,uint eventTime);
    [DllImport("user32.dll")]internal static extern IntPtr SetWinEventHook(uint eventMin,uint eventMax,IntPtr module,WinEventProc callback,uint processId,uint threadId,uint flags);
    [DllImport("user32.dll")]internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]public static extern bool GetGUIThreadInfo(uint threadId,ref GuiThreadInfo info);
    [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")]public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [DllImport("user32.dll")]public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]public static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")]public static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")]public static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")]public static extern bool ScreenToClient(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")]public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,Input[] inputs,int size);
    private static void Send(params Input[] inputs)
    {
        var sent=SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Input>());
        if(sent==(uint)inputs.Length)return;
        if(sent>0)
        {
            // A partial batch must not leave our Ctrl/key/mouse-down event held.
            var releases=inputs.Where(i=>i.Type==1&&(i.Data.Keyboard.Flags&2)!=0||i.Type==0&&(i.Data.Mouse.Flags&4)!=0).ToArray();
            if(releases.Length>0)SendInput((uint)releases.Length,releases,Marshal.SizeOf<Input>());
        }
        throw new InvalidOperationException("Windowsが入力を許可しませんでした。Riotとこのアプリを通常権限で起動してください。");
    }
    private static Input Keyboard(ushort key,bool up=false)=>new(){Type=1,Data=new InputUnion{Keyboard=new KeyboardInput{Key=key,Flags=up?2u:0u}}};
    public static void Press(ushort key)=>Send(Keyboard(key),Keyboard(key,true));
    public static void SelectAll()=>Send(Keyboard(0x11),Keyboard(0x41),Keyboard(0x41,true),Keyboard(0x11,true));
    public static void Unicode(char c)=>Send(new Input{Type=1,Data=new InputUnion{Keyboard=new KeyboardInput{Scan=c,Flags=4}}},new Input{Type=1,Data=new InputUnion{Keyboard=new KeyboardInput{Scan=c,Flags=6}}});
    public static void Click()=>Send(new Input{Data=new InputUnion{Mouse=new MouseInput{Flags=2}}},new Input{Data=new InputUnion{Mouse=new MouseInput{Flags=4}}});
    internal static int InputSize=>Marshal.SizeOf<Input>();
}
