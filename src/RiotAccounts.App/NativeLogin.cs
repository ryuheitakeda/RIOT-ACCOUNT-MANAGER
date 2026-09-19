using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace RiotAccounts.App;

public interface IRiotLoginAutomation
{
    Task LoginAsync(Credentials credentials,IProgress<string> progress,CancellationToken ct);
}
public sealed record InputBounds(int X,int Y,int Width,int Height);
public sealed record CalibrationProgress(int Step,string Field);
public sealed record Calibration(int Width,int Height,uint Dpi,string ClientVersion,int UserX,int UserY,int PasswordX,int PasswordY,int SubmitX,int SubmitY,byte[] Template,int SchemaVersion=0,InputBounds? UserBounds=null,InputBounds? PasswordBounds=null,byte[]? PasswordTemplate=null,InputBounds? SubmitBounds=null,string? SubmitName=null,string? SubmitAutomationId=null);
public sealed class NativeLogin(Store store):IRiotLoginAutomation
{
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
    private Process? FindClient()
    {
        var root=Path.GetDirectoryName(ClientPath()??throw new InvalidOperationException("設定でRiotClientServices.exeを選択してください。"))!;
        Process? found=null;
        foreach(var process in Process.GetProcessesByName("RiotClientUx"))
        {
            try
            {
                var executable=process.MainModule?.FileName;
                var hwnd=process.MainWindowHandle;
                if(!process.HasExited&&hwnd!=IntPtr.Zero&&Win.IsWindowVisible(hwnd)&&executable!=null&&Path.GetFullPath(executable).StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
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
        var found=FindClient();if(found!=null)return found;
        using var started=Process.Start(new ProcessStartInfo(ClientPath()??throw new InvalidOperationException("Riotクライアントが見つかりません。設定で実行ファイルを選択してください。")){UseShellExecute=true});
        for(var i=0;i<60;i++){await Task.Delay(500,ct);found=FindClient();if(found!=null)return found;}
        throw new InvalidOperationException("Riotのログイン画面を開いてから再試行してください。");
    }
    private static (AutomationElement User,AutomationElement Password)? Detect(Target target)
    {
        try
        {
            var root=AutomationElement.FromHandle(target.Hwnd);
            var edits=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit))
                .Cast<AutomationElement>().Where(e=>ValidField(target,e,null)).ToList();
            var passwords=edits.Where(e=>e.Current.IsPassword).ToList();
            var users=edits.Where(e=>!e.Current.IsPassword).ToList();
            if(passwords.Count==1&&users.Count==1)return(users[0],passwords[0]);
        }
        catch(ElementNotAvailableException){ }
        catch(System.Runtime.InteropServices.COMException){ }
        return null;
    }
    // Setup inspection never reads credentials, types text, or submits the form.
    public async Task OpenForSetupAsync(CancellationToken ct)
    {
        using var process=await OpenClient(ct);
        Win.ShowWindow(process.MainWindowHandle,9);
        Win.SetForegroundWindow(process.MainWindowHandle);
    }
    public async Task<bool> CheckFieldsAsync(CancellationToken ct)
    {
        using var process=await OpenClient(ct);
        var hwnd=process.MainWindowHandle;
        Win.ShowWindow(hwnd,9);Win.SetForegroundWindow(hwnd);await Task.Delay(600,ct);
        using var target=new Target(process,hwnd);
        target.Check(ct);
        var fields=await Task.Run(()=>Detect(target),ct);
        target.Check(ct);
        return fields!=null;
    }
    public async Task LoginAsync(Credentials credentials,IProgress<string> progress,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(credentials.Username)||string.IsNullOrEmpty(credentials.Password)||credentials.Username.Any(char.IsControl)||credentials.Password.Any(char.IsControl))
            throw new InvalidOperationException("ログインIDとパスワードを確認してください。空欄・制御文字は入力できません。");
        progress.Report("Riotのログイン画面を確認中… Escで中止できます。入力中はキーボード・マウスを操作しないでください。");
        using var process=await OpenClient(ct);
        var hwnd=process.MainWindowHandle;
        Win.ShowWindow(hwnd,9);Win.SetForegroundWindow(hwnd);await Task.Delay(600,ct);
        using var target=new Target(process,hwnd);
        target.Check(ct);
        var fields=await Task.Run(()=>Detect(target),ct);
        target.Check(ct);
        var savedCalibration=fields==null?store.GetSecret("login-calibration-v2"):null;
        var calibration=savedCalibration==null?null:JsonSerializer.Deserialize<Calibration>(savedCalibration);
        if(fields==null)
        {
            if(calibration==null)throw new InvalidOperationException("入力欄を検出できません。設定の「自動入力を設定する」を開いてください。");
            ValidateCalibrationGeometry(target,calibration,ct);
        }
        var user=await Focus(target,fields?.User,calibration?.UserX??0,calibration?.UserY??0,false,ct);
        if(calibration!=null)ValidateCalibration(target,calibration,user,ct);
        await TypeText(target,user,credentials.Username,ct);
        var password=await Focus(target,fields?.Password,calibration?.PasswordX??0,calibration?.PasswordY??0,true,ct);
        // In coordinate mode, recheck the password area after the username changed the page.
        if(calibration!=null)
        {
            var current=Capture(target.Hwnd);
            if(RelativeBounds(target,password)!=calibration.PasswordBounds||!RegionMatches(calibration.PasswordTemplate!,current,calibration.Width,calibration.Height,calibration.PasswordBounds!))
                throw new InvalidOperationException("パスワード欄の表示が登録時と異なるため中止しました。空のフォームで再試行してください。");
        }
        await TypeText(target,password,credentials.Password,ct);
        CheckFieldFocus(target,password,true,ct);
        if(fields!=null)Win.Press(0x0D);
        else
        {
            var submit=FindButton(target,calibration!.SubmitX,calibration.SubmitY,true);
            if(RelativeBounds(target,submit)!=calibration.SubmitBounds||submit.Current.Name!=calibration.SubmitName||submit.Current.AutomationId!=calibration.SubmitAutomationId)throw UnsafeFocus();
            CheckFieldFocus(target,password,true,ct);
            Click(target,calibration.SubmitX,calibration.SubmitY,ct);
        }
        progress.Report("ログインを送信しました。認証成功は未確認です。認証結果・追加認証はRiotクライアントで確認してください。");
    }
    private static bool ValidField(Target target,AutomationElement field,bool? password)
    {
        var info=field.Current;
        if(info.ControlType!=ControlType.Edit||!info.IsEnabled||info.IsOffscreen||!info.IsKeyboardFocusable||password.HasValue&&info.IsPassword!=password.Value)return false;
        var bounds=info.BoundingRectangle;
        if(bounds.IsEmpty||bounds.Width<15||bounds.Height<10||bounds.Left<target.X||bounds.Top<target.Y||bounds.Right>target.X+target.Width||bounds.Bottom>target.Y+target.Height)return false;
        return BelongsToTarget(target,field);
    }
    private static bool BelongsToTarget(Target target,AutomationElement element)
    {
        var root=element;
        for(var i=0;i<64&&root!=null;i++)
        {
            if(root.Current.NativeWindowHandle==target.Hwnd.ToInt64())return true;
            root=TreeWalker.ControlViewWalker.GetParent(root);
        }
        return false;
    }
    private static AutomationElement FindButton(Target target,int x,int y,bool requireEnabled)
    {
        var point=ScreenPoint(target,x,y);
        var button=AutomationElement.FromPoint(new System.Windows.Point(point.X,point.Y));
        for(var i=0;i<64&&button!=null;i++)
        {
            var info=button.Current;
            if(info.ControlType==ControlType.Button&&!info.IsOffscreen&&(!requireEnabled||info.IsEnabled)&&BelongsToTarget(target,button)&&info.BoundingRectangle.Contains(new System.Windows.Point(point.X,point.Y)))return button;
            if(info.NativeWindowHandle==target.Hwnd.ToInt64())break;
            button=TreeWalker.ControlViewWalker.GetParent(button);
        }
        throw new InvalidOperationException("登録位置のログインボタンを確認できません。位置を再登録するか個別コピーで入力してください。");
    }
    private static async Task<AutomationElement> Focus(Target target,AutomationElement? field,int x,int y,bool password,CancellationToken ct)
    {
        target.Check(ct);CheckModifiers();
        if(field!=null)
        {
            if(!ValidField(target,field,password))throw UnsafeFocus();
            field.SetFocus();
        }
        else Click(target,x,y,ct);
        await Task.Delay(100,ct);
        target.Check(ct);
        var focused=AutomationElement.FocusedElement;
        if(focused==null||!ValidField(target,focused,password)||field!=null&&!Automation.Compare(focused,field))throw UnsafeFocus();
        if(field==null&&!focused.Current.BoundingRectangle.Contains(new System.Windows.Point(target.X+x,target.Y+y)))throw UnsafeFocus();
        target.RememberCursor();
        return focused;
    }
    private static InvalidOperationException UnsafeFocus()=>new("安全な入力先を確認できないため中止しました。Riotのログイン欄を確認してください。認識できない場合は個別コピーで入力してください。");
    private static void CheckFieldFocus(Target target,AutomationElement field,bool password,CancellationToken ct)
    {
        target.Check(ct);CheckModifiers();
        var nativeFocus=new Win.GuiThreadInfo{Size=(uint)Marshal.SizeOf<Win.GuiThreadInfo>()};
        if(!Win.GetGUIThreadInfo(0,ref nativeFocus)||nativeFocus.Focus==IntPtr.Zero||Win.GetAncestor(nativeFocus.Focus,2)!=target.Hwnd)throw UnsafeFocus();
        var focused=AutomationElement.FocusedElement;
        if(focused==null||!Automation.Compare(focused,field)||!ValidField(target,field,password)||!field.Current.HasKeyboardFocus)throw UnsafeFocus();
        target.CheckCursor();
    }
    private static async Task TypeText(Target target,AutomationElement field,string value,CancellationToken ct)
    {
        var password=field.Current.IsPassword;
        var bounds=field.Current.BoundingRectangle;
        CheckFieldFocus(target,field,password,ct);
        Win.SelectAll();
        foreach(var c in value)
        {
            await Task.Delay(8,ct); // Pump foreground events and cancellation before every character.
            CheckFieldFocus(target,field,password,ct);
            if(field.Current.BoundingRectangle!=bounds)throw UnsafeFocus();
            Win.Unicode(c);
        }
        await Task.Delay(80,ct);
        CheckFieldFocus(target,field,password,ct);
        if(!password&&field.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)&&((ValuePattern)pattern).Current.Value!=value)
            throw new InvalidOperationException("ログインIDの入力結果を確認できなかったため中止しました。");
    }
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
        foreach(var label in new[]{"ID欄","パスワード欄","ログインボタン"})
        {
            progress.Report(new(points.Count+1,label));
            while((Win.GetAsyncKeyState(0x77)&0x8000)!=0){target.Check(ct);await Task.Delay(50,ct);}
            while((Win.GetAsyncKeyState(0x77)&0x8000)==0){target.Check(ct);await Task.Delay(50,ct);}
            target.Check(ct);
            if(!Win.GetCursorPos(out var point)||!Win.ScreenToClient(hwnd,ref point))throw UnsafeFocus();
            ScreenPoint(target,point.X,point.Y);
            points.Add(point);await Task.Delay(150,ct);
        }
        if(Math.Abs(points[0].Y-points[1].Y)<15)throw new InvalidOperationException("ID欄とパスワード欄に別の位置を指定してください。");
        var submit=FindButton(target,points[2].X,points[2].Y,false);
        var submitBounds=RelativeBounds(target,submit);
        // Verify semantic focus even if enumeration failed. Custom fields that expose no
        // focused edit control cannot safely receive stored secrets; use manual copy instead.
        var user=await Focus(target,null,points[0].X,points[0].Y,false,ct);
        if(!user.TryGetCurrentPattern(ValuePattern.Pattern,out var value)||!string.IsNullOrEmpty(((ValuePattern)value).Current.Value))
            throw new InvalidOperationException("ID欄が空であることを確認できません。入力を空にして再登録してください。");
        var userBounds=RelativeBounds(target,user);
        var template=Capture(hwnd);
        var password=await Focus(target,null,points[1].X,points[1].Y,true,ct);
        var passwordBounds=RelativeBounds(target,password);
        var passwordTemplate=Capture(hwnd);
        if(userBounds==passwordBounds||userBounds.Y+userBounds.Height>passwordBounds.Y)
            throw new InvalidOperationException("入力欄の配置を確認できません。位置を再登録してください。");
        // Protected controls deliberately do not expose their value. The user confirms
        // both fields are empty in Settings before starting. Store each focused blank form.
        var result=new Calibration(target.Width,target.Height,target.Dpi,target.Version,points[0].X,points[0].Y,points[1].X,points[1].Y,points[2].X,points[2].Y,template,2,userBounds,passwordBounds,passwordTemplate,submitBounds,submit.Current.Name,submit.Current.AutomationId);
        target.Check(ct);store.SetSecret("login-calibration-v2",JsonSerializer.Serialize(result));return result;
    }
    private static InputBounds RelativeBounds(Target target,AutomationElement element)
    {
        var r=element.Current.BoundingRectangle;
        return new((int)Math.Floor(r.Left)-target.X,(int)Math.Floor(r.Top)-target.Y,(int)Math.Ceiling(r.Width),(int)Math.Ceiling(r.Height));
    }
    internal static bool CalibrationGeometryMatches(Calibration c,int width,int height,uint dpi,string version)=>
        c.SchemaVersion==2&&c.PasswordTemplate is{Length:>0}&&c.Template is{Length:>0}&&c.Width==width&&c.Height==height&&c.Dpi==dpi&&c.ClientVersion==version&&!string.IsNullOrWhiteSpace(version)&&width>0&&height>0&&dpi>0&&
        ValidBounds(c.UserBounds,width,height)&&ValidBounds(c.PasswordBounds,width,height)&&ValidBounds(c.SubmitBounds,width,height)&&
        Contains(c.UserBounds!,c.UserX,c.UserY)&&Contains(c.PasswordBounds!,c.PasswordX,c.PasswordY)&&Contains(c.SubmitBounds!,c.SubmitX,c.SubmitY)&&c.SubmitX>=0&&c.SubmitY>=0&&c.SubmitX<width&&c.SubmitY<height;
    private static bool ValidBounds(InputBounds? b,int width,int height)=>b!=null&&b.Width>=15&&b.Height>=10&&b.X>=0&&b.Y>=0&&(long)b.X+b.Width<=width&&(long)b.Y+b.Height<=height;
    private static bool Contains(InputBounds b,int x,int y)=>x>=b.X&&y>=b.Y&&x<(long)b.X+b.Width&&y<(long)b.Y+b.Height;
    private static void ValidateCalibrationGeometry(Target target,Calibration calibration,CancellationToken ct)
    {
        target.Check(ct);
        if(!CalibrationGeometryMatches(calibration,target.Width,target.Height,target.Dpi,target.Version))
            throw new InvalidOperationException("Riot画面の構成・サイズ・表示倍率・バージョンが変わりました。ログイン位置を再登録してください。");
        ScreenPoint(target,calibration.UserX,calibration.UserY);ScreenPoint(target,calibration.PasswordX,calibration.PasswordY);ScreenPoint(target,calibration.SubmitX,calibration.SubmitY);
    }
    private static void ValidateCalibration(Target target,Calibration calibration,AutomationElement user,CancellationToken ct)
    {
        ValidateCalibrationGeometry(target,calibration,ct);
        if(RelativeBounds(target,user)!=calibration.UserBounds||!user.TryGetCurrentPattern(ValuePattern.Pattern,out var value)||!string.IsNullOrEmpty(((ValuePattern)value).Current.Value))
            throw new InvalidOperationException("空のID欄と登録位置を確認できません。入力を空にして再試行してください。");
        var current=Capture(target.Hwnd);target.Check(ct);
        if(!RegionMatches(calibration.Template,current,calibration.Width,calibration.Height,calibration.UserBounds!)||!RegionMatches(calibration.Template,current,calibration.Width,calibration.Height,calibration.PasswordBounds!))
            throw new InvalidOperationException("登録した空のログイン画面と一致しません。ID・パスワード欄を空にし、同じ表示状態で再試行してください。配置が変わった場合は再登録してください。");
    }
    internal static byte[] Capture(IntPtr hwnd)
    {
        if(!Win.GetClientRect(hwnd,out var rect)||rect.Right<=0||rect.Bottom<=0||rect.Right>8192||rect.Bottom>8192)throw UnsafeFocus();
        var origin=new Win.Point();if(!Win.ClientToScreen(hwnd,ref origin))throw UnsafeFocus();
        using var bitmap=new Bitmap(rect.Right,rect.Bottom,PixelFormat.Format24bppRgb);
        using(var graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(origin.X,origin.Y,0,0,bitmap.Size);
        using var memory=new MemoryStream();bitmap.Save(memory,ImageFormat.Png);return memory.ToArray();
    }
    internal static bool TemplatesMatch(byte[] expected,byte[] actual,int width,int height,int ux,int uy,int px,int py)=>
        RegionMatches(expected,actual,width,height,Strip(width,height,ux,uy))&&RegionMatches(expected,actual,width,height,Strip(width,height,px,py));
    private static InputBounds Strip(int width,int height,int x,int y)
    {
        var left=Math.Max(0,x-130);var top=Math.Max(0,y-24);
        return new(left,top,Math.Min(width,x+130)-left,Math.Min(height,y+24)-top);
    }
    private static bool RegionMatches(byte[] expected,byte[] actual,int width,int height,InputBounds region)
    {
        if(expected==null||actual==null||!ValidBounds(region,width,height))return false;
        try
        {
            using var aStream=new MemoryStream(expected);using var bStream=new MemoryStream(actual);
            using var a=new Bitmap(aStream);using var b=new Bitmap(bStream);
            if(a.Width!=width||b.Width!=width||a.Height!=height||b.Height!=height)return false;
            // No averaged pixel threshold: even one entered character must invalidate the
            // blank template. Caret/hover changes may require a retry; fail closed.
            for(var y=region.Y;y<region.Y+region.Height;y++)
                for(var x=region.X;x<region.X+region.Width;x++)
                {
                    var c=a.GetPixel(x,y);var d=b.GetPixel(x,y);
                    if(Math.Abs(c.R-d.R)>2||Math.Abs(c.G-d.G)>2||Math.Abs(c.B-d.B)>2)return false;
                }
            return true;
        }
        catch(ArgumentException){return false;}
        catch(ExternalException){return false;}
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
